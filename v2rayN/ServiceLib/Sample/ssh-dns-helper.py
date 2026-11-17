#!/usr/bin/python3
"""Fixed-action, root-owned SSH DNS transaction helper. No caller-selected paths or commands."""
import base64
import contextlib
import datetime
import fcntl
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import socket
import stat
import struct
import subprocess
import sys
import tempfile
import time
import urllib.request

ROOT = Path("/opt/v2rayn/ssh-dns")
CONTROL = Path("/var/lib/v2rayn-ssh-dns-control")
UNIT = "v2rayn-ssh-dns.service"
TIMER = "v2rayn-ssh-dns-rollback"
RECOVERY = "v2rayn-ssh-dns-recover.service"
LEGACY_CONFIG = Path("/opt/ssh-egress-dns/config/unbound.conf")
LEGACY_CONFIG_SHA256 = "e65eefc210431fb133e9a43436a69b35d406fac69f153d85d1a4e5468fa86f94"
ENV = {"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LANG": "C", "LC_ALL": "C"}
PACKAGES = {
    "amd64": [
        ("main/n/nghttp2/libnghttp2-14_1.59.0-1ubuntu0.4_amd64.deb", "73adcbb9df32cd7c9d46b1c7598bbbbcf249be705d62d966694cfc9976a15dfe"),
        ("main/p/python3.12/libpython3.12t64_3.12.3-1ubuntu0.17_amd64.deb", "0d29f4763eb95b8a4283a4aca3ef1d9658629c618be1778def41bdc31be12b46"),
        ("main/p/protobuf-c/libprotobuf-c1_1.4.1-1ubuntu4_amd64.deb", "577b713651a61e82226e46c9c9e3c23cd16df9ee3c2b27b22b49c5abb2ef8f61"),
        ("main/d/dns-root-data/dns-root-data_2024071801~ubuntu0.24.04.1_all.deb", "ee4617a269ec00257bb56ea28eb0eca3b1d4248d02ff65f79d4d40244f732df4"),
        ("main/libe/libevent/libevent-2.1-7t64_2.1.12-stable-9ubuntu2.2_amd64.deb", "083ced6efb23476cb4de4e441b741af02db5653e67fa7fcd6cf7e5f7b78f6101"),
        ("universe/h/hiredis/libhiredis1.1.0_1.2.0-6ubuntu3_amd64.deb", "912a25553f29a47d1524c0b58a3c5bff2d067109da2adae9fa9c4fb15efaa568"),
        ("universe/u/unbound/unbound_1.19.2-1ubuntu3.12_amd64.deb", "b6e58bf5976b08db28876a9d7d6c2de546f4f14354f14ecdf93b7d932257e305"),
    ],
    "arm64": [
        ("main/n/nghttp2/libnghttp2-14_1.59.0-1ubuntu0.4_arm64.deb", "ec812e0b82dfb315b140020f4c8434191a89cf42c49dbb82605d0892b6783b59"),
        ("main/p/python3.12/libpython3.12t64_3.12.3-1ubuntu0.17_arm64.deb", "9e9c6f862540ecb99e495cc8b67373c9f53cff635c3b1d56db3cc6fa47bfd3a1"),
        ("main/p/protobuf-c/libprotobuf-c1_1.4.1-1ubuntu4_arm64.deb", "85af0991f551b2b7b0cfc3c98d65bf5c877ccc652da8a6430f4d5bb50f39c655"),
        ("main/d/dns-root-data/dns-root-data_2024071801~ubuntu0.24.04.1_all.deb", "ee4617a269ec00257bb56ea28eb0eca3b1d4248d02ff65f79d4d40244f732df4"),
        ("main/libe/libevent/libevent-2.1-7t64_2.1.12-stable-9ubuntu2.2_arm64.deb", "fa673bc6df96f47db3aad32fa0158fccd0d15af9065fa4a4e11d90007abe4c9f"),
        ("universe/h/hiredis/libhiredis1.1.0_1.2.0-6ubuntu3_arm64.deb", "378a26e4425a5de22fac07d94c7f98525b0638f790b3ee13c04c2b4438bcf3e1"),
        ("universe/u/unbound/unbound_1.19.2-1ubuntu3.12_arm64.deb", "d9f8b09cec5dfe9b2bec1d894b5cd28d4add8e39d832ac209c097e0f19ce915a"),
    ],
}


class Failure(Exception):
    pass


def require(condition, code):
    if not condition:
        raise Failure(code)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def run(args, timeout=20, allowed=(0,), env=None):
    result = subprocess.run(args, capture_output=True, timeout=timeout, env=env or ENV)
    require(result.returncode in allowed, "E_COMMAND_" + Path(args[0]).name.upper().replace("-", "_"))
    require(len(result.stdout) <= 1048576, "E_OUTPUT_LIMIT")
    return result.stdout.decode().strip()


def safe_parent(path):
    for parent in reversed(path.parents):
        require(not parent.is_symlink(), "E_PATH_SYMLINK")
        if parent.exists():
            info = parent.stat()
            require(info.st_uid == 0 and not info.st_mode & 0o022, "E_PATH_OWNER")
    require(not path.is_symlink(), "E_PATH_SYMLINK")


def atomic(path, data, mode=0o600):
    safe_parent(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd, name = tempfile.mkstemp(prefix=".dns-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(name, mode)
        os.replace(name, path)
        fd = os.open(path.parent, os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def read_state():
    path = CONTROL / "state.json"
    return json.loads(path.read_bytes()) if path.exists() else None


def save(state):
    atomic(CONTROL / "state.json", canonical(state))


def snapshot(path):
    safe_parent(path)
    if not path.exists():
        return None
    info = path.stat()
    require(stat.S_ISREG(info.st_mode) and not info.st_mode & 0o022, "E_FILE_OWNER")
    data = path.read_bytes()
    require(len(data) < 1048576, "E_FILE_SIZE")
    checksum = digest(data)
    # The verified manual deployment retained its macOS archive uid/gid on
    # this one data file. Accept only those exact bytes and metadata, under
    # the same root-owned parent checks. Never relax ownership for backups,
    # service units or arbitrary configuration, and never chown a live file.
    legacy_archive = (path == LEGACY_CONFIG and info.st_uid == 501 and info.st_gid == 50
                      and stat.S_IMODE(info.st_mode) == 0o644 and checksum == LEGACY_CONFIG_SHA256)
    require(info.st_uid == 0 or legacy_archive, "E_FILE_OWNER")
    return {"data": base64.b64encode(data).decode(), "mode": stat.S_IMODE(info.st_mode), "hash": checksum}


def write_managed(state, path, data, mode=0o644):
    key = str(path)
    if key not in state["files"]:
        state["files"][key] = {"before": snapshot(path), "applied": digest(data)}
    else:
        state["files"][key]["applied"] = digest(data)
    save(state)  # Intent must survive termination between replace and return.
    atomic(path, data, mode)


def link_value(action, interface):
    output = run(["resolvectl", action, interface])
    require(": " in output or output.endswith(":"), "E_RESOLVED_FORMAT")
    return output.split(":", 1)[1].strip().split()


def external_ip():
    value = json.loads(run(["curl", "--noproxy", "*", "-4", "--fail", "--silent", "--show-error",
                           "--max-time", "20", "https://ipv4.ipleak.net/json/"], timeout=25)).get("ip", "")
    require(ipaddress.ip_address(value).version == 4, "E_EXIT_ADDRESS")
    return value


def check_dns_scopes(output, interface, link_dns, existing_resolver):
    """Only an already installed resolver may be repeated in the global scope.

    A fresh deployment changes link DNS only, so even identical *external*
    global DNS must still be rejected. Other links remain unsupported.
    """
    scopes = {}
    current = None
    for line in output.splitlines():
        if not line.strip():
            continue
        match = re.fullmatch(r"(Global|Link [0-9]+ \(([^)]+)\)):\s*(.*)", line.strip())
        if match:
            current = None if match[1] == "Global" else match[2]
            require(current not in scopes, "E_RESOLVED_FORMAT")
            scopes[current] = match[3].split()
        else:
            require(line[0].isspace() and current in scopes, "E_RESOLVED_FORMAT")
            scopes[current].extend(line.split())
    require(interface in scopes and scopes[interface] == link_dns, "E_CONFIGURATION_DRIFT")
    for scope, servers in scopes.items():
        if scope == interface or not servers:
            continue
        # Legacy adoption still validates the complete service/config/backup
        # before saving a record; a marker alone never authorizes mutation.
        same_installed_resolver = (existing_resolver and scope is None
                                   and link_dns == ["127.0.0.1"] and servers == ["127.0.0.1"])
        require(same_installed_resolver, "E_SPLIT_DNS_UNSUPPORTED")


def facts():
    release = dict(line.split("=", 1) for line in Path("/etc/os-release").read_text().splitlines() if "=" in line)
    require(release.get("ID", "").strip('"') == "ubuntu" and release.get("VERSION_ID", "").strip('"') == "24.04", "E_UBUNTU_2404")
    arch = run(["dpkg", "--print-architecture"])
    require(arch in PACKAGES, "E_ARCH_UNSUPPORTED")
    for name in ("curl", "dpkg-deb", "netplan", "networkctl", "resolvectl", "systemctl", "ip", "ldd"):
        require(shutil.which(name, path=ENV["PATH"]), "E_DEPENDENCY_" + name.upper().replace("-", "_"))
    run(["systemctl", "is-active", "systemd-resolved", "systemd-networkd"])
    require(Path("/etc/resolv.conf").is_symlink()
            and Path("/etc/resolv.conf").resolve() == Path("/run/systemd/resolve/stub-resolv.conf"), "E_RESOLVER_LAYOUT")
    routes = json.loads(run(["ip", "-j", "-4", "route", "show", "default"]))
    require(len(routes) == 1, "E_MULTIPLE_ROUTES")
    interface = routes[0]["dev"]
    require(re.fullmatch(r"[A-Za-z0-9_.-]{1,32}", interface), "E_INTERFACE")
    status = run(["networkctl", "status", "--no-pager", interface])
    match = re.search(r"Network File:\s+(/run/systemd/network/10-netplan-([A-Za-z0-9_.-]+)\.network)", status)
    require(match is not None, "E_NETPLAN_LAYOUT")
    network_file, netplan_id = match.groups()
    dns = link_value("dns", interface)
    domains = link_value("domain", interface)
    default_route = link_value("default-route", interface)
    state = read_state()
    managed = state and state["phase"] not in ("rolled_back", "failed")
    legacy = Path("/opt/ssh-egress-dns/config/unbound.conf").exists()
    require(dns and all(ipaddress.ip_address(x).version in (4, 6) for x in dns), "E_DNS_UPSTREAM")
    if not managed and not legacy:
        require(not domains, "E_SPLIT_DNS_UNSUPPORTED")
        require("127.0.0.1" not in dns, "E_EXISTING_RESOLVER")
    check_dns_scopes(run(["resolvectl", "dns"]), interface, dns, bool(managed or legacy))
    files = {str(p): snapshot(p) for p in sorted(Path("/etc/netplan").glob("*.yaml"))}
    files[network_file] = snapshot(Path(network_file))
    require(files[network_file] is not None, "E_NETPLAN_LAYOUT")
    return {"hostId": digest(Path("/etc/machine-id").read_bytes().strip()), "architecture": arch,
            "interface": interface, "netplanId": netplan_id, "networkFile": network_file,
            "dns": dns, "domains": domains, "defaultRoute": default_route,
            "sourceFiles": files, "legacy": legacy, "exit": external_ip()}


def inspect():
    current = facts()
    state = read_state()
    if state and state["phase"] in ("prepared", "awaiting_verification", "preparing"):
        raise Failure("E_PENDING_TRANSACTION")
    current["phase"] = state["phase"] if state else ("legacy" if current["legacy"] else "absent")
    if current["phase"] in ("absent", "rolled_back"):
        check_unused_unit()
        check_port_available()
    current["token"] = digest(canonical(current))
    return current


def check_unused_unit():
    require(not Path("/etc/systemd/system/" + UNIT).exists(), "E_INSTALLATION_CONFLICT")
    require(not run(["systemctl", "show", UNIT, "-p", "FragmentPath", "--value"], allowed=(0, 1)), "E_INSTALLATION_CONFLICT")
    require(not run(["systemctl", "show", UNIT, "-p", "DropInPaths", "--value"], allowed=(0, 1)), "E_INSTALLATION_CONFLICT")


def check_port_available():
    for family in (socket.SOCK_STREAM, socket.SOCK_DGRAM):
        with socket.socket(socket.AF_INET, family) as sock:
            try:
                sock.bind(("127.0.0.1", 53))
            except OSError:
                raise Failure("E_PORT_OCCUPIED")


def arm(state):
    # Persistent calendar timer plus boot reconciliation protect against client death and server reboot.
    helper = CONTROL / "helper.py"
    atomic(helper, Path(__file__).read_bytes(), 0o700)
    deadline = datetime.datetime.fromtimestamp(state["deadline"], datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    atomic(Path("/etc/systemd/system/" + TIMER + ".service"),
           f"[Unit]\nDescription=Restore uncommitted SSH DNS\n[Service]\nType=oneshot\nExecStart=/usr/bin/python3 -I {helper} --expire\n".encode(), 0o644)
    atomic(Path("/etc/systemd/system/" + TIMER + ".timer"),
           f"[Unit]\nDescription=SSH DNS transaction deadline\n[Timer]\nOnCalendar={deadline}\nPersistent=true\nAccuracySec=1s\n[Install]\nWantedBy=timers.target\n".encode(), 0o644)
    atomic(Path("/etc/systemd/system/" + RECOVERY),
           f"[Unit]\nDescription=Recover uncommitted SSH DNS\nAfter=network-online.target systemd-resolved.service\nWants=network-online.target\n[Service]\nType=oneshot\nExecStart=/usr/bin/python3 -I {helper} --recover\n[Install]\nWantedBy=multi-user.target\n".encode(), 0o644)
    run(["systemctl", "daemon-reload"])
    run(["systemctl", "enable", RECOVERY])
    run(["systemctl", "enable", "--now", TIMER + ".timer"])
    run(["systemctl", "is-active", TIMER + ".timer"])


def disarm():
    run(["systemctl", "disable", "--now", TIMER + ".timer"], allowed=(0, 1, 5))
    run(["systemctl", "disable", RECOVERY], allowed=(0, 1, 5))


def build_config(base, alibaba):
    text = f'''server:
    interface: 127.0.0.1
    port: 53
    access-control: 127.0.0.0/8 allow
    access-control: 0.0.0.0/0 refuse
    do-ip4: yes
    do-ip6: no
    username: ""
    chroot: ""
    directory: "/var/lib/v2rayn-ssh-dns"
    pidfile: ""
    use-syslog: no
    log-queries: no
    hide-identity: yes
    hide-version: yes
    edns-buffer-size: 1232
    prefetch: yes
    qname-minimisation: yes
    module-config: "validator iterator"
    auto-trust-anchor-file: "/var/lib/v2rayn-ssh-dns/root.key"
    root-hints: "{base}/root/usr/share/dns/root.hints"
'''
    if alibaba:
        for domain in ("cloud.aliyuncs.com.", "aegis.aliyun.com."):
            text += f'\nforward-zone:\n    name: "{domain}"\n    forward-addr: 100.100.2.136\n    forward-addr: 100.100.2.138\n    forward-first: no\n'
    return text.encode()


def install_resolver(state):
    check_unused_unit()
    check_port_available()
    require(not ROOT.exists(), "E_INSTALLATION_CONFLICT")
    ROOT.mkdir(parents=True, mode=0o755)
    ROOT.chmod(0o755)  # Bootstrap intentionally has umask 077; DynamicUser needs traversal.
    (ROOT / "root").mkdir(mode=0o755)
    packages = ROOT / "packages"
    packages.mkdir(mode=0o700)
    arch = state["original"]["architecture"]
    base = "https://archive.ubuntu.com/ubuntu/pool/" if arch == "amd64" else "https://ports.ubuntu.com/ubuntu-ports/pool/"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    for relative, checksum in PACKAGES[arch]:
        with opener.open(base + relative, timeout=35) as response:
            data = response.read(32 * 1024 * 1024 + 1)
        require(len(data) <= 32 * 1024 * 1024 and digest(data) == checksum, "E_PACKAGE_CHECKSUM")
        package = packages / Path(relative).name
        atomic(package, data)
        run(["dpkg-deb", "--extract", str(package), str(ROOT / "root")])
    triple = "x86_64-linux-gnu" if arch == "amd64" else "aarch64-linux-gnu"
    env = dict(ENV, LD_LIBRARY_PATH=str(ROOT / "root/usr/lib" / triple))
    binary = str(ROOT / "root/usr/sbin/unbound")
    require("not found" not in run(["ldd", binary], env=env), "E_RUNTIME_DEPENDENCY")
    config = build_config(ROOT, set(state["original"]["dns"]) == {"100.100.2.136", "100.100.2.138"})
    write_managed(state, ROOT / "unbound.conf", config)
    unit = f'''[Unit]
Description=Private recursive DNS for SSH
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
DynamicUser=yes
StateDirectory=v2rayn-ssh-dns
StateDirectoryMode=0750
Environment=LD_LIBRARY_PATH={env["LD_LIBRARY_PATH"]}
ExecStartPre=/bin/sh -c 'test -s /var/lib/v2rayn-ssh-dns/root.key || /usr/bin/install -m 0644 {ROOT}/root/usr/share/dns/root.key /var/lib/v2rayn-ssh-dns/root.key'
ExecStartPre={ROOT}/root/usr/sbin/unbound-checkconf {ROOT}/unbound.conf
ExecStart={binary} -d -c {ROOT}/unbound.conf
Restart=on-failure
RestartSec=2
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
MemoryMax=256M
TimeoutStopSec=10
[Install]
WantedBy=multi-user.target
'''
    check_unused_unit()  # Downloads must not widen a race into overwriting another service.
    write_managed(state, Path("/etc/systemd/system/" + UNIT), unit.encode())
    run(["systemctl", "daemon-reload"])
    state["resolverStartIntent"] = True
    save(state)  # Persist ownership before a start that may succeed without an acknowledgement.
    run(["systemctl", "start", UNIT])
    deadline = time.monotonic() + 15
    while True:
        run(["systemctl", "is-active", UNIT])
        try:
            with socket.create_connection(("127.0.0.1", 53), timeout=0.5):
                break
        except OSError:
            require(time.monotonic() < deadline, "E_DNS_START_TIMEOUT")
            time.sleep(0.1)
    # Test both transports before touching the system resolver.
    dns_query("example.com", tcp=False)
    dns_query("example.com", tcp=True)


def prepare(request):
    current = inspect()
    require(current["token"] == request.get("planToken"), "E_PLAN_CHANGED")
    require(current["exit"] == request.get("expectedExit"), "E_EXIT_MISMATCH")
    require(current["phase"] in ("absent", "rolled_back"), "E_ALREADY_INSTALLED")
    previous = read_state()
    if previous:
        require(previous["phase"] == "rolled_back", "E_PENDING_TRANSACTION")
        atomic(CONTROL / "history" / (previous["id"] + ".json"), canonical(previous))
        # Retain old software/backup; retries use a new installation directory only after a verified rollback.
        if ROOT.exists():
            require(previous.get("ownedRoot") is True, "E_INSTALLATION_CONFLICT")
            os.rename(ROOT, CONTROL / ("retained-" + previous["id"]))
    require(re.fullmatch(r"[a-f0-9]{32}", request.get("transactionId", "")), "E_TRANSACTION_ID")
    state = {"id": request["transactionId"], "phase": "preparing", "original": current,
             "deadline": int(time.time()) + 600, "files": {}, "service": UNIT, "ownedRoot": True}
    save(state)
    try:
        arm(state)
        install_resolver(state)
        state["phase"] = "prepared"
        save(state)
    except Exception:
        rollback(state)
        raise
    return state


def check_drift(state):
    if state.get("legacyConfig"):
        config = snapshot(LEGACY_CONFIG)
        require(config and config["hash"] == state["legacyConfig"], "E_CONFIGURATION_DRIFT")
    if state.get("legacyUnit"):
        unit = snapshot(Path("/etc/systemd/system/ssh-egress-dns.service"))
        require(unit and unit["hash"] == state["legacyUnit"], "E_CONFIGURATION_DRIFT")
    for name, entry in state["files"].items():
        now = snapshot(Path(name))
        before = entry["before"]
        allowed = [entry["applied"], before["hash"] if before else None]
        require((now["hash"] if now else None) in allowed, "E_CONFIGURATION_DRIFT")
    for name, before in state["original"]["sourceFiles"].items():
        # Generated Netplan output is verified separately against an isolated generation.
        if name.startswith("/etc/netplan/"):
            now = snapshot(Path(name))
            require(now == before, "E_CONFIGURATION_DRIFT")
    if state.get("resolverStartIntent") or state.get("legacyConfig"):
        require(not run(["systemctl", "show", state["service"], "-p", "DropInPaths", "--value"], allowed=(0, 1)), "E_CONFIGURATION_DRIFT")


def activate(state):
    require(state["phase"] == "prepared", "E_TRANSACTION_STATE")
    check_drift(state)
    original = state["original"]
    require(link_value("dns", original["interface"]) == original["dns"]
            and link_value("domain", original["interface"]) == original["domains"], "E_CONFIGURATION_DRIFT")
    identifier = original["netplanId"]
    overlay = f"""# Managed by v2rayN SSH DNS
network:
  version: 2
  ethernets:
    {identifier}:
      dhcp4-overrides:
        use-dns: false
      dhcp6-overrides:
        use-dns: false
      nameservers:
        addresses: [127.0.0.1]
        search: ['~.']
"""
    path = Path("/etc/netplan/90-v2rayn-ssh-dns.yaml")
    dropin = Path("/etc/systemd/network") / (Path(original["networkFile"]).name + ".d/90-v2rayn-ssh-dns.conf")
    require(not path.exists() and not dropin.exists(), "E_INSTALLATION_CONFLICT")
    # Generate only inside a private root; never run netplan apply/generate on the host.
    with tempfile.TemporaryDirectory(prefix="netplan-", dir=CONTROL) as temporary:
        staging = Path(temporary)
        shutil.copytree("/etc/netplan", staging / "etc/netplan")
        atomic(staging / "etc/netplan" / path.name, overlay.encode())
        run(["netplan", "generate", "--root-dir", temporary])
        generated = staging / original["networkFile"].lstrip("/")
        require(generated.exists(), "E_NETPLAN_LAYOUT")
        original_text = Path(original["networkFile"]).read_text()
        # Generator output may differ only in DNS-related lines/sections.
        require(network_without_dns(original_text) == network_without_dns(generated.read_text()), "E_NETWORK_CHANGE")
        state["generated"] = base64.b64encode(generated.read_bytes()).decode()
        save(state)
    write_managed(state, path, overlay.encode(), 0o600)
    # networkd reads configuration as systemd-network, not root. A private 0700
    # directory silently hides the override on DHCP reconfiguration and reboot.
    safe_parent(dropin)
    if not dropin.parent.exists():
        dropin.parent.mkdir(mode=0o755)
        dropin.parent.chmod(0o755)  # Bootstrap uses umask 077.
    require(dropin.parent.stat().st_mode & 0o005 == 0o005, "E_NETWORK_CONFIG_PERMISSIONS")
    write_managed(state, dropin, b"# Managed by v2rayN SSH DNS\n[Network]\nDNS=\nDNS=127.0.0.1\nDomains=\nDomains=~.\n[DHCPv4]\nUseDNS=no\n[DHCPv6]\nUseDNS=no\n")
    # daemon-reload invokes generators, so verify their output before changing runtime DNS.
    run(["systemctl", "enable", UNIT])
    require(network_without_dns(Path(original["networkFile"]).read_text())
            == network_without_dns(base64.b64decode(original["sourceFiles"][original["networkFile"]]["data"]).decode()), "E_NETWORK_CHANGE")
    state["phase"] = "awaiting_verification"
    save(state)
    run(["resolvectl", "dns", original["interface"], "127.0.0.1"])
    run(["resolvectl", "domain", original["interface"], "~."])
    run(["resolvectl", "flush-caches"])
    run(["getent", "ahostsv4", "example.com"])
    return state


def network_without_dns(text):
    ignored = ("DNS=", "Domains=", "UseDNS=", "UseDomains=")
    return [line.strip() for line in text.splitlines() if line.strip() and not line.lstrip().startswith(("#", ";", *ignored))]


def rollback(state):
    require(state is not None, "E_NOT_INSTALLED")
    if state["phase"] == "rolled_back":
        return state
    check_drift(state)  # Validate the whole set before changing any file.
    original = state["original"]
    now = link_value("dns", original["interface"])
    require(now == original["dns"] or now == ["127.0.0.1"], "E_CONFIGURATION_DRIFT")
    run(["resolvectl", "dns", original["interface"], *original["dns"]])
    run(["resolvectl", "domain", original["interface"], *(original["domains"] or [""])])
    if original["defaultRoute"]:
        run(["resolvectl", "default-route", original["interface"], *original["defaultRoute"]])
    run(["resolvectl", "flush-caches"])
    # Verify the restored resolver before stopping the replacement.
    run(["getent", "ahostsv4", "example.com"])
    if state.get("resolverStartIntent") or state.get("legacyConfig"):
        run(["systemctl", "stop", state["service"]], allowed=(0, 5))
        run(["systemctl", "disable", state["service"]], allowed=(0, 1, 5))
    for name, entry in reversed(list(state["files"].items())):
        path = Path(name)
        if entry["before"] is None:
            path.unlink(missing_ok=True)
        else:
            atomic(path, base64.b64decode(entry["before"]["data"]), entry["before"]["mode"])
    run(["systemctl", "daemon-reload"])
    run(["resolvectl", "flush-caches"])
    state["phase"] = "rolled_back"
    save(state)
    disarm()
    return state


def commit(state, request):
    require(state["phase"] == "awaiting_verification" and time.time() < state["deadline"], "E_TRANSACTION_EXPIRED")
    require(request.get("verifiedExit") == state["original"]["exit"]
            and request.get("verifiedDns") == [state["original"]["exit"]], "E_VERIFICATION_REQUIRED")
    check_drift(state)
    require(link_value("dns", state["original"]["interface"]) == ["127.0.0.1"], "E_CONFIGURATION_DRIFT")
    state["phase"] = "committed"
    save(state)
    disarm()
    return state


def adopt(request):
    require(re.fullmatch(r"[a-f0-9]{32}", request.get("transactionId", "")), "E_TRANSACTION_ID")
    current = inspect()
    require(current["token"] == request.get("planToken") and current["phase"] == "legacy", "E_PLAN_CHANGED")
    require(current["exit"] == request.get("expectedExit"), "E_EXIT_MISMATCH")
    backups = list(Path("/root").glob("ssh-dns-backup-*"))
    require(len(backups) == 1, "E_LEGACY_BACKUP")
    backup = backups[0]
    safe_parent(backup / "dns.txt")
    require(backup.stat().st_uid == 0 and not backup.stat().st_mode & 0o077, "E_LEGACY_BACKUP")
    dns = (backup / "dns.txt").read_text().split(":", 1)[1].strip().split()
    require(set(dns) == {"100.100.2.136", "100.100.2.138"}, "E_LEGACY_BACKUP")
    domains = (backup / "domains.txt").read_text().split(":", 1)[1].strip().split()
    require(not domains, "E_LEGACY_BACKUP")
    config = snapshot(LEGACY_CONFIG)
    require(config is not None and config["hash"] == LEGACY_CONFIG_SHA256, "E_LEGACY_CONFIG")
    unit = snapshot(Path("/etc/systemd/system/ssh-egress-dns.service"))
    require(unit is not None and unit["hash"] == "1837df5e878f5c6f9ecbc9d65e660b2ef2b6ab42f51c5ed563a1a70b829f1520", "E_LEGACY_CONFIG")
    require(not run(["systemctl", "show", "ssh-egress-dns.service", "-p", "DropInPaths", "--value"]), "E_LEGACY_CONFIG")
    run(["systemctl", "is-active", "ssh-egress-dns.service"])
    paths = [Path("/etc/netplan/90-ssh-egress-dns.yaml"),
             Path("/etc/systemd/network") / (Path(current["networkFile"]).name + ".d/90-ssh-egress-dns.conf")]
    files = {}
    expected = ["8efc34644c11274eaa3f75a8a58b959b3a608e50bd457268c6d7a44b3c0f4658",
                "34bb81a9629edcc09f260146f5757f9e003c94ee33089d161c442a0749b4c39e"]
    for path, checksum in zip(paths, expected):
        original = snapshot(path)
        require(original and original["hash"] == checksum, "E_LEGACY_CONFIG")
        files[str(path)] = {"before": None, "applied": original["hash"]}
    require(link_value("dns", current["interface"]) == ["127.0.0.1"], "E_LEGACY_CONFIG")
    # Validate and retain the original Netplan baseline, not the already-managed live view.
    source = {str(Path("/etc/netplan") / p.name): snapshot(p) for p in (backup / "netplan").glob("*.yaml")}
    require(source and all(snapshot(Path(k)) == v for k, v in source.items()), "E_LEGACY_BACKUP")
    old_network = snapshot(backup / "generated-network" / Path(current["networkFile"]).name)
    require(old_network is not None, "E_LEGACY_BACKUP")
    source[current["networkFile"]] = old_network
    current.update(dns=dns, domains=[], defaultRoute=["yes"], sourceFiles=source)
    state = {"id": request["transactionId"], "phase": "committed", "original": current,
             "files": files, "service": "ssh-egress-dns.service", "ownedRoot": False,
             "legacyBackup": str(backup), "legacyConfig": config["hash"], "legacyUnit": unit["hash"]}
    save(state)
    return state


def dns_query(name, tcp=False):
    require(re.fullmatch(r"[a-zA-Z0-9.-]{1,253}", name), "E_QUERY_NAME")
    query_id = os.urandom(2)
    packet = query_id + b"\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00"
    packet += b"".join(bytes([len(label)]) + label.encode() for label in name.split(".")) + b"\x00\x00\x01\x00\x01"
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM if tcp else socket.SOCK_DGRAM) as sock:
        sock.settimeout(25)
        sock.connect(("127.0.0.1", 53))
        if tcp:
            sock.sendall(struct.pack("!H", len(packet)) + packet)
            def exact(size):
                value = b""
                while len(value) < size:
                    chunk = sock.recv(size - len(value))
                    require(chunk, "E_DNS_RESPONSE")
                    value += chunk
                return value
            data = exact(struct.unpack("!H", exact(2))[0])
        else:
            sock.send(packet)
            data = sock.recv(65535)
    require(len(data) >= 12 and data[:2] == query_id and data[3] & 15 == 0, "E_DNS_RESPONSE")
    def skip(offset):
        while data[offset] != 0:
            if data[offset] & 192 == 192:
                return offset + 2
            offset += data[offset] + 1
        return offset + 1
    offset = 12
    for _ in range(struct.unpack("!H", data[4:6])[0]):
        offset = skip(offset) + 4
    addresses = []
    for _ in range(struct.unpack("!H", data[6:8])[0]):
        offset = skip(offset)
        kind, _, _, length = struct.unpack("!HHIH", data[offset:offset + 10])
        offset += 10
        if kind == 1 and length == 4:
            addresses.append(socket.inet_ntoa(data[offset:offset + length]))
        offset += length
    require(addresses, "E_DNS_EMPTY")
    return addresses


def candidate(request):
    name = request.get("name", "")
    require(re.fullmatch(r"[a-f0-9]{40}-[a-f0-9]{10}\.ipleak\.net", name), "E_QUERY_NAME")
    address = dns_query(name)[0]
    return json.loads(run(["curl", "--noproxy", "*", "-4", "--fail", "--silent", "--show-error", "--max-time", "25",
                           "--resolve", f"{name}:443:{address}", f"https://{name}/dnsdetection/"], timeout=30))


def public(state):
    if not state:
        return {"phase": "absent"}
    return {key: state[key] for key in ("id", "phase", "deadline", "service") if key in state} | {
        "hostId": state["original"]["hostId"], "exit": state["original"]["exit"]}


def dispatch(request):
    require(request.get("version") == 1, "E_PROTOCOL")
    action = request.get("action")
    if action == "inspect":
        value = inspect()
        # File backups are kept exclusively on the server.
        return {k: v for k, v in value.items() if k != "sourceFiles"}
    state = read_state()
    if action == "status":
        return public(state)
    if action == "prepare":
        return public(prepare(request))
    if action == "adopt":
        return public(adopt(request))
    require(state is not None, "E_NOT_INSTALLED")
    require(request.get("transactionId") == state["id"], "E_TRANSACTION_ID")
    if action == "candidate":
        return candidate(request)
    if action == "activate":
        try:
            return public(activate(state))
        except Exception:
            rollback(state)
            raise
    if action == "commit":
        return public(commit(state, request))
    if action == "rollback":
        return public(rollback(state))
    raise Failure("E_ACTION")


def main():
    require(os.geteuid() == 0, "E_ROOT_REQUIRED")
    safe_parent(CONTROL / "lock")
    CONTROL.mkdir(parents=True, mode=0o700, exist_ok=True)
    with (CONTROL / "lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if len(sys.argv) == 2 and sys.argv[1] in ("--expire", "--recover"):
            state = read_state()
            if state and state["phase"] in ("preparing", "prepared", "awaiting_verification"):
                if sys.argv[1] == "--recover" or time.time() >= state["deadline"]:
                    rollback(state)
            return
        print("V2RAYN_DNS_READY", flush=True)
        data = sys.stdin.buffer.readline(65537)
        require(len(data) <= 65536, "E_REQUEST_LIMIT")
        try:
            result = dispatch(json.loads(data))
            print(json.dumps({"version": 1, "state": "ok", "result": result}), flush=True)
        except Exception as error:
            code = str(error) if isinstance(error, Failure) else (
                "E_DNS_QUERY_TIMEOUT" if isinstance(error, TimeoutError) else
                "E_PROTOCOL" if isinstance(error, json.JSONDecodeError) else "E_REMOTE_OPERATION")
            print(json.dumps({"version": 1, "state": "error", "error": code}), flush=True)


if __name__ == "__main__":
    main()

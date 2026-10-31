#!/usr/bin/env python3
"""Read-only macOS check of the running v2rayN core and its SSH UDP path.

Uses Python's standard library, macOS ps/lsof, and the running App's proxy-check.
Prints a JSON report to stdout. Does not restart the App or change its settings.
"""

import argparse
import concurrent.futures
import hashlib
import ipaddress
import json
from pathlib import Path
import shlex
import subprocess
import sys
import time
from datetime import datetime
import urllib.parse
import urllib.request


UDP_TARGETS = (
    ("stun", "stun.cloudflare.com:3478"),
    ("stun", "stun.l.google.com:19302"),
    ("h3", "https://www.cloudflare.com/cdn-cgi/trace"),
)
OBSERVED_TARGETS = {
    ("stun.cloudflare.com", "3478"),
    ("stun.l.google.com", "19302"),
    ("www.cloudflare.com", "443"),
}


def command(args):
    return subprocess.run(
        args, capture_output=True, text=True, check=True, timeout=10
    ).stdout.strip()


def listener_pid(port):
    pids = set(command([
        "lsof", "-nP", f"-iTCP:{port}", "-sTCP:LISTEN", "-t"
    ]).splitlines())
    if len(pids) != 1:
        raise RuntimeError(f"Expected one core listening on TCP {port}; found {len(pids)}")
    return int(pids.pop())


def lsof_names(pid, descriptor):
    return [
        Path(line[1:])
        for line in command([
            "lsof", "-a", "-p", str(pid), "-d", descriptor, "-Fn"
        ]).splitlines()
        if line.startswith("n")
    ]


def inspect_running_core(port):
    pid = listener_pid(port)
    binaries = [p for p in lsof_names(pid, "txt") if p.name == "sing-box"]
    if len(binaries) != 1:
        raise RuntimeError("The listening process does not identify one sing-box executable")
    core = binaries[0]
    cwd = lsof_names(pid, "cwd")[0]
    parent_pid = int(command(["ps", "-p", str(pid), "-o", "ppid="]))
    parent = Path(command(["ps", "-p", str(parent_pid), "-o", "comm="]))
    if "v2rayn" not in parent.name.lower():
        raise RuntimeError("The core's parent process is not v2rayN")
    worker = parent.parent / "proxy-check"
    if not worker.is_file():
        raise RuntimeError(f"Missing App probe worker: {worker}")

    launch = command(["ps", "-p", str(pid), "-o", "args="])
    prefix = str(core) + " "
    if not launch.startswith(prefix):
        raise RuntimeError("Cannot identify the running core's command line")
    args = shlex.split(launch[len(prefix):])
    configs = []
    for index, arg in enumerate(args):
        if arg in ("-C", "--config-directory") or arg.startswith("--config-directory="):
            raise RuntimeError("Merged configuration directories are not supported by this checker")
        if arg in ("-c", "--config"):
            configs.append(args[index + 1])
        elif arg.startswith("--config="):
            configs.append(arg.split("=", 1)[1])
    if len(configs) != 1:
        raise RuntimeError("Expected one explicit running configuration file")
    config_path = (cwd / configs[0]).resolve()
    return {
        "pid": pid,
        "parent_pid": parent_pid,
        "executable": str(core),
        "parent_executable": str(parent),
        "config_path": str(config_path),
        "worker": str(worker),
    }


def api_client(config):
    api = config.get("experimental", {}).get("clash_api", {})
    controller = api.get("external_controller", "")
    parsed = urllib.parse.urlsplit("http://" + controller)
    host = parsed.hostname
    if host in ("0.0.0.0", "::"):
        host = "127.0.0.1" if host == "0.0.0.0" else "::1"
    if not host or not ipaddress.ip_address(host).is_loopback or not parsed.port:
        raise RuntimeError("A loopback Clash API controller is required for live route verification")
    address = f"[{host}]" if ":" in host else host
    base = f"http://{address}:{parsed.port}"
    headers = {"Authorization": "Bearer " + api["secret"]} if api.get("secret") else {}
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def get(path):
        with opener.open(urllib.request.Request(base + path, headers=headers), timeout=2) as response:
            return json.load(response)

    return get


def probe(worker, proxy, kind, target, round_number, timeout):
    request = {
        "version": 1, "kind": kind, "target": target,
        "proxy": proxy, "timeout": timeout,
    }
    try:
        completed = subprocess.run(
            [worker], input=json.dumps(request), capture_output=True,
            text=True, check=True, timeout=timeout + 3,
        )
        # Credentials are supplied only on stdin and never included in the report.
        result = json.loads(completed.stdout.replace(proxy, "socks5://[local-proxy]"))
    except (subprocess.SubprocessError, json.JSONDecodeError) as error:
        result = {"state": "failed", "error": type(error).__name__}
    return {"round": round_number, "kind": kind, "target": target, "result": result}


def check(options):
    running = inspect_running_core(options.port)
    config_path = Path(running["config_path"])
    config_bytes = config_path.read_bytes()
    config = json.loads(config_bytes)
    inbound = next((
        item for item in config.get("inbounds", [])
        if item.get("type") in ("mixed", "socks") and item.get("listen_port") == options.port
    ), None)
    if inbound is None:
        raise RuntimeError("The running configuration does not declare the selected SOCKS port")
    proxy = f"socks5://127.0.0.1:{options.port}"
    if inbound.get("users"):
        user = inbound["users"][0]
        username = urllib.parse.quote(user["username"], safe="")
        password = urllib.parse.quote(user["password"], safe="")
        proxy = f"socks5://{username}:{password}@127.0.0.1:{options.port}"

    core = Path(running["executable"])
    version_output = command([str(core), "version"])
    version = next(line.split()[-1] for line in version_output.splitlines() if line.startswith("sing-box version "))
    manifest_path = core.parent / "ssh-udp-core.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.is_file() else {}
    digest = hashlib.sha256(core.read_bytes()).hexdigest()
    manifest_matches = (
        manifest.get("SchemaVersion") == 1
        and manifest.get("Version") == version
        and manifest.get("Sha256", "").lower() == digest
    )
    ssh_outbounds = [
        {key: item[key] for key in ("tag", "server", "server_port", "udp_forwarding") if key in item}
        for item in config.get("outbounds", []) if item.get("type") == "ssh"
    ]
    ssh_tags = {item.get("tag") for item in ssh_outbounds if item.get("udp_forwarding") is True}
    get_api = api_client(config)
    live_version = get_api("/version").get("version")
    mode = get_api("/configs").get("mode")

    observed = {}
    results = []
    api_errors = set()
    for round_number in range(1, options.rounds + 1):
        targets = list(UDP_TARGETS)
        if round_number == 1:
            targets.append(("https", "https://www.cloudflare.com/cdn-cgi/trace"))
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
            pending = [
                pool.submit(probe, running["worker"], proxy, kind, target, round_number, options.timeout)
                for kind, target in targets
            ]
            while not all(future.done() for future in pending):
                try:
                    for connection in get_api("/connections").get("connections", []):
                        metadata = connection.get("metadata", {})
                        host = metadata.get("host", "")
                        port = str(metadata.get("destinationPort", ""))
                        if metadata.get("network") != "udp" or (host, port) not in OBSERVED_TARGETS:
                            continue
                        entry = {
                            "host": host, "port": port, "network": "udp",
                            "chains": connection.get("chains", []),
                            "rule": connection.get("rule"),
                        }
                        observed[json.dumps(entry, sort_keys=True)] = entry
                except Exception as error:
                    api_errors.add(type(error).__name__)
                time.sleep(0.05)
            results.extend(future.result() for future in pending)

    seen_targets = {(item["host"], item["port"]) for item in observed.values()}
    passed = sum(item["result"].get("state") == "passed" for item in results)
    egresses = sorted({item["result"]["egress"] for item in results if item["result"].get("egress")})
    checks = {
        "custom_core_version": "-ssh-udp" in version,
        "manifest_matches_binary": manifest_matches,
        "live_version_matches_binary": live_version == "sing-box " + version,
        "ssh_udp_enabled": bool(ssh_tags),
        "all_probes_passed": passed == len(results),
        "all_probes_reported_egress": all(item["result"].get("egress") for item in results),
        "all_udp_targets_observed": seen_targets == OBSERVED_TARGETS,
        "observed_udp_routes_use_ssh": bool(observed) and all(
            ssh_tags.intersection(item["chains"]) for item in observed.values()
        ),
        "core_pid_unchanged": listener_pid(options.port) == running["pid"],
        "config_file_unchanged": config_path.read_bytes() == config_bytes,
        "mode_unchanged": get_api("/configs").get("mode") == mode,
    }
    if options.expected_egress:
        checks["expected_egress_matches"] = egresses == [str(options.expected_egress)]
    return {
        "ok": all(checks.values()),
        "checked_at": datetime.now().astimezone().isoformat(),
        "running_core": running,
        "core_version": version,
        "live_core_version": live_version,
        "core_sha256": digest,
        "mode": mode,
        "ssh_outbounds": ssh_outbounds,
        "checks": checks,
        "passed": passed,
        "total": len(results),
        "egresses": egresses,
        "probes": results,
        "observed_live_udp_connections": list(observed.values()),
        "connection_snapshot_errors": sorted(api_errors),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=10808, help="Current local SOCKS port (default: 10808)")
    parser.add_argument("--rounds", type=int, default=3, help="UDP probe rounds (default: 3)")
    parser.add_argument("--timeout", type=int, default=12, help="Seconds per probe (default: 12)")
    parser.add_argument("--expected-egress", type=ipaddress.ip_address, help="Optional expected exit IP")
    options = parser.parse_args()
    if not (1 <= options.port <= 65535 and 1 <= options.rounds <= 10 and 1 <= options.timeout <= 120):
        parser.error("port must be 1..65535, rounds 1..10, and timeout 1..120")
    try:
        report = check(options)
    except Exception as error:
        report = {"ok": False, "error": str(error)}
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())

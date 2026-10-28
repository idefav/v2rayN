#!/usr/bin/env python3
"""Isolated macOS TUN smoke test. No real nodes, credentials or default-route takeover.

Uses only 198.18.77.0/30 for capture and local TCP/UDP/DNS fixtures.
--check-only validates all six configs without creating a TUN device.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import socket
import socketserver
import struct
import subprocess
import sys
import threading
import time

DEST = "198.18.77.2"
PREFIX = "198.18.77.0/30"
TUN = "172.31.252.1/30"
DNS = "172.31.252.2"


class TcpEcho(socketserver.BaseRequestHandler):
    def handle(self):
        while True:
            data = self.request.recv(65536)
            if not data:
                return
            self.request.sendall(data)


class UdpEcho(socketserver.BaseRequestHandler):
    def handle(self):
        data, reply = self.request
        reply.sendto(data, self.client_address)


class TcpServer(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = True


def route(address=DEST):
    result = subprocess.run(["/sbin/route", "-n", "get", address], capture_output=True, text=True, check=True)
    return {k.strip(): v.strip() for line in result.stdout.splitlines() if ":" in line
            for k, v in [line.split(":", 1)] if k.strip() in ("destination", "mask", "gateway", "interface")}


def wait_port(port, process):
    for _ in range(100):
        if process.poll() is not None:
            raise RuntimeError("fixture core exited")
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                return
        except OSError:
            time.sleep(0.05)
    raise RuntimeError("fixture listener did not start")


def stop(process):
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def config(stack, topology, tcp_port, udp_port, socks_port):
    overrides = [
        {"ip_cidr": [DEST + "/32"], "network": "tcp", "action": "route", "outbound": "direct",
         "override_address": "127.0.0.1", "override_port": tcp_port},
        {"ip_cidr": [DEST + "/32"], "network": "udp", "action": "route", "outbound": "direct",
         "override_address": "127.0.0.1", "override_port": udp_port},
    ]
    common = {
        "log": {"level": "info"},
        "dns": {"servers": [{"type": "hosts", "tag": "fixture", "predefined": {"tun-smoke.invalid": [DEST]}}],
                "final": "fixture"},
        "route": {"auto_detect_interface": True, "rules": [], "final": "direct"},
        "outbounds": [{"type": "direct", "tag": "direct"}],
    }
    if topology == "fixture":
        common["inbounds"] = [{"type": "mixed", "tag": "fixture", "listen": "127.0.0.1", "listen_port": socks_port}]
        common["route"]["rules"] = overrides
        return common
    # Native DNS configuration is disabled deliberately: no system resolver is changed.
    # A controlled query to the virtual DNS peer is handled by the explicit route rule.
    common["inbounds"] = [{"type": "tun", "tag": "tun", "address": [TUN], "mtu": 1500,
                           "auto_route": True, "strict_route": True, "dns_mode": "disabled",
                           "route_address": [PREFIX], "stack": stack}]
    common["route"]["rules"] = [
        {"ip_cidr": [TUN.split("/")[0] + "/32"], "action": "reject", "method": "drop"},
        {"port": [53], "action": "hijack-dns"},
    ]
    if topology == "native":
        common["route"]["rules"] += overrides
    else:
        common["outbounds"].insert(0, {"type": "socks", "tag": "proxy", "server": "127.0.0.1",
                                      "server_port": socks_port, "version": "5"})
        common["route"]["final"] = "proxy"
    return common


def probe(tcp_port, udp_port):
    data = os.urandom(32768)
    with socket.create_connection((DEST, tcp_port), timeout=4) as client:
        client.settimeout(4)
        client.sendall(data)
        received = bytearray()
        while len(received) < len(data):
            part = client.recv(len(data) - len(received))
            if not part:
                raise RuntimeError("TCP fixture closed early")
            received += part
        if received != data:
            raise RuntimeError("TCP integrity mismatch")
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
        client.settimeout(4)
        message = os.urandom(1000)
        client.sendto(message, (DEST, udp_port))
        if client.recv(65535) != message:
            raise RuntimeError("UDP integrity mismatch")
        query = b"".join(bytes([len(label)]) + label.encode() for label in "tun-smoke.invalid".split(".")) + b"\0"
        request = struct.pack("!HHHHHH", 0x7455, 0x0100, 1, 0, 0, 0) + query + struct.pack("!HH", 1, 1)
        client.sendto(request, (DNS, 53))
        response = client.recv(4096)
        if response[:2] != b"tU" or response[3] & 15 or socket.inet_aton(DEST) not in response:
            raise RuntimeError("DNS fixture answer mismatch")
    return {"tcp_sha256": hashlib.sha256(data).hexdigest(), "tcp_bytes": len(data),
            "udp_bytes": len(message), "dns": DEST}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core", default="/Applications/v2rayN.app/Contents/MacOS/bin/sing_box/sing-box")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--check-only", action="store_true")
    args = parser.parse_args()
    if sys.platform != "darwin":
        parser.error("this smoke test is for macOS")
    if not args.check_only and os.geteuid() != 0:
        parser.error("TUN devices require local administrator authorization; use sudo or --check-only")
    args.output = args.output.resolve()
    new_output = not args.output.exists()
    args.output.mkdir(parents=True, exist_ok=True)
    before = route() if not args.check_only else None
    default_before = route("1.1.1.1") if before else None
    dns_before = subprocess.check_output(["/usr/sbin/scutil", "--dns"]) if before else None
    if before:
        for address in (DEST, DNS, TUN.split("/")[0]):
            existing = route(address)
            if existing.get("interface", "").startswith("utun") or existing.get("destination") not in ("default", "0.0.0.0"):
                raise RuntimeError("a test address already has a specific route/VPN; refusing to overlay " + address)
    processes = []
    files = []
    generated = []
    report = {"check_only": args.check_only, "capture_prefix": PREFIX, "system_dns_changed": False,
              "default_route_changed": False, "samples": [], "before": before}
    tcp = TcpServer(("127.0.0.1", 0), TcpEcho)
    udp = socketserver.ThreadingUDPServer(("127.0.0.1", 0), UdpEcho)
    udp.daemon_threads = True
    for server in (tcp, udp):
        threading.Thread(target=server.serve_forever, daemon=True).start()
    tcp_port, udp_port = tcp.server_address[1], udp.server_address[1]
    with socket.socket() as reserve:
        reserve.bind(("127.0.0.1", 0))
        socks_port = reserve.getsockname()[1]
    def launch(path):
        log = (args.output / (path.stem + ".log")).open("w")
        generated.append(args.output / (path.stem + ".log"))
        files.append(log)
        process = subprocess.Popen([args.core, "run", "-c", str(path), "--disable-color"],
                                   stdout=log, stderr=subprocess.STDOUT)
        processes.append(process)
        return process
    try:
        fixture_path = args.output / "fixture.json"
        fixture_path.write_text(json.dumps(config("gvisor", "fixture", tcp_port, udp_port, socks_port), indent=2))
        generated.append(fixture_path)
        subprocess.run([args.core, "check", "-c", str(fixture_path)], check=True, timeout=20)
        if not args.check_only:
            fixture = launch(fixture_path)
            wait_port(socks_port, fixture)
        for topology in ("native", "pre-socks"):
            for stack in ("gvisor", "system", "mixed"):
                path = args.output / (topology + "-" + stack + ".json")
                path.write_text(json.dumps(config(stack, topology, tcp_port, udp_port, socks_port), indent=2))
                generated.append(path)
                subprocess.run([args.core, "check", "-c", str(path)], check=True, timeout=20)
                sample = {"topology": topology, "stack": stack, "config_check": True}
                if not args.check_only:
                    process = launch(path)
                    try:
                        for _ in range(100):
                            if process.poll() is not None:
                                raise RuntimeError("TUN core exited: " + path.stem)
                            if route().get("interface", "").startswith("utun"):
                                break
                            time.sleep(0.05)
                        else:
                            raise RuntimeError("capture route did not appear")
                        sample.update(probe(tcp_port, udp_port))
                        sample["tun_interface"] = route().get("interface")
                    finally:
                        stop(process)
                    for _ in range(100):
                        if route() == before:
                            break
                        time.sleep(0.05)
                    else:
                        raise RuntimeError("capture route was not restored")
                    sample["route_restored"] = True
                report["samples"].append(sample)
                print(json.dumps(sample), flush=True)
        report["success"] = True
    except BaseException as exc:
        report["success"] = False
        report["error"] = str(exc)
        raise
    finally:
        for process in reversed(processes):
            stop(process)
        for stream in files:
            stream.close()
        for server in (tcp, udp):
            server.shutdown()
            server.server_close()
        if before:
            report["after"] = route()
            report["cleanup_verified"] = report["after"] == before
            report["default_route_unchanged_verified"] = route("1.1.1.1") == default_before
            dns_after = subprocess.check_output(["/usr/sbin/scutil", "--dns"])
            report["system_dns_unchanged_verified"] = dns_after == dns_before
            if not all(report[key] for key in ("cleanup_verified", "default_route_unchanged_verified", "system_dns_unchanged_verified")):
                report["success"] = False
        report_path = args.output / "report.json"
        report_path.write_text(json.dumps(report, indent=2))
        generated.append(report_path)
        if os.geteuid() == 0 and "SUDO_UID" in os.environ:
            for path in generated:
                if path.is_file():
                    os.chown(path, int(os.environ["SUDO_UID"]), int(os.environ["SUDO_GID"]))
            if new_output:
                os.chown(args.output, int(os.environ["SUDO_UID"]), int(os.environ["SUDO_GID"]))
    if not report["success"]:
        raise RuntimeError("test cleanup verification failed; inspect report.json")


if __name__ == "__main__":
    def interrupted(signum, frame):
        raise KeyboardInterrupt("interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    main()

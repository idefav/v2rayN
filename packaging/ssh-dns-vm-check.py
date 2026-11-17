"""Explicitly disposable VM systemd/Netplan acceptance; public recursion is a separate test.

Adds a local example.com zone to Unbound ONLY in the test process. The installed
production helper, systemd service, transaction, timer and network operations are real.
Observer proof is simulated here and is never reported as public DNS-egress evidence.
"""
import importlib.util
import json
import os
from pathlib import Path
import socket
import struct
import sys
import time
import uuid

assert os.geteuid() == 0
assert socket.gethostname().startswith("lima-ssh-dns-noble-"), "Disposable test VM required"
spec = importlib.util.spec_from_file_location("helper", "/tmp/ssh-dns-helper.py")
h = importlib.util.module_from_spec(spec)
spec.loader.exec_module(h)
original_build = h.build_config
h.build_config = lambda base, alibaba: original_build(base, alibaba) + b'\n    # Disposable VM fixture: no public recursion claim\n    local-zone: "example.com." static\n    local-data: "example.com. 60 IN A 192.0.2.10"\n    local-data: "example.com. 60 IN AAAA 2001:db8::10"\n    local-data: "alias.example.com. 60 IN CNAME example.com."\n'
# These VMs use no Alibaba forward zones, so the local-data remains in server:.
original_external = h.external_ip
# Pin the already measured VM HTTP exit for persistence tests; no observer claims.
h.external_ip = lambda: "203.0.113.10"

def status():
    state = h.read_state()
    return h.public(state)

def pids():
    return h.run(["systemctl", "show", "ssh", "systemd-resolved", "systemd-networkd", "-p", "MainPID"])

def wire_query(name, kind):
    identifier = os.urandom(2)
    packet = identifier + b"\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00"
    packet += b"".join(bytes([len(part)]) + part.encode() for part in name.split(".")) + b"\x00" + struct.pack("!HH", kind, 1)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.settimeout(3)
        sock.sendto(packet, ("127.0.0.1", 53))
        data, _ = sock.recvfrom(4096)
    assert data[:2] == identifier
    return data

def answer_types(data):
    def skip_name(offset):
        while data[offset]:
            if data[offset] & 0xC0 == 0xC0:
                return offset + 2
            offset += data[offset] + 1
        return offset + 1
    offset = 12
    for _ in range(struct.unpack("!H", data[4:6])[0]):
        offset = skip_name(offset) + 4
    result = []
    for _ in range(struct.unpack("!H", data[6:8])[0]):
        offset = skip_name(offset)
        kind, _, _, length = struct.unpack("!HHIH", data[offset:offset + 10])
        result.append(kind)
        offset += 10 + length
    return result

action = sys.argv[1]
if action in ("commit", "pending", "expire"):
    old = h.read_state()
    if old and old["phase"] not in ("rolled_back",):
        h.rollback(old)
    if not old:
        # The disposable VM inherits host DHCP search domains. Establish the
        # supported single-link/no-split-DNS fixture; production never clears them.
        link = json.loads(h.run(["ip", "-j", "-4", "route", "show", "default"]))[0]["dev"]
        h.run(["resolvectl", "domain", link, ""])
    plan = h.inspect()
    before = pids()
    state = h.prepare({"planToken": plan["token"], "expectedExit": plan["exit"], "transactionId": uuid.uuid4().hex})
    assert h.dns_query("example.com") == ["192.0.2.10"]
    assert h.dns_query("example.com", tcp=True) == ["192.0.2.10"]
    aaaa = wire_query("example.com", 28)
    assert aaaa[3] & 15 == 0 and socket.inet_pton(socket.AF_INET6, "2001:db8::10") in aaaa
    assert 5 in answer_types(wire_query("alias.example.com", 1))
    assert wire_query("missing.example.com", 1)[3] & 15 == 3
    state = h.activate(state)
    assert h.link_value("dns", plan["interface"]) == ["127.0.0.1"]
    assert "192.0.2.10" in h.run(["getent", "ahostsv4", "example.com"])
    assert pids() == before, "SSH/resolved/networkd restarted"
    if action == "commit":
        h.commit(state, {"verifiedExit": plan["exit"], "verifiedDns": [plan["exit"]]})
        h.run(["networkctl", "renew", plan["interface"]])
        time.sleep(3)
        assert h.link_value("dns", plan["interface"]) == ["127.0.0.1"]
        assert pids() == before
    elif action == "expire":
        state["deadline"] = int(time.time()) + 8
        h.save(state)
        h.arm(state)
        deadline = time.monotonic() + 40
        while h.read_state()["phase"] != "rolled_back" and time.monotonic() < deadline:
            time.sleep(1)
        assert h.read_state()["phase"] == "rolled_back", "Actual timer did not restore DNS"
        assert pids() == before
elif action == "after-boot":
    state = h.read_state()
    assert state["phase"] == "committed"
    assert h.link_value("dns", state["original"]["interface"]) == ["127.0.0.1"]
    h.run(["systemctl", "is-active", h.UNIT])
    assert h.dns_query("example.com", tcp=True) == ["192.0.2.10"]
elif action == "after-pending-boot":
    deadline = time.monotonic() + 50
    while h.read_state()["phase"] != "rolled_back" and time.monotonic() < deadline:
        time.sleep(1)
    assert h.read_state()["phase"] == "rolled_back"
elif action == "rollback":
    state = h.read_state()
    before = pids()
    h.rollback(state)
    assert h.link_value("dns", state["original"]["interface"]) == state["original"]["dns"]
    assert pids() == before
else:
    raise ValueError(action)
print(json.dumps({"fixture": "local-zone-and-simulated-observer", "action": action, "result": "PASS", "state": status()}))

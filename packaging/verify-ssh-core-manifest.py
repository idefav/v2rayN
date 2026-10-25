#!/usr/bin/env python3
"""Fail packaging if the shipped feature core does not match its pin."""
import hashlib
import json
import pathlib
import sys

core = pathlib.Path(sys.argv[1])
manifest = json.loads((core.parent / "ssh-udp-core.json").read_text())
if manifest.get("SchemaVersion") != 1 or not manifest.get("Version", "").startswith("1.14.3-ssh-udp."):
    raise SystemExit("Invalid SSH UDP core manifest version")
if manifest.get("Sha256", "").lower() != hashlib.sha256(core.read_bytes()).hexdigest():
    raise SystemExit("SSH UDP core digest mismatch")
print("Verified pinned SSH UDP core:", manifest["Version"])

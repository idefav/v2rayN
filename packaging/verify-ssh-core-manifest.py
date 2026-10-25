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
magic = core.read_bytes()[:4]
library_name = 'libcronet.so' if magic == b'\x7fELF' else 'libcronet.dll' if magic[:2] == b'MZ' else None
if library_name and not (core.parent / library_name).is_file():
    raise SystemExit("Missing Naive shared library: " + library_name)
for name, digest in manifest.get('Libraries', {}).items():
    if name not in ['libcronet.so', 'libcronet.dll']:
        raise SystemExit('Unknown library in core manifest')
    if hashlib.sha256((core.parent / name).read_bytes()).hexdigest() != digest.lower():
        raise SystemExit('Naive shared library digest mismatch: ' + name)
print("Verified pinned SSH UDP core:", manifest["Version"])

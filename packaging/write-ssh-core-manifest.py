#!/usr/bin/env python3
"""Write the feature-core pin after the binary's final signing step."""
import hashlib
import json
import pathlib
import sys

core = pathlib.Path(sys.argv[1])
manifest = dict(SchemaVersion=1, Version="1.14.3-ssh-udp.1", Sha256=hashlib.sha256(core.read_bytes()).hexdigest())
(core.parent / "ssh-udp-core.json").write_text(json.dumps(manifest, indent=2) + "\n")

#!/usr/bin/env python3
"""Write the feature-core pin after the binary's final signing step."""
import hashlib
import json
import pathlib
import sys

core = pathlib.Path(sys.argv[1])
manifest = dict(SchemaVersion=1, Version="1.14.3-ssh-udp.1", Sha256=hashlib.sha256(core.read_bytes()).hexdigest())
libraries = {}
for name in ['libcronet.so', 'libcronet.dll']:
    library = core.parent / name
    if library.exists(): libraries[name] = hashlib.sha256(library.read_bytes()).hexdigest()
if libraries: manifest['Libraries'] = libraries
(core.parent / "ssh-udp-core.json").write_text(json.dumps(manifest, indent=2) + "\n")

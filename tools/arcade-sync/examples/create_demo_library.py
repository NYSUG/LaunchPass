#!/usr/bin/env python3
"""Create inert synthetic files for the offline example. These are NOT playable ROMs."""
from pathlib import Path
import sys

root = Path(sys.argv[1])
root.mkdir(parents=True, exist_ok=False)
for platform, filename, data in (
    ('arcade', '1941.zip', b'PK\x03\x04synthetic-one'),
    ('arcade', '1942.zip', b'PK\x03\x04synthetic-two'),
    ('genesis', 'example.md', b'synthetic-genesis'),
    ('snes', 'example.sfc', b'synthetic-snes'),
):
    (root / platform).mkdir(exist_ok=True)
    (root / platform / filename).write_bytes(data)
print('Created synthetic offline library:', root)

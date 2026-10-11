#!/usr/bin/env python3
"""Print the funcmap entries around an address: python scripts/research/neighbors.py game 0x80063DF8 [radius]"""

import sys

from mipsdis import load_map

ov, addr = sys.argv[1], int(sys.argv[2], 16)
radius = int(sys.argv[3]) if len(sys.argv) > 3 else 4
m = load_map(ov)
ks = sorted(m)
i = next(n for n, k in enumerate(ks) if k >= addr)
for k in ks[max(0, i - radius): i + radius + 1]:
    mark = ">" if k == addr else " "
    print(f"{mark} 0x{k:08X} {m[k]['name']:24} {m[k]['size']}")

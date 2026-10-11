#!/usr/bin/env python3
"""Read u32 words from an overlay's file image: python scripts/research/peek.py open 0x8003F9CC [count]

Only static data is visible -- anything the game writes at run time is not.
"""

import struct
import sys

from mipsdis import load_image

ov, addr = sys.argv[1], int(sys.argv[2], 16)
count = int(sys.argv[3]) if len(sys.argv) > 3 else 1
img, base = load_image(ov)
for i in range(count):
    a = addr + i * 4
    off = a - base
    if not 0 <= off < len(img) - 3:
        print(f"0x{a:08X}  (outside the file image: bss or another region)")
        continue
    print(f"0x{a:08X}  0x{struct.unpack_from('<I', img, off)[0]:08X}")

#!/usr/bin/env python3
"""List FDAT.T entries: u16 count, then u16 start sectors (2048-byte units).

For each entry, prints its offset/size and how code-like it looks: the count of
`jr ra` and `addiu sp, sp, -N` words, and how many of its first 32 words are
pointers into KSEG0 RAM (a module's dispatch table).
"""

import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
data = (ROOT / "build/FDAT.T").read_bytes()


def entries():
    count = struct.unpack_from("<H", data, 0)[0]
    starts = [struct.unpack_from("<H", data, 2 + 2 * i)[0] for i in range(count + 1)]
    for i in range(count):
        s, e = starts[i] * 2048, starts[i + 1] * 2048
        if e <= s:
            e = len(data) if i == count - 1 else s
        yield i, s, e - s


def score(blob):
    words = [struct.unpack_from("<I", blob, o)[0] for o in range(0, len(blob) - 3, 4)]
    jr = sum(1 for w in words if w == 0x03E00008)
    pro = sum(1 for w in words if (w & 0xFFFF8000) == 0x27BD8000)
    ptrs = sum(1 for w in words[:32] if 0x80000000 <= w < 0x80200000)
    return jr, pro, ptrs


if __name__ == "__main__":
    for i, off, size in entries():
        jr, pro, ptrs = score(data[off: off + size])
        tag = "CODE" if jr >= 3 and pro >= 3 else ""
        print(f"{i:3} off=0x{off:07X} ({off:8}) size={size:7}  jr={jr:3} prologues={pro:3} ptrs32={ptrs:2} {tag}")

#!/usr/bin/env python3
"""Disassemble a function (or a range) of one overlay, with funcmap names on jal targets.

Usage:
    python scripts/research/mipsdis.py open 0x8001B020            # the whole function, size from the funcmap
    python scripts/research/mipsdis.py game 0x80061E00 -n 40      # 40 instructions
    python scripts/research/mipsdis.py game --name CdInit         # by funcmap name
"""

import argparse
import json
import struct
from pathlib import Path

from capstone import Cs, CS_ARCH_MIPS, CS_MODE_MIPS32, CS_MODE_LITTLE_ENDIAN

ROOT = Path(__file__).resolve().parents[2]
FILES = {"open": "OPEN.EXE", "game": "GAME.EXE", "end": "END.EXE", "main": "SLUS_002.55"}
BASES = {"open": 0x80011000, "game": 0x80011000, "end": 0x80011000, "main": 0x80010000}


def load_map(ov):
    data = json.loads((ROOT / "config/funcmaps" / f"{ov}.json").read_text())
    funcs = data["functions"] if isinstance(data, dict) else data
    return {int(f["address"], 16): f for f in funcs}


def load_image(ov):
    raw = (ROOT / "build" / FILES[ov]).read_bytes()
    return raw[0x800:], BASES[ov]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("overlay")
    ap.add_argument("address", nargs="?")
    ap.add_argument("--name")
    ap.add_argument("-n", type=int)
    a = ap.parse_args()

    fmap = load_map(a.overlay)
    names = {addr: f["name"] for addr, f in fmap.items()}
    if a.name:
        addr = next(k for k, v in names.items() if v == a.name)
    else:
        addr = int(a.address, 16)
    img, base = load_image(a.overlay)
    count = a.n or (fmap[addr]["size"] // 4 if addr in fmap else 32)

    md = Cs(CS_ARCH_MIPS, CS_MODE_MIPS32 + CS_MODE_LITTLE_ENDIAN)
    off = addr - base
    print(f"{names.get(addr, hex(addr))}:")
    for i in range(count):
        a_ = addr + i * 4
        if a_ in names and i:
            print(f"{names[a_]}:")
        word = img[off + i * 4: off + i * 4 + 4]
        insn = next(md.disasm(word, a_), None)
        if insn is None:
            print(f"  {a_:08X}  .word 0x{struct.unpack('<I', word)[0]:08X}")
            continue
        ops = insn.op_str
        if insn.mnemonic in ("jal", "j"):
            t = int(ops, 16)
            ops += f"   <{names.get(t, '?')}>"
        print(f"  {a_:08X}  {insn.mnemonic:8} {ops}")


if __name__ == "__main__":
    main()

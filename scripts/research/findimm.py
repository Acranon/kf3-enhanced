#!/usr/bin/env python3
"""List the funcmap functions of an overlay whose code uses all the given immediates.

Usage:
    python scripts/research/findimm.py open 0x249 0x246
"""

import sys

from capstone import Cs, CS_ARCH_MIPS, CS_MODE_MIPS32, CS_MODE_LITTLE_ENDIAN

from mipsdis import load_image, load_map


def main():
    ov = sys.argv[1]
    wanted = [int(x, 16) for x in sys.argv[2:]]
    fmap = load_map(ov)
    img, base = load_image(ov)
    md = Cs(CS_ARCH_MIPS, CS_MODE_MIPS32 + CS_MODE_LITTLE_ENDIAN)
    for addr in sorted(fmap):
        f = fmap[addr]
        code = img[addr - base: addr - base + f["size"]]
        imms = set()
        for insn in md.disasm(code, addr):
            for tok in insn.op_str.replace(",", " ").replace("(", " ").replace(")", " ").split():
                try:
                    v = int(tok, 0)
                except ValueError:
                    continue
                imms.add(v & 0xFFFF)
                imms.add(v)
        if all(w in imms for w in wanted):
            print(f"0x{addr:08X} {f['name']:24} size={f['size']}")


if __name__ == "__main__":
    main()

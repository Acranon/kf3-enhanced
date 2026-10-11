#!/usr/bin/env python3
"""List funcmap functions whose code runs off their end into the next function.

A function that does not end in an unconditional jump (jr / j, plus its delay
slot) falls through into whatever follows. The recompiler emits each funcmap
entry as its own method and simply returns at the end, so such a split silently
cuts the routine short (GAME.EXE's card-load screen did this: Continue never
loaded). Usage: python scripts/research/find_fallthrough.py game
"""
import sys, struct
from capstone import Cs, CS_ARCH_MIPS, CS_MODE_MIPS32, CS_MODE_LITTLE_ENDIAN
from mipsdis import load_image, load_map

def main():
    ov = sys.argv[1]
    fm = load_map(ov); img, base = load_image(ov)
    md = Cs(CS_ARCH_MIPS, CS_MODE_MIPS32 + CS_MODE_LITTLE_ENDIAN)
    addrs = sorted(fm)
    for a in addrs:
        size = fm[a]["size"]
        if size < 8: continue
        end = a + size
        # the second-to-last word is the last real instruction (last is its delay slot)
        off = end - 8 - base
        if off < 0 or off + 4 > len(img): continue
        w = struct.unpack_from('<I', img, off)[0]
        ins = next(md.disasm(img[off:off+4], end - 8), None)
        mn = ins.mnemonic if ins else '?'
        if mn in ('jr', 'j', 'b'): continue
        # also accept a final 'jr' as the very last word with nop slot missing
        nxt = end in fm
        print(f"0x{a:08X} {fm[a]['name']:28} size {size:5} ends with {mn:6} {'-> next fn ' + fm[end]['name'] if nxt else ''}")

if __name__ == '__main__':
    main()

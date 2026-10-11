#!/usr/bin/env python3
"""Find each executable's PSY-Q interrupt-callback table (Interrupts.CallbackTable).

InterruptCallback in this libapi is a thunk: `lw v0, PTR; lw v0, 8(v0); jalr v0`.
PTR points at libapi's driver table, whose slot 2 is the real routine; that
routine indexes the callback table with irq*4 (`lui a1, hi; addiu a1, a1, lo;
sll v0, s1, 2; addu a0, v0, a1`) and keeps the enable mask 11 slots past it.
"""

import struct

from capstone import Cs, CS_ARCH_MIPS, CS_MODE_MIPS32, CS_MODE_LITTLE_ENDIAN

from mipsdis import load_image, load_map

md = Cs(CS_ARCH_MIPS, CS_MODE_MIPS32 + CS_MODE_LITTLE_ENDIAN)


def insns(img, base, addr, n):
    return list(md.disasm(img[addr - base: addr - base + n * 4], addr))


def hi_lo(seq, reg):
    """First `lui reg, hi` followed by `addiu/lw reg, lo(reg)` -> the full address."""
    hi = None
    for i in seq:
        ops = [o.strip() for o in i.op_str.split(",")]
        if i.mnemonic == "lui" and ops[0] == reg:
            hi = int(ops[1], 0) << 16
        elif hi is not None and i.mnemonic in ("addiu", "lw") and ops[0] == reg:
            lo = int(ops[-1].split("(")[0], 0) if i.mnemonic == "lw" else int(ops[2], 0)
            return (hi + lo) & 0xFFFFFFFF
    return None


for ov in ("open", "game", "end"):
    img, base = load_image(ov)
    m = load_map(ov)
    thunk = next(a for a, f in m.items() if f["name"] == "InterruptCallback")
    ptr = hi_lo(insns(img, base, thunk, 4), "$v0")
    drv = struct.unpack_from("<I", img, ptr - base)[0]
    real = struct.unpack_from("<I", img, drv + 8 - base)[0]
    table = hi_lo(insns(img, base, real, 12), "$a1")
    print(f"{ov:5} InterruptCallback 0x{thunk:08X} -> driver 0x{drv:08X} -> 0x{real:08X}  table 0x{table:08X}")

#!/usr/bin/env python3
"""Read RAM out of a quick save (.kfstate), for finding where the game keeps things.

A quick save (patches/Core/SaveState.cs) is: u32 magic, s32 version, the active
area module's name (a .NET string: length byte, then UTF-8), the CPU registers,
then s32 RAM length and the RAM itself (2 MB, or 8 MB with the dev-kit memory).

As a module:

    from savestate import load, u8, u16, u32
    area, ram = load("tests/saves/throne.kfstate")
    print(area, hex(u32(ram, 0x801B25F0)))

From the command line, print words at an address (or diff two saves):

    python scripts/research/savestate.py tests/saves/a.kfstate 0x801B24FA [count]
    python scripts/research/savestate.py --diff a.kfstate b.kfstate 0x801B2400 0x801B2700
"""

import struct
import sys


def load(path):
    """Returns (area module name, RAM bytes)."""
    data = open(path, "rb").read()
    n = data[8]
    area = data[9:9 + n].decode()
    # The RAM length follows the register snapshot, whose size is the runtime's;
    # find it by value instead of by offset.
    for length in (0x200000, 0x800000):
        i = data.find(struct.pack("<I", length), 9 + n, 9 + n + 2000)
        if i > 0:
            return area, data[i + 4:i + 4 + length]
    raise ValueError(f"{path}: no RAM block found")


def u8(ram, addr):
    return ram[addr & 0x1FFFFF]


def u16(ram, addr):
    return struct.unpack_from("<H", ram, addr & 0x1FFFFF)[0]


def u32(ram, addr):
    return struct.unpack_from("<I", ram, addr & 0x1FFFFF)[0]


def main(argv):
    if len(argv) >= 5 and argv[1] == "--diff":
        _, a = load(argv[2])
        _, b = load(argv[3])
        lo, hi = int(argv[4], 0), int(argv[5], 0) if len(argv) > 5 else int(argv[4], 0) + 0x100
        for addr in range(lo, hi):
            if u8(a, addr) != u8(b, addr):
                print(f"0x{addr:08X}: {u8(a, addr):02X} -> {u8(b, addr):02X}")
        return
    if len(argv) < 3:
        print(__doc__)
        return
    area, ram = load(argv[1])
    addr, count = int(argv[2], 0), int(argv[3], 0) if len(argv) > 3 else 8
    print(f"area module: {area}")
    for i in range(count):
        print(f"0x{addr + 4 * i:08X}: {u32(ram, addr + 4 * i):08X}")


if __name__ == "__main__":
    main(sys.argv)

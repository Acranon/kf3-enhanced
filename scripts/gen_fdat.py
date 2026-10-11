#!/usr/bin/env python3
"""Build the function maps and overlay entries for the FDAT.T area code modules.

FDAT.T holds 28 areas as (map, data, code) triplets; the code is entry 3n+2.
Every module is linked for 0x801E8308 -- GAME.EXE carries that address as three
static words at 0x800136E8, and scoring each module's dispatch-table targets
against function starts peaks there unanimously, 14/14 to 16/16.

A module is a header (an id word, 31 dispatch slots, and in some modules a
second short pointer table) followed by code. Code starts at the lowest dispatch
target, which is always the shared `jr ra` no-op stub.

For each module this runs the recompiler's linear sweep over the code, then
merges in every dispatch target, every pointer in the header, and every internal
`jal` target, since a sweep misses entry points it never lands on. Sizes are
recomputed from the sorted starts (the last function runs to the end of the
entry). Run scripts/merge_branch_spans.py afterwards to undo the splits a sweep
makes at a `jr` through a switch table.

The overlay entries replace any "fdat*" overlays in config/kf3.json.
"""

import json
import re
import struct
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BASE = 0x801E8308
FDAT = (ROOT / "build/FDAT.T").read_bytes()
RC = ["dotnet", "run", "--project", str(ROOT / "tools/RecompOne/RecompOne.Recompiler"),
      "-c", "Release", "--no-build", "--"]

import sys
sys.path.insert(0, str(Path(__file__).parent))
from fdat import entries  # noqa: E402


def u32(blob, off):
    return struct.unpack_from("<I", blob, off)[0]


def module(i, off, size):
    blob = FDAT[off: off + size]
    end = BASE + size
    slots = [u32(blob, 4 * k) for k in range(1, 32)]
    code = min(slots)
    header_ptrs = {u32(blob, o) for o in range(0, code - BASE, 4)}
    starts = {p for p in header_ptrs if code <= p < end}

    name = f"fdat{i:02d}"
    out = ROOT / "config/funcmaps" / f"{name}.json"
    skip = code - BASE
    subprocess.run(RC + ["--generate-function-file", "-linear-sweep",
                         "-disc", str(ROOT / "disc/KingsFieldIII.cue"),
                         "-file", "CD/COM/FDAT.T", "-base", f"{code:X}",
                         "-offset", f"{off:X}", "-skip", f"{skip:X}",
                         "-size", f"{size - skip:X}", "-out", str(out)],
                   check=True, capture_output=True)
    swept = json.loads(out.read_text())
    swept = swept["functions"] if isinstance(swept, dict) else swept
    starts |= {int(f["address"], 16) for f in swept}

    # internal jal targets
    for o in range(skip, size, 4):
        w = u32(blob, o)
        if w >> 26 == 3:
            t = ((BASE + o) & 0xF0000000) | ((w & 0x03FFFFFF) << 2)
            if code <= t < end:
                starts.add(t)

    starts = sorted(starts)
    funcs = []
    for n, a in enumerate(starts):
        nxt = starts[n + 1] if n + 1 < len(starts) else end
        funcs.append({"address": f"0x{a:08X}", "name": f"func_{a:08X}", "size": nxt - a})
    out.write_text(json.dumps({"functions": funcs}, indent=2) + "\n")
    print(f"{name}: off=0x{off:06X} size={size} code=0x{code:08X} "
          f"swept={len(swept)} total={len(funcs)}")
    return {"name": name, "file": "CD/COM/FDAT.T", "base": f"0x{BASE:08X}",
            "offset": off, "skip": 0, "size": size, "funcMap": f"funcmaps/{name}.json"}


def main():
    overlays = [module(i, off, size) for i, off, size in entries()
                if 2 <= i <= 83 and (i - 2) % 3 == 0]

    cfg_path = ROOT / "config/kf3.json"
    text = cfg_path.read_text(encoding="utf-8-sig")
    cfg = json.loads(re.sub(r"^\s*//.*$", "", text, flags=re.M))
    cfg["overlays"] = [o for o in cfg["overlays"] if not o["name"].startswith("fdat")] + overlays
    cfg_path.write_text(json.dumps(cfg, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()

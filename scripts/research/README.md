# Research tools

Tools used to work out how the game does things. None is part of the build; they are
kept so findings in docs/research can be checked and reproduced. Most read the
extracted executables in `build/` (made by the pipeline from your disc) and the
function maps in `config/funcmaps/`. Requires Python 3 with `capstone`.

| Script | What it answers |
|---|---|
| `mipsdis.py` | What does this function do? Disassembles it, with funcmap names on calls. |
| `findimm.py` | Which function uses these constants? Searches an overlay for immediates. |
| `neighbors.py` | What is around this address? Prints the nearby funcmap entries. |
| `peek.py` | What is in this static data? Reads words from an executable's image. |
| `irq_table.py` | Where is each executable's interrupt-callback table? (Bound in Program.cs.) |
| `find_fallthrough.py` | Which functions run off their end into the next? (A recompiler hazard.) |
| `savestate.py` | What is in RAM at this point of play? Reads or diffs quick saves. |
| `ocr/` | What do the text images in ITEM.T say? See [ocr/README.md](ocr/README.md). |

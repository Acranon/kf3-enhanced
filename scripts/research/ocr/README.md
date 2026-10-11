# ITEM.T text recognition

King's Field III keeps its item, spell, place and character descriptions as 613
pre-rendered TIM images in `ITEM.T`, not as text. These scripts turn them into the
text in `data/item_t_text_all.json` (kept locally, not in the repository: it is the
game's text), which `scripts/gen_creature_names.py` and the map hints read.

They are research tools, run once from this folder; they are kept so the result can
be reproduced. Requires Python 3 with Pillow and NumPy.

| Step | Script | What it does |
|---|---|---|
| 1 | `python tim.py ITEM.T tims_item` | Finds every TIM in the archive and writes it as a PNG to `tims_item/`. |
| 2 | `python ocr2.py` | Cuts the text images into 15-pixel line bands and glyph cells (helpers in `ocr.py`), collects the distinct glyph shapes, and writes `glyphs.png` (a numbered sheet) and `glyphkeys.pkl`. |
| 3 | `python ocr3.py` | Turns the hand-made labels in its `LAB` table (glyph number on the sheet to the letters it shows; the font joins some pairs, such as "st") into one template per character, `templates.pkl`. |
| 4 | `python dec2.py <first> <last>` | Decodes images first..last-1 by template matching and writes `dec2_<first>_<last>.json`, counting the images that still have unknown glyphs. |

`ITEM.T` comes from the disc (`scripts/extract_file.py`). The `.pkl` files and the
images are derived from the game's font and are not committed.

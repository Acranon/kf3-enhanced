#!/usr/bin/env python3
"""Draw the placeholder icon: a sword over "III" in the port's colours.

Writes kf3.png (256 x 256, the window icon) and kf3.ico (16 to 256, the executable
and installer icon) beside this script. Requires Pillow. Replace both files with
real art whenever there is some; nothing else depends on how they look.

    python packaging/icon/make-icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
BG = (27, 27, 29, 255)        # interface.ini Background #1B1B1D
ACCENT = (34, 160, 107, 255)  # interface.ini Accent #22A06B
STEEL = (214, 220, 226, 255)
GOLD = (196, 160, 72, 255)


def draw(n=1024):
    im = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    r = n * 0.18
    d.rounded_rectangle([0, 0, n - 1, n - 1], radius=r, fill=BG, outline=ACCENT, width=int(n * 0.035))

    cx = n / 2
    # blade
    d.polygon([(cx, n * 0.10), (cx + n * 0.055, n * 0.20), (cx + n * 0.055, n * 0.62),
               (cx - n * 0.055, n * 0.62), (cx - n * 0.055, n * 0.20)], fill=STEEL)
    d.line([(cx, n * 0.16), (cx, n * 0.60)], fill=(150, 158, 166, 255), width=int(n * 0.012))
    # guard, grip, pommel
    d.rounded_rectangle([cx - n * 0.21, n * 0.62, cx + n * 0.21, n * 0.67], radius=n * 0.02, fill=GOLD)
    d.rectangle([cx - n * 0.03, n * 0.67, cx + n * 0.03, n * 0.78], fill=(110, 78, 46, 255))
    d.ellipse([cx - n * 0.05, n * 0.77, cx + n * 0.05, n * 0.87], fill=GOLD)

    # "III" across the blade
    try:
        font = ImageFont.truetype("georgiab.ttf", int(n * 0.26))
    except OSError:
        font = ImageFont.load_default()
    text = "III"
    box = d.textbbox((0, 0), text, font=font)
    w, h = box[2] - box[0], box[3] - box[1]
    x, y = cx - w / 2 - box[0], n * 0.40 - h / 2 - box[1]
    for dx in range(-6, 7, 3):
        for dy in range(-6, 7, 3):
            d.text((x + dx * n / 1024, y + dy * n / 1024), text, font=font, fill=BG)
    d.text((x, y), text, font=font, fill=ACCENT)
    return im


def main():
    big = draw()
    big.resize((256, 256), Image.LANCZOS).save(HERE / "kf3.png")
    big.save(HERE / "kf3.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("wrote", HERE / "kf3.png", "and", HERE / "kf3.ico")


if __name__ == "__main__":
    main()

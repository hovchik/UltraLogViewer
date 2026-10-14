"""Draws the UltraLogViewer app icon and writes app.ico (16-256 px) and app.png (512 px).

    python3 scripts/make-icon.py            # needs Pillow

The icon is a blue tile holding log rows, each with a level marker
(debug, info, warning, error), using the app's theme colors.
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "dotnet/src/UltraLogViewer.Desktop/Assets"
SS = 4  # supersampling factor

TOP, BOTTOM = (0x4A, 0x86, 0xF0), (0x1E, 0x4F, 0xB8)
ROW = (255, 255, 255)
LEVELS = [(0xB8, 0xC4, 0xD8), (255, 255, 255), (0xFF, 0xB0, 0x3A), (0xFF, 0x5A, 0x4F)]


def tile(size: int) -> Image.Image:
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Vertical gradient clipped to a rounded square.
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / (s - 1)
        gd.line([(0, y), (s, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM)) + (255,))
    mask = Image.new("L", (s, s), 0)
    margin = round(s * (0.02 if size <= 24 else 0.06))
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, s - margin - 1, s - margin - 1], radius=round(s * 0.2), fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    # Small sizes get fewer, thicker rows so they stay readable.
    rows = 3 if size <= 24 else 4
    lengths = [0.62, 0.48, 0.56] if rows == 3 else [0.58, 0.44, 0.52, 0.36]
    levels = [LEVELS[1], LEVELS[2], LEVELS[3]] if rows == 3 else LEVELS
    left, right = s * 0.2, s * 0.8
    span_top, span_bottom = s * 0.25, s * 0.75
    pitch = (span_bottom - span_top) / rows
    bar_h = pitch * (0.5 if rows == 3 else 0.46)
    for i in range(rows):
        cy = span_top + pitch * (i + 0.5)
        y0, y1 = cy - bar_h / 2, cy + bar_h / 2
        # Level marker.
        d.rounded_rectangle([left, y0, left + bar_h, y1], radius=bar_h * 0.3, fill=levels[i] + (255,))
        # Message bar.
        x0 = left + bar_h * 1.6
        x1 = min(right, x0 + (right - left) * lengths[i])
        alpha = 255 if i != 0 or rows == 3 else 235
        d.rounded_rectangle([x0, y0, x1, y1], radius=bar_h / 2, fill=ROW + (alpha,))

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [tile(n) for n in sizes]
    images[-1].save(OUT / "app.ico", format="ICO", sizes=[(n, n) for n in sizes], append_images=images[:-1])
    tile(512).save(OUT / "app.png")
    print(f"wrote {OUT / 'app.ico'} and {OUT / 'app.png'}")


if __name__ == "__main__":
    main()

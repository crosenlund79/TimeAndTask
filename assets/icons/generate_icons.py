"""Generates WorkTrace icon assets (SVG + multi-resolution ICO + 256 px PNG).

Design: a rounded tile containing a clock ring whose hands form a checkmark, with a small
activity marker at twelve o'clock. Each ICO frame is rendered separately with 8x supersampling
so the 16 px tray icon stays crisp.

Usage: python3 assets/icons/generate_icons.py   (requires Pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

VARIANTS = {
    # name: (tile colour, marker colour)
    "worktrace": ("#0F766E", "#F59E0B"),
    "tray-active": ("#0F766E", "#F59E0B"),
    "tray-paused": ("#6B7280", "#D1D5DB"),
}

# Geometry on a 256 x 256 canvas.
TILE = (8, 8, 248, 248)
TILE_RADIUS = 56
RING_CENTER = (128, 136)
RING_RADIUS = 78
RING_WIDTH = 22
CHECK = [(94, 138), (120, 164), (166, 108)]
CHECK_WIDTH = 24
MARKER = (128, 50, 17)  # cx, cy, r


def render(size: int, tile: str, marker: str) -> Image.Image:
    scale = 8
    s = 256 * scale
    # Small frames get heavier strokes so the glyph survives the downscale.
    weight = 1.2 if size <= 24 else 1.0
    k = s / 256
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    d.rounded_rectangle([v * k for v in TILE], radius=TILE_RADIUS * k, fill=tile)

    cx, cy = RING_CENTER
    r = RING_RADIUS
    d.ellipse([(cx - r) * k, (cy - r) * k, (cx + r) * k, (cy + r) * k], outline="white", width=round(RING_WIDTH * k))

    pts = [(x * k, y * k) for x, y in CHECK]
    w = round(CHECK_WIDTH * weight * k)
    d.line(pts, fill="white", width=w, joint="curve")
    for x, y in pts:  # round caps
        d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill="white")

    mx, my, mr = MARKER
    mr = mr * weight
    # Marker sits on the ring with a tile-coloured halo so it reads as a separate dot.
    halo = mr + 7
    d.ellipse([(mx - halo) * k, (my - halo) * k, (mx + halo) * k, (my + halo) * k], fill=tile)
    d.ellipse([(mx - mr) * k, (my - mr) * k, (mx + mr) * k, (my + mr) * k], fill=marker)

    return img.resize((size, size), Image.Resampling.LANCZOS)


def svg(tile: str, marker: str) -> str:
    (x1, y1, x2, y2) = TILE
    cx, cy = RING_CENTER
    mx, my, mr = MARKER
    pts = " ".join(f"{x},{y}" for x, y in CHECK)
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <title>WorkTrace</title>
  <rect x="{x1}" y="{y1}" width="{x2 - x1}" height="{y2 - y1}" rx="{TILE_RADIUS}" fill="{tile}"/>
  <circle cx="{cx}" cy="{cy}" r="{RING_RADIUS}" fill="none" stroke="#FFFFFF" stroke-width="{RING_WIDTH}"/>
  <polyline points="{pts}" fill="none" stroke="#FFFFFF" stroke-width="{CHECK_WIDTH}" stroke-linecap="round" stroke-linejoin="round"/>
  <circle cx="{mx}" cy="{my}" r="{mr + 7}" fill="{tile}"/>
  <circle cx="{mx}" cy="{my}" r="{mr}" fill="{marker}"/>
</svg>
"""


def main() -> None:
    for name, (tile, marker) in VARIANTS.items():
        frames = [render(s, tile, marker) for s in SIZES]
        largest = frames[-1]
        largest.save(OUT / f"{name}.ico", format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
        (OUT / f"{name}.svg").write_text(svg(tile, marker), encoding="utf-8")
    render(256, *VARIANTS["worktrace"]).save(OUT / "worktrace-256.png")


if __name__ == "__main__":
    main()

"""Icons for the visual previews: the WorkTrace mark in each variant's colours (assets/icons/preview/<Variant>.ico,
also used for the active tray icon). Same geometry as assets/icons/generate_icons.py.

Usage: python3 tools/visual-preview/generate_icons.py   (requires Pillow)
"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "assets" / "icons"))
from generate_icons import SIZES, render  # noqa: E402

OUT = ROOT / "assets" / "icons" / "preview"

VARIANTS = {
    # tile, marker
    "ModernFluent": ("#6352D6", "#F5B94A"),
    "DarkProfessional": ("#2A2238", "#A07CF8"),
    "RefinedNeutral": ("#5F6B45", "#D9B36C"),
    "ModernGradient": ("#6A45F5", "#5FD3FF"),
    "GlassDepth": ("#7C3AED", "#F0ABFC"),
}

OUT.mkdir(parents=True, exist_ok=True)
for name, (tile, marker) in VARIANTS.items():
    frames = [render(s, tile, marker) for s in SIZES]
    frames[-1].save(OUT / f"{name}.ico", format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
    print(name)

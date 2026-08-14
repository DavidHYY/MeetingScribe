"""Rasterise assets/icon.svg into the app's icon/PNG deliverables.

Usage:
    python tools/generate_icons.py

Requires (repo has no vendored/system fallback - install into a venv,
never system Python, if these are missing):
    pip install cairosvg pillow

Produces:
    assets/icon.ico   - multi-resolution: 16, 24, 32, 48, 64, 128, 256
    assets/icon-256.png
    assets/icon-512.png

Regenerate this any time assets/icon.svg changes - the .ico/.png files are
derived output and are not hand-edited.
"""

from __future__ import annotations

import sys
from pathlib import Path

try:
    import cairosvg
except ImportError as exc:  # pragma: no cover - environment guard, not app logic
    raise SystemExit(
        "cairosvg is required (pip install cairosvg). "
        "Install into a venv, not system Python."
    ) from exc

try:
    from PIL import Image
except ImportError as exc:  # pragma: no cover - environment guard, not app logic
    raise SystemExit(
        "Pillow is required (pip install pillow). "
        "Install into a venv, not system Python."
    ) from exc

REPO_ROOT = Path(__file__).resolve().parent.parent
ASSETS_DIR = REPO_ROOT / "assets"
SVG_SOURCE = ASSETS_DIR / "icon.svg"

# Windows Explorer/Alt-Tab/Start menu each pick a different size from the .ico;
# shipping only one size forces the shell to upscale it and it looks soft/blurry.
ICO_SIZES: tuple[int, ...] = (16, 24, 32, 48, 64, 128, 256)
PNG_EXPORT_SIZES: tuple[int, ...] = (256, 512)


def render_png(size: int) -> Path:
    """Rasterise the SVG at `size`x`size` px and return the temp PNG path."""
    out_path = ASSETS_DIR / f"_icon_{size}.png"
    cairosvg.svg2png(
        url=str(SVG_SOURCE),
        write_to=str(out_path),
        output_width=size,
        output_height=size,
    )
    return out_path


def main() -> int:
    if not SVG_SOURCE.exists():
        print(f"Missing source SVG: {SVG_SOURCE}", file=sys.stderr)
        return 1

    ASSETS_DIR.mkdir(parents=True, exist_ok=True)

    # --- multi-resolution .ico ---
    # Pillow's IcoImagePlugin only considers a requested size "available" if
    # it is <= the base image passed to .save() (see IcoImagePlugin._save:
    # `if size[0] > width or size[1] > height: continue`, checked against the
    # base image's own size, not the largest of append_images). Saving from
    # the smallest frame silently drops every larger size. Always save from
    # the LARGEST rendered frame, with the rest passed as append_images.
    rendered = {size: render_png(size) for size in sorted(set(ICO_SIZES) | set(PNG_EXPORT_SIZES))}
    ico_images_desc = [Image.open(rendered[size]).convert("RGBA") for size in sorted(ICO_SIZES, reverse=True)]
    ico_path = ASSETS_DIR / "icon.ico"
    ico_images_desc[0].save(
        ico_path,
        format="ICO",
        sizes=[(s, s) for s in ICO_SIZES],
        append_images=ico_images_desc[1:],
    )
    print(f"Wrote {ico_path} ({', '.join(str(s) for s in ICO_SIZES)} px)")

    # --- standalone PNG exports (README, installer, future .icns) ---
    for size in PNG_EXPORT_SIZES:
        png_path = ASSETS_DIR / f"icon-{size}.png"
        Image.open(rendered[size]).convert("RGBA").save(png_path)
        print(f"Wrote {png_path}")

    # --- clean up scratch renders ---
    for path in rendered.values():
        path.unlink(missing_ok=True)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/bin/sh
# Regenerates assets/icon.icns from assets/icon-512.png.
#
# macOS-only (uses sips + iconutil, both /usr/bin system tools - no extra install).
# Run this after assets/icon.svg changes and tools/generate_icons.py has been re-run
# to refresh icon-512.png; this script does not touch the SVG or the .ico/.png files,
# only the .icns.
#
# Usage:
#   sh tools/generate_icns.sh
#
# Source: assets/icon-512.png (512x512, the largest rasterised export). All .iconset
# member sizes are downsamples of that source (sips -z, never upscaled - a blurry
# upscaled icon fails the same 16px-legibility bar documented in README.md's
# "Application icon" section). The 1024x1024 (icon_512x512@2x.png) representation is
# intentionally omitted: there is no >512px source to derive it from without
# upscaling.

set -eu

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$REPO_ROOT/assets/icon-512.png"
OUT="$REPO_ROOT/assets/icon.icns"
ICONSET="$(mktemp -d)/icon.iconset"

if [ "$(uname)" != "Darwin" ]; then
    echo "generate_icns.sh requires macOS (sips/iconutil are macOS-only system tools)." >&2
    exit 1
fi

if [ ! -f "$SRC" ]; then
    echo "Missing source PNG: $SRC (run tools/generate_icons.py first)" >&2
    exit 1
fi

mkdir -p "$ICONSET"

sips -z 16 16 "$SRC" --out "$ICONSET/icon_16x16.png" >/dev/null
sips -z 32 32 "$SRC" --out "$ICONSET/icon_16x16@2x.png" >/dev/null
sips -z 32 32 "$SRC" --out "$ICONSET/icon_32x32.png" >/dev/null
sips -z 64 64 "$SRC" --out "$ICONSET/icon_32x32@2x.png" >/dev/null
sips -z 128 128 "$SRC" --out "$ICONSET/icon_128x128.png" >/dev/null
sips -z 256 256 "$SRC" --out "$ICONSET/icon_128x128@2x.png" >/dev/null
sips -z 256 256 "$SRC" --out "$ICONSET/icon_256x256.png" >/dev/null
sips -z 512 512 "$SRC" --out "$ICONSET/icon_256x256@2x.png" >/dev/null
sips -z 512 512 "$SRC" --out "$ICONSET/icon_512x512.png" >/dev/null

iconutil -c icns "$ICONSET" -o "$OUT"
rm -rf "$ICONSET"

echo "Wrote $OUT"

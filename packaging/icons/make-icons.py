#!/usr/bin/env python3
"""Generate the VirtDeck application icon.

The icon is two overlapping rounded cards ("a deck of screens") on a blue tile.
This script is the single source of truth for that geometry: it emits the
scalable SVG, the PNG sizes a freedesktop hicolor theme wants, and the Windows
.ico -- all from the same numbers, so they can never drift apart.

    python3 packaging/icons/make-icons.py

Output is split by who consumes it: the two files the app itself carries go to
VirtDeck.Avalonia/Assets/, and the freedesktop icon theme a Linux package
installs goes to packaging/icons/hicolor/. Rasterising is done here rather than
by shelling out to rsvg/ImageMagick so that regenerating needs nothing but a
stock Python: the CI runners have no SVG rasteriser installed and the icon must
not become a build-time dependency.

Coordinates below are in a 0..1 unit square and scaled per size; anti-aliasing
comes from the signed distance to each shape, so the small sizes stay crisp.
"""

import struct
import zlib
from pathlib import Path

# --- geometry (unit square) ------------------------------------------------

TILE_RADIUS = 0.22
BACK_CARD = (0.325, 0.165, 0.845, 0.545)
FRONT_CARD = (0.155, 0.335, 0.675, 0.715)
CARD_RADIUS = 0.055
# The front card is separated from the back one by a gap of tile colour rather
# than a stroke, so the two never merge into one blob at 16px.
CARD_GAP = 0.035

# --- colours ---------------------------------------------------------------

TILE_TOP = (0x2E, 0x7D, 0xD1)
TILE_BOTTOM = (0x1A, 0x4A, 0x87)
CARD = (0xFF, 0xFF, 0xFF)
BACK_CARD_ALPHA = 0.42

HICOLOR_SIZES = (16, 24, 32, 48, 64, 128, 256, 512)
# Frames Windows picks from. The large ones are PNG-compressed (mandatory at
# 256, and it keeps the .ico ~25 KB instead of ~110 KB); the small ones stay
# plain DIBs, which is what every ICO reader handles.
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
ICO_PNG_FROM = 128
# The one size the app embeds for its window/taskbar icon.
APP_PNG_SIZE = 256

ROOT = Path(__file__).resolve().parents[2]
ASSETS_DIR = ROOT / "VirtDeck.Avalonia" / "Assets"
HICOLOR_DIR = ROOT / "packaging" / "icons" / "hicolor"


def rounded_rect_sdf(px, py, x0, y0, x1, y1, r):
    """Signed distance from (px, py) to a rounded rectangle; negative = inside."""
    hw, hh = (x1 - x0) / 2.0, (y1 - y0) / 2.0
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    qx = abs(px - cx) - (hw - r)
    qy = abs(py - cy) - (hh - r)
    outside = (max(qx, 0.0) ** 2 + max(qy, 0.0) ** 2) ** 0.5
    return outside + min(max(qx, qy), 0.0) - r


def coverage(sdf):
    """Distance in pixels -> 0..1 alpha. The 0.5 offset centres the edge."""
    return min(max(0.5 - sdf, 0.0), 1.0)


def render(size):
    """Render the icon at `size` px; returns straight (non-premultiplied) RGBA bytes."""
    s = float(size)
    tile = tuple(v * s for v in (0.0, 0.0, 1.0, 1.0))
    back = tuple(v * s for v in BACK_CARD)
    front = tuple(v * s for v in FRONT_CARD)
    gap_rect = (front[0] - CARD_GAP * s, front[1] - CARD_GAP * s,
                front[2] + CARD_GAP * s, front[3] + CARD_GAP * s)
    tile_r, card_r, gap_r = TILE_RADIUS * s, CARD_RADIUS * s, (CARD_RADIUS + CARD_GAP) * s

    rows = bytearray()
    for y in range(size):
        py = y + 0.5
        # Vertical gradient across the tile, evaluated once per row.
        t = py / s
        tile_rgb = tuple(TILE_TOP[i] + (TILE_BOTTOM[i] - TILE_TOP[i]) * t for i in range(3))
        rows.append(0)  # PNG filter type 0 (None)
        for x in range(size):
            px = x + 0.5
            a = coverage(rounded_rect_sdf(px, py, *tile, tile_r))
            if a <= 0.0:
                rows += b"\x00\x00\x00\x00"
                continue

            r, g, b = tile_rgb
            # Back card, then the tile-coloured gap, then the front card. Each
            # is composited over what is already there ("source over").
            for rect, radius, colour, alpha in (
                (back, card_r, CARD, BACK_CARD_ALPHA),
                (gap_rect, gap_r, tile_rgb, 1.0),
                (front, card_r, CARD, 1.0),
            ):
                ca = coverage(rounded_rect_sdf(px, py, *rect, radius)) * alpha
                if ca > 0.0:
                    r = colour[0] * ca + r * (1.0 - ca)
                    g = colour[1] * ca + g * (1.0 - ca)
                    b = colour[2] * ca + b * (1.0 - ca)

            rows += bytes((int(r + 0.5), int(g + 0.5), int(b + 0.5), int(a * 255 + 0.5)))
    return bytes(rows)


def png(size, raw):
    """Wrap filtered scanlines in a minimal 8-bit RGBA PNG."""
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def dib(size, raw):
    """ICO frame as a bottom-up 32bpp BITMAPINFOHEADER image + empty AND mask."""
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    stride = size * 4 + 1  # rendered rows carry a leading PNG filter byte
    pixels = bytearray()
    for y in range(size - 1, -1, -1):  # DIB rows run bottom-to-top
        row = raw[y * stride + 1: (y + 1) * stride]
        for x in range(size):
            r, g, b, a = row[x * 4: x * 4 + 4]
            pixels += bytes((b, g, r, a))  # BGRA
    mask_stride = ((size + 31) // 32) * 4  # 1bpp, rows padded to 4 bytes
    return header + bytes(pixels) + bytes(mask_stride * size)


def ico(frames):
    """frames: [(size, image_bytes, is_png)] -> a Windows .ico."""
    out = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries, blobs = b"", b""
    for size, data, _ in frames:
        # 256 is stored as 0 in the directory (the field is a single byte).
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return out + entries + blobs


def svg():
    x0, y0, x1, y1 = (v * 256 for v in BACK_CARD)
    fx0, fy0, fx1, fy1 = (v * 256 for v in FRONT_CARD)
    g = CARD_GAP * 256
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!-- Generated by packaging/icons/make-icons.py - edit the geometry there, not here. -->
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#{TILE_TOP[0]:02X}{TILE_TOP[1]:02X}{TILE_TOP[2]:02X}"/>
      <stop offset="1" stop-color="#{TILE_BOTTOM[0]:02X}{TILE_BOTTOM[1]:02X}{TILE_BOTTOM[2]:02X}"/>
    </linearGradient>
  </defs>
  <rect width="256" height="256" rx="{TILE_RADIUS * 256:g}" fill="url(#tile)"/>
  <rect x="{x0:g}" y="{y0:g}" width="{x1 - x0:g}" height="{y1 - y0:g}"
        rx="{CARD_RADIUS * 256:g}" fill="#FFFFFF" fill-opacity="{BACK_CARD_ALPHA}"/>
  <!-- The gap is painted in tile colour so the cards stay separate at any size. -->
  <rect x="{fx0 - g:g}" y="{fy0 - g:g}" width="{fx1 - fx0 + 2 * g:g}" height="{fy1 - fy0 + 2 * g:g}"
        rx="{(CARD_RADIUS + CARD_GAP) * 256:g}" fill="url(#tile)"/>
  <rect x="{fx0:g}" y="{fy0:g}" width="{fx1 - fx0:g}" height="{fy1 - fy0:g}"
        rx="{CARD_RADIUS * 256:g}" fill="#FFFFFF"/>
</svg>
"""


def main():
    sizes = sorted(set(HICOLOR_SIZES) | set(ICO_SIZES) | {APP_PNG_SIZE})
    rendered = {size: render(size) for size in sizes}
    written = []

    def write(path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data) if isinstance(data, bytes) else path.write_text(data, encoding="utf-8")
        written.append(path.relative_to(ROOT))

    # What the app carries: one PNG for the window icon, one .ico for the Windows exe.
    write(ASSETS_DIR / f"virtdeck-{APP_PNG_SIZE}.png", png(APP_PNG_SIZE, rendered[APP_PNG_SIZE]))
    frames = [(size,
               png(size, rendered[size]) if size >= ICO_PNG_FROM else dib(size, rendered[size]),
               size >= ICO_PNG_FROM)
              for size in ICO_SIZES]
    write(ASSETS_DIR / "virtdeck.ico", ico(frames))

    # What a Linux package installs into /usr/share/icons/.
    for size in HICOLOR_SIZES:
        write(HICOLOR_DIR / f"{size}x{size}" / "apps" / "virtdeck.png", png(size, rendered[size]))
    write(HICOLOR_DIR / "scalable" / "apps" / "virtdeck.svg", svg())

    for path in written:
        print(f"wrote {path}")


if __name__ == "__main__":
    main()

"""Generates the application icon.

The icon is drawn here rather than checked in as an opaque binary so that it can be adjusted,
reviewed in a diff, and regenerated at any size. It has no third-party dependencies: Pillow is
not installed on every machine that might build this, and an icon is not a good reason to add a
dependency.

    python tools/make_icon.py

Writes src/VoiceTranscript.App/icon.ico.

The design has to survive 16x16, because that is the size that matters most: this application
lives in the notification area and that tray slot is the only thing most people will ever see of
it. Detail is therefore spent on nothing — a solid tile in a blue that reads on both a light and
a dark taskbar, and a few chunky bars that still say "sound" when each one is two pixels wide.
"""

from __future__ import annotations

import os
import struct
import zlib

# Matches MeBrush in the application theme, so the icon and the speaker stripe agree.
TILE = (0x0F, 0x6C, 0xBD)
INK = (0xFF, 0xFF, 0xFF)

SIZES = [16, 20, 24, 32, 48, 64, 128, 256]

# Supersampling factor. Coverage is averaged down from this, which is what gives the curves
# their smooth edges without any drawing library.
SS = 8

# Geometry in unit coordinates, so it scales exactly.
CORNER_RADIUS = 0.215

# Two drawings, not one scaled drawing.
#
# Five bars at 16 pixels come out 1.4 pixels wide with a 0.9 pixel gap: the outer pair fades to
# nothing and the centre one lands between pixels, so the icon reads as a smudge. Below 32 the
# same idea is therefore drawn with three fatter bars, which survives. This is ordinary icon
# practice — the small sizes are their own artwork, not a resampling of the large one.
LARGE = {"heights": [0.30, 0.56, 0.86, 0.56, 0.30], "width": 0.088, "gap": 0.056}
SMALL = {"heights": [0.44, 0.80, 0.44], "width": 0.140, "gap": 0.100}

SMALL_UP_TO = 24


def _rounded_rect_contains(x: float, y: float, radius: float) -> bool:
    """Whether a unit-square point lies inside a rounded square inset slightly from the edge."""
    inset = 0.045
    lo, hi = inset, 1.0 - inset

    if not (lo <= x <= hi and lo <= y <= hi):
        return False

    # Only the four corner boxes need the circle test.
    cx = lo + radius if x < lo + radius else (hi - radius if x > hi - radius else x)
    cy = lo + radius if y < lo + radius else (hi - radius if y > hi - radius else y)

    dx, dy = x - cx, y - cy
    return dx * dx + dy * dy <= radius * radius


def _bar_spans(shape: dict) -> list[tuple[float, float, float, float]]:
    """The bars as (x0, x1, y0, y1) in unit coordinates, centred as a group."""
    heights, width, gap = shape["heights"], shape["width"], shape["gap"]
    total = len(heights) * width + (len(heights) - 1) * gap
    x = (1.0 - total) / 2.0

    spans = []
    for height in heights:
        y0 = (1.0 - height) / 2.0
        spans.append((x, x + width, y0, y0 + height))
        x += width + gap

    return spans


def _bar_contains(x: float, y: float, spans, radius: float) -> bool:
    """Rounded-end bars: square ends look broken at small sizes, round ones read as a level meter."""
    for x0, x1, y0, y1 in spans:
        if not (x0 <= x <= x1):
            continue

        cx = (x0 + x1) / 2.0
        top, bottom = y0 + radius, y1 - radius

        if top <= y <= bottom:
            return True

        cy = top if y < top else bottom
        dx, dy = x - cx, y - cy
        if dx * dx + dy * dy <= radius * radius:
            return True

    return False


def render(size: int) -> bytes:
    """Renders one square RGBA bitmap, top row first."""
    shape = SMALL if size <= SMALL_UP_TO else LARGE
    spans = _bar_spans(shape)
    bar_radius = shape["width"] / 2.0
    n = size * SS
    step = 1.0 / n

    rows = bytearray()

    for py in range(size):
        row = bytearray()
        for px in range(size):
            tile_hits = 0
            ink_hits = 0

            for sy in range(SS):
                y = (py * SS + sy + 0.5) * step
                for sx in range(SS):
                    x = (px * SS + sx + 0.5) * step

                    if not _rounded_rect_contains(x, y, CORNER_RADIUS):
                        continue

                    tile_hits += 1
                    if _bar_contains(x, y, spans, bar_radius):
                        ink_hits += 1

            samples = SS * SS
            alpha = tile_hits / samples

            if tile_hits == 0:
                row += bytes((0, 0, 0, 0))
                continue

            # Blend ink over tile by coverage, then premultiply nothing: ICO wants straight alpha.
            ink_ratio = ink_hits / tile_hits
            colour = tuple(
                round(TILE[i] * (1.0 - ink_ratio) + INK[i] * ink_ratio) for i in range(3)
            )
            row += bytes((colour[0], colour[1], colour[2], round(alpha * 255)))

        rows += row

    return bytes(rows)


def _png(size: int, rgba: bytes) -> bytes:
    """Minimal PNG encoder. ICO has allowed PNG-compressed entries since Windows Vista."""

    def chunk(tag: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + tag
            + payload
            + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)
        )

    stride = size * 4
    raw = bytearray()
    for y in range(size):
        raw.append(0)  # filter type 0 (none)
        raw += rgba[y * stride : (y + 1) * stride]

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)  # 8-bit RGBA

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def build_ico(path: str) -> None:
    images = [(size, _png(size, render(size))) for size in SIZES]

    directory = bytearray()
    offset = 6 + 16 * len(images)
    payload = bytearray()

    for size, data in images:
        directory += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,  # 0 means 256
            0 if size >= 256 else size,
            0,  # palette size: 0 for true colour
            0,  # reserved
            1,  # colour planes
            32,  # bits per pixel
            len(data),
            offset,
        )
        payload += data
        offset += len(data)

    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(images)))
        f.write(bytes(directory))
        f.write(bytes(payload))


if __name__ == "__main__":
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    target = os.path.join(here, "src", "VoiceTranscript.App", "icon.ico")

    build_ico(target)
    print(f"{target} yazildi ({os.path.getsize(target):,} bayt, {len(SIZES)} boyut)")

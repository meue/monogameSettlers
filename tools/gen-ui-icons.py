#!/usr/bin/env python3
"""Regenerates the game's own UI icon assets in data/ui-icons/ (no original assets involved).

  tools/gen-ui-icons.py            # write data/ui-icons/work-area.png (relative to the repo root)

work-area.png: the building panel's work-area button, 30x30 rings in spectral colors, every ring
edged dark. Byte-identical to the runtime circle drawing it replaces: the center distances
dx = x + 0.5 - size/2 are exact half-integers (never 0), so dx*dx + dy*dy is always an exact
"integer + 0.5" below 2^23, and no threshold squared (210.25, 169, 121, ...) is of that shape,
which keeps every comparison stable between 32-bit float and Python's double.
"""

import math
import struct
import sys
import zlib
from pathlib import Path

SIZE = 30
EDGE = (40, 30, 15, 255)
TRANSPARENT = (0, 0, 0, 0)


def work_area_pixel(x, y):
    dx = x + 0.5 - SIZE / 2.0
    dy = y + 0.5 - SIZE / 2.0
    d = math.sqrt(dx * dx + dy * dy)
    for limit, color in (
        (14.5, TRANSPARENT),
        (13.0, EDGE),
        (11.0, (205, 60, 25, 255)),
        (10.0, EDGE),
        (8.0, (245, 190, 40, 255)),
        (7.0, EDGE),
        (5.0, (70, 165, 60, 255)),
        (4.0, EDGE),
        (2.2, (50, 110, 200, 255)),
    ):
        if d > limit:
            return color
    return (205, 160, 245, 255)


def chunk(tag, data):
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))


def write_png(path, size, pixel):
    raw = b"".join(
        b"\x00" + bytes(component for x in range(size) for component in pixel(x, y))
        for y in range(size)
    )
    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )
    Path(path).write_bytes(png)
    print(f"{path}: {size}x{size}, {len(png)} bytes")


def main():
    root = Path(__file__).resolve().parent.parent
    icons = root / "data" / "ui-icons"
    icons.mkdir(parents=True, exist_ok=True)
    write_png(icons / "work-area.png", SIZE, work_area_pixel)


if __name__ == "__main__":
    sys.exit(main())

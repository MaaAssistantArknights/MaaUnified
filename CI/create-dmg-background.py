#!/usr/bin/env python3
"""Generate the Finder background used by the macOS DMG installer."""

from __future__ import annotations

import os
import struct
import sys
import zlib


WIDTH = 640
HEIGHT = 420

BACKGROUND = (247, 248, 250)
TEXT = (42, 48, 58)
MUTED = (93, 104, 119)
ACCENT = (48, 112, 192)
ACCENT_SOFT = (225, 235, 248)
COMMAND_BG = (255, 255, 255)
COMMAND_BORDER = (201, 211, 224)
ARROW = (72, 127, 203)


FONT = {
    "A": ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
    "B": ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
    "C": ["01111", "10000", "10000", "10000", "10000", "10000", "01111"],
    "D": ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
    "E": ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
    "F": ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
    "G": ["01111", "10000", "10000", "10011", "10001", "10001", "01111"],
    "H": ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
    "I": ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
    "J": ["00111", "00010", "00010", "00010", "00010", "10010", "01100"],
    "K": ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
    "L": ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
    "M": ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
    "N": ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
    "O": ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    "P": ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
    "Q": ["01110", "10001", "10001", "10001", "10101", "10010", "01101"],
    "R": ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
    "S": ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
    "T": ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    "U": ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
    "V": ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
    "W": ["10001", "10001", "10001", "10101", "10101", "10101", "01010"],
    "X": ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
    "Y": ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
    "Z": ["11111", "00001", "00010", "00100", "01000", "10000", "11111"],
    "0": ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
    "1": ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
    "2": ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
    "3": ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
    "4": ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
    "5": ["11111", "10000", "10000", "11110", "00001", "00001", "11110"],
    "6": ["01110", "10000", "10000", "11110", "10001", "10001", "01110"],
    "7": ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
    "8": ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
    "9": ["01110", "10001", "10001", "01111", "00001", "00001", "01110"],
    ".": ["00000", "00000", "00000", "00000", "00000", "01100", "01100"],
    ",": ["00000", "00000", "00000", "00000", "01100", "00100", "01000"],
    ":": ["00000", "01100", "01100", "00000", "01100", "01100", "00000"],
    "-": ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
    "/": ["00001", "00010", "00010", "00100", "01000", "01000", "10000"],
    "\"": ["01010", "01010", "01010", "00000", "00000", "00000", "00000"],
    " ": ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
}


def make_canvas() -> list[list[tuple[int, int, int]]]:
    return [[BACKGROUND for _ in range(WIDTH)] for _ in range(HEIGHT)]


def rect(canvas: list[list[tuple[int, int, int]]], x: int, y: int, w: int, h: int, color: tuple[int, int, int]) -> None:
    for yy in range(max(0, y), min(HEIGHT, y + h)):
        row = canvas[yy]
        for xx in range(max(0, x), min(WIDTH, x + w)):
            row[xx] = color


def border(canvas: list[list[tuple[int, int, int]]], x: int, y: int, w: int, h: int, color: tuple[int, int, int]) -> None:
    rect(canvas, x, y, w, 1, color)
    rect(canvas, x, y + h - 1, w, 1, color)
    rect(canvas, x, y, 1, h, color)
    rect(canvas, x + w - 1, y, 1, h, color)


def triangle(canvas: list[list[tuple[int, int, int]]], points: list[tuple[int, int]], color: tuple[int, int, int]) -> None:
    min_y = max(0, min(y for _, y in points))
    max_y = min(HEIGHT - 1, max(y for _, y in points))
    for y in range(min_y, max_y + 1):
        xs: list[float] = []
        for i, (x1, y1) in enumerate(points):
            x2, y2 = points[(i + 1) % len(points)]
            if y1 == y2:
                continue
            if min(y1, y2) <= y < max(y1, y2):
                xs.append(x1 + (y - y1) * (x2 - x1) / (y2 - y1))
        if len(xs) < 2:
            continue
        xs.sort()
        rect(canvas, int(xs[0]), y, int(xs[-1] - xs[0]) + 1, 1, color)


def text_width(text: str, scale: int) -> int:
    return sum((len(FONT.get(ch, FONT[" "])[0]) + 1) * scale for ch in text.upper()) - scale


def draw_text(
    canvas: list[list[tuple[int, int, int]]],
    x: int,
    y: int,
    text: str,
    color: tuple[int, int, int],
    scale: int,
) -> None:
    cursor = x
    for ch in text.upper():
        glyph = FONT.get(ch, FONT[" "])
        for row_index, row in enumerate(glyph):
            for col_index, pixel in enumerate(row):
                if pixel == "1":
                    rect(canvas, cursor + col_index * scale, y + row_index * scale, scale, scale, color)
        cursor += (len(glyph[0]) + 1) * scale


def centered_text(canvas: list[list[tuple[int, int, int]]], y: int, text: str, color: tuple[int, int, int], scale: int) -> None:
    draw_text(canvas, (WIDTH - text_width(text, scale)) // 2, y, text, color, scale)


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)


def write_png(path: str, canvas: list[list[tuple[int, int, int]]]) -> None:
    raw = bytearray()
    for row in canvas:
        raw.append(0)
        for pixel in row:
            raw.extend(pixel)

    data = b"\x89PNG\r\n\x1a\n"
    data += png_chunk(b"IHDR", struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 2, 0, 0, 0))
    data += png_chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    data += png_chunk(b"IEND", b"")

    output_dir = os.path.dirname(path)
    if output_dir:
        os.makedirs(output_dir, exist_ok=True)
    with open(path, "wb") as output:
        output.write(data)


def main() -> int:
    if len(sys.argv) != 2:
        print(f"Usage: {sys.argv[0]} <output-png>", file=sys.stderr)
        return 1

    canvas = make_canvas()

    rect(canvas, 0, 0, WIDTH, 92, ACCENT_SOFT)
    centered_text(canvas, 28, "DRAG TO APPLICATIONS", TEXT, 3)
    centered_text(canvas, 66, "MAAUNIFIED.APP", MUTED, 2)

    rect(canvas, 252, 196, 136, 12, ARROW)
    triangle(canvas, [(388, 178), (388, 226), (430, 202)], ARROW)

    rect(canvas, 178, 346, 430, 48, COMMAND_BG)
    border(canvas, 178, 346, 430, 48, COMMAND_BORDER)
    centered_text(canvas, 312, "IF MACOS SAYS \"DAMAGED\", SEE INSTALL HELP.TXT", MUTED, 1)
    centered_text(canvas, 360, "XATTR -DR COM.APPLE.QUARANTINE \"/APPLICATIONS/MAAUNIFIED.APP\"", ACCENT, 1)

    write_png(sys.argv[1], canvas)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

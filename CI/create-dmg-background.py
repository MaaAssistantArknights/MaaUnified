#!/usr/bin/env python3
"""Generate the Finder background used by the macOS DMG installer."""

from __future__ import annotations

import os
import shutil
import struct
import subprocess
import sys
import tempfile
import textwrap
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


def render_with_appkit(path: str) -> bool:
    if sys.platform != "darwin" or shutil.which("swift") is None:
        return False

    swift_source = textwrap.dedent(
        r'''
        import AppKit

        let outputPath = CommandLine.arguments[1]
        let width: CGFloat = 640
        let height: CGFloat = 420

        let background = NSColor(calibratedRed: 247.0 / 255.0, green: 248.0 / 255.0, blue: 250.0 / 255.0, alpha: 1)
        let text = NSColor(calibratedRed: 42.0 / 255.0, green: 48.0 / 255.0, blue: 58.0 / 255.0, alpha: 1)
        let muted = NSColor(calibratedRed: 93.0 / 255.0, green: 104.0 / 255.0, blue: 119.0 / 255.0, alpha: 1)
        let accent = NSColor(calibratedRed: 48.0 / 255.0, green: 112.0 / 255.0, blue: 192.0 / 255.0, alpha: 1)
        let accentSoft = NSColor(calibratedRed: 225.0 / 255.0, green: 235.0 / 255.0, blue: 248.0 / 255.0, alpha: 1)
        let commandBackground = NSColor.white
        let commandBorder = NSColor(calibratedRed: 201.0 / 255.0, green: 211.0 / 255.0, blue: 224.0 / 255.0, alpha: 1)

        func rect(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat, _ color: NSColor) {
            color.setFill()
            NSBezierPath(rect: NSRect(x: x, y: height - y - h, width: w, height: h)).fill()
        }

        func strokeRect(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat, _ color: NSColor) {
            color.setStroke()
            let path = NSBezierPath(rect: NSRect(x: x, y: height - y - h, width: w, height: h))
            path.lineWidth = 1
            path.stroke()
        }

        func drawText(
            _ value: String,
            x: CGFloat,
            y: CGFloat,
            width: CGFloat,
            size: CGFloat,
            weight: NSFont.Weight = .regular,
            color: NSColor = text,
            alignment: NSTextAlignment = .center
        ) {
            let paragraph = NSMutableParagraphStyle()
            paragraph.alignment = alignment
            let attrs: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: size, weight: weight),
                .foregroundColor: color,
                .paragraphStyle: paragraph,
            ]
            let lineHeight = size * 1.45
            let rect = NSRect(x: x, y: height - y - lineHeight, width: width, height: lineHeight)
            (value as NSString).draw(with: rect, options: [.usesLineFragmentOrigin], attributes: attrs)
        }

        let image = NSImage(size: NSSize(width: width, height: height))
        image.lockFocus()

        rect(0, 0, width, height, background)
        rect(0, 0, width, 106, accentSoft)
        drawText("拖到 Applications / Drag to Applications", x: 20, y: 12, width: 600, size: 20, weight: .semibold)
        drawText("将 MAAUnified.app 拖到右侧 Applications 文件夹", x: 20, y: 42, width: 600, size: 11, color: muted)
        drawText("Drag MAAUnified.app to the Applications folder", x: 20, y: 57, width: 600, size: 11, color: muted)
        drawText("MAAUnified.app を Applications フォルダへドラッグ", x: 20, y: 72, width: 600, size: 11, color: muted)
        drawText("MAAUnified.app을 Applications 폴더로 드래그", x: 20, y: 87, width: 600, size: 11, color: muted)

        rect(230, 162, 140, 12, accent)
        let arrow = NSBezierPath()
        arrow.move(to: NSPoint(x: 410, y: height - 168))
        arrow.line(to: NSPoint(x: 370, y: height - 144))
        arrow.line(to: NSPoint(x: 370, y: height - 192))
        arrow.close()
        accent.setFill()
        arrow.fill()

        let commandBoxWidth: CGFloat = 430
        let commandBoxX: CGFloat = 24
        rect(commandBoxX, 260, commandBoxWidth, 106, commandBackground)
        strokeRect(commandBoxX, 260, commandBoxWidth, 106, commandBorder)
        drawText("如果打开时提示“已损坏”，双击右侧说明文件图标复制命令", x: commandBoxX + 14, y: 270, width: commandBoxWidth - 28, size: 10.8, color: muted, alignment: .right)
        drawText("If macOS says “damaged”, double-click the note icon to copy the command", x: commandBoxX + 14, y: 289, width: commandBoxWidth - 28, size: 10.8, color: muted, alignment: .right)
        drawText("「壊れている」と表示されたら、右のメモアイコンからコピー", x: commandBoxX + 14, y: 308, width: commandBoxWidth - 28, size: 10.8, color: muted, alignment: .right)
        drawText("“손상됨” 경고가 나오면 오른쪽 안내 아이콘에서 명령을 복사", x: commandBoxX + 14, y: 327, width: commandBoxWidth - 28, size: 10.8, color: muted, alignment: .right)

        image.unlockFocus()

        guard let tiff = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff),
              let data = bitmap.representation(using: .png, properties: [:]) else {
            exit(2)
        }

        try data.write(to: URL(fileURLWithPath: outputPath))
        '''
    )

    with tempfile.NamedTemporaryFile("w", suffix=".swift", delete=False, encoding="utf-8") as temp_file:
        temp_file.write(swift_source)
        temp_path = temp_file.name
    module_cache_dir = tempfile.mkdtemp(prefix="maaunified-swift-cache-")

    try:
        env = os.environ.copy()
        env["CLANG_MODULE_CACHE_PATH"] = module_cache_dir
        env["SWIFT_MODULECACHE_PATH"] = module_cache_dir
        result = subprocess.run(
            ["swift", temp_path, path],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=env,
        )
        return result.returncode == 0
    finally:
        os.unlink(temp_path)
        shutil.rmtree(module_cache_dir, ignore_errors=True)


def main() -> int:
    if len(sys.argv) != 2:
        print(f"Usage: {sys.argv[0]} <output-png>", file=sys.stderr)
        return 1

    if render_with_appkit(sys.argv[1]):
        return 0

    canvas = make_canvas()

    rect(canvas, 0, 0, WIDTH, 92, ACCENT_SOFT)
    centered_text(canvas, 28, "DRAG MAAUNIFIED.APP TO APPLICATIONS", TEXT, 2)
    centered_text(canvas, 66, "IF MACOS SHOWS A DAMAGED WARNING, RUN THE TERMINAL COMMAND BELOW", MUTED, 1)

    rect(canvas, 250, 182, 140, 12, ARROW)
    triangle(canvas, [(390, 164), (390, 212), (430, 188)], ARROW)

    rect(canvas, 142, 320, 470, 74, COMMAND_BG)
    border(canvas, 142, 320, 470, 74, COMMAND_BORDER)
    centered_text(canvas, 296, "IF MACOS SAYS \"DAMAGED\"", MUTED, 1)
    centered_text(canvas, 318, "RUN THIS IN TERMINAL:", TEXT, 1)
    centered_text(canvas, 352, "XATTR -DR COM.APPLE.QUARANTINE \"/APPLICATIONS/MAAUNIFIED.APP\"", ACCENT, 1)

    write_png(sys.argv[1], canvas)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""RawInspector の虫眼鏡カーソル（.cur）を生成します。

WPF の標準カーソルに虫眼鏡はありません。外部の素材も使わないので、ここで描きます。

.cur は .ico とほぼ同じ構造で、違いは2か所だけです。
  - ヘッダの種別が 1（アイコン）ではなく 2（カーソル）
  - 各エントリの「プレーン数 / ビット数」の位置が、ホットスポットの X / Y になる
そのため Pillow で .ico として書き出してから、その2か所を上書きします。

使い方:
    python tools/make_cursor.py
    python tools/make_cursor.py --png-preview generated/cursor_preview.png
"""

from __future__ import annotations

import argparse
import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 32

# 暗い背景でも明るい背景でも見えるよう、白い線に濃い縁を付けます。
INK = (255, 255, 255, 255)
EDGE = (0, 0, 0, 190)


def draw_magnifier() -> tuple[Image.Image, tuple[int, int]]:
    """虫眼鏡を描き、画像とホットスポット（レンズの中心）を返します。"""
    scale = 8
    s = SIZE * scale
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # レンズは左上寄りに置きます。ホットスポットがレンズ中心になるので、
    # 中心が画像の端に寄りすぎないぎりぎりの位置にします。
    radius = int(s * 0.30)
    cx = int(s * 0.36)
    cy = int(s * 0.36)
    box = (cx - radius, cy - radius, cx + radius, cy + radius)

    ring = max(scale, int(s * 0.055))
    edge = max(scale, int(ring * 0.55))

    # 取っ手（縁を先に太く描き、その上に白を重ねます）
    start = (cx + int(radius * 0.72), cy + int(radius * 0.72))
    end = (int(s * 0.92), int(s * 0.92))
    draw.line((start, end), fill=EDGE, width=int(ring * 1.9) + edge)
    draw.line((start, end), fill=INK, width=int(ring * 1.5))

    # レンズの縁（外側と内側に濃い線を置いて、白いリングを挟みます）
    draw.ellipse(
        (box[0] - edge, box[1] - edge, box[2] + edge, box[3] + edge),
        outline=EDGE, width=ring + edge * 2)
    draw.ellipse(box, outline=INK, width=ring)

    return image.resize((SIZE, SIZE), Image.LANCZOS), (SIZE * 36 // 100, SIZE * 36 // 100)


def to_cur(image: Image.Image, hotspot: tuple[int, int]) -> bytes:
    """Pillow の .ico 出力を .cur へ書き換えます。"""
    buffer = io.BytesIO()
    image.save(buffer, format="ICO", sizes=[(SIZE, SIZE)])
    data = bytearray(buffer.getvalue())

    # ICONDIR: reserved(2) / type(2) / count(2)
    struct.pack_into("<H", data, 2, 2)  # 1=アイコン → 2=カーソル

    # ICONDIRENTRY の 5〜8バイト目が、カーソルではホットスポット X / Y になります。
    entry = 6
    struct.pack_into("<HH", data, entry + 4, hotspot[0], hotspot[1])

    return bytes(data)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default="apps/RawInspector/zoom.cur")
    parser.add_argument("--png-preview", default="")
    args = parser.parse_args()

    repository_root = Path(__file__).resolve().parent.parent
    out_path = (repository_root / args.out).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)

    image, hotspot = draw_magnifier()
    out_path.write_bytes(to_cur(image, hotspot))
    print(f"書き出しました: {out_path}  {SIZE}x{SIZE}  ホットスポット {hotspot}")

    if args.png_preview:
        preview_path = (repository_root / args.png_preview).resolve()
        # 明るい背景と暗い背景の両方で、等倍と拡大を並べます。
        cell = 160
        sheet = Image.new("RGBA", (cell * 2, cell), (255, 255, 255, 255))
        sheet.paste(Image.new("RGBA", (cell, cell), (32, 32, 32, 255)), (cell, 0))
        big = image.resize((128, 128), Image.NEAREST)
        sheet.paste(big, (16, 16), big)
        sheet.paste(big, (cell + 16, 16), big)
        sheet.paste(image, (4, 4), image)
        sheet.paste(image, (cell + 4, 4), image)
        sheet.save(preview_path)
        print(f"確認用PNG: {preview_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

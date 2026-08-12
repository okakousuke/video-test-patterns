#!/usr/bin/env python3
"""RawInspector のアプリアイコン（.ico）を生成します。

図案は「カラーバー＋ルーペ」です。このリポジトリが作るものがカラーバーで、
ビューアの役目が拡大して画素を確かめることなので、その2つを重ねています。

外部の素材は使わず、ここのコードだけで描きます。
16px でも潰れないよう、小さいサイズではバーの本数を減らし、ルーペの線を太くします。

使い方:
    python tools/make_icon.py
    python tools/make_icon.py --out apps/RawInspector/app.ico
"""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw

# SMPTE 風のカラーバー（白・黄・シアン・緑・マゼンタ・赤・青・黒）
BARS = (
    (255, 255, 255),
    (255, 255, 0),
    (0, 255, 255),
    (0, 255, 0),
    (255, 0, 255),
    (255, 0, 0),
    (0, 0, 255),
    (16, 16, 16),
)

# 16px では8本入れると1本あたり1pxを割って灰色に潰れます。
# 並び順は保ったまま間引き、黄と赤（いちばんカラーバーらしい色）を残します。
SMALL_BARS = (BARS[0], BARS[1], BARS[2], BARS[5])

SIZES = (16, 24, 32, 48, 64, 128, 256)

BACKGROUND = (24, 26, 32, 255)
LENS_EDGE = (255, 255, 255, 255)
LENS_SHADOW = (0, 0, 0, 170)


def draw_icon(size: int) -> Image.Image:
    """1サイズぶん描きます。小さいサイズでは要素を減らして潰れを避けます。"""
    # 4倍で描いてから縮小します。円と斜めの線のふちを滑らかにするためです。
    scale = 4
    s = size * scale
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    radius = max(2, int(s * 0.16))
    draw.rounded_rectangle((0, 0, s - 1, s - 1), radius=radius, fill=BACKGROUND)

    # 24px以下はバーを間引き、ルーペも省きます。
    # 小さいサイズでルーペを入れると、レンズ内が1色で埋まってただの丸に見えます。
    bars = BARS if size >= 32 else SMALL_BARS
    with_lens = size >= 32

    margin = int(s * 0.11)
    left_edge, top_edge = margin, margin
    inner_width = s - margin * 2
    inner_height = s - margin * 2
    bar_width = inner_width / len(bars)

    for index, color in enumerate(bars):
        x0 = left_edge + index * bar_width
        draw.rectangle((x0, top_edge, x0 + bar_width, top_edge + inner_height), fill=color + (255,))

    if not with_lens:
        # ルーペを入れる余地がないサイズは、カラーバーだけにします。
        return image.resize((size, size), Image.LANCZOS)

    lens_r = int(s * 0.27)
    cx, cy = int(s * 0.60), int(s * 0.60)
    lens_box = (cx - lens_r, cy - lens_r, cx + lens_r, cy + lens_r)
    ring = max(2, int(s * (0.070 if size < 32 else 0.050)))

    # レンズの中は、同じバーを拡大したものにします。
    # 「画素を拡大して確かめる道具」だと一目で分かるようにするためです。
    # 倍率を上げすぎるとレンズ内が1色で埋まり、ただの丸に見えてしまいます。
    # レンズの直径に縞が3本ほど入る倍率にします。
    zoom = 1.7
    lens_layer = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    lens_draw = ImageDraw.Draw(lens_layer)
    magnified = bar_width * zoom
    # レンズ中心の下に、拡大前と同じ色が来るように起点をずらします。
    origin = cx - (cx - left_edge) * zoom
    for index, color in enumerate(bars):
        x0 = origin + index * magnified
        lens_draw.rectangle((x0, 0, x0 + magnified, s), fill=color + (255,))

    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).ellipse(lens_box, fill=255)
    image.paste(lens_layer, (0, 0), mask)

    # 取っ手を先に描いて、リングで根元を隠します。
    handle_start = (cx + int(lens_r * 0.68), cy + int(lens_r * 0.68))
    handle_end = (int(s * 0.90), int(s * 0.90))
    draw.line((handle_start, handle_end), fill=LENS_SHADOW, width=int(ring * 1.9))
    draw.line((handle_start, handle_end), fill=LENS_EDGE, width=int(ring * 1.1))

    # 濃い縁はリングの外側へ置きます。同じ円に太い線を重ねると、
    # ふちが混ざってリング全体が灰色に見えてしまいます。
    outer = int(ring * 0.6)
    draw.ellipse(
        (lens_box[0] - outer, lens_box[1] - outer, lens_box[2] + outer, lens_box[3] + outer),
        outline=LENS_SHADOW, width=max(1, int(ring * 0.5)))
    draw.ellipse(lens_box, outline=LENS_EDGE, width=ring)

    return image.resize((size, size), Image.LANCZOS)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default="apps/RawInspector/app.ico")
    parser.add_argument("--png-preview", default="", help="確認用にPNGも書き出す場合のパス")
    args = parser.parse_args()

    repository_root = Path(__file__).resolve().parent.parent
    out_path = (repository_root / args.out).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)

    images = [draw_icon(size) for size in SIZES]
    images[-1].save(out_path, format="ICO", sizes=[(s, s) for s in SIZES])
    print(f"書き出しました: {out_path}  （{', '.join(f'{s}x{s}' for s in SIZES)}）")

    if args.png_preview:
        preview_path = (repository_root / args.png_preview).resolve()
        # 上段に等倍、下段に最近傍で引き伸ばしたものを並べます。
        # 等倍だけでは 16px や 24px が潰れているかどうか判断できないためです。
        gap = 12
        zoomed = [image.resize((128, 128), Image.NEAREST) for image in images]
        width = max(
            sum(image.width for image in images) + gap * (len(images) + 1),
            sum(image.width for image in zoomed) + gap * (len(zoomed) + 1),
        )
        top_height = max(image.height for image in images)
        height = top_height + 128 + gap * 3
        sheet = Image.new("RGBA", (width, height), (245, 246, 248, 255))

        x = gap
        for image in images:
            sheet.paste(image, (x, gap + top_height - image.height), image)
            x += image.width + gap

        x = gap
        for image in zoomed:
            sheet.paste(image, (x, gap * 2 + top_height), image)
            x += image.width + gap

        sheet.save(preview_path)
        print(f"確認用PNG: {preview_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

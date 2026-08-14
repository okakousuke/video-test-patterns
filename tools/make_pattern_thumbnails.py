#!/usr/bin/env python3
"""生成画面に出す、パターンの参考図（サムネイル）をまとめて作ります。

これは「押す前に、どんな形の絵かを見せる」ためのものです。実物ではありません。

**実寸で描いてから縮めます。** 小さい寸法で直接描くと絵そのものが変わります。
つまみの多くは画素で効くので（hatch の period=2px、dots の step=16px、
linepairs の widths=[1,2,3,4,6,8]px など）、640幅で描けば線の本数が 1/3 になり、
1920 で出てくる絵とは別物になります。実寸で描いてから縮めるかぎり、
少なくとも**構図と密度の比**は本番と同じです。

縮小は Lanczos です。細かすぎて出せないものは灰色に溶けます。
「見えない」ほうが「嘘の模様が見える」より正しい負け方だからです。

最近傍は論外として、面積平均（BOX）も実際に試して落としました。BOX は
止める力が弱く、zoneplate では**画面いっぱいに偽の同心円が残ります**
（1080p→480 で輝度の標準偏差 19.0、Lanczos なら 15.7。差はそのまま
出るはずのない模様の量です）。中心の読める範囲だけが残り、その外側が
灰色に溶ける Lanczos のほうが、この絵の見え方として正しいものです。

色は RGB / 4:4:4 / 8bit / full で固定します。ここで見たいのは形であって、
色差の間引きや range の効きではありません。それらは生成したあとに
ビューアで等倍にして見るものです。

生成条件はすべて既定値です。つまみを触ったときの絵は、画面側が
その場で生成して差し替えます（静止画は既定の姿だけを持ちます）。

使い方:
    python tools/make_pattern_thumbnails.py            # apps/RawInspector/thumbnails/ へ
    python tools/make_pattern_thumbnails.py --long 480 # 長辺を変える
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "src"))

import numpy as np  # noqa: E402
from PIL import Image  # noqa: E402

from vtp.patterns import PATTERN_NAMES, render  # noqa: E402

# 実寸。1080p を基準にします。ここを変えると構図の比も変わります。
SOURCE_WIDTH = 1920
SOURCE_HEIGHT = 1080


def build(pattern: str, long_side: int) -> Image.Image:
    rgb = render(pattern, SOURCE_WIDTH, SOURCE_HEIGHT, {})
    full = Image.fromarray(
        np.clip(np.rint(rgb * 255.0), 0, 255).astype(np.uint8), mode="RGB"
    )
    height = round(long_side * SOURCE_HEIGHT / SOURCE_WIDTH)
    return full.resize((long_side, height), Image.Resampling.LANCZOS)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", default=str(REPO / "apps" / "RawInspector" / "thumbnails"))
    parser.add_argument("--long", type=int, default=480, help="長辺の画素数")
    args = parser.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    total = 0
    for pattern in PATTERN_NAMES:
        path = out / f"{pattern}.png"
        # optimize は既定のエンコーダより時間を掛けて詰めます。
        # exe へ埋め込むので、1枚ぶんの差でも全体では効きます。
        build(pattern, args.long).save(path, format="PNG", optimize=True)
        size = path.stat().st_size
        total += size
        print(f"{pattern:16} {size:7,} bytes")

    print(f"\n{len(PATTERN_NAMES)} 枚 / 合計 {total:,} bytes -> {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

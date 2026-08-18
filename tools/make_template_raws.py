#!/usr/bin/env python3
"""一般的な映像確認用途の RAW + manifest を samples/raw に同梱します。

TG45/TG3K の資料から、機器固有の名称や数値を持ち出さずに一般化した
確認カテゴリだけを選びました。これは格納形式を網羅するセットではなく、
ビューアやデコーダの入力に使いやすい「用途別のひな形」です。

各パターンは、目的に合わせて代表的な格納形式を 1 つ選んでいます。
同じ画像を形式違いで比較する目的のセットは make_reference_raws.py を使います。

使い方:
    python tools/make_template_raws.py
    python tools/make_template_raws.py --out generated
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

from make_reference_raws import SIZES, build_command


# (公開用の識別名, パターン, 格納条件)
# 機器名・資料番号・実測値は含めず、汎用的な確認目的が分かる名前にする。
TEMPLATES = (
    ("raster_neutral_nv12", "raster", ("ycbcr", "4:2:0", 8, "nv12", "limited", "bt709", None)),
    ("colorbar_422_packed", "colorbar", ("ycbcr", "4:2:2", 8, "packed", "limited", "bt709", None)),
    ("graysteps_rgb10", "graysteps", ("rgb", "4:4:4", 10, "planar", "full", None, "lsb")),
    ("pluge_444_limited", "pluge", ("ycbcr", "4:4:4", 8, "planar", "limited", "bt709", None)),
    ("window_444_limited", "window", ("ycbcr", "4:4:4", 8, "planar", "limited", "bt709", None)),
    ("crosshair_rgb_packed", "crosshair", ("rgb", "4:4:4", 8, "packed", "full", None, None)),
    ("grid_rgb_planar", "grid", ("rgb", "4:4:4", 8, "planar", "full", None, None)),
    ("multiburst_v210", "multiburst", ("ycbcr", "4:2:2", 10, "v210", "limited", "bt709", None)),
    ("resolutioncard_rgb_packed", "resolutioncard", ("rgb", "4:4:4", 8, "packed", "full", None, None)),
    ("monoscope_nv12", "monoscope", ("ycbcr", "4:2:0", 8, "nv12", "limited", "bt709", None)),
    ("geometrycard_rgb_planar", "geometrycard", ("rgb", "4:4:4", 8, "planar", "full", None, None)),
    ("digitalcard_mipi10", "digitalcard", ("ycbcr", "4:4:4", 10, "mipi10", "limited", "bt709", None)),
    ("hatch_chroma_nv12", "hatch", ("ycbcr", "4:2:0", 8, "nv12", "limited", "bt709", None)),
    ("colormatrix_444_limited", "colormatrix", ("ycbcr", "4:4:4", 8, "planar", "limited", "bt709", None)),
    ("gamma_rgb_planar", "gamma", ("rgb", "4:4:4", 8, "planar", "full", None, None)),
)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default="samples/raw", help="出力先ディレクトリ")
    parser.add_argument("--size", choices=tuple(SIZES), default="small", help="画像サイズ")
    parser.add_argument("--dry-run", action="store_true", help="生成せず、予定だけ表示する")
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent
    environment = os.environ.copy()
    source_path = str(root / "src")
    environment["PYTHONPATH"] = source_path + os.pathsep + environment.get("PYTHONPATH", "")
    out_directory = (root / args.out).resolve()
    width, height = SIZES[args.size]
    print(f"出力先: {out_directory}")
    print(f"サイズ: {args.size} {width} x {height}")

    failures = 0
    total = 0
    for name, pattern, profile in TEMPLATES:
        out_base = out_directory / f"template_{name}"
        if args.dry_run:
            print(f"{out_base.name:<42} {pattern:<16} {profile}")
            continue

        out_directory.mkdir(parents=True, exist_ok=True)
        result = subprocess.run(
            build_command(pattern, profile, out_base, [], width, height),
            cwd=root, env=environment, capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
        if result.returncode != 0:
            failures += 1
            message = (result.stderr or result.stdout).strip().splitlines()
            print(f"失敗: {out_base.name}: {message[-1] if message else '(出力なし)'}")
            continue

        raw = out_base.with_suffix(".raw")
        total += raw.stat().st_size
        print(f"生成: {out_base.name} ({raw.stat().st_size:,} B)")

    if args.dry_run:
        print(f"{len(TEMPLATES)} 件を作る予定です。")
        return 0
    if failures:
        print(f"{len(TEMPLATES) - failures} 件成功、{failures} 件失敗しました。")
        return 1
    print(f"{len(TEMPLATES)} 件を生成しました。RAW 合計 {total / 1024 / 1024:.2f} MB。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

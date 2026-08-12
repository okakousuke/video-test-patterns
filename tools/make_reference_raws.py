#!/usr/bin/env python3
"""リポジトリに同梱する参照用の RAW と manifest を作ります。

`make_samples.py` が「パターンを一通り並べる」ためのものなのに対し、
こちらは **格納形式を網羅する** ためのものです。目的が違うので分けています。

- パターンは 1 つに固定します。形式ごとの違いだけを見たいので、絵が変わると比べられません
- 画像は小さく作ります。リポジトリへ入れるものなので、全部合わせて数 MB に収めます
- 同じ絵を全形式で出すので、読み手側の実装（C/C++ 版など）の入力テストにそのまま使えます

サイズは `--size` で選びます。既定の small（192 × 144）は、v210 は幅が 6 の倍数、
mipi10 は色差を間引くと幅が 8 の倍数、4:2:0 は幅も高さも偶数、という条件を
全部同時に満たす小さい値です。同梱物の既定を小さくしているのは容量のためで、
20 形式ぶんの合計が HD で 57MB、FHD で 128MB、4K で 511MB になります。

実サイズで見たいときは HD / FHD / 4K を指定してください。
**その場合の出力先は generated/ です**（リポジトリに入れる大きさではありません）。
ファイル名にサイズは入れないので、同じ出力先へ別のサイズを出すと上書きになります。
形式ごとの違いだけを見るためのものなので、サイズが混ざっているほうが困ります。

使い方:
    python tools/make_reference_raws.py                             # samples/raw へ 192x144
    python tools/make_reference_raws.py --size FHD --out generated  # generated へ FHD
    python tools/make_reference_raws.py --out DIR
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

# 名前 -> (幅, 高さ)。どれも v210（幅 6 の倍数）と mipi10 4:2:0（幅 8 の倍数）を満たします。
SIZES = {
    "small": (192, 144),      # 192 は 6 でも 8 でも割り切れ、144 は偶数
    "HD": (1280, 720),
    "FHD": (1920, 1080),
    "4K": (3840, 2160),
}

# (色モデル, サブサンプリング, ビット深度, 格納形式, range, matrix, alignment)
# 生成器が受け付ける組み合わせを、重複なく全部並べたものです。
COMBINATIONS = (
    ("rgb", "4:4:4", 8, "planar", "full", None, None),
    ("rgb", "4:4:4", 8, "packed", "full", None, None),
    ("rgb", "4:4:4", 10, "planar", "full", None, "lsb"),
    ("rgb", "4:4:4", 10, "planar", "full", None, "msb"),
    ("rgb", "4:4:4", 10, "mipi10", "full", None, None),
    ("ycbcr", "4:4:4", 8, "planar", "limited", "bt709", None),
    ("ycbcr", "4:4:4", 8, "planar", "full", "bt709", None),
    ("ycbcr", "4:4:4", 8, "packed", "limited", "bt601", None),
    ("ycbcr", "4:4:4", 10, "planar", "limited", "bt2020", "lsb"),
    ("ycbcr", "4:4:4", 10, "mipi10", "limited", "bt709", None),
    ("ycbcr", "4:2:2", 8, "planar", "limited", "bt709", None),
    ("ycbcr", "4:2:2", 8, "packed", "limited", "bt709", None),
    ("ycbcr", "4:2:2", 10, "planar", "limited", "bt709", "lsb"),
    ("ycbcr", "4:2:2", 10, "v210", "limited", "bt709", None),
    ("ycbcr", "4:2:2", 10, "mipi10", "limited", "bt709", None),
    ("ycbcr", "4:2:0", 8, "planar", "limited", "bt601", None),
    ("ycbcr", "4:2:0", 8, "nv12", "limited", "bt709", None),
    ("ycbcr", "4:2:0", 10, "planar", "limited", "bt709", "lsb"),
    ("ycbcr", "4:2:0", 10, "p010", "limited", "bt709", "msb"),
    ("ycbcr", "4:2:0", 10, "mipi10", "limited", "bt709", None),
)

# 形式の網羅とは別に、色差の間引きで何が起きるかを 1 本だけ入れておきます。
# 赤と青の 1 画素縞は輝度が近く色差が遠いので、4:2:0 にすると色だけが潰れます。
EXTRA = (
    (
        "hatch",
        ("ycbcr", "4:2:0", 8, "nv12", "limited", "bt709", None),
        ["on=[1,0,0]", "off=[0,0,1]"],
        "hatch_redblue_ycbcr420_8bit_nv12",
    ),
)


def build_command(pattern: str, combination: tuple, out_base: Path, pattern_options: list[str],
                  width: int, height: int) -> list[str]:
    color_model, subsampling, bit_depth, storage, range_name, matrix, alignment = combination
    command = [
        sys.executable, "-m", "vtp",
        "--pattern", pattern,
        "--width", str(width),
        "--height", str(height),
        "--color-model", color_model,
        "--subsampling", subsampling,
        "--bit-depth", str(bit_depth),
        "--range", range_name,
        "--storage", storage,
        "--output", str(out_base),
    ]
    if matrix:
        command += ["--matrix", matrix]
    if alignment:
        command += ["--alignment", alignment]
    for option in pattern_options:
        command += ["--pattern-option", option]
    return command


def run(pattern: str, combination: tuple, out_base: Path, pattern_options: list[str], root: Path,
        width: int, height: int) -> str | None:
    result = subprocess.run(
        build_command(pattern, combination, out_base, pattern_options, width, height),
        cwd=root, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    if result.returncode == 0:
        return None
    message = (result.stderr or result.stdout).strip().splitlines()
    return message[-1] if message else "(出力なし)"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default="samples/raw", help="出力先ディレクトリ")
    parser.add_argument("--pattern", default="colorbar", help="使うパターン（既定 colorbar）")
    parser.add_argument("--size", choices=tuple(SIZES), default="small",
                        help="画像サイズ。small は同梱用（1.7MB）。HD/FHD/4K は 57/128/511MB になるので generated/ へ")
    args = parser.parse_args()

    width, height = SIZES[args.size]

    root = Path(__file__).resolve().parent.parent
    out_directory = (root / args.out).resolve()
    out_directory.mkdir(parents=True, exist_ok=True)

    print(f"出力先: {out_directory}")
    print(f"サイズ: {args.size} {width} x {height}   パターン: {args.pattern}")
    print()
    print(f"{'ファイル':<44} {'RAWサイズ':>12}  条件")
    print("-" * 100)

    failures = 0
    total = 0

    jobs = [
        (args.pattern, c, [], f"{args.pattern}_{c[0]}{c[1].replace(':', '')}_{c[2]}bit_{c[3]}"
                             + (f"_{c[5]}" if c[5] else "") + (f"_{c[6]}" if c[6] else "")
                             + (f"_{c[4]}" if c[0] == "ycbcr" else ""))
        for c in COMBINATIONS
    ]
    jobs += [(p, c, o, n) for p, c, o, n in EXTRA]

    for pattern, combination, pattern_options, name in jobs:
        out_base = out_directory / name
        error = run(pattern, combination, out_base, pattern_options, root, width, height)
        raw = out_base.with_suffix(".raw")
        if error:
            failures += 1
            print(f"{name:<44} {'-':>12}  失敗: {error}")
            continue
        size = raw.stat().st_size
        total += size
        color_model, subsampling, bit_depth, storage, range_name, matrix, alignment = combination
        condition = f"{color_model} {subsampling} {bit_depth}bit {range_name} {storage}"
        if matrix:
            condition += f" {matrix}"
        if alignment:
            condition += f" {alignment}"
        print(f"{name:<44} {size:>10,} B  {condition}")

    print()
    if failures:
        print(f"{len(jobs) - failures} 件成功、{failures} 件失敗しました。")
        return 1
    print(f"{len(jobs)} 件を生成しました。RAW 合計 {total / 1024 / 1024:.2f} MB。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

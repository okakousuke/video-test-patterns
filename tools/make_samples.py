#!/usr/bin/env python3
"""ビューア確認用のサンプルRAWとmanifestをまとめて生成します。

すべてのパターンを、格納形式・ビット深度・rangeを散らした条件で1本ずつ作ります。
画像サイズは、実際に使われている規格の解像度（VGA / HD / FHD / SD NTSC など）から選びます。
どのパターンにどの解像度を割り当てるかは乱数で決めますが、**シードを固定**しているので
毎回同じ結果になります。サイズを1つに揃えないのは、幅や高さに依存する不具合
（行のまたぎ、色差面の端数、パック単位の切り上げ）を隠さないためです。

各格納形式には成立する条件があります（v210は幅が6の倍数、mipi10は各プレーンの幅が
4の倍数、4:2:0は幅も高さも偶数など）。規格の解像度のうち、その条件を満たすものだけを割り当てます。
生成器側は成立しない指定をエラーで止めるので、このスクリプトが条件を取り違えていれば、
生成の時点で分かります。

使い方:
    python tools/make_samples.py                        # generated/samples/ へ出力
    python tools/make_samples.py --out DIR              # 出力先を変える
    python tools/make_samples.py --seed 123             # 別の割り当てにする
    python tools/make_samples.py --max-pixels 8294400   # 4K まで使う
"""

from __future__ import annotations

import argparse
import random
import subprocess
import sys
from pathlib import Path

PATTERNS = (
    "colorbar",
    "colorbar75",
    "grayramp",
    "graysteps",
    "frame",
    "crosshair",
    "grid",
    "circles",
    "radial",
    "hatch",
    "dots",
    "blocks",
    "smptebars",
    "pluge",
    "multiburst",
    "window",
    "zoneplate",
    "checker",
    "pulsebar",
    "splitbars",
    "rainbow",
    "sweep",
    "shallowramp",
    "triangleramp",
    "square",
    "stepmatrix",
    "wedge",
    "testcard",
    "gamma",
    "colorramp",
    "colormatrix",
    "noise",
)

# 実際に使われている画面・映像の解像度から選びます。
# 適当な数字にすると「その幅だから起きた不具合」なのか「珍しい幅だから起きた不具合」なのか
# 区別が付かなくなります。規格上の解像度なら、同じ幅で実機を通したときと突き合わせられます。
#
# (名前, 幅, 高さ)
STANDARD_SIZES = (
    ("QCIF", 176, 144),
    ("CIF", 352, 288),
    ("VGA", 640, 480),
    ("SD NTSC", 720, 480),
    ("SD PAL", 720, 576),
    ("SVGA", 800, 600),
    ("XGA", 1024, 768),
    ("HD 720p", 1280, 720),
    ("SXGA", 1280, 1024),
    ("WXGA", 1366, 768),
    ("HD+", 1600, 900),
    ("FHD 1080p", 1920, 1080),
    ("WUXGA", 1920, 1200),
    ("QHD", 2560, 1440),
    ("4K UHD", 3840, 2160),
)

# 既定ではこれを超える画素数を使いません。10bit の 4K は 1 本で 50MB 近くになるためです。
DEFAULT_MAX_PIXELS = 1920 * 1200

# (color_model, subsampling, bit_depth, storage, range, matrix, alignment)
# matrix と alignment は、その条件で意味を持つときだけ値を入れます。
PROFILES = (
    ("rgb", "4:4:4", 8, "planar", "full", None, None),
    ("rgb", "4:4:4", 8, "packed", "full", None, None),
    ("rgb", "4:4:4", 10, "planar", "full", None, "lsb"),
    ("rgb", "4:4:4", 10, "planar", "full", None, "msb"),
    ("rgb", "4:4:4", 10, "mipi10", "full", None, None),
    ("ycbcr", "4:4:4", 8, "planar", "limited", "bt709", None),
    ("ycbcr", "4:4:4", 8, "packed", "full", "bt601", None),
    ("ycbcr", "4:4:4", 10, "planar", "limited", "bt2020", "lsb"),
    ("ycbcr", "4:2:2", 8, "packed", "limited", "bt709", None),
    ("ycbcr", "4:2:2", 10, "v210", "limited", "bt709", None),
    ("ycbcr", "4:2:2", 10, "mipi10", "limited", "bt2020", None),
    ("ycbcr", "4:2:0", 8, "planar", "limited", "bt601", None),
    ("ycbcr", "4:2:0", 8, "nv12", "limited", "bt709", None),
    ("ycbcr", "4:2:0", 10, "p010", "limited", "bt709", "msb"),
    ("ycbcr", "4:2:0", 10, "mipi10", "limited", "bt709", None),
)


def width_multiple(subsampling: str, storage: str) -> int:
    """その条件で幅が満たすべき倍数を返します。"""
    if storage == "v210":
        return 6
    if storage == "mipi10":
        # 各プレーンの幅が4の倍数。色差を半分に間引く場合は幅そのものが8の倍数でないと足りません。
        return 4 if subsampling == "4:4:4" else 8
    if subsampling in ("4:2:2", "4:2:0"):
        return 2
    return 1


def height_multiple(subsampling: str) -> int:
    return 2 if subsampling == "4:2:0" else 1


def build_command(pattern: str, profile: tuple, width: int, height: int, out_base: Path) -> list[str]:
    color_model, subsampling, bit_depth, storage, rng, matrix, alignment = profile
    command = [
        sys.executable, "-m", "vtp",
        "--pattern", pattern,
        "--width", str(width),
        "--height", str(height),
        "--color-model", color_model,
        "--subsampling", subsampling,
        "--bit-depth", str(bit_depth),
        "--range", rng,
        "--storage", storage,
        "--output", str(out_base),
    ]
    if matrix:
        command += ["--matrix", matrix]
    if alignment:
        command += ["--alignment", alignment]
    return command


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default="generated/samples", help="出力先ディレクトリ")
    parser.add_argument("--seed", type=int, default=20260812, help="解像度の割り当てに使う乱数シード")
    parser.add_argument("--max-pixels", type=int, default=DEFAULT_MAX_PIXELS,
                        help="使う解像度の上限画素数。既定は WUXGA 相当。4K まで使うなら 8294400 を指定")
    args = parser.parse_args()

    repository_root = Path(__file__).resolve().parent.parent
    out_directory = (repository_root / args.out).resolve()
    out_directory.mkdir(parents=True, exist_ok=True)

    rng = random.Random(args.seed)
    failures = 0

    usable = [s for s in STANDARD_SIZES if s[1] * s[2] <= args.max_pixels]
    if not usable:
        print("使えるサイズがありません。--max-pixels を大きくしてください。")
        return 1

    print(f"出力先: {out_directory}")
    print(f"シード: {args.seed}（同じシードなら毎回同じ割り当てになります）")
    print()
    print(f"{'パターン':<12} {'解像度':>18}  条件")
    print("-" * 86)

    # 同じサイズばかりにならないよう、シードを固定した並びから順に割り当てます。
    order = list(usable)
    rng.shuffle(order)
    cursor = 0

    for index, pattern in enumerate(PATTERNS):
        profile = PROFILES[index % len(PROFILES)]
        color_model, subsampling, bit_depth, storage, range_name, matrix, alignment = profile

        wm = width_multiple(subsampling, storage)
        hm = height_multiple(subsampling)

        # その格納形式で成立するサイズだけを選びます（v210 は幅が 6 の倍数、など）。
        chosen = None
        for step in range(len(order)):
            candidate = order[(cursor + step) % len(order)]
            if candidate[1] % wm == 0 and candidate[2] % hm == 0:
                chosen = candidate
                cursor = (cursor + step + 1) % len(order)
                break

        if chosen is None:
            failures += 1
            print(f"{pattern:<12} {'-':>18}  この条件で使える規格解像度がありません: {profile}")
            continue

        size_name, width, height = chosen
        name = f"{pattern}_{color_model}{subsampling.replace(':', '')}_{bit_depth}bit_{storage}"
        out_base = out_directory / name

        condition = f"{color_model} {subsampling} {bit_depth}bit {range_name} {storage}"
        if matrix:
            condition += f" {matrix}"
        if alignment:
            condition += f" {alignment}"

        result = subprocess.run(
            build_command(pattern, profile, width, height, out_base),
            cwd=repository_root,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )

        label = f"{size_name} {width}x{height}"
        if result.returncode == 0:
            print(f"{pattern:<12} {label:>18}  {condition}")
        else:
            failures += 1
            message = (result.stderr or result.stdout).strip().splitlines()
            print(f"{pattern:<12} {label:>18}  失敗: {message[-1] if message else '(出力なし)'}")

    print()
    if failures:
        print(f"{len(PATTERNS) - failures} 件成功、{failures} 件失敗しました。")
        return 1

    print(f"{len(PATTERNS)} 件すべて生成しました。RawInspector でこのフォルダを開いて確認できます。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

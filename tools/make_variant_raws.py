#!/usr/bin/env python3
"""同じパターンを条件だけ変えて量産します（サイズ違い・塗り違い）。

`make_samples.py` が「パターンを一通り 1 本ずつ」なのに対し、こちらは
**1 つのパターンを条件で振る** ためのものです。目的が違うので分けています。
一通り並べたサンプルでは、サイズや塗りに依存する挙動が 1 本ずつしか出ないため、
「このサイズだから起きたのか」を確かめられません。

3 つの群があります。

raster
    一様な塗りを、色と振幅を変えて並べます。むら・純度・レベルの確認用です。
    合わせて、同じ塗りを 4:4:4 / 4:2:2 / 4:2:0 で出します。一様な面には色差の
    細かい変化が無いので、この 3 本は同じ結果になるはずです。差が出たら、
    それは間引きの損失ではなく色変換かビット詰めの誤りです。

smptebars
    同じカラーバーを、規格の解像度ぶん並べます。形式は 1 つに固定してあるので、
    違いはサイズだけです。幅や高さの端数（7 等分の割り切れなさ、色差面の切り上げ）が
    サイズによってどう出るかを、横に並べて比べられます。

resolution
    解像を見るパターンを、サイズと格納形式の両方で振ります。この種の絵は
    「何画素あるか」で見え方が決まるので、1 サイズだけでは足りません。

出力先は generated/ 直下です。ここは .gitignore の対象なので、リポジトリには残りません。
同梱するサンプルは samples/ にあり、そちらとは役割が違います。

使い方:
    python tools/make_variant_raws.py                    # 3 群すべて
    python tools/make_variant_raws.py --group smptebars  # 群を選ぶ
    python tools/make_variant_raws.py --out DIR
    python tools/make_variant_raws.py --dry-run          # 何を作るかだけ表示
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

# サイズの表と成立条件は make_samples 側の 1 つだけを使います。
# 同じ表を 2 か所に置くと、片方だけ直したときに黙ってずれます。
from make_samples import STANDARD_SIZES, height_multiple, width_multiple  # noqa: E402

# (色モデル, サブサンプリング, ビット深度, 格納形式, range, matrix, alignment)
NV12 = ("ycbcr", "4:2:0", 8, "nv12", "limited", "bt709", None)
RGB8 = ("rgb", "4:4:4", 8, "packed", "full", None, None)
V210 = ("ycbcr", "4:2:2", 10, "v210", "limited", "bt709", None)
I444 = ("ycbcr", "4:4:4", 8, "planar", "limited", "bt709", None)
I422 = ("ycbcr", "4:2:2", 8, "planar", "limited", "bt709", None)

# --------------------------------------------------------------- raster 群

# (名前, color, level)。色は R'G'B' の 0〜1 です。
RASTER_FILLS = (
    ("white", [1.0, 1.0, 1.0], 1.0),
    ("gray75", [1.0, 1.0, 1.0], 0.75),
    ("gray50", [1.0, 1.0, 1.0], 0.5),
    ("gray25", [1.0, 1.0, 1.0], 0.25),
    ("gray10", [1.0, 1.0, 1.0], 0.1),
    ("black", [0.0, 0.0, 0.0], 1.0),
    ("red", [1.0, 0.0, 0.0], 1.0),
    ("green", [0.0, 1.0, 0.0], 1.0),
    ("blue", [0.0, 0.0, 1.0], 1.0),
    ("cyan", [0.0, 1.0, 1.0], 1.0),
    ("magenta", [1.0, 0.0, 1.0], 1.0),
    ("yellow", [1.0, 1.0, 0.0], 1.0),
    ("red75", [1.0, 0.0, 0.0], 0.75),
    ("green75", [0.0, 1.0, 0.0], 0.75),
    ("blue75", [0.0, 0.0, 1.0], 0.75),
)

# 一様な面が間引きで変わらないことを確かめるための 3 本。塗りは 1 つに固定します。
RASTER_SUBSAMPLING_CHECK = (
    ("444", I444),
    ("422", I422),
    ("420", NV12),
)

# ---------------------------------------------------------- resolution 群

RESOLUTION_PATTERNS = (
    "siemens",
    "linepairs",
    "slantedge",
    "wedge",
    "multiburst",
    "sweep",
    "zoneplate",
    "square",
    "resolutioncard",
)

# 解像は画素数で決まるので、粗いほうと細かいほうを両端に置きます。
RESOLUTION_SIZES = ("VGA", "HD 720p", "FHD 1080p")

# 同じ絵を、色差を持つ形式と持たない形式の両方で出します。
# 白黒の絵なので 4:2:0 でもほとんど落ちないはずで、落ちるなら色変換のほうを疑います。
RESOLUTION_PROFILES = (RGB8, NV12, V210)


def size_by_name(name: str) -> tuple[str, int, int]:
    for entry in STANDARD_SIZES:
        if entry[0] == name:
            return entry
    raise KeyError(f"規格の解像度に {name} がありません")


def fits(profile: tuple, width: int, height: int) -> bool:
    _, subsampling, _, storage, *_ = profile
    return width % width_multiple(subsampling, storage) == 0 and height % height_multiple(subsampling) == 0


def profile_tag(profile: tuple) -> str:
    color_model, subsampling, bit_depth, storage, *_ = profile
    return f"{color_model}{subsampling.replace(':', '')}_{bit_depth}bit_{storage}"


def profile_text(profile: tuple) -> str:
    color_model, subsampling, bit_depth, storage, range_name, matrix, alignment = profile
    text = f"{color_model} {subsampling} {bit_depth}bit {range_name} {storage}"
    if matrix:
        text += f" {matrix}"
    if alignment:
        text += f" {alignment}"
    return text


def build_command(
    pattern: str, profile: tuple, width: int, height: int, out_base: Path, pattern_options: list[str]
) -> list[str]:
    color_model, subsampling, bit_depth, storage, range_name, matrix, alignment = profile
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


Job = tuple[str, str, tuple, int, int, list[str]]  # (名前, パターン, 形式, 幅, 高さ, パターン指定)


def raster_jobs(width: int, height: int) -> list[Job]:
    jobs: list[Job] = []
    for name, color, level in RASTER_FILLS:
        options = [f"color=[{color[0]},{color[1]},{color[2]}]", f"level={level}"]
        jobs.append((f"raster_{name}_{width}x{height}_{profile_tag(NV12)}", "raster", NV12, width, height, options))

    # 間引きで変わらないことの確認。塗りは色差が最も遠いものにします。
    check = ["color=[1.0,0.0,0.0]", "level=1.0"]
    for tag, profile in RASTER_SUBSAMPLING_CHECK:
        if not fits(profile, width, height):
            continue
        jobs.append((f"raster_red_same_in_{tag}_{profile_tag(profile)}", "raster", profile, width, height, check))
    return jobs


def smptebars_jobs(max_pixels: int) -> list[Job]:
    jobs: list[Job] = []
    for _, width, height in STANDARD_SIZES:
        if width * height > max_pixels or not fits(NV12, width, height):
            continue
        jobs.append((f"smptebars_{width}x{height}_{profile_tag(NV12)}", "smptebars", NV12, width, height, []))
    return jobs


def resolution_jobs(max_pixels: int) -> list[Job]:
    jobs: list[Job] = []
    for pattern in RESOLUTION_PATTERNS:
        for size_name in RESOLUTION_SIZES:
            _, width, height = size_by_name(size_name)
            if width * height > max_pixels:
                continue
            for profile in RESOLUTION_PROFILES:
                if not fits(profile, width, height):
                    continue
                jobs.append((
                    f"{pattern}_{width}x{height}_{profile_tag(profile)}", pattern, profile, width, height, [],
                ))
    return jobs


GROUPS = ("raster", "smptebars", "resolution")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default="generated", help="出力先ディレクトリ")
    parser.add_argument("--group", choices=(*GROUPS, "all"), default="all", help="作る群")
    parser.add_argument("--raster-size", default="HD 720p", help="raster 群のサイズ（規格名）")
    # 既定で 4K まで含めます。サイズ違いを見るための群なので、上を切ると意味が薄れます。
    parser.add_argument("--max-pixels", type=int, default=3840 * 2160, help="使う解像度の上限画素数")
    parser.add_argument("--dry-run", action="store_true", help="生成せず、作る内容だけ表示する")
    args = parser.parse_args()

    repository_root = Path(__file__).resolve().parent.parent
    out_directory = (repository_root / args.out).resolve()

    _, raster_width, raster_height = size_by_name(args.raster_size)

    jobs: list[Job] = []
    if args.group in ("raster", "all"):
        jobs += raster_jobs(raster_width, raster_height)
    if args.group in ("smptebars", "all"):
        jobs += smptebars_jobs(args.max_pixels)
    if args.group in ("resolution", "all"):
        jobs += resolution_jobs(args.max_pixels)

    if not jobs:
        print("作るものがありません。--max-pixels を大きくしてください。")
        return 1

    print(f"出力先: {out_directory}")
    print(f"群: {args.group}   件数: {len(jobs)}")
    print()
    print(f"{'ファイル':<52} {'解像度':>11}  条件")
    print("-" * 108)

    if not args.dry_run:
        out_directory.mkdir(parents=True, exist_ok=True)

    failures = 0
    total = 0

    for name, pattern, profile, width, height, options in jobs:
        label = f"{width}x{height}"
        if args.dry_run:
            print(f"{name:<52} {label:>11}  {profile_text(profile)}")
            continue

        out_base = out_directory / name
        result = subprocess.run(
            build_command(pattern, profile, width, height, out_base, options),
            cwd=repository_root, capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
        if result.returncode != 0:
            failures += 1
            message = (result.stderr or result.stdout).strip().splitlines()
            print(f"{name:<52} {label:>11}  失敗: {message[-1] if message else '(出力なし)'}")
            continue

        total += out_base.with_suffix(".raw").stat().st_size
        print(f"{name:<52} {label:>11}  {profile_text(profile)}")

    print()
    if args.dry_run:
        print(f"{len(jobs)} 件を作る予定です（--dry-run なので生成していません）。")
        return 0
    if failures:
        print(f"{len(jobs) - failures} 件成功、{failures} 件失敗しました。")
        return 1
    print(f"{len(jobs)} 件を生成しました。RAW 合計 {total / 1024 / 1024:.1f} MB。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

"""コマンドライン入口.

方針:

- GUI は作らず、``-h`` で使い方が分かる CLI にする
- 設定は「コマンドライン引数 > 設定ファイル > 既定値」の順で上書きする
- 成立しない組み合わせは補正せずエラーで落とす
"""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any, Sequence

from . import __version__
from .config import (
    describe_combinations,
    ALIGNMENTS,
    COLOR_MODELS,
    MATRICES,
    OUTPUTS,
    RANGES,
    STORAGES,
    SUBSAMPLINGS,
    Config,
    ConfigError,
    describe_storages,
    load_jsonc,
    validate,
)
from .pack import expected_size
from .patterns import PATTERN_NAMES
from .pipeline import generate

EPILOG = """\
例:
  # 1920x1080 の 8bit RGB カラーバー（PNG + RAW + manifest）
  vtp --pattern colorbar --width 1920 --height 1080

  # BT.709 limited range の 10bit 4:2:2 を v210 で詰める
  vtp --pattern hatch --width 1920 --height 1080 \\
      --color-model ycbcr --subsampling 4:2:2 --bit-depth 10 \\
      --range limited --matrix bt709 --storage v210 \\
      --output generated/hatch_v210

  # 設定ファイルを土台にして、サイズだけ上書きする
  vtp --config configs/colorbar_bt709_10bit_v210.jsonc --width 1280 --height 720
"""


def _parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="vtp",
        description="映像テストパターン生成器（Python リファレンス実装）",
        epilog=EPILOG,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("--version", action="version", version=f"video-test-patterns {__version__}")

    g = p.add_argument_group("パターン")
    g.add_argument("--pattern", choices=PATTERN_NAMES, help="生成するパターン")
    g.add_argument("--width", type=int, help="幅（画素）")
    g.add_argument("--height", type=int, help="高さ（画素）")
    g.add_argument(
        "--pattern-option",
        action="append",
        metavar="KEY=VALUE",
        default=[],
        help="パターン固有のオプション（例: --pattern-option steps=16）。複数指定可",
    )

    g = p.add_argument_group("色とデータ形式")
    g.add_argument("--color-model", choices=COLOR_MODELS, help="色成分の表現")
    g.add_argument("--subsampling", choices=SUBSAMPLINGS, help="色差の間引き")
    g.add_argument("--bit-depth", type=int, choices=(8, 10), help="1 成分のビット数")
    g.add_argument("--range", dest="range_", choices=RANGES, help="コード値の使用範囲")
    g.add_argument("--matrix", choices=MATRICES, help="RGB との変換係数")
    g.add_argument("--storage", choices=tuple(STORAGES), help="メモリ上の並べ方")
    g.add_argument(
        "--alignment",
        choices=ALIGNMENTS,
        help="10bit を 16bit コンテナへ入れるときの寄せ方（既定: lsb）",
    )

    g = p.add_argument_group("出力")
    g.add_argument("--output", help="出力ファイルの基準パス（拡張子は自動で付く）")
    g.add_argument(
        "--outputs",
        help="生成する出力をカンマ区切りで指定（既定: raw,png,json / 選択肢: "
        + ",".join(OUTPUTS)
        + "）",
    )
    g.add_argument("--config", help="JSONC 設定ファイル")
    g.add_argument(
        "--dry-run",
        action="store_true",
        help="検証と RAW サイズの計算だけ行い、ファイルを書かない",
    )
    g.add_argument("--quiet", action="store_true", help="標準出力への要約を抑制する")

    g = p.add_argument_group("情報表示")
    g.add_argument("--list-patterns", action="store_true", help="パターン一覧を表示して終了")
    g.add_argument(
        "--list-storages", action="store_true", help="格納形式と制約を表示して終了"
    )
    g.add_argument(
        "--describe",
        action="store_true",
        help="成立する組み合わせと幅・高さの倍数を JSON で表示して終了（GUI など別の実装向け）",
    )
    return p


def _parse_pattern_options(items: Sequence[str]) -> dict[str, Any]:
    out: dict[str, Any] = {}
    for item in items:
        if "=" not in item:
            raise ConfigError(f"--pattern-option は KEY=VALUE 形式です: {item}")
        k, v = item.split("=", 1)
        try:
            out[k.strip()] = json.loads(v)
        except json.JSONDecodeError:
            out[k.strip()] = v
    return out


def build_config(args: argparse.Namespace) -> Config:
    """設定ファイルとコマンドライン引数から ``Config`` を組み立てる."""
    data: dict[str, Any] = {}
    if args.config:
        data.update(load_jsonc(args.config))

    mapping = {
        "pattern": args.pattern,
        "width": args.width,
        "height": args.height,
        "color_model": args.color_model,
        "subsampling": args.subsampling,
        "bit_depth": args.bit_depth,
        "range": args.range_,
        "matrix": args.matrix,
        "storage": args.storage,
        "alignment": args.alignment,
        "output": args.output,
    }
    for key, value in mapping.items():
        if value is not None:
            data[key] = value

    if args.outputs:
        data["outputs"] = tuple(s.strip() for s in args.outputs.split(",") if s.strip())

    if args.pattern_option:
        opts = dict(data.get("pattern_options") or {})
        opts.update(_parse_pattern_options(args.pattern_option))
        data["pattern_options"] = opts

    return Config.from_dict(data)


def main(argv: Sequence[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)

    if args.list_patterns:
        print("利用可能なパターン:")
        for name in PATTERN_NAMES:
            print(f"  {name}")
        return 0

    if args.list_storages:
        print("利用可能な格納形式:")
        print(describe_storages())
        return 0

    # 別の実装（GUI など）が同じ判定を持たずに済むよう、正解を JSON で渡します。
    # 規則を書き写させると必ずずれるので、ここから読ませます。
    if args.describe:
        print(json.dumps(describe_combinations(PATTERN_NAMES), ensure_ascii=False, indent=2))
        return 0

    try:
        cfg = build_config(args)
        validate(cfg, PATTERN_NAMES)
    except ConfigError as e:
        print(f"エラー: {e}", file=sys.stderr)
        return 2

    if args.dry_run:
        if not args.quiet:
            print(json.dumps(cfg.to_dict(), ensure_ascii=False, indent=2))
            print(f"RAW サイズ（計算値）: {expected_size(cfg)} バイト")
        return 0

    try:
        result = generate(cfg)
    except (ValueError, OSError) as e:
        print(f"エラー: {e}", file=sys.stderr)
        return 1

    if not result.roundtrip_ok:
        print(
            "エラー: 詰めたデータを読み戻した結果が元のプレーンと一致しません",
            file=sys.stderr,
        )
        return 1

    if not args.quiet:
        for kind, path in sorted(result.outputs.items()):
            print(f"{kind:<5} {path}")
        print(f"RAW サイズ: {result.manifest['raw_bytes']} バイト（往復確認 OK）")
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())

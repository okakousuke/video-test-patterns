"""生成条件の保持・既定値・組み合わせ検証.

このモジュールの役割は「生成の前に落とす」ことです。
形式として成立しない組み合わせを黙って補正すると、
出力を見ても何が起きたのか分からなくなります。
"""

from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

# ---------------------------------------------------------------- 語彙の定義

COLOR_MODELS = ("rgb", "ycbcr")
SUBSAMPLINGS = ("4:4:4", "4:2:2", "4:2:0")
BIT_DEPTHS = (8, 10)
RANGES = ("full", "limited")
MATRICES = ("bt601", "bt709", "bt2020")

#: 格納形式ごとの制約。
#:
#: ``color_models`` / ``subsamplings`` / ``bit_depths`` は「その格納形式が
#: 受け付ける値」を表します。空タプルは「制約なし」ではなく定義漏れとみなします。
STORAGES: dict[str, dict[str, Any]] = {
    "planar": {
        "color_models": ("rgb", "ycbcr"),
        "subsamplings": ("4:4:4", "4:2:2", "4:2:0"),
        "bit_depths": (8, 10),
        "description": "成分ごとに連続した領域へ格納する（I420 / I422 / I444 相当）",
    },
    "packed": {
        "color_models": ("rgb", "ycbcr"),
        "subsamplings": ("4:4:4", "4:2:2"),
        "bit_depths": (8,),
        "description": "画素単位で成分を交互に並べる（RGB24 / YCbCr24 / UYVY）",
    },
    "nv12": {
        "color_models": ("ycbcr",),
        "subsamplings": ("4:2:0",),
        "bit_depths": (8,),
        "description": "Y プレーンの後ろに CbCr を交互に置く 4:2:0（8bit）",
    },
    "p010": {
        "color_models": ("ycbcr",),
        "subsamplings": ("4:2:0",),
        "bit_depths": (10,),
        "description": "NV12 と同じ配置で、各成分を 16bit コンテナの上位詰めにした 10bit",
    },
    "v210": {
        "color_models": ("ycbcr",),
        "subsamplings": ("4:2:2",),
        "bit_depths": (10,),
        "description": "10bit 4:2:2 を 32bit ワードへ 3 サンプルずつ詰める放送系形式",
    },
    "mipi10": {
        "color_models": ("rgb", "ycbcr"),
        "subsamplings": ("4:4:4", "4:2:2", "4:2:0"),
        "bit_depths": (10,),
        "description": "各プレーンを 4 サンプル 5 バイトへ詰める（MIPI RAW10 風のバイト詰め）",
    },
}

#: 16bit コンテナへ 10bit を入れるときの寄せ方。
ALIGNMENTS = ("lsb", "msb")

OUTPUTS = ("raw", "png", "json")


class ConfigError(ValueError):
    """生成条件が形式として成立しないときに送出される."""


# ---------------------------------------------------------------- 設定本体


@dataclass
class Config:
    """1 回の生成に使う条件の全体."""

    pattern: str = "colorbar"
    width: int = 1920
    height: int = 1080
    color_model: str = "rgb"
    subsampling: str = "4:4:4"
    bit_depth: int = 8
    range: str = "full"
    matrix: str = "bt709"
    storage: str = "planar"
    alignment: str = "lsb"
    outputs: tuple[str, ...] = ("raw", "png", "json")
    output: str = "generated/pattern"
    pattern_options: dict[str, Any] = field(default_factory=dict)

    # ---------------------------------------------------------- 変換

    def to_dict(self) -> dict[str, Any]:
        d = asdict(self)
        d["outputs"] = list(self.outputs)
        if self.color_model == "rgb":
            # RGB のままなら matrix / range は「使っていない」ことを明示する。
            d["matrix"] = None
        return d

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "Config":
        known = {f for f in cls.__dataclass_fields__}
        unknown = sorted(set(data) - known)
        if unknown:
            raise ConfigError(
                "設定に未知のキーがあります: " + ", ".join(unknown)
            )
        data = dict(data)
        if "outputs" in data and data["outputs"] is not None:
            data["outputs"] = tuple(data["outputs"])
        return cls(**data)


# ---------------------------------------------------------------- JSONC


def strip_jsonc_comments(text: str) -> str:
    """JSONC から ``//`` と ``/* */`` を取り除く.

    文字列リテラルの中の ``//`` を消さないよう、1 文字ずつ状態を持って走査します。
    正規表現 1 本で済ませると ``"http://example.com"`` のような値が壊れます。
    """
    out: list[str] = []
    i = 0
    n = len(text)
    in_string = False
    escaped = False

    while i < n:
        c = text[i]

        if in_string:
            out.append(c)
            if escaped:
                escaped = False
            elif c == "\\":
                escaped = True
            elif c == '"':
                in_string = False
            i += 1
            continue

        if c == '"':
            in_string = True
            out.append(c)
            i += 1
            continue

        if c == "/" and i + 1 < n:
            nxt = text[i + 1]
            if nxt == "/":
                while i < n and text[i] != "\n":
                    i += 1
                continue
            if nxt == "*":
                i += 2
                while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                    # 改行は残す（エラー行番号をずらさないため）
                    if text[i] == "\n":
                        out.append("\n")
                    i += 1
                i += 2
                continue

        out.append(c)
        i += 1

    return "".join(out)


def load_jsonc(path: str | Path) -> dict[str, Any]:
    """JSONC ファイルを読み込んで dict を返す."""
    raw = Path(path).read_text(encoding="utf-8")
    data = json.loads(strip_jsonc_comments(raw))
    if not isinstance(data, dict):
        raise ConfigError("設定ファイルの最上位はオブジェクトである必要があります")
    return data


# ---------------------------------------------------------------- 検証


def validate(cfg: Config, known_patterns: tuple[str, ...]) -> None:
    """組み合わせを検証する。成立しない指定は ``ConfigError`` を送出する."""
    if cfg.pattern not in known_patterns:
        raise ConfigError(
            f"未知のパターン: {cfg.pattern}（利用可能: {', '.join(known_patterns)}）"
        )

    if cfg.width <= 0 or cfg.height <= 0:
        raise ConfigError("width / height は 1 以上である必要があります")

    if cfg.color_model not in COLOR_MODELS:
        raise ConfigError(f"未知の color_model: {cfg.color_model}")
    if cfg.subsampling not in SUBSAMPLINGS:
        raise ConfigError(f"未知の subsampling: {cfg.subsampling}")
    if cfg.bit_depth not in BIT_DEPTHS:
        raise ConfigError(f"未対応の bit_depth: {cfg.bit_depth}")
    if cfg.range not in RANGES:
        raise ConfigError(f"未知の range: {cfg.range}")
    if cfg.matrix not in MATRICES:
        raise ConfigError(f"未知の matrix: {cfg.matrix}")
    if cfg.alignment not in ALIGNMENTS:
        raise ConfigError(f"未知の alignment: {cfg.alignment}")
    if cfg.storage not in STORAGES:
        raise ConfigError(
            f"未知の storage: {cfg.storage}（利用可能: {', '.join(STORAGES)}）"
        )
    for o in cfg.outputs:
        if o not in OUTPUTS:
            raise ConfigError(f"未知の output: {o}")
    if not cfg.outputs:
        raise ConfigError("outputs が空です")

    # --- RGB は色差を持たない
    if cfg.color_model == "rgb" and cfg.subsampling != "4:4:4":
        raise ConfigError(
            "color_model=rgb では subsampling は 4:4:4 のみです"
            f"（指定: {cfg.subsampling}）。色差の間引きは ycbcr で行ってください"
        )
    if cfg.color_model == "rgb" and cfg.range != "full":
        raise ConfigError(
            "この生成器では RGB を full range のみで扱います"
            "（limited RGB は matrix と合わせて別途定義が必要なため、初期版では対象外）"
        )

    # --- 格納形式ごとの制約
    spec = STORAGES[cfg.storage]
    if cfg.color_model not in spec["color_models"]:
        raise ConfigError(
            f"storage={cfg.storage} は color_model={cfg.color_model} に対応していません"
            f"（対応: {', '.join(spec['color_models'])}）"
        )
    if cfg.subsampling not in spec["subsamplings"]:
        raise ConfigError(
            f"storage={cfg.storage} は subsampling={cfg.subsampling} に対応していません"
            f"（対応: {', '.join(spec['subsamplings'])}）"
        )
    if cfg.bit_depth not in spec["bit_depths"]:
        raise ConfigError(
            f"storage={cfg.storage} は {cfg.bit_depth}bit に対応していません"
            f"（対応: {', '.join(str(b) for b in spec['bit_depths'])}bit）"
        )

    # --- p010 は上位詰めが定義そのもの
    if cfg.storage == "p010" and cfg.alignment != "msb":
        raise ConfigError(
            "storage=p010 は 16bit コンテナの上位詰めが定義に含まれます"
            "（alignment=msb を指定してください）"
        )

    # --- 画素数の割り切れ
    if cfg.subsampling in ("4:2:2", "4:2:0") and cfg.width % 2 != 0:
        raise ConfigError(
            f"subsampling={cfg.subsampling} では width が偶数である必要があります"
            f"（指定: {cfg.width}）"
        )
    if cfg.subsampling == "4:2:0" and cfg.height % 2 != 0:
        raise ConfigError(
            f"subsampling=4:2:0 では height が偶数である必要があります"
            f"（指定: {cfg.height}）"
        )

    # --- v210 は 6 画素単位で 4 ワードに詰まる
    if cfg.storage == "v210" and cfg.width % 6 != 0:
        raise ConfigError(
            "storage=v210 では width が 6 の倍数である必要があります"
            f"（指定: {cfg.width}）。端数の詰め方は実装ごとに揺れるため、"
            "この生成器では割り切れる幅のみを扱います"
        )

    # --- mipi10 は 4 サンプル単位
    if cfg.storage == "mipi10":
        for name, w in _plane_widths(cfg):
            if w % 4 != 0:
                raise ConfigError(
                    f"storage=mipi10 では各プレーンの幅が 4 の倍数である必要があります"
                    f"（{name} プレーンの幅が {w}）"
                )

    if "png" in cfg.outputs and cfg.width * cfg.height > 64_000_000:
        raise ConfigError("PNG プレビューには大きすぎるサイズです")


def version() -> str:
    """パッケージ版数。循環参照を避けるため、必要になった時点で読みます。"""
    from vtp import __version__

    return __version__


def describe_combinations(known_patterns: tuple[str, ...]) -> dict[str, Any]:
    """成立する組み合わせと、そのときの幅・高さの倍数を数え上げる.

    表を書き写すのではなく、``validate`` を実際に通して作ります。
    別の実装（GUI など）が同じ判定を持ちたくなったときに、規則を写すと必ずずれます。
    ここから出した結果を読ませれば、正解は ``validate`` の 1 箇所だけになります。

    幅・高さの倍数も、規則を書き出すのではなく**通る最小の値を探して**求めます。
    制約はどれも「N の倍数」の形なので、通る最小値がそのまま N です。
    """

    def passes(width: int, height: int, **kwargs: Any) -> bool:
        try:
            validate(Config(width=width, height=height, **kwargs), known_patterns)
        except ConfigError:
            return False
        return True

    def smallest(limit: int, probe: Callable[[int], bool]) -> int | None:
        for value in range(1, limit + 1):
            if probe(value):
                return value
        return None

    combinations: list[dict[str, Any]] = []
    for color_model in COLOR_MODELS:
        for subsampling in SUBSAMPLINGS:
            for bit_depth in BIT_DEPTHS:
                for storage in STORAGES:
                    for alignment in ALIGNMENTS:
                        for range_name in RANGES:
                            fixed = dict(
                                color_model=color_model, subsampling=subsampling,
                                bit_depth=bit_depth, storage=storage,
                                alignment=alignment, range=range_name,
                            )
                            # 高さを十分に割り切れる値へ固定してから、通る最小の幅を探します。
                            width_multiple = smallest(48, lambda w: passes(w, 48, **fixed))
                            if width_multiple is None:
                                continue
                            height_multiple = smallest(8, lambda h: passes(width_multiple * 8, h, **fixed))
                            if height_multiple is None:
                                continue
                            combinations.append({
                                **fixed,
                                "width_multiple": width_multiple,
                                "height_multiple": height_multiple,
                            })

    return {
        "generator": f"video-test-patterns {version()}",
        "patterns": list(known_patterns),
        "matrices": list(MATRICES),
        "outputs": list(OUTPUTS),
        "storages": [{"name": name, "description": spec["description"]} for name, spec in STORAGES.items()],
        "combinations": combinations,
    }


def _plane_widths(cfg: Config) -> list[tuple[str, int]]:
    if cfg.color_model == "rgb":
        return [("R", cfg.width), ("G", cfg.width), ("B", cfg.width)]
    if cfg.subsampling == "4:4:4":
        cw = cfg.width
    else:
        cw = cfg.width // 2
    return [("Y", cfg.width), ("Cb", cw), ("Cr", cw)]


def describe_storages() -> str:
    """``--list-storages`` 用の説明文を返す."""
    lines = []
    for name, spec in STORAGES.items():
        lines.append(f"  {name:<8} {spec['description']}")
        lines.append(
            f"           color_model={'/'.join(spec['color_models'])}"
            f"  subsampling={'/'.join(spec['subsamplings'])}"
            f"  bit_depth={'/'.join(str(b) for b in spec['bit_depths'])}"
        )
    return "\n".join(lines)

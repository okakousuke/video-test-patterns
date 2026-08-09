"""生成の流れをひとつにまとめる.

    パターン生成（float R'G'B'）
        ↓  color_convert : 量子化・Y'CbCr 変換
    成分プレーン（uint16 コード値）
        ↓  subsample     : 4:2:2 / 4:2:0
    成分プレーン（色差は小さい）
        ↓  pack          : planar / packed / nv12 / p010 / v210 / mipi10
    バイト列
        ↓  unpack        : 往復確認（詰め方の誤りをここで捕まえる）
    プレーン → PNG プレビュー / manifest

各段は前の段の結果しか見ません。段をまたいだ「気を利かせた補正」は入れません。
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np

from . import color_convert, manifest, pack, patterns, preview, subsample
from .config import Config
from .pack import Frame


@dataclass
class Result:
    frame: Frame
    raw: bytes
    outputs: dict[str, str]
    manifest: dict[str, Any]
    roundtrip_ok: bool


def build_frame(cfg: Config) -> Frame:
    """条件からプレーン群を作る（ファイルには触れない）."""
    rgb = patterns.render(cfg.pattern, cfg.width, cfg.height, cfg.pattern_options)

    if cfg.color_model == "rgb":
        codes = color_convert.rgb_to_codes(rgb, cfg.bit_depth)
        planes = (codes[:, :, 0].copy(), codes[:, :, 1].copy(), codes[:, :, 2].copy())
        return Frame(planes, cfg.color_model, cfg.subsampling, cfg.bit_depth)

    y, cb, cr = color_convert.rgb_to_ycbcr(rgb, cfg.matrix, cfg.range, cfg.bit_depth)
    cb = subsample.downsample(cb, cfg.subsampling)
    cr = subsample.downsample(cr, cfg.subsampling)
    return Frame((y, cb, cr), cfg.color_model, cfg.subsampling, cfg.bit_depth)


def verify_roundtrip(frame: Frame, raw: bytes, cfg: Config) -> bool:
    """詰めたバイト列を読み戻し、元のプレーンと一致するか確認する."""
    back = pack.unpack(raw, cfg)
    return all(
        np.array_equal(a, b) for a, b in zip(frame.planes, back.planes, strict=True)
    )


def generate(cfg: Config, out_base: str | Path | None = None) -> Result:
    """条件どおりに生成し、指定された出力を書き出す."""
    base = Path(out_base if out_base is not None else cfg.output)
    base.parent.mkdir(parents=True, exist_ok=True)

    frame = build_frame(cfg)
    raw = pack.pack(frame, cfg)

    expected = pack.expected_size(cfg)
    if len(raw) != expected:
        raise AssertionError(
            f"RAW のバイト数が計算値と一致しません: 実際 {len(raw)} / 期待 {expected}"
        )

    ok = verify_roundtrip(frame, raw, cfg)

    outputs: dict[str, str] = {}
    if "raw" in cfg.outputs:
        p = base.with_suffix(".raw")
        p.write_bytes(raw)
        outputs["raw"] = str(p)
    if "png" in cfg.outputs:
        p = base.with_suffix(".preview.png")
        preview.save_png(frame, cfg, p)
        outputs["png"] = str(p)

    doc = manifest.build(
        cfg,
        outputs,
        raw_size=len(raw),
        relative_to=base.parent,
        extra={"roundtrip_verified": ok},
    )
    if "json" in cfg.outputs:
        p = base.with_suffix(".manifest.json")
        manifest.write(doc, p)
        outputs["json"] = str(p)
        # manifest 自身のハッシュは載せない（自己参照になるため）

    return Result(frame=frame, raw=raw, outputs=outputs, manifest=doc, roundtrip_ok=ok)

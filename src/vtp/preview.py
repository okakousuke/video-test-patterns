"""PNG プレビューの書き出し.

プレビューは「理想のパターン」ではなく、**実際に格納したデータから戻した絵**
を描きます。4:2:0 で色差がつぶれた事実や、limited range でコード値が
16..235 に収まっている事実を、そのまま目で確認したいためです。

戻し方（色差の複製、逆行列）は最も単純な方法に固定しています。
プレビューは検証の出発点であって、画質を語るためのものではありません。
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

from .color_convert import codes_to_rgb, ycbcr_to_rgb
from .config import Config
from .pack import Frame
from .subsample import upsample


def frame_to_rgb8(frame: Frame, cfg: Config) -> np.ndarray:
    """プレーン群を 8bit RGB (h, w, 3) uint8 へ戻す."""
    if cfg.color_model == "rgb":
        rgb = codes_to_rgb(np.stack(frame.planes, axis=-1), cfg.bit_depth)
    else:
        y, cb, cr = frame.planes
        cb = upsample(cb, cfg.subsampling)
        cr = upsample(cr, cfg.subsampling)
        rgb = ycbcr_to_rgb(y, cb, cr, cfg.matrix, cfg.range, cfg.bit_depth)
    return np.clip(np.rint(rgb * 255.0), 0, 255).astype(np.uint8)


def save_png(frame: Frame, cfg: Config, path: str | Path) -> None:
    Image.fromarray(frame_to_rgb8(frame, cfg), mode="RGB").save(path, format="PNG")

"""色差サブサンプリング（4:4:4 / 4:2:2 / 4:2:0）と、その戻し.

間引きは「平均」で行います。単純な間引き（最近傍の 1 点を採る）でも
形式としては成立しますが、1 画素幅のハッチのように隣接画素が交互に
変わるパターンで、位相によって結果が変わってしまいます。
平均なら、どちらの位相でも同じ値になります。

戻し（アップサンプル）は複製（最近傍）です。プレビューで
「色差が間引かれた事実」をそのまま見せたいので、補間はしません。
"""

from __future__ import annotations

import numpy as np


def chroma_size(width: int, height: int, subsampling: str) -> tuple[int, int]:
    """色差プレーンの (幅, 高さ) を返す."""
    if subsampling == "4:4:4":
        return width, height
    if subsampling == "4:2:2":
        return width // 2, height
    if subsampling == "4:2:0":
        return width // 2, height // 2
    raise ValueError(f"未知の subsampling: {subsampling}")


def downsample(plane: np.ndarray, subsampling: str) -> np.ndarray:
    """色差プレーンを間引く（入力・出力とも uint16 のコード値）."""
    if subsampling == "4:4:4":
        return plane.copy()

    h, w = plane.shape
    if subsampling == "4:2:2":
        if w % 2:
            raise ValueError("4:2:2 では幅が偶数である必要があります")
        acc = plane.astype(np.uint32).reshape(h, w // 2, 2)
        return _round_div(acc.sum(axis=2), 2)

    if subsampling == "4:2:0":
        if w % 2 or h % 2:
            raise ValueError("4:2:0 では幅・高さが偶数である必要があります")
        acc = plane.astype(np.uint32).reshape(h // 2, 2, w // 2, 2)
        return _round_div(acc.sum(axis=(1, 3)), 4)

    raise ValueError(f"未知の subsampling: {subsampling}")


def upsample(plane: np.ndarray, subsampling: str) -> np.ndarray:
    """間引いた色差プレーンを輝度と同じ大きさへ戻す（最近傍複製）."""
    if subsampling == "4:4:4":
        return plane.copy()
    if subsampling == "4:2:2":
        return np.repeat(plane, 2, axis=1)
    if subsampling == "4:2:0":
        return np.repeat(np.repeat(plane, 2, axis=0), 2, axis=1)
    raise ValueError(f"未知の subsampling: {subsampling}")


def _round_div(total: np.ndarray, n: int) -> np.ndarray:
    """四捨五入付きの整数除算（(total + n//2) // n）."""
    return ((total + n // 2) // n).astype(np.uint16)

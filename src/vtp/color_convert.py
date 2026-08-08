"""R'G'B' と Y'CbCr の相互変換、および量子化.

ここで扱うのはすべて **非線形（ガンマ適用後）の R'G'B'** です。
BT.601 / BT.709 / BT.2020 の係数は、非線形信号に対して適用される
という前提で定義されています（いわゆる Y'CbCr）。

range の扱い:

- ``limited``: Y' は 16..235、Cb/Cr は 16..240 相当（bit_depth に応じて左シフト）
- ``full``   : Y' は 0..2^d-1、Cb/Cr は 0..2^d-1（中央値 2^(d-1)）
"""

from __future__ import annotations

import numpy as np

#: 輝度係数 (Kr, Kg, Kb)。Kg = 1 - Kr - Kb。
LUMA_COEFFS: dict[str, tuple[float, float, float]] = {
    "bt601": (0.299, 0.587, 0.114),
    "bt709": (0.2126, 0.7152, 0.0722),
    "bt2020": (0.2627, 0.6780, 0.0593),
}


def max_code(bit_depth: int) -> int:
    """その深度で表現できる最大コード値."""
    return (1 << bit_depth) - 1


def _levels(bit_depth: int, range_: str) -> tuple[float, float, float, float]:
    """(y_offset, y_scale, c_offset, c_scale) を返す."""
    if range_ == "limited":
        shift = 1 << (bit_depth - 8)
        return (16.0 * shift, 219.0 * shift, 128.0 * shift, 224.0 * shift)
    if range_ == "full":
        peak = float(max_code(bit_depth))
        return (0.0, peak, float(1 << (bit_depth - 1)), peak)
    raise ValueError(f"未知の range: {range_}")


def rgb_to_ycbcr(
    rgb: np.ndarray, matrix: str, range_: str, bit_depth: int
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """R'G'B' float [0,1] を Y'/Cb/Cr のコード値（uint16）へ変換する.

    戻り値の 3 つの配列はいずれも形状 (h, w) です。
    """
    if matrix not in LUMA_COEFFS:
        raise ValueError(f"未知の matrix: {matrix}")
    kr, kg, kb = LUMA_COEFFS[matrix]

    r = rgb[:, :, 0].astype(np.float64)
    g = rgb[:, :, 1].astype(np.float64)
    b = rgb[:, :, 2].astype(np.float64)

    y = kr * r + kg * g + kb * b            # [0, 1]
    cb = (b - y) / (2.0 * (1.0 - kb))       # [-0.5, 0.5]
    cr = (r - y) / (2.0 * (1.0 - kr))       # [-0.5, 0.5]

    y_off, y_scale, c_off, c_scale = _levels(bit_depth, range_)
    peak = max_code(bit_depth)

    yq = np.rint(y * y_scale + y_off)
    cbq = np.rint(cb * c_scale + c_off)
    crq = np.rint(cr * c_scale + c_off)

    out = tuple(
        np.clip(a, 0, peak).astype(np.uint16) for a in (yq, cbq, crq)
    )
    return out  # type: ignore[return-value]


def ycbcr_to_rgb(
    y: np.ndarray, cb: np.ndarray, cr: np.ndarray, matrix: str, range_: str, bit_depth: int
) -> np.ndarray:
    """Y'/Cb/Cr のコード値から R'G'B' float [0,1] へ戻す（プレビュー・検証用）."""
    kr, kg, kb = LUMA_COEFFS[matrix]
    y_off, y_scale, c_off, c_scale = _levels(bit_depth, range_)

    yf = (y.astype(np.float64) - y_off) / y_scale
    cbf = (cb.astype(np.float64) - c_off) / c_scale
    crf = (cr.astype(np.float64) - c_off) / c_scale

    r = yf + 2.0 * (1.0 - kr) * crf
    b = yf + 2.0 * (1.0 - kb) * cbf
    g = (yf - kr * r - kb * b) / kg

    rgb = np.stack([r, g, b], axis=-1)
    return np.clip(rgb, 0.0, 1.0).astype(np.float32)


def rgb_to_codes(rgb: np.ndarray, bit_depth: int) -> np.ndarray:
    """R'G'B' float [0,1] を full range のコード値（uint16, (h,w,3)）へ量子化する."""
    peak = max_code(bit_depth)
    return np.clip(np.rint(rgb.astype(np.float64) * peak), 0, peak).astype(np.uint16)


def codes_to_rgb(codes: np.ndarray, bit_depth: int) -> np.ndarray:
    """full range のコード値を R'G'B' float [0,1] へ戻す."""
    return (codes.astype(np.float32) / float(max_code(bit_depth))).astype(np.float32)

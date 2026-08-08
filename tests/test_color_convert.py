"""手計算できる値で色変換を確認する.

「絵を見て正しそう」ではなく、数値そのものを固定します。
ここが崩れると、以降のサブサンプリングやパッキングの検証が意味を失います。
"""

import numpy as np
import pytest

from vtp.color_convert import (
    codes_to_rgb,
    max_code,
    rgb_to_codes,
    rgb_to_ycbcr,
    ycbcr_to_rgb,
)


def _one_pixel(r, g, b):
    return np.array([[[r, g, b]]], dtype=np.float32)


def test_limited_range_white_and_black_8bit():
    """limited range の白は 235、黒は 16。色差はどちらも中央値 128."""
    y, cb, cr = rgb_to_ycbcr(_one_pixel(1, 1, 1), "bt709", "limited", 8)
    assert (int(y[0, 0]), int(cb[0, 0]), int(cr[0, 0])) == (235, 128, 128)

    y, cb, cr = rgb_to_ycbcr(_one_pixel(0, 0, 0), "bt709", "limited", 8)
    assert (int(y[0, 0]), int(cb[0, 0]), int(cr[0, 0])) == (16, 128, 128)


def test_full_range_white_and_black_8bit():
    y, cb, cr = rgb_to_ycbcr(_one_pixel(1, 1, 1), "bt709", "full", 8)
    assert (int(y[0, 0]), int(cb[0, 0]), int(cr[0, 0])) == (255, 128, 128)

    y, cb, cr = rgb_to_ycbcr(_one_pixel(0, 0, 0), "bt709", "full", 8)
    assert (int(y[0, 0]), int(cb[0, 0]), int(cr[0, 0])) == (0, 128, 128)


def test_limited_range_10bit_is_8bit_shifted():
    """limited range の基準値は深度で左シフトされる（16→64、235→940）."""
    y, _, _ = rgb_to_ycbcr(_one_pixel(0, 0, 0), "bt709", "limited", 10)
    assert int(y[0, 0]) == 64
    y, _, _ = rgb_to_ycbcr(_one_pixel(1, 1, 1), "bt709", "limited", 10)
    assert int(y[0, 0]) == 940


@pytest.mark.parametrize(
    "matrix,expected_y",
    [
        # Y = 16 + 219 * Kr で手計算できる
        ("bt601", round(16 + 219 * 0.299)),
        ("bt709", round(16 + 219 * 0.2126)),
        ("bt2020", round(16 + 219 * 0.2627)),
    ],
)
def test_pure_red_luma_depends_on_matrix(matrix, expected_y):
    """同じ赤でも matrix が違えば輝度が変わる（取り違えの検出点）."""
    y, _, _ = rgb_to_ycbcr(_one_pixel(1, 0, 0), matrix, "limited", 8)
    assert int(y[0, 0]) == expected_y


def test_pure_blue_cb_is_at_upper_bound():
    """純青の Cb は limited range の上限 240 に達する."""
    _, cb, _ = rgb_to_ycbcr(_one_pixel(0, 0, 1), "bt709", "limited", 8)
    assert int(cb[0, 0]) == 240


@pytest.mark.parametrize("matrix", ["bt601", "bt709", "bt2020"])
@pytest.mark.parametrize("range_", ["full", "limited"])
@pytest.mark.parametrize("bit_depth", [8, 10])
def test_roundtrip_is_close(matrix, range_, bit_depth):
    """RGB → Y'CbCr → RGB が量子化誤差の範囲で戻る."""
    rng = np.random.default_rng(1234)
    rgb = rng.random((8, 8, 3), dtype=np.float32)
    y, cb, cr = rgb_to_ycbcr(rgb, matrix, range_, bit_depth)
    back = ycbcr_to_rgb(y, cb, cr, matrix, range_, bit_depth)
    # limited range は 219/255 に圧縮されるぶん、誤差の上限が広がる
    tol = 3.0 / max_code(bit_depth) * (255 / 219 if range_ == "limited" else 1.0)
    assert np.max(np.abs(back - rgb)) < tol


def test_code_quantization_endpoints():
    codes = rgb_to_codes(np.array([[[0.0, 0.5, 1.0]]], dtype=np.float32), 10)
    assert codes[0, 0, 0] == 0
    assert codes[0, 0, 2] == 1023
    assert abs(int(codes[0, 0, 1]) - 512) <= 1
    back = codes_to_rgb(codes, 10)
    assert back[0, 0, 2] == pytest.approx(1.0)


def test_out_of_gamut_is_clipped_not_wrapped():
    """範囲外の入力が巡回して別の色になっていないこと."""
    y, cb, cr = rgb_to_ycbcr(_one_pixel(2.0, -1.0, 0.5), "bt709", "full", 8)
    for a in (y, cb, cr):
        assert 0 <= int(a[0, 0]) <= 255

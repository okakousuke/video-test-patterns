"""パターン生成そのものを確認する（色変換・格納形式を通す前の段階）."""

import numpy as np
import pytest

from vtp.patterns import PATTERN_NAMES, render


@pytest.mark.parametrize("name", PATTERN_NAMES)
def test_shape_dtype_and_range(name):
    img = render(name, 48, 32)
    assert img.shape == (32, 48, 3)
    assert img.dtype == np.float32
    assert img.min() >= 0.0 and img.max() <= 1.0


@pytest.mark.parametrize("name", PATTERN_NAMES)
def test_deterministic(name):
    """同じ条件なら常に同じ絵になること（manifest で再現できる前提）."""
    assert np.array_equal(render(name, 24, 16), render(name, 24, 16))


def test_colorbar_order_and_values():
    """幅 8 のカラーバーは、1 画素ずつ 8 色が並ぶ."""
    img = render("colorbar", 8, 1)
    expected = [
        (1, 1, 1), (1, 1, 0), (0, 1, 1), (0, 1, 0),
        (1, 0, 1), (1, 0, 0), (0, 0, 1), (0, 0, 0),
    ]
    for i, e in enumerate(expected):
        assert tuple(img[0, i]) == e


def test_colorbar75_is_scaled():
    img = render("colorbar75", 8, 1)
    assert tuple(img[0, 0]) == pytest.approx((0.75, 0.75, 0.75))
    assert tuple(img[0, 7]) == (0, 0, 0)


def test_colorbar_width_not_divisible_by_eight():
    """8 で割り切れない幅でも、8 本すべてが現れて総画素数が合うこと."""
    img = render("colorbar", 13, 1)
    colors = {tuple(px) for px in img[0]}
    assert len(colors) == 8
    assert img.shape[1] == 13


def test_grayramp_is_monotonic():
    row = render("grayramp", 64, 1)[0, :, 0]
    assert np.all(np.diff(row) >= 0)
    assert row[0] == pytest.approx(0.0)
    assert row[-1] == pytest.approx(1.0)


def test_graysteps_hits_both_endpoints():
    img = render("graysteps", 44, 1, {"steps": 11})
    values = sorted({float(v) for v in img[0, :, 0]})
    assert len(values) == 11
    assert values[0] == pytest.approx(0.0)
    assert values[-1] == pytest.approx(1.0)


def test_hatch_alternates_every_pixel():
    row = render("hatch", 8, 1, {"period": 2})[0, :, 0]
    assert list(row) == [1, 0, 1, 0, 1, 0, 1, 0]


def test_hatch_accepts_colours():
    """輝度が近く色差が遠い 2 色（赤・青）で縞を作れること."""
    img = render("hatch", 4, 1, {"period": 2, "on": [1, 0, 0], "off": [0, 0, 1]})
    assert tuple(img[0, 0]) == (1, 0, 0)
    assert tuple(img[0, 1]) == (0, 0, 1)


def test_hatch_both_is_checkerboard():
    img = render("hatch", 4, 4, {"period": 2, "orientation": "both"})[:, :, 0]
    assert img[0, 0] != img[0, 1]
    assert img[0, 0] != img[1, 0]
    assert img[0, 0] == img[1, 1]


def test_dots_are_sparse():
    img = render("dots", 64, 64, {"step": 16})
    assert int((img[:, :, 0] > 0.5).sum()) == 16  # 4x4 個


def test_grid_draws_border():
    img = render("grid", 32, 32, {"step": 8, "thickness": 1})[:, :, 0]
    assert img[0, :].min() == 1.0
    assert img[:, 0].min() == 1.0
    assert img[-1, :].min() == 1.0
    assert img[:, -1].min() == 1.0


def test_circles_are_centered():
    """同心円は中心について左右・上下対称になる."""
    img = render("circles", 64, 64, {"step": 8})[:, :, 0]
    assert np.array_equal(img, img[:, ::-1])
    assert np.array_equal(img, img[::-1, :])


def test_blocks_markers_differ_between_tiles():
    """タイルごとの二進マーカーが異なること（領域の入れ替わりを検出できる）."""
    img = render("blocks", 160, 120, {"cols": 4, "rows": 3})[:, :, 0]
    tiles = [img[0:40, 0:40], img[0:40, 40:80], img[40:80, 0:40]]
    assert not np.array_equal(tiles[0], tiles[1])
    assert not np.array_equal(tiles[0], tiles[2])


def test_unknown_pattern_raises():
    with pytest.raises(ValueError):
        render("nosuch", 8, 8)

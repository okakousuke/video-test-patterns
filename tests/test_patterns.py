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


# --- 追加パターン（放送・表示の確認でよく使われる形） ---


def test_smptebars_has_three_bands():
    """上段・中段・下段で並びが変わること（1 枚で別々の確認ができる）."""
    img = render("smptebars", 210, 300)[:, :, 0]
    top = img[10, :]
    middle = img[210, :]
    bottom = img[290, :]
    assert not np.array_equal(top, middle)
    assert not np.array_equal(middle, bottom)


def test_smptebars_top_starts_with_white_and_ends_with_blue():
    """上段の色順が崩れていないこと（R と B が入れ替わると並びが変わる）."""
    img = render("smptebars", 700, 300, {"level": 1.0})
    top = img[10]
    assert tuple(top[10]) == (1.0, 1.0, 1.0)     # 白
    assert tuple(top[-10]) == (0.0, 0.0, 1.0)    # 青


def test_pluge_steps_upward_from_black():
    """左端は黒そのもので、右へ行くほど明るくなること."""
    img = render("pluge", 240, 60, {"bars": 6, "delta": 0.06})[:, :, 0]
    row = img[30]
    # 白の基準を除いた範囲で、値が単調に増えること
    values = sorted(set(float(v) for v in row if v < 0.5))
    assert values[0] == 0.0
    assert values == sorted(values)
    assert abs(values[-1] - 0.06) < 1e-6


def test_pluge_cannot_express_sub_black():
    """黒より暗い帯は作れない（このモジュールの出力は [0, 1] のため）."""
    img = render("pluge", 64, 32)
    assert img.min() == 0.0


def test_multiburst_gets_finer_to_the_right():
    """右へ行くほど縞が細かくなること（変化の回数で数える）."""
    img = render("multiburst", 480, 32, {"periods": [16, 4]})[:, :, 0]
    row = img[16]
    left = row[160:280]
    right = row[300:420]
    transitions = lambda a: int(np.count_nonzero(np.diff(a) != 0))
    assert transitions(right) > transitions(left)


def test_window_area_matches_requested_ratio():
    """白い矩形の面積が指定した比率に近いこと."""
    img = render("window", 400, 400, {"size": 0.25})[:, :, 0]
    ratio = float((img > 0.5).sum()) / img.size
    assert abs(ratio - 0.25) < 0.01


def test_zoneplate_is_symmetric_and_peaks_at_center():
    """中心対称で、中心が明るいこと."""
    img = render("zoneplate", 128, 128)[:, :, 0]
    assert np.allclose(img, img[:, ::-1], atol=1e-6)
    assert np.allclose(img, img[::-1, :], atol=1e-6)
    assert img[64, 64] > 0.9


def _count_extrema(line):
    return int(np.count_nonzero(np.diff(np.sign(np.diff(line))) != 0))


def test_zoneplate_does_not_alias_within_the_inscribed_circle():
    """既定では、山と谷が理論どおりの数だけ数えられること.

    生成した時点で折り返していると、受け取った側の処理が原因なのか
    元の絵が原因なのかを切り分けられなくなる。折り返すと山谷が畳まれて
    数えられる数が理論値より減るので、その数で判定する。

    中心から半径 r までの半周期の数は max_frequency × scale で、
    scale は短辺の半分。既定 (0.5) なら 256 画素角で 64 になる。
    """
    size = 256
    scale = size / 2
    img = render("zoneplate", size, size)[:, :, 0]
    observed = _count_extrema(img[size // 2, size // 2 :])
    expected = 0.5 * scale
    assert abs(observed - expected) <= 3


def test_zoneplate_above_nyquist_folds_and_loses_cycles():
    """ナイキストを超える指定にすると、数えられる山谷が理論値より大きく減ること.

    「生成した時点で折り返す設定にもできる」ことと、
    既定がそうなっていないことを、同じ数え方で対比する。
    """
    size = 256
    scale = size / 2
    img = render("zoneplate", size, size, {"max_frequency": 2.0})[:, :, 0]
    observed = _count_extrema(img[size // 2, size // 2 :])
    expected = 2.0 * scale
    assert observed < expected * 0.75


def test_checker_is_black_and_white_only():
    img = render("checker", 64, 64, {"cols": 4, "rows": 4})
    assert set(np.unique(img).tolist()) == {0.0, 1.0}


def test_checker_neighbours_differ():
    """隣り合うマスの色が違うこと."""
    img = render("checker", 64, 64, {"cols": 4, "rows": 4})[:, :, 0]
    assert img[8, 8] != img[8, 24]
    assert img[8, 8] != img[24, 8]


def test_pulsebar_has_a_thin_line_and_a_wide_bar():
    """細い線と広い帯が別々に存在すること."""
    img = render("pulsebar", 800, 40, {"pulse": 2, "bar": 0.25})[:, :, 0]
    row = img[20]
    runs = []
    count = 0
    for value in row:
        if value > 0.5:
            count += 1
        elif count:
            runs.append(count)
            count = 0
    if count:
        runs.append(count)
    assert len(runs) == 2
    assert min(runs) == 2
    assert abs(max(runs) - 200) <= 1


# --- さらに追加したパターン ---


def test_colorbar_vertical_stacks_bands_top_to_bottom():
    """縦向きにすると、同じ色順が上から下へ並ぶこと."""
    img = render("colorbar", 80, 800, {"orientation": "vertical"})
    assert tuple(img[10, 40]) == (1.0, 1.0, 1.0)    # 上端は白
    assert tuple(img[-10, 40]) == (0.0, 0.0, 0.0)   # 下端は黒
    # 横方向には変化しないこと
    assert np.array_equal(img[:, 0, :], img[:, -1, :])


def test_colorbar_rejects_unknown_orientation():
    with pytest.raises(ValueError):
        render("colorbar", 16, 16, {"orientation": "diagonal"})


def test_splitbars_upper_and_lower_differ_in_amplitude():
    """上下で同じ色相・違う振幅になること."""
    img = render("splitbars", 80, 40, {"top": 1.0, "bottom": 0.5})
    top = img[5, 5]
    bottom = img[35, 5]
    assert tuple(top) == (1.0, 1.0, 1.0)
    assert tuple(bottom) == (0.5, 0.5, 0.5)


def test_rainbow_starts_and_ends_at_red():
    """色相を一周するので、両端はどちらも赤へ戻り、中央でシアンを通ること."""
    img = render("rainbow", 600, 8)
    left, middle, right = img[4, 0], img[4, 300], img[4, -1]
    assert left[0] > 0.9 and left[1] < 0.05 and left[2] < 0.05
    assert right[0] > 0.9 and right[1] < 0.05 and right[2] < 0.05
    assert middle[0] < 0.05 and middle[1] > 0.9 and middle[2] > 0.9


def test_rainbow_has_many_distinct_hues():
    """カラーバーと違い、色が連続して変わること."""
    img = render("rainbow", 600, 4)
    row = img[2]
    distinct = {tuple(np.round(v, 3)) for v in row}
    assert len(distinct) > 200


def test_sweep_gets_finer_towards_the_end():
    img = render("sweep", 800, 16)[:, :, 0]
    row = img[8]
    transitions = lambda a: int(np.count_nonzero(np.diff(np.sign(np.diff(a))) != 0))
    assert transitions(row[600:780]) > transitions(row[20:200])


def test_sweep_default_stays_within_nyquist():
    """既定では生成時点で折り返さないこと（数えられる山谷が理論値と合う）."""
    width = 1024
    img = render("sweep", width, 8)[:, :, 0]
    observed = int(np.count_nonzero(np.diff(np.sign(np.diff(img[4]))) != 0))
    # 全体の周期数は平均周波数 × 幅 = (start + end) / 2 × width。
    # 山と谷はその 2 倍あるので、数えられる極値は (start + end) × width になる。
    expected = (0.02 + 0.5) * width
    assert abs(observed - expected) <= 6


def test_shallowramp_uses_only_a_narrow_range():
    img = render("shallowramp", 256, 8, {"center": 0.5, "amplitude": 0.05})[:, :, 0]
    assert abs(float(img.min()) - 0.45) < 0.01
    assert abs(float(img.max()) - 0.55) < 0.01


def test_triangleramp_peaks_in_the_middle():
    img = render("triangleramp", 320, 8)[:, :, 0]
    row = img[4]
    assert int(np.argmax(row)) in range(155, 166)
    assert np.all(np.diff(row[:159]) >= -1e-6)
    assert np.all(np.diff(row[161:]) <= 1e-6)


def test_square_sides_have_the_same_pixel_count():
    """縦と横が同じ画素数であること（画素が正方でなければ長方形に見える）."""
    img = render("square", 400, 300, {"size": 0.5, "thickness": 2})[:, :, 0]
    lit_columns = np.flatnonzero(img.max(axis=0) > 0.5)
    lit_rows = np.flatnonzero(img.max(axis=1) > 0.5)
    # 中心の十字が伸びるぶんを除くため、枠そのものの範囲で比べる
    width_span = lit_columns[-1] - lit_columns[0] + 1
    height_span = lit_rows[-1] - lit_rows[0] + 1
    assert width_span == height_span == 150


def test_stepmatrix_covers_every_8bit_code():
    """既定の 16 × 16 で 256 階調をすべて置くこと."""
    img = render("stepmatrix", 320, 320)
    assert len(np.unique(img)) == 256
    assert float(img.min()) == 0.0
    assert float(img.max()) == 1.0


def test_stepmatrix_increases_in_raster_order():
    """左上から右下へ向かって明るくなること."""
    img = render("stepmatrix", 160, 160, {"cols": 4, "rows": 4})[:, :, 0]
    assert img[20, 20] < img[20, 60] < img[20, 100] < img[20, 140]
    assert img[20, 140] < img[60, 20]

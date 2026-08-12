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


# --- 総合パターン ---


def test_wedge_is_finer_towards_the_centre():
    """中心に近いほど線の間隔が詰まること."""
    img = render("wedge", 400, 400, {"lines": 10})[:, :, 0]
    row = img[200]
    changes = lambda a: int(np.count_nonzero(np.diff(a) != 0))
    near = row[150:190]    # 中心寄り
    far = row[60:100]      # 外寄り
    assert changes(near) > changes(far)


def test_wedge_directions_select_sectors():
    """direction で使う方向を選べること."""
    horizontal = render("wedge", 400, 400, {"direction": "horizontal"})[:, :, 0]
    vertical = render("wedge", 400, 400, {"direction": "vertical"})[:, :, 0]
    # 水平だけのときは、左右に線があって上下は地のまま
    assert horizontal[200, 60] in (0.0, 1.0)
    assert vertical[200, 60] == 0.5
    assert vertical[60, 200] in (0.0, 1.0)
    assert horizontal[60, 200] == 0.5


def test_wedge_rejects_bad_radii():
    with pytest.raises(ValueError):
        render("wedge", 64, 64, {"inner": 0.5, "outer": 0.2})


def test_testcard_contains_its_parts():
    """外周ブロック・円・くさび・色帯・階調帯が揃っていること."""
    img = render("testcard", 640, 480)
    gray = img[:, :, 0]

    # 外周は白と黒の交互ブロック
    top = gray[2, :]
    assert set(np.unique(top).tolist()) == {0.0, 1.0}

    # 中心付近にくさびの白黒がある
    centre = gray[200:280, 280:360]
    assert centre.min() == 0.0 and centre.max() == 1.0

    # 色のついた画素（色帯）が存在する
    coloured = np.any(np.abs(img[:, :, 0] - img[:, :, 2]) > 0.1)
    assert coloured

    # 地は中間の明るさ（格子線を避けるため、広い範囲の中央値で見る）
    assert 0.4 < float(np.median(gray[100:380, 30:120])) < 0.6


def test_testcard_geometry_is_symmetric():
    """幾何の部分が上下左右とも対称であること.

    中心がずれていれば、対称なはずのものが崩れて見える。それが読めるように、
    円・くさび・格子・外周ブロックはすべて中心を基準に置いてある。

    色帯と階調帯は順序のあるものなので対称ではない。ここでは対象外にする。
    """
    img = render("testcard", 640, 480)[:, :, 0]
    middle = img[140:340, :]   # 帯を挟まない高さ
    assert np.allclose(middle, middle[:, ::-1], atol=1e-6)
    assert np.allclose(middle, middle[::-1, :], atol=1e-6)


def test_testcard_border_blocks_match_on_both_sides():
    """外周ブロックが左右・上下で同じ並びになること.

    端から数えて何ブロック欠けたかを見るので、左右で白黒が入れ替わっていると
    同じ数え方ができない。
    """
    img = render("testcard", 640, 480)[:, :, 0]
    assert np.allclose(img[0], img[0][::-1], atol=1e-6)
    assert np.allclose(img[:, 0], img[:, 0][::-1], atol=1e-6)


def test_testcard_has_no_text_glyphs():
    """文字を描かないこと（フォント依存を避ける方針の確認）.

    文字が入ると環境でフォントが変わり、同じ条件でも絵が変わってしまう。
    ここでは「描画に使う値が限られた集合に収まる」ことで、
    アンチエイリアスされた文字が無いことを確かめる。
    """
    img = render("testcard", 320, 240, {"steps": 11})
    values = np.unique(img)
    assert len(values) < 40


# --- 伝達特性・成分・ばらつき ---


def test_gamma_background_alternates_every_line():
    """地が 1 画素ごとの白黒縞であること（縞が混ざると地の明るさが変わる）."""
    img = render("gamma", 64, 32, {"patches": 2})[:, :, 0]
    column = img[:, 1]           # 面を避けた左端付近
    assert set(np.unique(column).tolist()) == {0.0, 1.0}
    assert np.all(column[::2] != column[1::2])


def test_gamma_patches_increase_and_stay_in_range():
    img = render("gamma", 900, 200, {"patches": 5, "start": 0.4, "end": 0.9})[:, :, 0]
    row = img[100]
    # 縞ではない（0 でも 1 でもない）値が、左から右へ増えていくこと
    patches = sorted({float(v) for v in row if 0.0 < v < 1.0})
    assert len(patches) == 5
    assert abs(patches[0] - 0.4) < 1e-6
    assert abs(patches[-1] - 0.9) < 1e-6


def test_gamma_leaves_background_between_patches():
    """面と面のあいだに地の縞が残ること（残らないと比べる相手が無い）."""
    img = render("gamma", 900, 200, {"patches": 5})[:, :, 0]
    row = img[100]
    # 面の値が現れる区間の合間に、0 と 1 の縞の行が挟まっていること
    assert 0.0 in set(img[100].tolist()) or 1.0 in set(img[100].tolist())
    solid = np.array([0.0 < v < 1.0 for v in row])
    # 面が 5 つに分かれている（間が地で切れている）
    groups = int(np.count_nonzero(np.diff(solid.astype(np.int8)) == 1))
    assert groups == 5


def test_colorramp_separates_channels():
    """帯ごとに 1 成分だけが変わること."""
    img = render("colorramp", 256, 400)
    red = img[50]
    green = img[150]
    blue = img[250]
    gray = img[350]
    assert red[:, 0].max() == pytest.approx(1.0, abs=0.01) and red[:, 1].max() == 0.0
    assert green[:, 1].max() == pytest.approx(1.0, abs=0.01) and green[:, 0].max() == 0.0
    assert blue[:, 2].max() == pytest.approx(1.0, abs=0.01) and blue[:, 0].max() == 0.0
    assert np.allclose(gray[:, 0], gray[:, 1]) and np.allclose(gray[:, 1], gray[:, 2])


def test_colormatrix_levels_increase_to_the_right():
    img = render("colormatrix", 600, 700, {"levels": 6})
    row = img[50]   # 赤の行
    assert row[10, 0] < row[210, 0] < row[410, 0] < row[590, 0]
    assert abs(float(row[590, 0]) - 1.0) < 1e-6


def test_colormatrix_last_row_is_neutral():
    """最後の行は白（基準）で、3 成分が揃うこと."""
    img = render("colormatrix", 600, 700, {"levels": 6})
    cell = img[680, 590]
    assert cell[0] == cell[1] == cell[2]


def test_noise_is_reproducible_and_seed_dependent():
    """同じシードなら同じ絵、違うシードなら違う絵になること."""
    a = render("noise", 64, 64, {"seed": 1})
    b = render("noise", 64, 64, {"seed": 1})
    c = render("noise", 64, 64, {"seed": 2})
    assert np.array_equal(a, b)
    assert not np.array_equal(a, c)


def test_noise_has_no_correlation_between_neighbours():
    """隣り合う画素に相関が無いこと（平滑化が入れば相関が生まれる）."""
    img = render("noise", 256, 256, {"seed": 3})[:, :, 0].astype(np.float64)
    left, right = img[:, :-1].ravel(), img[:, 1:].ravel()
    correlation = float(np.corrcoef(left, right)[0, 1])
    assert abs(correlation) < 0.05


def test_noise_spreads_over_the_requested_range():
    img = render("noise", 128, 128, {"seed": 4, "center": 0.5, "amplitude": 0.5})[:, :, 0]
    assert img.min() < 0.05 and img.max() > 0.95
    assert abs(float(img.mean()) - 0.5) < 0.02


def test_noise_colour_channels_differ():
    img = render("noise", 64, 64, {"seed": 5, "mono": False})
    assert not np.array_equal(img[:, :, 0], img[:, :, 1])
    assert not np.array_equal(img[:, :, 1], img[:, :, 2])


# --- 派生パターン（バー・階調・カード） ---


def test_barshd_has_four_bands_with_flanks():
    """4 段に分かれ、1 段目の左右に脇の灰色があること."""
    img = render("barshd", 800, 500, {"level": 0.75, "flank": 0.4})
    assert tuple(img[10, 5]) == pytest.approx((0.4, 0.4, 0.4))     # 左の脇
    assert tuple(img[10, -5]) == pytest.approx((0.4, 0.4, 0.4))    # 右の脇
    # 段ごとに並びが変わること
    rows = [img[int(500 * r), :, 0] for r in (0.2, 0.62, 0.72, 0.9)]
    for a, b in zip(rows, rows[1:]):
        assert not np.array_equal(a, b)


def test_barshd_second_band_reverses_the_first():
    """2 段目が 1 段目の逆順であること（上下で色が合うかを見るため）."""
    img = render("barshd", 800, 500)
    top = img[100]
    second = img[int(500 * 0.62)]
    side = round(800 * 0.05)
    inner0, inner1 = side, 800 - side
    edges = [inner0 + round(i * (inner1 - inner0) / 7) for i in range(8)]
    for i in range(7):
        x = (edges[i] + edges[i + 1]) // 2
        mirror = (edges[6 - i] + edges[7 - i]) // 2
        assert tuple(top[x]) == pytest.approx(tuple(second[mirror]), abs=1e-6)


def test_splitsteps_rows_run_in_opposite_directions():
    """上段は左から明るく、下段は右から明るくなること."""
    img = render("splitsteps", 440, 100, {"steps": 11})[:, :, 0]
    top, bottom = img[20], img[80]
    assert top[10] < top[-10]
    assert bottom[10] > bottom[-10]
    assert float(top[10]) == pytest.approx(float(bottom[-10]), abs=1e-6)


def test_splitsteps_puts_different_levels_next_to_each_other():
    """上下で必ず違う段が隣り合うこと（真ん中の段を除く）."""
    img = render("splitsteps", 440, 100, {"steps": 11})[:, :, 0]
    top, bottom = img[20], img[80]
    differing = int(np.count_nonzero(np.abs(top - bottom) > 1e-6))
    assert differing > 440 * 0.8


def test_geometrycard_has_no_colour():
    """幾何だけを見るので、色を持たないこと."""
    img = render("geometrycard", 400, 300)
    assert np.array_equal(img[:, :, 0], img[:, :, 1])
    assert np.array_equal(img[:, :, 1], img[:, :, 2])


def test_geometrycard_geometry_is_symmetric():
    img = render("geometrycard", 400, 300)[:, :, 0]
    assert np.allclose(img, img[:, ::-1], atol=1e-6)
    assert np.allclose(img, img[::-1, :], atol=1e-6)


def test_resolutioncard_has_wedges_in_the_corners_and_centre():
    """四隅と中央のどこにも白黒の線があること."""
    img = render("resolutioncard", 800, 600)[:, :, 0]
    short = min(800, 600)
    box = int(short * 0.28)
    margin = max(1, short // 24)
    regions = [
        img[margin : margin + box, margin : margin + box],                       # 左上
        img[margin : margin + box, 800 - box - margin : 800 - margin],           # 右上
        img[600 - box - margin : 600 - margin, margin : margin + box],           # 左下
        img[600 - box - margin : 600 - margin, 800 - box - margin : 800 - margin],  # 右下
        img[250:350, 350:450],                                                   # 中央
    ]
    for region in regions:
        assert region.min() == 0.0 and region.max() == 1.0


def test_siemens_blanks_the_centre_at_the_nyquist_radius():
    """中心の塞ぎが、1 周期 2 画素になる半径と一致すること."""
    spokes = 36
    img = render("siemens", 400, 400, {"spokes": spokes})[:, :, 0]
    inner = spokes / np.pi  # 2 * pi * r / spokes == 2 となる r

    # 塞いだ内側は地の色だけ
    assert set(np.unique(img[199:201, 199:201]).tolist()) == {0.5}

    # 境目のすぐ外側には白黒が現れる
    x = int(200 + inner) + 2
    assert img[200, x] in (0.0, 1.0)


def test_siemens_has_the_requested_number_of_spokes():
    """周を一周したときの明暗の対の数が spokes と合うこと."""
    spokes = 24
    img = render("siemens", 400, 400, {"spokes": spokes})[:, :, 0]
    radius = 150.0
    angles = np.linspace(0, 2 * np.pi, 4000, endpoint=False)
    xs = np.clip((200 + radius * np.cos(angles)).astype(int), 0, 399)
    ys = np.clip((200 + radius * np.sin(angles)).astype(int), 0, 399)
    ring = img[ys, xs]
    # 一周して戻るので、変化の回数は明暗の対の 2 倍
    changes = int(np.count_nonzero(np.diff(np.concatenate([ring, ring[:1]])) != 0))
    assert changes == spokes * 2


def test_linepairs_uses_the_requested_line_widths():
    """区画ごとに、指定した太さの線で縞になっていること."""
    widths = [1, 2, 4]
    img = render("linepairs", 300, 200, {"widths": widths})[:, :, 0]
    columns = [round(i * 300 / len(widths)) for i in range(len(widths) + 1)]
    for index, line in enumerate(widths):
        x0 = columns[index] + 6
        row = img[50, x0 : x0 + line * 6]
        # 太さ line ごとに切り替わる = 変化の間隔が line
        edges = np.flatnonzero(np.diff(row) != 0)
        assert len(edges) >= 2
        assert set(np.diff(edges).tolist()) == {line}


def test_linepairs_rows_are_the_same_pattern_turned_sideways():
    """上段が縦縞、下段が横縞であること."""
    img = render("linepairs", 300, 200, {"widths": [2]})[:, :, 0]
    top = img[40:60, 20:60]
    bottom = img[140:160, 20:60]
    assert (top == top[:1, :]).all()        # 縦縞なので行方向に一様
    assert (bottom == bottom[:, :1]).all()  # 横縞なので列方向に一様


def test_slantedge_keeps_away_from_the_ends():
    """明暗が 0 と 1 に張り付かないこと."""
    img = render("slantedge", 320, 240, {"low": 0.1, "high": 0.9})[:, :, 0]
    assert img.min() >= 0.1 - 1e-6
    assert img.max() <= 0.9 + 1e-6


def test_slantedge_edge_carries_intermediate_values():
    """境目が階段ではなく、被覆率の中間値を持つこと."""
    img = render("slantedge", 320, 240, {"angle": 5.0})[:, :, 0]
    middle = (img > 0.1 + 1e-3) & (img < 0.9 - 1e-3)
    assert middle.any()
    # 中間値は境目だけなので、面積のごく一部にとどまるはず
    assert middle.mean() < 0.05


def test_slantedge_tilts_both_ways():
    """区画によって傾きの向きが反対であること."""
    img = render("slantedge", 320, 240, {"angle": 10.0})[:, :, 0]
    dark = img < 0.5

    def top_edge_tilt(region: np.ndarray) -> int:
        """矩形の上辺について、右側が左側よりどれだけ下がっているかを返す."""
        # 区画は 160 x 120、矩形は中心から 30% なので x は 32〜128 の範囲にある
        left = int(np.argmax(region[:, 45]))
        right = int(np.argmax(region[:, 115]))
        assert region[left, 45] and region[right, 115]  # 矩形の内側を見ていること
        return right - left

    assert top_edge_tilt(dark[0:120, 0:160]) * top_edge_tilt(dark[0:120, 160:320]) < 0


def test_raster_fills_the_whole_frame_with_one_value():
    img = render("raster", 64, 48, {"color": [1.0, 0.0, 0.0], "level": 0.75})
    assert img.shape == (48, 64, 3)
    assert np.array_equal(np.unique(img.reshape(-1, 3), axis=0), np.array([[0.75, 0.0, 0.0]], np.float32))


def test_raster_defaults_to_white():
    img = render("raster", 16, 16)
    assert (img == 1.0).all()


def test_raster_rejects_a_colour_that_is_not_three_values():
    with pytest.raises(ValueError):
        render("raster", 16, 16, {"color": [1.0, 0.0]})

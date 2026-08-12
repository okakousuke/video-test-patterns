"""人工パターンの生成.

このモジュールは「何を描くか」だけを担当し、ビット深度・色差・格納形式には
一切関与しません。出力はすべて **非線形 R'G'B' の float32、範囲 [0, 1]、
形状 (height, width, 3)** に統一します。

こう分けておくと、あとで出た不具合が「絵のバグ」なのか
「色変換・サブサンプリング・パッキングのバグ」なのかを切り分けられます。
"""

from __future__ import annotations

from typing import Any, Callable

import numpy as np

RGB = np.ndarray  # (h, w, 3) float32 in [0, 1]

# ---------------------------------------------------------------- 補助


def _canvas(width: int, height: int, value: float = 0.0) -> RGB:
    return np.full((height, width, 3), value, dtype=np.float32)


def _coords(width: int, height: int) -> tuple[np.ndarray, np.ndarray]:
    """画素中心の座標を返す（x は左から、y は上から）."""
    x = np.arange(width, dtype=np.float32) + 0.5
    y = np.arange(height, dtype=np.float32) + 0.5
    return np.meshgrid(x, y)


def _opt(options: dict[str, Any], key: str, default: Any) -> Any:
    return options.get(key, default)


# 8 色のカラーバー。輝度の高い順に並べる（この順が崩れればチャンネルの入れ替わり）。
BAR_COLORS = np.array(
    [
        (1, 1, 1),  # 白
        (1, 1, 0),  # 黄
        (0, 1, 1),  # シアン
        (0, 1, 0),  # 緑
        (1, 0, 1),  # マゼンタ
        (1, 0, 0),  # 赤
        (0, 0, 1),  # 青
        (0, 0, 0),  # 黒
    ],
    dtype=np.float32,
)


def _edges(total: int, count: int) -> list[int]:
    """total を count 等分する境界。端数は最後の区画へ寄せて挙動を固定する."""
    return [round(i * total / count) for i in range(count + 1)]


def _axis(width: int, height: int, orientation: str) -> np.ndarray:
    """向きに応じて 0→1 で進む座標を返す（画素中心基準）."""
    xx, yy = _coords(width, height)
    if orientation == "horizontal":
        return xx / width
    if orientation == "vertical":
        return yy / height
    raise ValueError(f"orientation は horizontal / vertical です（指定: {orientation}）")


def _gray(value: np.ndarray) -> RGB:
    """濃淡 1 面を RGB 3 面へ広げる."""
    return np.repeat(value.astype(np.float32)[:, :, None], 3, axis=2)


# 数字の字形（5 x 7）。フォントを使うと環境で結果が変わるので、字形をコードに持ちます。
# 同じ指定なら、どの機械で作っても 1 画素まで同じものが出ます。
_DIGIT_ROWS: dict[str, tuple[str, ...]] = {
    "0": (".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."),
    "1": ("..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."),
    "2": (".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"),
    "3": ("#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###."),
    "4": ("...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."),
    "5": ("#####", "#....", "####.", "....#", "....#", "#...#", ".###."),
    "6": ("..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."),
    "7": ("#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."),
    "8": (".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."),
    "9": (".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."),
    " ": (".....", ".....", ".....", ".....", ".....", ".....", "....."),
}

_DIGIT_WIDTH = 5
_DIGIT_HEIGHT = 7


def _text_size(text: str, scale: int) -> tuple[int, int]:
    """字形を scale 倍で置いたときの大きさ（幅, 高さ）を返す."""
    if not text:
        return 0, 0
    # 字間は 1 桁ぶんの scale。詰めすぎると隣の数字とつながって読めなくなります。
    return len(text) * (_DIGIT_WIDTH + 1) * scale - scale, _DIGIT_HEIGHT * scale


def _draw_text(img: RGB, text: str, x: int, y: int, scale: int, value: float) -> None:
    """(x, y) を左上として数字を書き込む（img を直接書き換える）."""
    height, width = img.shape[:2]
    cursor = x
    for character in text:
        rows = _DIGIT_ROWS.get(character)
        if rows is None:
            raise ValueError(f"字形を持っていない文字です: {character!r}")
        for row_index, row in enumerate(rows):
            for column_index, cell in enumerate(row):
                if cell != "#":
                    continue
                x0 = cursor + column_index * scale
                y0 = y + row_index * scale
                # 画面からはみ出す桁は書かずに落とします（端でも例外にしません）
                if x0 < 0 or y0 < 0 or x0 >= width or y0 >= height:
                    continue
                img[y0 : y0 + scale, x0 : x0 + scale, :] = np.float32(value)
        cursor += (_DIGIT_WIDTH + 1) * scale


# ---------------------------------------------------------------- パターン


def colorbar(width: int, height: int, options: dict[str, Any]) -> RGB:
    """8 色のカラーバー.

    ``level`` で 100% / 75% を切り替えます（既定は 100%）。
    色順は白・黄・シアン・緑・マゼンタ・赤・青・黒で、
    輝度の高い順に並びます。チャンネルの入れ替わりが起きると
    この並びが崩れるため、一目で分かります。

    ``orientation`` に ``horizontal``（既定・縦バーが横に並ぶ）か
    ``vertical``（横帯が縦に並ぶ）を指定します。走査の向きに依存する不具合は、
    バーの向きを変えると出方が変わります。
    """
    level = float(_opt(options, "level", 1.0))
    orientation = str(_opt(options, "orientation", "horizontal"))
    bars = BAR_COLORS * level

    img = _canvas(width, height)
    if orientation == "horizontal":
        edges = _edges(width, 8)
        for i in range(8):
            img[:, edges[i] : edges[i + 1], :] = bars[i]
    elif orientation == "vertical":
        edges = _edges(height, 8)
        for i in range(8):
            img[edges[i] : edges[i + 1], :, :] = bars[i]
    else:
        raise ValueError(f"colorbar の orientation は horizontal / vertical です（指定: {orientation}）")
    return img


def colorbar75(width: int, height: int, options: dict[str, Any]) -> RGB:
    opts = dict(options)
    opts.setdefault("level", 0.75)
    return colorbar(width, height, opts)


def grayramp(width: int, height: int, options: dict[str, Any]) -> RGB:
    """左から右への連続グラデーション（階調の欠落・バンディングの確認用）."""
    ramp = np.linspace(0.0, 1.0, width, dtype=np.float32)
    img = np.repeat(ramp[None, :, None], height, axis=0)
    return np.repeat(img, 3, axis=2)


def graysteps(width: int, height: int, options: dict[str, Any]) -> RGB:
    """等分割のグレーステップ（既定 11 段）.

    黒つぶれ・白飛びの確認に使います。``steps`` で段数を変えられます。
    """
    steps = int(_opt(options, "steps", 11))
    if steps < 2:
        raise ValueError("graysteps の steps は 2 以上にしてください")
    edges = [round(i * width / steps) for i in range(steps + 1)]
    img = _canvas(width, height)
    for i in range(steps):
        img[:, edges[i] : edges[i + 1], :] = np.float32(i / (steps - 1))
    return img


def frame(width: int, height: int, options: dict[str, Any]) -> RGB:
    """外周枠と安全領域枠.

    ``safe`` に安全領域の比率（既定 0.9 と 0.8）を渡します。
    オーバースキャン・アンダースキャンで、どの枠が欠けるかを見ます。
    """
    thickness = int(_opt(options, "thickness", max(1, min(width, height) // 240)))
    safes = _opt(options, "safe", [0.9, 0.8])
    img = _canvas(width, height, 0.0)

    def draw_rect(x0: int, y0: int, x1: int, y1: int, color: tuple[float, float, float]) -> None:
        t = thickness
        img[y0 : y0 + t, x0:x1] = color
        img[max(y0, y1 - t) : y1, x0:x1] = color
        img[y0:y1, x0 : x0 + t] = color
        img[y0:y1, max(x0, x1 - t) : x1] = color

    draw_rect(0, 0, width, height, (1.0, 1.0, 1.0))
    palette = [(1.0, 1.0, 0.0), (0.0, 1.0, 1.0), (1.0, 0.0, 1.0)]
    for i, s in enumerate(safes):
        s = float(s)
        w = int(width * s)
        h = int(height * s)
        x0 = (width - w) // 2
        y0 = (height - h) // 2
        draw_rect(x0, y0, x0 + w, y0 + h, palette[i % len(palette)])
    return img


def crosshair(width: int, height: int, options: dict[str, Any]) -> RGB:
    """中心線と目盛り（中心位置・アスペクト比のずれの確認用）."""
    thickness = int(_opt(options, "thickness", max(1, min(width, height) // 360)))
    tick = int(_opt(options, "tick", max(8, min(width, height) // 20)))
    img = _canvas(width, height, 0.0)

    cy0 = (height - thickness) // 2
    cx0 = (width - thickness) // 2
    img[cy0 : cy0 + thickness, :] = 1.0
    img[:, cx0 : cx0 + thickness] = 1.0

    tick_len = tick // 2
    for x in range(0, width, tick):
        img[0:tick_len, x : x + thickness] = (0.0, 1.0, 1.0)
        img[height - tick_len : height, x : x + thickness] = (0.0, 1.0, 1.0)
    for y in range(0, height, tick):
        img[y : y + thickness, 0:tick_len] = (1.0, 1.0, 0.0)
        img[y : y + thickness, width - tick_len : width] = (1.0, 1.0, 0.0)
    return img


def grid(width: int, height: int, options: dict[str, Any]) -> RGB:
    """等間隔の格子（歪み・射影変換のずれの確認用）."""
    step = int(_opt(options, "step", max(8, min(width, height) // 16)))
    thickness = int(_opt(options, "thickness", max(1, min(width, height) // 480)))
    img = _canvas(width, height, 0.0)
    for x in range(0, width, step):
        img[:, x : x + thickness] = 1.0
    for y in range(0, height, step):
        img[y : y + thickness, :] = 1.0
    img[:, width - thickness :] = 1.0
    img[height - thickness :, :] = 1.0
    return img


def circles(width: int, height: int, options: dict[str, Any]) -> RGB:
    """中心からの同心円（アスペクト比・レンズ歪みの確認用）."""
    step = int(_opt(options, "step", max(8, min(width, height) // 16)))
    thickness = float(_opt(options, "thickness", max(1.0, min(width, height) / 480)))
    xx, yy = _coords(width, height)
    cx, cy = width / 2.0, height / 2.0
    r = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    phase = np.abs((r % step) - step / 2.0)
    mask = phase > (step / 2.0 - thickness)
    img = _canvas(width, height, 0.0)
    img[mask] = 1.0
    return img


def radial(width: int, height: int, options: dict[str, Any]) -> RGB:
    """中心からの放射線（回転ずれ・歪みの確認用）."""
    spokes = int(_opt(options, "spokes", 36))
    xx, yy = _coords(width, height)
    ang = np.arctan2(yy - height / 2.0, xx - width / 2.0)
    sector = (ang + np.pi) / (2 * np.pi) * spokes
    mask = (np.floor(sector).astype(np.int32) % 2) == 0
    img = _canvas(width, height, 0.0)
    img[mask] = 1.0
    return img


def hatch(width: int, height: int, options: dict[str, Any]) -> RGB:
    """1 画素幅の白黒縦縞（サブサンプリング・スケーリングの劣化確認用）.

    ``orientation`` に ``vertical`` / ``horizontal`` / ``both`` を指定します。

    既定は白黒ですが、``on`` / ``off`` に RGB を渡すと色縞になります。
    白黒のままだと色差が一定なので 4:2:0 を通しても劣化が見えません。
    色差の間引きを目で見たい場合は、赤と青のように **輝度が近く色差が遠い**
    2 色を指定してください（例: ``--pattern-option on=[1,0,0] --pattern-option off=[0,0,1]``）。
    """
    period = int(_opt(options, "period", 2))
    orientation = str(_opt(options, "orientation", "vertical"))
    on = np.array(_opt(options, "on", [1.0, 1.0, 1.0]), dtype=np.float32)
    off = np.array(_opt(options, "off", [0.0, 0.0, 0.0]), dtype=np.float32)
    xx, yy = _coords(width, height)
    vx = (np.floor(xx) % period) < (period / 2)
    hy = (np.floor(yy) % period) < (period / 2)
    if orientation == "vertical":
        mask = vx
    elif orientation == "horizontal":
        mask = hy
    elif orientation == "both":
        mask = vx ^ hy
    else:
        raise ValueError("hatch の orientation は vertical / horizontal / both です")
    img = _canvas(width, height, 0.0)
    img[mask] = on
    img[~mask] = off
    return img


def dots(width: int, height: int, options: dict[str, Any]) -> RGB:
    """規則的な単画素ドット（ドット欠け・画素の欠落の確認用）."""
    step = int(_opt(options, "step", 16))
    img = _canvas(width, height, 0.0)
    img[::step, ::step] = 1.0
    return img


def blocks(width: int, height: int, options: dict[str, Any]) -> RGB:
    """タイル状の矩形に二進マーカーを描く（領域の重複・入れ替わりの確認用）.

    番号を数字で描くとフォント依存になるため、各タイルの上端へ
    タイル番号を二進で並べたマーカーを置きます。フォント無しで
    「どのタイルか」を機械的にも目視でも判定できます。
    """
    cols = int(_opt(options, "cols", 8))
    rows = int(_opt(options, "rows", 6))
    img = _canvas(width, height, 0.0)
    xs = [round(i * width / cols) for i in range(cols + 1)]
    ys = [round(i * height / rows) for i in range(rows + 1)]
    for r in range(rows):
        for c in range(cols):
            idx = r * cols + c
            shade = 0.25 + 0.5 * ((r + c) % 2)
            img[ys[r] : ys[r + 1], xs[c] : xs[c + 1]] = shade
            # 枠
            img[ys[r], xs[c] : xs[c + 1]] = 1.0
            img[ys[r + 1] - 1, xs[c] : xs[c + 1]] = 1.0
            img[ys[r] : ys[r + 1], xs[c]] = 1.0
            img[ys[r] : ys[r + 1], xs[c + 1] - 1] = 1.0
            # 二進マーカー
            bw = max(2, (xs[c + 1] - xs[c]) // 12)
            bh = max(2, (ys[r + 1] - ys[r]) // 12)
            for bit in range(8):
                on = (idx >> bit) & 1
                x0 = xs[c] + 2 + bit * bw
                if x0 + bw >= xs[c + 1] - 1:
                    break
                img[ys[r] + 2 : ys[r] + 2 + bh, x0 : x0 + bw] = 1.0 if on else 0.0
    return img


def smptebars(width: int, height: int, options: dict[str, Any]) -> RGB:
    """3 段構成のカラーバー（上段 75% バー、中段の反転バー、下段の黒レベル帯）.

    放送で古くから使われている構成を、公開されている一般的な定義から自作しています。
    上段だけのカラーバーと違い、1 枚で「色順」「青チャンネルの確認」「黒レベル」を
    同時に見られます。下段左寄りには、黒より少し暗い/明るい帯を並べます。

    ``level`` で上段の振幅（既定 0.75）、``pluge`` で下段の黒帯の振れ幅を指定します。
    """
    level = float(_opt(options, "level", 0.75))
    pluge = float(_opt(options, "pluge", 0.04))

    top_end = round(height * 2 / 3)
    middle_end = round(height * 3 / 4)

    img = _canvas(width, height)
    edges = [round(i * width / 7) for i in range(8)]

    # 上段: 白・黄・シアン・緑・マゼンタ・赤・青（黒を含めない 7 本）
    top = np.array(
        [(1, 1, 1), (1, 1, 0), (0, 1, 1), (0, 1, 0), (1, 0, 1), (1, 0, 0), (0, 0, 1)],
        dtype=np.float32,
    ) * level
    for i in range(7):
        img[:top_end, edges[i] : edges[i + 1], :] = top[i]

    # 中段: 上段の色順を逆にした青系の帯。青チャンネルだけを見たときに
    # 上段と中段が同じ明るさで並ぶかどうかで、青の再現を確認できます。
    middle = np.array(
        [(0, 0, 1), (0, 0, 0), (1, 0, 1), (0, 0, 0), (0, 1, 1), (0, 0, 0), (1, 1, 1)],
        dtype=np.float32,
    ) * level
    for i in range(7):
        img[top_end:middle_end, edges[i] : edges[i + 1], :] = middle[i]

    # 下段: 黒・白の基準と、黒のすぐ上を刻んだ帯。
    # 黒より暗い帯はこの段では作れません（出力が [0, 1] の正規化値のため）。
    bottom = img[middle_end:, :, :]
    bottom[:] = 0.0

    seg = max(1, round(width / 6))
    bottom[:, :seg, :] = np.float32(0.0)          # 基準の黒
    bottom[:, seg : seg * 2, :] = np.float32(1.0)  # 基準の白
    bottom[:, seg * 2 : seg * 3, :] = np.float32(0.0)

    steps = 3
    start = seg * 3
    band = max(1, (width - start) // steps)
    for index in range(steps):
        x0 = start + index * band
        x1 = start + (index + 1) * band if index < steps - 1 else width
        bottom[:, x0:x1, :] = np.float32(index * pluge / (steps - 1))

    return img


def pluge(width: int, height: int, options: dict[str, Any]) -> RGB:
    """黒のすぐ上を細かく刻んだ帯（黒レベル合わせの確認用）.

    黒地の上に、黒からわずかずつ明るい帯を並べます。左端は黒そのものなので必ず沈みます。
    そこから右へ、何本目まで黒と区別できるかを見ます。黒が浮いていれば左端まで全部見え、
    黒がつぶれていれば右のほうまで沈みます。白の基準を隣に置いて、
    黒側だけの問題か表示全体の問題かを切り分けます。

    ``delta`` は最も明るい帯の値（既定 0.06）、``bars`` は帯の本数（既定 6）です。

    なお **黒より暗い帯はここでは作れません**。このモジュールの出力は [0, 1] に
    正規化した値で、黒より下という概念を持たないためです。sub-black を扱うには、
    コード値の段（``color_convert``）で limited range の下限より小さい値を置く必要があります。
    """
    delta = float(_opt(options, "delta", 0.06))
    bars = int(_opt(options, "bars", 6))
    if bars < 2:
        raise ValueError("pluge の bars は 2 以上にしてください")

    img = _canvas(width, height, 0.0)

    band_height = max(1, height // 2)
    y0 = (height - band_height) // 2

    # 右端は白の基準にします。黒側の帯はその左へ並べます。
    reference = max(1, width // 8)
    reference_x = width - reference
    img[y0 : y0 + band_height, reference_x:, :] = 1.0

    usable = reference_x
    step = max(1, usable // bars)
    for index in range(bars):
        x0 = index * step
        x1 = (index + 1) * step if index < bars - 1 else usable
        if x0 >= usable:
            break
        img[y0 : y0 + band_height, x0:x1, :] = np.float32(index * delta / (bars - 1))

    return img


def multiburst(width: int, height: int, options: dict[str, Any]) -> RGB:
    """周波数の異なる縦縞を横に並べた帯（解像度・周波数特性の確認用）.

    左から右へ、縞の周期を細かくしていきます。どこまで縞が分離して見えるかで、
    解像限界や低域通過の効きが分かります。縞の振幅が右へ行くほど落ちるなら、
    その周波数で応答が下がっています。

    ``periods`` に周期（画素）を並べます。既定は 16, 8, 4, 3, 2 です。
    """
    periods = _opt(options, "periods", [16, 8, 4, 3, 2])
    periods = [int(p) for p in periods]
    if any(p < 2 for p in periods):
        raise ValueError("multiburst の periods は 2 以上にしてください")

    img = _canvas(width, height, 0.5)
    xx, _ = _coords(width, height)

    # 先頭に白と黒の基準を置き、そのあとに縞の束を並べます。
    lead = max(1, width // (len(periods) + 2))
    img[:, :lead, :] = 1.0
    img[:, lead : lead * 2, :] = 0.0

    remaining = width - lead * 2
    block = max(1, remaining // len(periods))
    for index, period in enumerate(periods):
        x0 = lead * 2 + index * block
        x1 = x0 + block if index < len(periods) - 1 else width
        if x0 >= width:
            break
        stripe = ((np.floor(xx[:, x0:x1]) % period) < (period / 2)).astype(np.float32)
        img[:, x0:x1, :] = stripe[:, :, None]

    return img


def window(width: int, height: int, options: dict[str, Any]) -> RGB:
    """黒地の中央に置いた白い矩形（レベルと表示の追従の確認用）.

    画面のうち白が占める割合を変えると、表示側の明るさ制御が働いて
    白の明るさ自体が変わることがあります。``size`` で面積比を変えて比べます。

    ``size`` は面積比（既定 0.25 = 画面の 1/4）、``level`` は矩形の明るさです。
    """
    size = float(_opt(options, "size", 0.25))
    level = float(_opt(options, "level", 1.0))
    if not 0.0 < size <= 1.0:
        raise ValueError("window の size は 0 より大きく 1 以下にしてください")

    img = _canvas(width, height, 0.0)
    ratio = float(np.sqrt(size))
    w = max(1, int(round(width * ratio)))
    h = max(1, int(round(height * ratio)))
    x0 = (width - w) // 2
    y0 = (height - h) // 2
    img[y0 : y0 + h, x0 : x0 + w, :] = np.float32(level)
    return img


def zoneplate(width: int, height: int, options: dict[str, Any]) -> RGB:
    """中心から外へ向かって細かくなる同心円の縞（折り返しの確認用）.

    半径の二乗に比例して周期が細かくなるため、1 枚で低い周波数から高い周波数までを
    連続して含みます。縮小やサブサンプリングで折り返し（エイリアス）が起きると、
    細かいはずの場所に大きな渦や別の模様が現れます。格子や同心円より見つけやすい絵です。

    ``max_frequency`` は、短辺の半径いっぱいの位置での縞の細かさ（cycles/pixel）です。
    既定の 0.5 はちょうどナイキスト周波数で、**この生成器の出力自体は折り返しません**。
    折り返しは、この絵を受け取った側が縮小や間引きをしたときに初めて現れます。
    0.5 より大きくすると生成した時点で折り返すので、比較の基準には向きません。

    四隅は短辺の半径より遠いため、既定でもナイキストを超えます。
    中心からの円の内側だけを判断に使ってください。
    """
    max_frequency = float(_opt(options, "max_frequency", 0.5))
    if max_frequency <= 0.0:
        raise ValueError("zoneplate の max_frequency は 0 より大きくしてください")

    xx, yy = _coords(width, height)
    cx = width / 2.0
    cy = height / 2.0
    scale = min(width, height) / 2.0

    # 位相 = π * a * r^2 とすると、半径 r での局所周波数は a * r になります。
    # r = scale で max_frequency にしたいので a = max_frequency / scale です。
    radius_squared = (xx - cx) ** 2 + (yy - cy) ** 2
    phase = np.pi * (max_frequency / scale) * radius_squared
    value = (0.5 + 0.5 * np.cos(phase)).astype(np.float32)
    return np.repeat(value[:, :, None], 3, axis=2)


def checker(width: int, height: int, options: dict[str, Any]) -> RGB:
    """白黒の市松模様（画素の抜け・反転・領域のずれの確認用）.

    ``cols`` と ``rows`` でマスの数を決めます。マスを細かくするほど、
    拡大縮小や圧縮での崩れが出やすくなります。
    """
    cols = int(_opt(options, "cols", 8))
    rows = int(_opt(options, "rows", 8))
    if cols < 1 or rows < 1:
        raise ValueError("checker の cols / rows は 1 以上にしてください")

    img = _canvas(width, height, 0.0)
    xs = [round(i * width / cols) for i in range(cols + 1)]
    ys = [round(i * height / rows) for i in range(rows + 1)]
    for r in range(rows):
        for c in range(cols):
            if (r + c) % 2 == 0:
                img[ys[r] : ys[r + 1], xs[c] : xs[c + 1], :] = 1.0
    return img


def pulsebar(width: int, height: int, options: dict[str, Any]) -> RGB:
    """細い縦線と幅の広い白帯を並べた絵（立ち上がりとにじみの確認用）.

    細い線は急な変化、白帯はゆるやかな変化に相当します。線だけが鈍る、
    帯の縁にだけ尾を引く、といった違いから、どの向きの変化が崩れているかを見ます。

    ``pulse`` で細い線の幅（画素）、``bar`` で白帯の幅の比を指定します。
    """
    pulse_width = int(_opt(options, "pulse", max(1, width // 160)))
    bar_ratio = float(_opt(options, "bar", 0.25))

    img = _canvas(width, height, 0.0)

    # 左寄りに細い線、右寄りに白帯を置きます。
    pulse_x = round(width * 0.25)
    img[:, pulse_x : pulse_x + max(1, pulse_width), :] = 1.0

    bar_w = max(1, int(round(width * bar_ratio)))
    bar_x = round(width * 0.55)
    img[:, bar_x : min(width, bar_x + bar_w), :] = 1.0

    return img


def splitbars(width: int, height: int, options: dict[str, Any]) -> RGB:
    """上下で振幅の違うカラーバーを並べる.

    上段を 100%、下段を 75% にすると、同じ色を振幅違いで隣り合わせに見られます。
    100% だけでは飽和して差が出ない条件でも、下段には余裕が残るため違いが現れます。
    上下の境目で色相がずれるなら、振幅によって色の出方が変わっています。

    ``top`` と ``bottom`` で各段の振幅を指定します（既定 1.0 と 0.75）。
    """
    top = float(_opt(options, "top", 1.0))
    bottom = float(_opt(options, "bottom", 0.75))

    img = _canvas(width, height)
    middle = height // 2
    edges = _edges(width, 8)
    for i in range(8):
        img[:middle, edges[i] : edges[i + 1], :] = BAR_COLORS[i] * top
        img[middle:, edges[i] : edges[i + 1], :] = BAR_COLORS[i] * bottom
    return img


def rainbow(width: int, height: int, options: dict[str, Any]) -> RGB:
    """色相を連続で変えた帯（色相の連続性・色域の確認用）.

    カラーバーが 8 点だけを見るのに対し、こちらは色相を切れ目なく回します。
    途中で色が飛ぶ、同じ色が続く、といった段差が出れば、その色相の付近で
    量子化や色域の丸めが起きています。

    ``orientation`` で向き、``saturation`` と ``value`` で彩度と明るさを指定します。
    """
    orientation = str(_opt(options, "orientation", "horizontal"))
    saturation = float(_opt(options, "saturation", 1.0))
    value = float(_opt(options, "value", 1.0))

    hue = _axis(width, height, orientation) * 6.0  # 0..6 の区間で 6 色を回る
    sector = np.floor(hue).astype(np.int32) % 6
    fraction = hue - np.floor(hue)

    up = value * (1.0 - saturation * (1.0 - fraction))
    down = value * (1.0 - saturation * fraction)
    low = np.full_like(hue, value * (1.0 - saturation))
    high = np.full_like(hue, value)

    # 赤 → 黄 → 緑 → シアン → 青 → マゼンタ の順に回します。
    table = [
        (high, up, low),
        (down, high, low),
        (low, high, up),
        (low, down, high),
        (up, low, high),
        (high, low, down),
    ]

    img = _canvas(width, height)
    for index, (r, g, b) in enumerate(table):
        mask = sector == index
        img[:, :, 0][mask] = r[mask]
        img[:, :, 1][mask] = g[mask]
        img[:, :, 2][mask] = b[mask]
    return img


def sweep(width: int, height: int, options: dict[str, Any]) -> RGB:
    """縞の細かさを端から端まで連続で変える（解像度の落ち方の確認用）.

    ``multiburst`` が決まった周波数を飛び飛びに置くのに対し、こちらは切れ目なく変えます。
    どこから縞が見えなくなるかが境目として読めるので、落ち始める位置を細かく追えます。

    ``end`` は終端での細かさ（cycles/pixel）です。既定の 0.5 はナイキストで、
    **生成した時点では折り返しません**。0.5 を超えると生成時点で折り返すため、
    受け取った側の処理が原因かどうかを切り分けられなくなります。
    """
    start = float(_opt(options, "start", 0.02))
    end = float(_opt(options, "end", 0.5))
    orientation = str(_opt(options, "orientation", "horizontal"))
    if start <= 0.0 or end <= 0.0:
        raise ValueError("sweep の start / end は 0 より大きくしてください")

    length = width if orientation == "horizontal" else height
    position = _axis(width, height, orientation) * length

    # 周波数を線形に変える（チャープ）。位相はその積分なので 2 次式になります。
    phase = 2.0 * np.pi * (start * position + (end - start) * position * position / (2.0 * length))
    return _gray(0.5 + 0.5 * np.cos(phase))


def shallowramp(width: int, height: int, options: dict[str, Any]) -> RGB:
    """狭い範囲だけを使うゆるやかな階調（量子化の段差の確認用）.

    全域を使うランプでは 1 段が細かすぎて段差が見えません。振れ幅を絞ると
    同じ画面幅を少ないコード値で埋めることになり、量子化の段差がそのまま縞として出ます。
    ビット深度を落としたときや、レベル変換を挟んだときの粗さを見るのに使います。

    ``center`` で中心の明るさ、``amplitude`` で上下の振れ幅を指定します。
    """
    center = float(_opt(options, "center", 0.5))
    amplitude = float(_opt(options, "amplitude", 0.05))
    orientation = str(_opt(options, "orientation", "horizontal"))

    position = _axis(width, height, orientation)
    value = center + (position * 2.0 - 1.0) * amplitude
    return _gray(np.clip(value, 0.0, 1.0))


def triangleramp(width: int, height: int, options: dict[str, Any]) -> RGB:
    """黒から白へ上がってまた黒へ戻る階調.

    片道のランプでは、上がりと下がりで挙動が違う処理を見分けられません。
    折り返しを 1 枚に入れておくと、左右で段差の出方が違うかどうかを同じ条件で比べられます。

    ``orientation`` で向きを指定します。
    """
    orientation = str(_opt(options, "orientation", "horizontal"))
    position = _axis(width, height, orientation)
    return _gray(1.0 - np.abs(position * 2.0 - 1.0))


def square(width: int, height: int, options: dict[str, Any]) -> RGB:
    """画素数で正方形になる枠（画素の縦横比の確認用）.

    短辺を基準に、縦と横が同じ画素数になる枠を描きます。表示側の画素が正方でなければ、
    この枠は長方形に見えます。``window`` が画面に対する面積比で矩形を置くのに対し、
    こちらは画素数で正方を作るので、目的が違います。

    ``size`` は短辺に対する一辺の比（既定 0.6）、``thickness`` は線の太さです。
    """
    size = float(_opt(options, "size", 0.6))
    if not 0.0 < size <= 1.0:
        raise ValueError("square の size は 0 より大きく 1 以下にしてください")
    thickness = int(_opt(options, "thickness", max(1, min(width, height) // 200)))

    side = max(1, int(round(min(width, height) * size)))
    x0 = (width - side) // 2
    y0 = (height - side) // 2

    img = _canvas(width, height, 0.0)
    t = max(1, min(thickness, side // 2))
    img[y0 : y0 + t, x0 : x0 + side, :] = 1.0
    img[y0 + side - t : y0 + side, x0 : x0 + side, :] = 1.0
    img[y0 : y0 + side, x0 : x0 + t, :] = 1.0
    img[y0 : y0 + side, x0 + side - t : x0 + side, :] = 1.0

    # 中心にも印を置き、枠が画面の中央にあるかを見られるようにします。
    cx, cy = width // 2, height // 2
    img[cy - t : cy + t + 1, cx - side // 8 : cx + side // 8, :] = 1.0
    img[cy - side // 8 : cy + side // 8, cx - t : cx + t + 1, :] = 1.0
    return img


def stepmatrix(width: int, height: int, options: dict[str, Any]) -> RGB:
    """階調を格子状に並べる（多階調の抜け・つぶれの確認用）.

    横一列のステップでは、段数を増やすと 1 段が細くなって見分けられません。
    格子に並べると、既定の 16 × 16 で 256 段を一度に置けます。
    8bit の全コード値を 1 枚で確かめられるので、どこで段が飛ぶかを面で探せます。

    ``cols`` と ``rows`` で段数を指定します（段数は cols × rows）。
    """
    cols = int(_opt(options, "cols", 16))
    rows = int(_opt(options, "rows", 16))
    if cols < 1 or rows < 1:
        raise ValueError("stepmatrix の cols / rows は 1 以上にしてください")

    total = cols * rows
    img = _canvas(width, height, 0.0)
    xs = _edges(width, cols)
    ys = _edges(height, rows)
    for r in range(rows):
        for c in range(cols):
            index = r * cols + c
            img[ys[r] : ys[r + 1], xs[c] : xs[c + 1], :] = np.float32(
                index / (total - 1) if total > 1 else 0.0
            )
    return img


def wedge(width: int, height: int, options: dict[str, Any]) -> RGB:
    """一点へ収束する線の束（向きごとの解像限界の確認用）.

    中心から放射状に線を引くので、中心へ近づくほど線の間隔が詰まります。
    どこで線がつながって見えるかが、その向きの解像限界です。

    ``radial`` が全周に線を回すのに対し、こちらは上下左右の 4 方向に分けて置きます。
    水平方向と垂直方向で限界が違う（片方だけ先に潰れる）ことがあるためです。

    ``lines`` は 1 つのくさびの本数、``direction`` は ``all`` / ``horizontal`` /
    ``vertical``、``inner`` と ``outer`` は短辺に対する内外の半径比です。
    """
    lines = int(_opt(options, "lines", 12))
    direction = str(_opt(options, "direction", "all"))
    inner_ratio = float(_opt(options, "inner", 0.05))
    outer_ratio = float(_opt(options, "outer", 0.46))
    background = float(_opt(options, "background", 0.5))
    if lines < 2:
        raise ValueError("wedge の lines は 2 以上にしてください")
    if direction not in ("all", "horizontal", "vertical"):
        raise ValueError(f"wedge の direction は all / horizontal / vertical です（指定: {direction}）")
    if not 0.0 <= inner_ratio < outer_ratio:
        raise ValueError("wedge は 0 <= inner < outer にしてください")

    xx, yy = _coords(width, height)
    cx, cy = width / 2.0, height / 2.0
    dx, dy = xx - cx, yy - cy

    radius = np.sqrt(dx * dx + dy * dy)
    angle = np.arctan2(dy, dx)
    short = min(width, height)

    # くさびは中心を挟んだ扇形。半角 22.5 度ずつ取り、中心付近は空けます。
    half = np.pi / 8.0
    horizontal = (np.abs(angle) < half) | (np.abs(angle) > np.pi - half)
    vertical = np.abs(np.abs(angle) - np.pi / 2.0) < half

    if direction == "horizontal":
        sector = horizontal
    elif direction == "vertical":
        sector = vertical
    else:
        sector = horizontal | vertical

    # 角度に対して等間隔に線を置くと、中心へ近づくほど間隔が詰まります。
    # cos を使うのは、上下左右のどちらへ折り返しても同じ形になるようにするためです。
    # sin（奇関数）だと鏡像で位相が反転し、中心がずれているのか縞がずれているのかを
    # 見分けられなくなります。lines * 2 は必ず偶数なので、左右の折り返しでも一致します。
    stripes = (np.cos(angle * lines * 2.0) > 0.0).astype(np.float32)
    band = sector & (radius > short * inner_ratio) & (radius < short * outer_ratio)

    value = np.full((height, width), background, dtype=np.float32)
    value[band] = stripes[band]
    return _gray(value)


def testcard(width: int, height: int, options: dict[str, Any]) -> RGB:
    """1 枚に確認要素をまとめた総合パターン（この実装の自作構成）.

    この種のパターンが共通して使う**部品**を組み合わせていますが、配置と比率は自分で決めたものです。
    特定の意匠を写したものではなく、文字・数字・ロゴは入れません
    （文字を入れるとフォントに依存し、環境で結果が変わるためでもあります）。

    入っている部品と、それぞれで見るもの。

    - 外周の交互ブロック : オーバースキャン。端の何ブロックが欠けたかで切れ量が読めます
    - 中央の円           : 画素の縦横比。楕円なら縦横比が崩れています
    - 中心のくさび       : 向きごとの解像限界。水平と垂直で違うことがあります
    - 格子               : レンズ歪み・射影変換のずれ
    - 上の色帯           : 色順とチャンネルの入れ替わり
    - 下の階調帯         : 黒つぶれ・白飛び

    ``background`` で地の明るさ、``blocks`` で外周ブロックの数、``grid`` で格子の間隔、
    ``steps`` で階調帯の段数を指定します。
    """
    background = float(_opt(options, "background", 0.5))
    blocks = int(_opt(options, "blocks", 16))
    grid_step = int(_opt(options, "grid", max(8, min(width, height) // 12)))
    steps = int(_opt(options, "steps", 11))
    if blocks < 2:
        raise ValueError("testcard の blocks は 2 以上にしてください")
    if steps < 2:
        raise ValueError("testcard の steps は 2 以上にしてください")

    img = _canvas(width, height, background)
    xx, yy = _coords(width, height)
    cx, cy = width / 2.0, height / 2.0
    short = min(width, height)
    thin = max(1, short // 400)

    # --- 格子（地の上に薄く） ---
    # 画面の端ではなく中心を基準に刻みます。端起点だと左右で位相がずれ、
    # 「中心からどれだけ離れているか」を数えられません。中心にも線が来ます。
    def lines_from_centre(coordinate: np.ndarray, centre: float) -> np.ndarray:
        offset = np.mod(coordinate - centre + grid_step / 2.0, grid_step) - grid_step / 2.0
        return np.abs(offset) < thin / 2.0 + 0.5

    grid_mask = lines_from_centre(xx, cx) | lines_from_centre(yy, cy)
    img[grid_mask] = np.float32(background * 0.55)

    # --- 中心のくさび（帯にかからない大きさに収める） ---
    wedges = wedge(width, height, {
        "lines": int(_opt(options, "wedge_lines", 10)),
        "inner": 0.045,
        "outer": 0.22,
        "background": background,
    })[:, :, 0]
    drawn = wedges != np.float32(background)
    img[drawn] = wedges[drawn][:, None]

    # --- 中央の円 ---
    circle_ratio = 0.44
    radius = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    ring = max(1.0, short / 300.0)
    img[np.abs(radius - short * circle_ratio) < ring] = 1.0

    # --- 色帯と階調帯。円の内側に収まる幅で置きます ---
    band = max(2, short // 16)
    offset = short * 0.32
    # 円の中心からその高さでの弦の半分。ここに収めれば円をはみ出しません。
    half_span = float(np.sqrt(max(0.0, (short * circle_ratio) ** 2 - offset ** 2)))
    x0 = max(0, int(cx - half_span))
    x1 = min(width, int(cx + half_span))

    def outline(y0: int, y1: int) -> None:
        """帯の外周に細い線を引く。

        地が中間の灰色なので、階調帯の真ん中あたりは地と同じ明るさになり、
        枠が無いと帯がどこまでか読めなくなる。
        """
        edge = max(1, thin)
        dark = np.float32(background * 0.35)
        img[y0 - edge : y0, x0:x1, :] = dark
        img[y1 : y1 + edge, x0:x1, :] = dark
        img[y0 - edge : y1 + edge, x0 - edge : x0, :] = dark
        img[y0 - edge : y1 + edge, x1 : x1 + edge, :] = dark

    top0 = max(1, int(cy - offset - band / 2))
    edges = [x0 + round(i * (x1 - x0) / 8) for i in range(9)]
    for i in range(8):
        img[top0 : top0 + band, edges[i] : edges[i + 1], :] = BAR_COLORS[i] * 0.75
    outline(top0, top0 + band)

    bottom0 = min(height - band - 1, int(cy + offset - band / 2))
    step_edges = [x0 + round(i * (x1 - x0) / steps) for i in range(steps + 1)]
    for i in range(steps):
        img[bottom0 : bottom0 + band, step_edges[i] : step_edges[i + 1], :] = np.float32(
            i / (steps - 1)
        )
    outline(bottom0, bottom0 + band)

    # --- 外周の交互ブロック（いちばん外側なので最後に描く） ---
    # 端ではなく中心から数えて色を決めます。端起点だと、ブロックが偶数個のときに
    # 左右（上下）で白黒が入れ替わり、「端から何ブロック欠けたか」を
    # 左右で同じように数えられなくなります。
    thickness = max(1, short // 40)
    step = max(1, round(width / blocks))

    def alternating(count: int, centre: float) -> np.ndarray:
        index = np.floor(np.abs(np.arange(count) + 0.5 - centre) / step)
        return np.where(index % 2 == 0, 1.0, 0.0).astype(np.float32)

    along_x = alternating(width, cx)
    along_y = alternating(height, cy)

    img[:thickness, :, :] = along_x[None, :, None]
    img[height - thickness :, :, :] = along_x[None, :, None]
    img[:, :thickness, :] = along_y[:, None, None]
    img[:, width - thickness :, :] = along_y[:, None, None]

    return img


def gamma(width: int, height: int, options: dict[str, Any]) -> RGB:
    """1 画素の白黒縞の中に、明るさの違う面を並べる（伝達特性の確認用）.

    地は 1 画素ごとの白黒縞です。光の量としては白と黒のちょうど中間になりますが、
    それがコード値のいくつに当たるかは、表示側の伝達特性（いわゆるガンマ）で変わります。
    並べた面のうち、地に溶けて見えなくなるものがその答えです。

    **等倍（100%）でしか成立しません。** 縮小や拡大が入ると縞が混ざり、
    地の明るさそのものが変わってしまいます。

    ``patches`` で面の数、``start`` と ``end`` で面の明るさの範囲を指定します。
    """
    patches = int(_opt(options, "patches", 9))
    start = float(_opt(options, "start", 0.35))
    end = float(_opt(options, "end", 0.95))
    if patches < 2:
        raise ValueError("gamma の patches は 2 以上にしてください")

    _, yy = _coords(width, height)
    img = _gray((np.floor(yy) % 2 < 1).astype(np.float32))

    # 面は横一列。面と面のあいだに地の縞が同じくらい残るよう、
    # 幅は「面の数 × 2」で割ります。隙間が狭いと、比べる相手の地が見えません。
    size = min(max(4, height // 3), max(4, width // (patches * 2)))
    y0 = (height - size) // 2
    gap = (width - size * patches) // (patches + 1)
    for i in range(patches):
        x0 = gap + i * (size + gap)
        if x0 + size > width:
            break
        level = start + (end - start) * i / (patches - 1)
        img[y0 : y0 + size, x0 : x0 + size, :] = np.float32(level)
    return img


def colorramp(width: int, height: int, options: dict[str, Any]) -> RGB:
    """R・G・B・グレーのグラデーションを並べる（成分ごとの段差の確認用）.

    grayramp は 3 成分が同じ値なので、1 つの成分だけで起きた段差が埋もれます。
    成分ごとに分けて並べると、どの成分で階調が落ちているかが分かります。
    いちばん下のグレーが比較の基準です。

    ``orientation`` で向きを指定します。
    """
    orientation = str(_opt(options, "orientation", "horizontal"))
    position = _axis(width, height, orientation)

    img = _canvas(width, height, 0.0)
    bands = [(0,), (1,), (2,), (0, 1, 2)]
    edges = _edges(height if orientation == "horizontal" else width, len(bands))

    for index, channels in enumerate(bands):
        if orientation == "horizontal":
            region = (slice(edges[index], edges[index + 1]), slice(None))
        else:
            region = (slice(None), slice(edges[index], edges[index + 1]))
        for channel in channels:
            img[region[0], region[1], channel] = position[region[0], region[1]]
    return img


def colormatrix(width: int, height: int, options: dict[str, Any]) -> RGB:
    """色を段階的に並べた表（色ごとの階調の確認用）.

    行が色、列が明るさです。カラーバーが最大値だけを見るのに対し、
    同じ色を薄いほうまで並べます。暗い側だけ色が転ぶ、特定の色だけ段が飛ぶ、
    といった偏りは、明るさを振らないと出てきません。

    ``levels`` で段数を指定します（既定 6）。
    """
    levels = int(_opt(options, "levels", 6))
    if levels < 2:
        raise ValueError("colormatrix の levels は 2 以上にしてください")

    hues = np.array(
        [
            (1, 0, 0),  # 赤
            (0, 1, 0),  # 緑
            (0, 0, 1),  # 青
            (0, 1, 1),  # シアン
            (1, 0, 1),  # マゼンタ
            (1, 1, 0),  # 黄
            (1, 1, 1),  # 白（基準）
        ],
        dtype=np.float32,
    )

    img = _canvas(width, height, 0.0)
    ys = _edges(height, len(hues))
    xs = _edges(width, levels)
    for r, hue in enumerate(hues):
        for c in range(levels):
            level = (c + 1) / levels
            img[ys[r] : ys[r + 1], xs[c] : xs[c + 1], :] = hue * level
    return img


def noise(width: int, height: int, options: dict[str, Any]) -> RGB:
    """画素ごとにばらついた模様（平滑化・圧縮の効き方の確認用）.

    隣り合う画素に相関が無いので、平滑化や圧縮が入ると真っ先に均されます。
    どのくらい残っているかで、経路のどこかで手が入っていないかを見ます。

    乱数は座標とシードから計算する固定の手続きで作ります。ライブラリの
    乱数実装に依存しないので、**同じシードなら環境が変わっても同じ絵**になります。

    ``seed`` で並び、``mono`` で白黒か色付きか、``center`` と ``amplitude`` で
    ばらつく範囲を指定します。
    """
    seed = int(_opt(options, "seed", 1))
    mono = bool(_opt(options, "mono", True))
    center = float(_opt(options, "center", 0.5))
    amplitude = float(_opt(options, "amplitude", 0.5))

    def hashed(channel: int) -> np.ndarray:
        """座標から 0..1 の値を作る。整数演算だけなので結果が環境で変わりません。"""
        mask = np.uint64(0xFFFFFFFF)
        x = np.arange(width, dtype=np.uint64)[None, :]
        y = np.arange(height, dtype=np.uint64)[:, None]
        h = (x * np.uint64(0x9E3779B1)) & mask
        h = (h ^ ((y * np.uint64(0x85EBCA77)) & mask)) & mask
        h = (h ^ ((np.uint64(seed * 2654435761 + channel * 40503)) & mask)) & mask
        h = (h ^ (h >> np.uint64(15))) & mask
        h = (h * np.uint64(0x2545F491)) & mask
        h = (h ^ (h >> np.uint64(13))) & mask
        h = (h * np.uint64(0x27220A95)) & mask
        h = (h ^ (h >> np.uint64(16))) & mask
        return (h.astype(np.float64) / float(1 << 32)).astype(np.float32)

    if mono:
        value = center + (hashed(0) - 0.5) * 2.0 * amplitude
        return _gray(np.clip(value, 0.0, 1.0))

    img = _canvas(width, height, 0.0)
    for channel in range(3):
        img[:, :, channel] = np.clip(center + (hashed(channel) - 0.5) * 2.0 * amplitude, 0.0, 1.0)
    return img


def barshd(width: int, height: int, options: dict[str, Any]) -> RGB:
    """4 段構成のカラーバー（HD 系で使われる考え方に沿った自作構成）.

    ``smptebars`` の 3 段構成に対し、こちらは段を分けて役割をはっきりさせています。

    - 1 段目 : 左右に灰色の脇を置いた 7 色のバー。脇があると、端の色だけ乱れる不具合が分かります
    - 2 段目 : 同じ 7 色を逆順に並べた帯。上下で色が合うかを見ます
    - 3 段目 : 端から端までのグラデーション。段差の出る位置を探します
    - 4 段目 : 黒・白の基準と、黒のすぐ上を刻んだ帯

    **特定の規格に合わせた値ではありません。** 段の比率も色の振幅も、この実装で決めたものです。
    規格の数値が要るときは、その規格の定義に従って ``level`` などを指定してください。

    ``level`` でバーの振幅（既定 0.75）、``flank`` で脇の灰色の明るさ（既定 0.4）、
    ``pluge`` で 4 段目の黒帯の振れ幅を指定します。
    """
    level = float(_opt(options, "level", 0.75))
    flank = float(_opt(options, "flank", 0.4))
    pluge_delta = float(_opt(options, "pluge", 0.06))

    img = _canvas(width, height, 0.0)
    rows = [round(height * r) for r in (0.0, 0.58, 0.68, 0.78, 1.0)]

    # 左右の脇。ここを除いた範囲に 7 色を並べます。
    side = max(1, round(width * 0.05))
    inner0, inner1 = side, max(side + 7, width - side)
    edges = [inner0 + round(i * (inner1 - inner0) / 7) for i in range(8)]
    seven = BAR_COLORS[:7] * level

    # 1 段目
    img[rows[0] : rows[1], :inner0, :] = np.float32(flank)
    img[rows[0] : rows[1], inner1:, :] = np.float32(flank)
    for i in range(7):
        img[rows[0] : rows[1], edges[i] : edges[i + 1], :] = seven[i]

    # 2 段目（逆順）
    img[rows[1] : rows[2], :inner0, :] = np.float32(flank)
    img[rows[1] : rows[2], inner1:, :] = np.float32(flank)
    for i in range(7):
        img[rows[1] : rows[2], edges[i] : edges[i + 1], :] = seven[6 - i]

    # 3 段目（グラデーション）
    ramp = np.linspace(0.0, 1.0, max(1, inner1 - inner0), dtype=np.float32)
    img[rows[2] : rows[3], :inner0, :] = np.float32(flank)
    img[rows[2] : rows[3], inner1:, :] = np.float32(flank)
    img[rows[2] : rows[3], inner0:inner1, :] = ramp[None, :, None]

    # 4 段目（黒・白の基準と、黒のすぐ上の刻み）
    bottom = img[rows[3] : rows[4], :, :]
    bottom[:] = np.float32(flank)
    segments = _edges(inner1 - inner0, 6)
    values = [0.0, 1.0, 0.0, 0.0, pluge_delta / 2.0, pluge_delta]
    for i, value in enumerate(values):
        bottom[:, inner0 + segments[i] : inner0 + segments[i + 1], :] = np.float32(value)
    return img


def splitsteps(width: int, height: int, options: dict[str, Any]) -> RGB:
    """上下で並びを逆にしたグレーステップ.

    上段は左から明るく、下段は右から明るくします。こうすると上下で必ず違う段が隣り合うので、
    「隣の段と見分けられるか」を段ごとに確かめられます。
    横一列だけだと、隣り合う段の差はいつも同じ幅で、境目が見えるかどうかしか分かりません。

    ``steps`` で段数を指定します（既定 11）。
    """
    steps = int(_opt(options, "steps", 11))
    if steps < 2:
        raise ValueError("splitsteps の steps は 2 以上にしてください")

    img = _canvas(width, height, 0.0)
    middle = height // 2
    edges = _edges(width, steps)
    for i in range(steps):
        level = np.float32(i / (steps - 1))
        img[:middle, edges[i] : edges[i + 1], :] = level
        img[middle:, edges[steps - 1 - i] : edges[steps - i], :] = level
    return img


def geometrycard(width: int, height: int, options: dict[str, Any]) -> RGB:
    """幾何の確認に寄せたテストカード（この実装の自作構成）.

    ``testcard`` が 1 枚で広く見るのに対し、こちらは形のずれだけを見ます。
    色も階調も入れないぶん、線が読みやすくなります。

    - 外周の交互ブロック : 画角の欠け
    - 全面の格子         : 歪み・射影変換のずれ
    - 中央と四隅の円     : 画素の縦横比。四隅は周辺の歪みを見ます
    - 対角線             : 中心のずれ。交点が画面中央から外れていれば位置がずれています

    ``grid`` で格子の間隔、``blocks`` で外周ブロックの数を指定します。
    """
    grid_step = int(_opt(options, "grid", max(8, min(width, height) // 16)))
    blocks = int(_opt(options, "blocks", 16))
    if blocks < 2:
        raise ValueError("geometrycard の blocks は 2 以上にしてください")

    img = _canvas(width, height, 0.0)
    xx, yy = _coords(width, height)
    cx, cy = width / 2.0, height / 2.0
    short = min(width, height)
    thin = max(1, short // 480)

    # 格子は中心を基準に刻みます（端起点だと左右で位相がずれます）。
    def lines_from_centre(coordinate: np.ndarray, centre: float) -> np.ndarray:
        offset = np.mod(coordinate - centre + grid_step / 2.0, grid_step) - grid_step / 2.0
        return np.abs(offset) < thin / 2.0 + 0.5

    img[lines_from_centre(xx, cx) | lines_from_centre(yy, cy)] = 0.45

    # 対角線
    diagonal = (np.abs((yy - cy) * width - (xx - cx) * height) < thin * max(width, height) / 2.0) | \
               (np.abs((yy - cy) * width + (xx - cx) * height) < thin * max(width, height) / 2.0)
    img[diagonal] = 0.7

    # 円（中央と四隅）
    ring = max(1.0, short / 300.0)
    corner = short * 0.16
    centres = [
        (cx, cy, short * 0.44),
        (corner, corner, corner * 0.7),
        (width - corner, corner, corner * 0.7),
        (corner, height - corner, corner * 0.7),
        (width - corner, height - corner, corner * 0.7),
    ]
    for x0, y0, r in centres:
        radius = np.sqrt((xx - x0) ** 2 + (yy - y0) ** 2)
        img[np.abs(radius - r) < ring] = 1.0

    # 外周の交互ブロック（中心起点で左右・上下を揃えます）
    thickness = max(1, short // 40)
    step = max(1, round(width / blocks))

    def alternating(count: int, centre: float) -> np.ndarray:
        index = np.floor(np.abs(np.arange(count) + 0.5 - centre) / step)
        return np.where(index % 2 == 0, 1.0, 0.0).astype(np.float32)

    along_x = alternating(width, cx)
    along_y = alternating(height, cy)
    img[:thickness, :, :] = along_x[None, :, None]
    img[height - thickness :, :, :] = along_x[None, :, None]
    img[:, :thickness, :] = along_y[:, None, None]
    img[:, width - thickness :, :] = along_y[:, None, None]
    return img


def resolutioncard(width: int, height: int, options: dict[str, Any]) -> RGB:
    """解像の確認に寄せたテストカード（この実装の自作構成）.

    中央だけでなく四隅にもくさびを置きます。レンズや処理によっては、
    画面の中央と周辺で解像限界が違うためです。上下には周波数の違う縞を並べ、
    どのくらいの細かさから落ちるかを合わせて読めるようにしています。

    ``lines`` でくさびの本数、``periods`` で縞の周期を指定します。
    """
    lines = int(_opt(options, "lines", 10))
    periods = [int(p) for p in _opt(options, "periods", [16, 8, 4, 3, 2])]
    background = float(_opt(options, "background", 0.5))
    if any(p < 2 for p in periods):
        raise ValueError("resolutioncard の periods は 2 以上にしてください")

    img = _canvas(width, height, background)
    short = min(width, height)

    # 中央のくさび
    centre = wedge(width, height, {
        "lines": lines, "inner": 0.035, "outer": 0.17, "background": background,
    })[:, :, 0]
    drawn = centre != np.float32(background)
    img[drawn] = centre[drawn][:, None]

    # 四隅のくさび。小さい絵を作って貼り付けます。
    box = int(short * 0.28)
    if box >= 8:
        corner = wedge(box, box, {
            "lines": lines, "inner": 0.06, "outer": 0.46, "background": background,
        })
        margin = max(1, short // 24)
        spots = [
            (margin, margin),
            (width - box - margin, margin),
            (margin, height - box - margin),
            (width - box - margin, height - box - margin),
        ]
        for x0, y0 in spots:
            if x0 < 0 or y0 < 0 or x0 + box > width or y0 + box > height:
                continue
            region = img[y0 : y0 + box, x0 : x0 + box, :]
            mask = corner[:, :, 0] != np.float32(background)
            region[mask] = corner[mask]

    # 上下の縞。左から右へ細かくしていきます。
    band = max(2, short // 14)
    xx, _ = _coords(width, height)
    span0 = int(width * 0.22)
    span1 = int(width * 0.78)
    block = max(1, (span1 - span0) // len(periods))
    for row0 in (int(height * 0.12), int(height * 0.88) - band):
        for index, period in enumerate(periods):
            x0 = span0 + index * block
            x1 = x0 + block if index < len(periods) - 1 else span1
            if x0 >= width:
                break
            stripe = ((np.floor(xx[row0 : row0 + band, x0:x1]) % period) < (period / 2)).astype(np.float32)
            img[row0 : row0 + band, x0:x1, :] = stripe[:, :, None]
    return img


def siemens(width: int, height: int, options: dict[str, Any]) -> RGB:
    """中心へ向かって細くなる放射状のくさび（解像限界と折り返しの確認用）.

    くさびは中心へ近づくほど詰まるので、**どの半径まで縞が分かれて見えるか**が
    そのまま解像限界になります。限界を越えたところでは縞が消えるだけでなく、
    渦のような模様が現れることがあります。これは折り返しで、
    「見えなくなる」より「無いものが見える」ほうが厄介なので、ここで見ておきます。

    中心近くは、この生成器の側でも表現できません。半径 r での 1 周期は
    2πr / spokes 画素なので、2 画素を割る半径（r < spokes / π）から内側は
    書いた時点で折り返しています。そこは灰色で塞いであります。
    塞いだ内側に模様が出たら、それは表示側ではなく拡大処理の影響です。

    ``spokes`` でくさびの本数（明暗の対の数）を指定します。
    """
    spokes = int(_opt(options, "spokes", 36))
    background = float(_opt(options, "background", 0.5))
    if spokes < 4:
        raise ValueError(f"siemens の spokes は 4 以上にしてください（指定: {spokes}）")

    xx, yy = _coords(width, height)
    dx = xx - width / 2
    dy = yy - height / 2
    radius = np.hypot(dx, dy)

    outer = min(width, height) / 2 - 1
    inner = spokes / np.pi  # ここから内側は 1 周期が 2 画素を切る

    value = (np.cos(np.arctan2(dy, dx) * spokes) >= 0).astype(np.float32)
    img = _canvas(width, height, background)
    ring = (radius >= inner) & (radius <= outer)
    img[ring] = value[ring][:, None]
    return img


def linepairs(width: int, height: int, options: dict[str, Any]) -> RGB:
    """線幅ごとの縞を並べた区画（どの太さから潰れるかを段で読む）.

    くさびが連続的に細くなるのに対し、こちらは**太さが飛び飛び**です。
    「1 画素は駄目だが 2 画素なら出る」のように、境目を言葉にしやすくなります。

    上段は縦縞、下段は横縞で、同じ太さを並べてあります。
    向きで結果が変わるなら、原因は解像そのものではなく、
    走査方向やインタレース、あるいは色差の間引き方向のほうです。

    ``widths`` に線の太さ（画素）を並べて指定します。
    """
    widths = [int(w) for w in _opt(options, "widths", [1, 2, 3, 4, 6, 8])]
    background = float(_opt(options, "background", 0.5))
    if not widths or any(w < 1 for w in widths):
        raise ValueError("linepairs の widths は 1 以上の値を並べてください")

    img = _canvas(width, height, background)
    xx, yy = _coords(width, height)
    columns = _edges(width, len(widths))
    rows = _edges(height, 2)
    margin = max(1, min(width, height) // 64)

    for index, line in enumerate(widths):
        x0, x1 = columns[index] + margin, columns[index + 1] - margin
        if x1 - x0 < 2:
            continue
        for row, axis in ((0, xx), (1, yy)):
            y0, y1 = rows[row] + margin, rows[row + 1] - margin
            if y1 - y0 < 2:
                continue
            # 区画の左上を起点にすることで、どの区画も白から始まります。
            offset = axis[y0:y1, x0:x1] - (x0 if row == 0 else y0)
            stripe = ((np.floor(offset) // line) % 2 == 0).astype(np.float32)
            img[y0:y1, x0:x1, :] = stripe[:, :, None]
    return img


def slantedge(width: int, height: int, options: dict[str, Any]) -> RGB:
    """わずかに傾いた明暗の境目を 4 つ置いたもの（立ち上がりの確認用）.

    真上や真横の境目は画素の並びと重なってしまうので、1 行だけ見ても
    どこで切り替わったのかしか分かりません。少し傾けると、行ごとに
    境目の位置が画素の内側でずれていきます。これを縦に集めると、
    画素より細かい間隔で立ち上がりを並べ直せます。

    傾きの境目は階段にせず、画素にかかった面積の割合で中間値を入れています。
    ここに出る中間値は**ぼけではなく被覆率**です。混同すると、
    元から入っている中間値をぼけと読み違えます。
    面積は距離に比例させた近似で、傾きが小さいうちは十分実用になります。

    明暗は 0 と 1 ではなく少し内側に置いてあります。端で張り付くと、
    立ち上がりの足が切れて読めなくなるためです。
    ``angle`` で傾き（度）、``low`` / ``high`` で明暗を指定します。
    """
    angle = float(_opt(options, "angle", 5.0))
    low = float(_opt(options, "low", 0.1))
    high = float(_opt(options, "high", 0.9))
    if not 0.5 <= abs(angle) <= 45.0:
        raise ValueError(f"slantedge の angle は 0.5〜45 度にしてください（指定: {angle}）")

    img = _canvas(width, height, high)
    xx, yy = _coords(width, height)
    columns = _edges(width, 2)
    rows = _edges(height, 2)
    radians = np.deg2rad(angle)
    cos, sin = np.cos(radians), np.sin(radians)

    # 傾きの向きを変えた区画を並べます。傾ける向きで結果が変わるなら、
    # 対称でない処理（片側にだけ寄る補間など）が入っています。
    for row in range(2):
        for column in range(2):
            y0, y1 = rows[row], rows[row + 1]
            x0, x1 = columns[column], columns[column + 1]
            sign = 1.0 if (row + column) % 2 == 0 else -1.0
            u = xx[y0:y1, x0:x1] - (x0 + x1) / 2
            v = yy[y0:y1, x0:x1] - (y0 + y1) / 2
            # 区画の中心まわりに座標を回してから、軸に沿った矩形として塗ります。
            ru = u * cos + sign * v * sin
            rv = -sign * u * sin + v * cos
            half_w = (x1 - x0) * 0.3
            half_h = (y1 - y0) * 0.3
            # 矩形の内側で正になる距離。辺から離れるほど大きくなります。
            depth = np.minimum(half_w - np.abs(ru), half_h - np.abs(rv))
            coverage = np.clip(depth + 0.5, 0.0, 1.0).astype(np.float32)
            img[y0:y1, x0:x1, :] = (high - (high - low) * coverage)[:, :, None]
    return img


def raster(width: int, height: int, options: dict[str, Any]) -> RGB:
    """画面全体を 1 色で塗る（むら・純度・レベルの確認用）.

    形が無いぶん、形では見えないものが見えます。

    - **むら** : 一様なはずの面に濃淡や色の傾きが出れば、それは表示側か経路の偏りです
    - **純度** : 単色で塗ったときに他の成分が混じっていないか
    - **レベル**: 面全体が同じコード値なので、狙った値になっているかを 1 点読めば分かります

    もう 1 つ、**色差を間引いても値が変わらない**という性質があります。
    一様な面には色差の細かい変化が無いので、4:2:0 でも 4:4:4 でも同じ結果になるはずです。
    ここに差が出たら、それは間引きによる損失ではなく色変換かビット詰めの誤りです。
    間引きのせいにできない絵なので、切り分けに使えます。

    ``color`` に [R, G, B]（各 0〜1）、``level`` に振幅を指定します。
    既定は白（[1, 1, 1] × 1.0）です。
    """
    color = _opt(options, "color", [1.0, 1.0, 1.0])
    level = float(_opt(options, "level", 1.0))

    values = np.asarray(color, dtype=np.float32)
    if values.shape != (3,):
        raise ValueError(f"raster の color は [R, G, B] の 3 つで指定してください（指定: {color}）")
    values = np.clip(values * level, 0.0, 1.0)

    img = np.empty((height, width, 3), dtype=np.float32)
    img[:] = values
    return img


def monoscope(width: int, height: int, options: dict[str, Any]) -> RGB:
    """放送で使われてきた総合カードの系統を、部品から組み直したもの（この実装の自作構成）.

    この系統のカードは、どれも同じ理由で同じ部品を持っています。
    円は縦横比、格子は歪み、外周ブロックは切れ量、四隅の放射は周辺の解像、
    中央の縞は解像限界です。特定のカードを写したのではなく、
    「何を見たいか」から置き直しました。局名や記号のような識別のための表示は入れません。

    縞に添える数字は**画面の高さぶんに何本入るか**（TV 本数）です。
    周期 p 画素の縞なら 2 × 高さ ÷ p 本になります。
    数字は狙った値ではなく、丸めたあとの**実際に描いた周期**から出しています。
    狙いを書くと、丸めで 1 割ずれていても気付けません。

    縞は 2 画素周期までです。それより細かい指定は落とします。
    書けないものを書くと、絵のほうが先に折り返してしまうためです。

    字形はコードに持っているので、フォントの有無で結果が変わりません。

    ``blocks`` で外周ブロックの数、``grid`` で格子の間隔、``periods`` で縞の周期、
    ``steps`` で階調帯の段数、``level`` で色帯の振幅を指定します。
    """
    background = float(_opt(options, "background", 0.5))
    blocks = int(_opt(options, "blocks", 20))
    grid_step = int(_opt(options, "grid", max(8, min(width, height) // 12)))
    periods = [int(p) for p in _opt(options, "periods", [12, 8, 6, 4, 3, 2])]
    steps = int(_opt(options, "steps", 9))
    level = float(_opt(options, "level", 0.75))
    if blocks < 2:
        raise ValueError("monoscope の blocks は 2 以上にしてください")
    if steps < 2:
        raise ValueError("monoscope の steps は 2 以上にしてください")

    # 2 画素を割る縞は、描いた時点で折り返します。指定されても落とします。
    periods = [p for p in periods if p >= 2]
    if not periods:
        raise ValueError("monoscope の periods は 2 以上のものが 1 つ以上必要です")

    img = _canvas(width, height, background)
    xx, yy = _coords(width, height)
    cx, cy = width / 2.0, height / 2.0
    short = min(width, height)
    thin = max(1, short // 400)
    thickness = max(2, short // 22)  # 外周ブロックの太さ
    # 円は外周ブロックの内側に収めます。ブロックに潜ると、切れ量を数える対象と
    # 縦横比を見る対象が重なって、どちらも読みにくくなります。
    radius = short / 2.0 - thickness - max(2, short // 60)

    # --- 格子 ---
    # 端ではなく中心を基準に刻みます。端起点だと左右で位相がずれ、
    # 「中心から何マス目か」を数えられなくなります。
    def lines_from_centre(coordinate: np.ndarray, centre: float) -> np.ndarray:
        offset = np.mod(coordinate - centre + grid_step / 2.0, grid_step) - grid_step / 2.0
        return np.abs(offset) < thin / 2.0 + 0.5

    img[lines_from_centre(xx, cx) | lines_from_centre(yy, cy)] = np.float32(background * 1.5)

    # --- 四隅の放射（周辺の解像。中央とは違うことがあります） ---
    corner = int(short * 0.21)
    if corner >= 16:
        star = siemens(corner, corner, {"spokes": 24, "background": background})
        # 外周ブロックの内側へ置きます。ブロックは最後に描くので、
        # 重ねると放射の外周が上書きされて、一番粗い側から読めなくなります。
        inset = thickness + max(2, short // 60)
        for x0, y0 in (
            (inset, inset),
            (width - corner - inset, inset),
            (inset, height - corner - inset),
            (width - corner - inset, height - corner - inset),
        ):
            if x0 < 0 or y0 < 0 or x0 + corner > width or y0 + corner > height:
                continue
            img[y0 : y0 + corner, x0 : x0 + corner, :] = star

    # --- 中央の円（縦横比。楕円なら崩れています） ---
    distance = np.hypot(xx - cx, yy - cy)
    edge = max(thin, short // 250)
    img[np.abs(distance - radius) < edge] = np.float32(1.0)

    def band(top: float, bottom: float) -> tuple[int, int, int, int] | None:
        """円の内側に収まる帯の範囲を返す（収まらなければ None）."""
        y0, y1 = int(cy + top * radius), int(cy + bottom * radius)
        reach = max(abs(y0 - cy), abs(y1 - cy))
        if reach >= radius:
            return None
        # その高さで円に収まる幅（弦）。少し内側へ寄せて、円の線に触れないようにします。
        half = np.sqrt(radius**2 - reach**2) * 0.93
        x0, x1 = int(cx - half), int(cx + half)
        if x1 - x0 < 8 or y1 - y0 < 3:
            return None
        return max(0, x0), max(0, y0), min(width, x1), min(height, y1)

    # 中心のまわりは空けておきます。十字と数字が重なると、どちらも読めなくなります。
    arm = int(radius * 0.085)

    # --- 上: 色帯（色順とチャンネルの入れ替わり） ---
    area = band(-0.68, -0.44)
    if area:
        x0, y0, x1, y1 = area
        colors = BAR_COLORS[:7] * level  # 黒を除いた 7 色
        cuts = _edges(x1 - x0, 7)
        for index in range(7):
            img[y0:y1, x0 + cuts[index] : x0 + cuts[index + 1], :] = colors[index]

    # --- 中上: 縞（解像限界）と、その本数 ---
    area = band(-0.38, -0.20)
    if area:
        x0, y0, x1, y1 = area
        label_height = max(7, int(short * 0.030))
        scale = max(1, label_height // _DIGIT_HEIGHT)
        label_top = y1 + max(2, short // 150)
        # 中心の十字にかかるなら数字を小さくします。それでも入らなければ書きません。
        while scale > 1 and label_top + _DIGIT_HEIGHT * scale > cy - arm:
            scale -= 1
        cuts = _edges(x1 - x0, len(periods))
        gap = max(1, (x1 - x0) // (len(periods) * 12))
        for index, period in enumerate(periods):
            bx0 = x0 + cuts[index] + gap
            bx1 = x0 + cuts[index + 1] - gap
            if bx1 - bx0 < 2:
                continue
            # 区画の左端を起点にすると、どの区画も白から始まります。
            offset = xx[y0:y1, bx0:bx1] - bx0
            stripe = ((np.floor(offset) % period) < (period / 2)).astype(np.float32)
            img[y0:y1, bx0:bx1, :] = stripe[:, :, None]

            # 実際に描いた周期から本数を出します（狙いではなく結果を書きます）
            text = str(round(2 * height / period))
            text_width, text_rows = _text_size(text, scale)
            if label_top + text_rows < cy - arm:
                _draw_text(img, text, (bx0 + bx1) // 2 - text_width // 2, label_top, scale, 1.0)

    # --- 中下: 白黒の細かい縦線と、細かい横線（走査方向の差） ---
    area = band(0.12, 0.32)
    if area:
        x0, y0, x1, y1 = area
        middle = (x0 + x1) // 2
        vertical = ((np.floor(xx[y0:y1, x0:middle]) % 4) < 2).astype(np.float32)
        img[y0:y1, x0:middle, :] = vertical[:, :, None]
        horizontal = ((np.floor(yy[y0:y1, middle:x1]) % 4) < 2).astype(np.float32)
        img[y0:y1, middle:x1, :] = horizontal[:, :, None]

    # --- 下: 階調帯（黒つぶれ・白飛び） ---
    area = band(0.44, 0.70)
    if area:
        x0, y0, x1, y1 = area
        cuts = _edges(x1 - x0, steps)
        for index in range(steps):
            value = index / (steps - 1)
            img[y0:y1, x0 + cuts[index] : x0 + cuts[index + 1], :] = np.float32(value)
        # 地と同じ明るさの段は、囲わないと帯が途切れて見えます（段は正しくても読めません）。
        outline = max(1, thin)
        img[y0 - outline : y0, x0:x1, :] = np.float32(1.0)
        img[y1 : y1 + outline, x0:x1, :] = np.float32(1.0)

    # --- 中心の十字（中心のずれ） ---
    cross = max(thin, short // 300)
    img[int(cy) - cross : int(cy) + cross, int(cx) - arm : int(cx) + arm, :] = np.float32(1.0)
    img[int(cy) - arm : int(cy) + arm, int(cx) - cross : int(cx) + cross, :] = np.float32(1.0)

    # --- 外周ブロック（切れ量。端の何ブロックが欠けたかで読めます） ---
    cuts = _edges(width, blocks)
    for index in range(blocks):
        value = np.float32(1.0 if index % 2 == 0 else 0.0)
        img[:thickness, cuts[index] : cuts[index + 1], :] = value
        img[height - thickness :, cuts[index] : cuts[index + 1], :] = value
    rows = _edges(height, max(2, round(blocks * height / width)))
    for index in range(len(rows) - 1):
        value = np.float32(1.0 if index % 2 == 0 else 0.0)
        img[rows[index] : rows[index + 1], :thickness, :] = value
        img[rows[index] : rows[index + 1], width - thickness :, :] = value
    return img


def digitalcard(width: int, height: int, options: dict[str, Any]) -> RGB:
    """伝送そのものを見るための総合カード（この実装の自作構成）.

    古典的なテストカードはアナログ時代のものなので、
    「色差をいくつ間引いたか」「何ビット残っているか」「行が落ちていないか」
    を見る要素を持っていません。`monoscope` がその系統をなぞるのに対し、
    こちらは**デジタルの経路でしか意味を持たない**要素だけを集めています。

    入っている要素と、それぞれで分かること。

    - 目盛り     : 端から何画素目かを絵の中から直接読めます。切り取りや拡大の量が数で出ます
    - 隅の直角   : 四隅そのものの位置。1 画素でも欠ければ角が欠けます
    - 輝度 / 色差: 同じ間隔の縞を、白黒の組と赤青の組で並べます。
                   4:2:0 を通すと赤青側だけが潰れるので、間引きの有無がその場で分かります
    - 色差の位相 : 同じ色の境目を、偶数列と奇数列に置きます。
                   間引いたあとで境目がどちらへ寄るかで、色差の位置合わせが読めます
    - 最下位ビット: 1 コード値ずつの階段を、8bit ぶんと 10bit ぶんで並べます。
                   8bit の経路では 10bit 側が段にならず、実際に何ビット残っているかが出ます
    - 行と列の抜け: 1 画素おきの横縞と縦縞。行や列が落ちる・重なると、縞が消えるか位相が飛びます

    色差の組は「輝度が近く色差が遠い」ことが要ります。ここでは赤と青を既定にしています。
    厳密に輝度を揃えることはできません。輝度の重みは matrix ごとに違い、
    このモジュールは matrix を知らないためです（知ってしまうと段の分離が崩れます）。
    揃えるのではなく、**白黒の組と比べて輝度差が小さい**ことで用が足ります。

    ``tick`` で目盛りの間隔、``pitch`` で縞の間隔、``chroma_on`` / ``chroma_off`` で
    色差の組、``steps`` で最下位ビットの段数を指定します。
    """
    background = float(_opt(options, "background", 0.5))
    tick = int(_opt(options, "tick", 10))
    pitch = int(_opt(options, "pitch", 2))
    steps = int(_opt(options, "steps", 8))
    chroma_on = np.asarray(_opt(options, "chroma_on", [1.0, 0.0, 0.0]), dtype=np.float32)
    chroma_off = np.asarray(_opt(options, "chroma_off", [0.0, 0.0, 1.0]), dtype=np.float32)
    if tick < 2:
        raise ValueError(f"digitalcard の tick は 2 以上にしてください（指定: {tick}）")
    if pitch < 2:
        raise ValueError(f"digitalcard の pitch は 2 以上にしてください（指定: {pitch}）")
    if steps < 2:
        raise ValueError(f"digitalcard の steps は 2 以上にしてください（指定: {steps}）")
    if chroma_on.shape != (3,) or chroma_off.shape != (3,):
        raise ValueError("digitalcard の chroma_on / chroma_off は [R, G, B] の 3 つで指定してください")

    img = _canvas(width, height, background)
    xx, yy = _coords(width, height)
    short = min(width, height)
    margin = max(2, width // 25)

    band_scale = max(1, int(short * 0.038) // _DIGIT_HEIGHT)

    def rows(top: float, bottom: float, number: int = 0) -> tuple[int, int] | None:
        """帯の範囲を返し、番号を付けておく。

        帯に番号があると、説明の側から「2 番の帯」と指せます。
        字形は数字しか持っていないので、名前ではなく番号にしています。
        """
        y0, y1 = int(height * top), int(height * bottom)
        if y1 - y0 < 2:
            return None
        if number:
            _, text_rows = _text_size(str(number), band_scale)
            label_top = y0 - text_rows - max(1, short // 300)
            if label_top >= 0:
                _draw_text(img, str(number), margin, label_top, band_scale, 1.0)
        return y0, y1

    def outline(x0: int, y0: int, x1: int, y1: int) -> None:
        """帯を細い線で囲む（地と見分けが付かない中身のときに、場所を示すため）."""
        line = max(1, short // 400)
        img[max(0, y0 - line) : y0, x0:x1, :] = np.float32(1.0)
        img[y1 : min(height, y1 + line), x0:x1, :] = np.float32(1.0)
        img[y0:y1, max(0, x0 - line) : x0, :] = np.float32(1.0)
        img[y0:y1, x1 : min(width, x1 + line), :] = np.float32(1.0)

    # --- 上端の目盛り（端から何画素目かを絵から読めます） ---
    ruler = rows(0.02, 0.085)
    if ruler:
        y0, y1 = ruler
        span = y1 - y0
        scale = max(1, int(short * 0.022) // _DIGIT_HEIGHT)
        for x in range(0, width, tick):
            # 5 本目を長く、10 本目をさらに長くします。数えるとき目が迷わないようにです。
            if x % (tick * 10) == 0:
                length = span
            elif x % (tick * 5) == 0:
                length = span * 2 // 3
            else:
                length = span // 3
            img[y0 : y0 + max(1, length), x : x + 1, :] = np.float32(1.0)

        label_top = y1 + max(1, short // 200)
        for x in range(0, width, tick * 10):
            text = str(x)
            text_width, text_rows = _text_size(text, scale)
            if x + text_width < width and label_top + text_rows < height:
                _draw_text(img, text, x + 1, label_top, scale, 1.0)

    # --- 輝度の縞と色差の縞（同じ間隔で並べます） ---
    area = rows(0.15, 0.34, 1)
    if area:
        y0, y1 = area
        middle = width // 2
        left0, left1 = margin, middle - margin // 2
        right0, right1 = middle + margin // 2, width - margin
        for x0, x1, on, off in (
            (left0, left1, np.float32([1.0, 1.0, 1.0]), np.float32([0.0, 0.0, 0.0])),
            (right0, right1, chroma_on, chroma_off),
        ):
            if x1 - x0 < 2:
                continue
            # 区画の左端を起点にすると、左右の縞の位相が揃って比べられます。
            stripe = ((np.floor(xx[y0:y1, x0:x1]) - x0) % pitch) < (pitch / 2)
            block = np.where(stripe[:, :, None], on, off).astype(np.float32)
            img[y0:y1, x0:x1, :] = block

    # --- 色差の位相（同じ境目を偶数列と奇数列に置きます） ---
    area = rows(0.37, 0.47, 2)
    if area:
        y0, y1 = area
        block = max(4, (width - margin * 2) // 8)
        x = margin
        parity = 0
        while x + block <= width - margin:
            # 境目を偶数列・奇数列へ交互に置きます。間引いたあとで寄る向きが変わります。
            edge = x + block // 2
            edge += (parity - edge % 2) % 2
            img[y0:y1, x:edge, :] = chroma_on
            img[y0:y1, edge : x + block, :] = chroma_off
            x += block
            parity ^= 1

    # --- 最下位ビットの階段（8bit ぶんと 10bit ぶん） ---
    area = rows(0.50, 0.66, 3)
    if area:
        y0, y1 = area
        middle = width // 2
        for x0, x1, unit in (
            (margin, middle - margin // 2, 1.0 / 255.0),
            (middle + margin // 2, width - margin, 1.0 / 1023.0),
        ):
            if x1 - x0 < steps:
                continue
            cuts = _edges(x1 - x0, steps)
            for index in range(steps):
                # 中央の灰色から 1 コード値ずつ。8bit の経路では 10bit 側が段になりません。
                value = 0.5 + (index - (steps - 1) / 2.0) * unit
                img[y0:y1, x0 + cuts[index] : x0 + cuts[index + 1], :] = np.float32(value)
            # 1 コード値の差は目では追えません。囲っておかないと、どこを読めばよいか分かりません。
            outline(x0, y0, x1, y1)

    # --- 行と列の抜け（1 画素おきの横縞・縦縞） ---
    area = rows(0.69, 0.90, 4)
    if area:
        y0, y1 = area
        middle = width // 2
        left0, left1 = margin, middle - margin // 2
        right0, right1 = middle + margin // 2, width - margin
        if left1 - left0 >= 2:
            horizontal = ((np.floor(yy[y0:y1, left0:left1]) - y0) % 2) < 1
            img[y0:y1, left0:left1, :] = horizontal[:, :, None].astype(np.float32)
        if right1 - right0 >= 2:
            vertical = ((np.floor(xx[y0:y1, right0:right1]) - right0) % 2) < 1
            img[y0:y1, right0:right1, :] = vertical[:, :, None].astype(np.float32)

    # --- 隅の直角（四隅そのものの位置） ---
    arm = max(3, short // 20)
    for y0, x0, dy, dx in (
        (0, 0, 1, 1),
        (0, width - 1, 1, -1),
        (height - 1, 0, -1, 1),
        (height - 1, width - 1, -1, -1),
    ):
        for step in range(arm):
            img[y0, x0 + dx * step, :] = np.float32(1.0)
            img[y0 + dy * step, x0, :] = np.float32(1.0)
    return img


PATTERNS: dict[str, Callable[[int, int, dict[str, Any]], RGB]] = {
    "colorbar": colorbar,
    "colorbar75": colorbar75,
    "grayramp": grayramp,
    "graysteps": graysteps,
    "frame": frame,
    "crosshair": crosshair,
    "grid": grid,
    "circles": circles,
    "radial": radial,
    "hatch": hatch,
    "dots": dots,
    "blocks": blocks,
    "smptebars": smptebars,
    "pluge": pluge,
    "multiburst": multiburst,
    "window": window,
    "zoneplate": zoneplate,
    "checker": checker,
    "pulsebar": pulsebar,
    "splitbars": splitbars,
    "rainbow": rainbow,
    "sweep": sweep,
    "shallowramp": shallowramp,
    "triangleramp": triangleramp,
    "square": square,
    "stepmatrix": stepmatrix,
    "wedge": wedge,
    "testcard": testcard,
    "gamma": gamma,
    "colorramp": colorramp,
    "colormatrix": colormatrix,
    "noise": noise,
    "barshd": barshd,
    "splitsteps": splitsteps,
    "geometrycard": geometrycard,
    "resolutioncard": resolutioncard,
    "siemens": siemens,
    "linepairs": linepairs,
    "slantedge": slantedge,
    "raster": raster,
    "monoscope": monoscope,
    "digitalcard": digitalcard,
}

PATTERN_NAMES: tuple[str, ...] = tuple(PATTERNS)


def render(pattern: str, width: int, height: int, options: dict[str, Any] | None = None) -> RGB:
    """パターン名から R'G'B' float32 (h, w, 3) を生成する."""
    if pattern not in PATTERNS:
        raise ValueError(f"未知のパターン: {pattern}")
    img = PATTERNS[pattern](width, height, options or {})
    if img.shape != (height, width, 3):
        raise AssertionError(f"{pattern} が想定外の形状を返しました: {img.shape}")
    return np.clip(img, 0.0, 1.0).astype(np.float32, copy=False)

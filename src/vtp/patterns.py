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

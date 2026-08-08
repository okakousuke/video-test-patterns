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


# ---------------------------------------------------------------- パターン


def colorbar(width: int, height: int, options: dict[str, Any]) -> RGB:
    """8 色の縦カラーバー.

    ``level`` で 100% / 75% を切り替えます（既定は 100%）。
    色順は白・黄・シアン・緑・マゼンタ・赤・青・黒で、
    輝度の高い順に並びます。チャンネルの入れ替わりが起きると
    この並びが崩れるため、一目で分かります。
    """
    level = float(_opt(options, "level", 1.0))
    bars = np.array(
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
    bars = bars * level
    # 端数は最後のバーへ寄せる（幅が 8 で割り切れない場合の挙動を固定する）
    edges = [round(i * width / 8) for i in range(9)]
    img = _canvas(width, height)
    for i in range(8):
        img[:, edges[i] : edges[i + 1], :] = bars[i]
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

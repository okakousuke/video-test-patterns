#!/usr/bin/env python3
"""パターンごとの見本 PNG（RGB888）を samples/patterns/ へ書き出します。

README の一覧から参照するものなので、リポジトリに同梱します（`generated/` ではありません）。

既定の指定のままでは見本にならないものだけ、ここで指定を足します。
たとえば `raster` は既定が白一色なので、そのまま出すと真っ白な画像になり、
壊れているのか一色なのかが見分けられません。
"""

from pathlib import Path
import sys

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))

from vtp.patterns import PATTERN_NAMES, render  # noqa: E402

# 見本として意味が出るように指定を足すパターン
SAMPLE_OPTIONS: dict[str, dict] = {
    # 白一色だと画像として何も伝わらないので、色の付いた塗りにします
    "raster": {"color": [0.0, 0.55, 0.85], "level": 1.0},
}


def main() -> None:
    output_dir = ROOT / "samples" / "patterns"
    output_dir.mkdir(parents=True, exist_ok=True)

    for name in PATTERN_NAMES:
        rgb = (render(name, 640, 480, SAMPLE_OPTIONS.get(name)) * 255.0 + 0.5).astype("uint8")
        Image.fromarray(rgb, mode="RGB").save(output_dir / f"{name}.png")
        print(f"{name}: {output_dir / f'{name}.png'}")


if __name__ == "__main__":
    main()

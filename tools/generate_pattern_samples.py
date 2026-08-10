#!/usr/bin/env python3
"""Generate small RGB888 PNG samples for every available test pattern."""

from pathlib import Path
import sys

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))

from vtp.patterns import PATTERN_NAMES, render  # noqa: E402


def main() -> None:
    output_dir = ROOT / "samples" / "patterns"
    output_dir.mkdir(parents=True, exist_ok=True)

    for name in PATTERN_NAMES:
        rgb = (render(name, 640, 480) * 255.0 + 0.5).astype("uint8")
        Image.fromarray(rgb, mode="RGB").save(output_dir / f"{name}.png")
        print(f"{name}: {output_dir / f'{name}.png'}")


if __name__ == "__main__":
    main()

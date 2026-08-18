"""参考図がパターンから取り残されていないことを確かめる.

生成画面は、パターンを選んだ瞬間に参考図（``apps/RawInspector/thumbnails/``）を
出します。図は ``tools/make_pattern_thumbnails.py`` が作った静止画で、
実行時に描いているわけではありません。

つまりパターンを足しても、ツールを走らせ直さなければ画面には何も出ません。
足りないことに気付けるのは、その画面を開いた人だけです。ここで縛ります。

図そのものの中身は見ません（絵の良し悪しは目で見るものです）。
見るのは「全パターン分あるか」「寸法が揃っているか」だけです。
"""

from __future__ import annotations

from pathlib import Path

import pytest

from vtp.patterns import PATTERN_NAMES

_THUMBNAILS = Path(__file__).resolve().parents[1] / "apps" / "RawInspector" / "thumbnails"

# tools/make_pattern_thumbnails.py の既定。長辺だけ決めておけば、
# 縦は元の比（16:9）から決まります。
_LONG_SIDE = 480


def test_every_pattern_has_a_thumbnail() -> None:
    missing = [name for name in PATTERN_NAMES if not (_THUMBNAILS / f"{name}.png").exists()]
    assert not missing, (
        "参考図がありません: " + ", ".join(missing) + "。"
        "python tools/make_pattern_thumbnails.py を走らせてください。"
    )


def test_no_thumbnail_is_left_behind() -> None:
    """消したパターンの図が残っていないことも見ます（exe へ埋め込むためです）。"""
    extra = sorted(p.stem for p in _THUMBNAILS.glob("*.png") if p.stem not in PATTERN_NAMES)
    assert not extra, "使われていない参考図が残っています: " + ", ".join(extra)


@pytest.mark.parametrize("pattern", PATTERN_NAMES)
def test_thumbnail_size(pattern: str) -> None:
    """大きさが揃っていないと、選ぶたびに画面の図だけが伸び縮みします。"""
    Image = pytest.importorskip("PIL.Image", reason="Pillow が必要です")
    with Image.open(_THUMBNAILS / f"{pattern}.png") as image:
        assert image.width == _LONG_SIDE
        assert image.height == round(_LONG_SIDE * 1080 / 1920)

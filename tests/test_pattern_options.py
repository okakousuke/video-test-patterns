"""目録と実装がずれていないことを確かめる.

``pattern_options.PATTERN_OPTIONS`` は ``patterns.py`` の ``_opt`` 呼び出しを
人手で書き写したものです。書き写しは必ずずれます。つまみを足したのに
目録へ書き忘れれば画面に出ず、名前を変えれば検証が素通りします。

なので突き合わせは目視に頼らず、``patterns.py`` の構文木を読んで機械的に
確かめます。ここが落ちたときは目録の側を直してください。
"""

from __future__ import annotations

import ast
from pathlib import Path
from typing import Any

import pytest

from vtp.pattern_options import (
    PATTERN_OPTIONS,
    describe_pattern_options,
    options_for,
    validate_pattern_options,
)
from vtp.patterns import PATTERNS

_SOURCE = Path(__file__).resolve().parents[1] / "src" / "vtp" / "patterns.py"

_MISSING = object()


def _calls_in_source() -> dict[str, dict[str, Any]]:
    """``パターン名 -> {つまみ名: 既定値}`` を構文木から集める.

    既定値が式（寸法から決まるもの）のときは ``_MISSING`` を入れます。
    """
    tree = ast.parse(_SOURCE.read_text(encoding="utf-8"))
    found: dict[str, dict[str, Any]] = {}

    for node in ast.walk(tree):
        if not isinstance(node, ast.FunctionDef):
            continue
        for call in ast.walk(node):
            if not isinstance(call, ast.Call):
                continue
            if not (isinstance(call.func, ast.Name) and call.func.id == "_opt"):
                continue
            name = ast.literal_eval(call.args[1])
            try:
                default = ast.literal_eval(call.args[2])
            except ValueError:
                default = _MISSING
            found.setdefault(node.name, {})[name] = default

    return found


CALLS = _calls_in_source()


def test_目録の対象は実在するパターンだけ() -> None:
    unknown = set(PATTERN_OPTIONS) - set(PATTERNS)
    assert not unknown, f"存在しないパターンが目録にあります: {sorted(unknown)}"


def test_つまみを持つパターンはすべて目録にある() -> None:
    # _opt を使っているのに目録に無ければ、画面へ出ず検証も効きません。
    missing = set(CALLS) - set(PATTERN_OPTIONS)
    assert not missing, f"目録に無いパターンがあります: {sorted(missing)}"


@pytest.mark.parametrize("pattern", sorted(PATTERN_OPTIONS))
def test_つまみの名前が実装と一致する(pattern: str) -> None:
    declared = {opt.name for opt in options_for(pattern)}
    actual = set(CALLS.get(pattern, {}))
    assert declared == actual, (
        f"{pattern}: 目録にだけある = {sorted(declared - actual)} / "
        f"実装にだけある = {sorted(actual - declared)}"
    )


@pytest.mark.parametrize("pattern", sorted(PATTERN_OPTIONS))
def test_既定値が実装と一致する(pattern: str) -> None:
    actual = CALLS.get(pattern, {})
    for opt in options_for(pattern):
        real = actual[opt.name]
        if real is _MISSING:
            # 寸法から決まるもの。目録では既定値を持たず、求め方を書きます。
            assert opt.default is None, f"{pattern}.{opt.name} は寸法依存なので default は None です"
            assert opt.auto, f"{pattern}.{opt.name} に auto（求め方）がありません"
            continue

        assert opt.default is not None, f"{pattern}.{opt.name} の既定値が目録にありません"
        assert not opt.auto, f"{pattern}.{opt.name} は固定の既定値なので auto は不要です"
        same = list(opt.default) == list(real) if isinstance(real, list) else opt.default == real
        assert same, f"{pattern}.{opt.name} の既定値がずれています（目録 {opt.default!r} / 実装 {real!r}）"


@pytest.mark.parametrize("pattern", sorted(PATTERN_OPTIONS))
def test_既定値は自分の検証を通る(pattern: str) -> None:
    """範囲を狭く書きすぎて既定値が弾かれる、という取り違えを防ぎます."""
    defaults = {opt.name: opt.default for opt in options_for(pattern) if opt.default is not None}
    validate_pattern_options(pattern, defaults)


@pytest.mark.parametrize("pattern", sorted(PATTERN_OPTIONS))
def test_説明が書かれている(pattern: str) -> None:
    for opt in options_for(pattern):
        assert opt.label, f"{pattern}.{opt.name} に label がありません"
        assert opt.help, f"{pattern}.{opt.name} に help がありません"
        assert opt.kind in ("int", "float", "bool", "choice", "ints", "floats", "color")
        if opt.kind == "choice":
            assert opt.choices, f"{pattern}.{opt.name} は choice なのに選択肢がありません"


@pytest.mark.parametrize("pattern", sorted(PATTERN_OPTIONS))
def test_説明に飾り記号を混ぜない(pattern: str) -> None:
    """label と help はそのまま画面へ出ます。

    受け取る側（WPF の TextBlock）は Markdown を解釈しないので、``**強調**`` と
    書くと星印がそのまま文字として出ます。実際に一度出してしまいました。
    強調したいことは語順で示してください。
    """
    for opt in options_for(pattern):
        for field, text in (("label", opt.label), ("help", opt.help)):
            assert "**" not in text, f"{pattern}.{opt.name} の {field} に ** が入っています"
            assert "`" not in text, f"{pattern}.{opt.name} の {field} に ` が入っています"


def test_未知の名前は弾く() -> None:
    with pytest.raises(ValueError, match="stpes"):
        validate_pattern_options("graysteps", {"stpes": 16})


def test_範囲の外は弾く() -> None:
    with pytest.raises(ValueError, match="以上"):
        validate_pattern_options("graysteps", {"steps": 1})
    with pytest.raises(ValueError, match="以下"):
        validate_pattern_options("zoneplate", {"max_frequency": 0.9})


def test_選択肢の外は弾く() -> None:
    with pytest.raises(ValueError, match="horizontal"):
        validate_pattern_options("colorbar", {"orientation": "diagonal"})


def test_色は3つ必要() -> None:
    with pytest.raises(ValueError, match="3 個"):
        validate_pattern_options("raster", {"color": [1.0, 0.0]})


def test_整数のつまみに小数は入れない() -> None:
    with pytest.raises(ValueError, match="整数"):
        validate_pattern_options("graysteps", {"steps": 10.5})


def test_つまみの無いパターンは何も受け取らない() -> None:
    plain = sorted(set(PATTERNS) - set(PATTERN_OPTIONS))
    assert plain, "つまみを持たないパターンが 1 つも無いのは想定外です"
    with pytest.raises(ValueError, match="オプションはありません"):
        validate_pattern_options(plain[0], {"steps": 4})


def test_describe_は素の値だけを出す() -> None:
    """JSON へそのまま載せられることを確かめます."""
    import json

    described = describe_pattern_options()
    assert set(described) == set(PATTERN_OPTIONS)
    json.dumps(described, ensure_ascii=False)  # 落ちなければ素の値だけです

    rows = {row["name"]: row for row in described["frame"]}
    assert rows["thickness"]["default"] is None
    assert "auto" in rows["thickness"]
    assert rows["safe"]["default"] == [0.9, 0.8]

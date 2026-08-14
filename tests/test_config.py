"""設定の読み込みと、組み合わせ検証を確認する."""

import pytest

from vtp.config import Config, ConfigError, load_jsonc, strip_jsonc_comments, validate
from vtp.patterns import PATTERN_NAMES


def ok(**kw):
    cfg = Config(**kw)
    validate(cfg, PATTERN_NAMES)
    return cfg


def ng(**kw):
    with pytest.raises(ConfigError) as e:
        validate(Config(**kw), PATTERN_NAMES)
    return str(e.value)


# ---------------------------------------------------------------- JSONC


def test_strip_line_and_block_comments():
    src = """
    {
      // 行コメント
      "pattern": "colorbar",
      /* ブロック
         コメント */
      "width": 16
    }
    """
    assert '"pattern": "colorbar"' in strip_jsonc_comments(src)
    assert "行コメント" not in strip_jsonc_comments(src)


def test_does_not_break_slashes_inside_strings():
    """文字列の中の // を消してしまわないこと（URL やパスが壊れる）."""
    src = '{"note": "see https://example.com/spec", "p": "a/*b*/c"}'
    out = strip_jsonc_comments(src)
    assert "https://example.com/spec" in out
    assert "a/*b*/c" in out


def test_block_comment_keeps_line_numbers():
    src = "{\n/* a\nb\nc */\n}"
    assert strip_jsonc_comments(src).count("\n") == src.count("\n")


def test_load_jsonc(tmp_path):
    p = tmp_path / "c.jsonc"
    p.write_text('{\n // コメント\n "width": 32\n}\n', encoding="utf-8")
    assert load_jsonc(p)["width"] == 32


def test_unknown_key_is_rejected():
    with pytest.raises(ConfigError):
        Config.from_dict({"widht": 16})


# ---------------------------------------------------------------- 検証


def test_valid_combinations():
    ok(color_model="rgb", subsampling="4:4:4", storage="planar", width=16, height=16)
    ok(color_model="ycbcr", subsampling="4:2:0", storage="nv12", width=16, height=16)
    ok(color_model="ycbcr", subsampling="4:2:2", bit_depth=10, storage="v210",
       width=24, height=8)
    ok(color_model="ycbcr", subsampling="4:2:0", bit_depth=10, storage="p010",
       alignment="msb", width=16, height=16)


def test_rgb_cannot_be_subsampled():
    msg = ng(color_model="rgb", subsampling="4:2:0", storage="planar")
    assert "4:4:4" in msg


def test_storage_rejects_wrong_subsampling():
    assert "nv12" in ng(color_model="ycbcr", subsampling="4:2:2", storage="nv12")


def test_storage_rejects_wrong_bit_depth():
    assert "10bit" in ng(color_model="ycbcr", subsampling="4:2:2",
                         bit_depth=10, storage="packed")


def test_p010_requires_msb_alignment():
    assert "msb" in ng(color_model="ycbcr", subsampling="4:2:0",
                       bit_depth=10, storage="p010", alignment="lsb")


def test_odd_size_rejected_for_chroma_subsampling():
    assert "偶数" in ng(color_model="ycbcr", subsampling="4:2:2",
                        storage="planar", width=15, height=8)
    assert "偶数" in ng(color_model="ycbcr", subsampling="4:2:0",
                        storage="planar", width=16, height=9)


def test_v210_requires_width_multiple_of_six():
    assert "6 の倍数" in ng(color_model="ycbcr", subsampling="4:2:2",
                            bit_depth=10, storage="v210", width=20, height=8)


def test_mipi10_requires_chroma_width_multiple_of_four():
    # 幅 8 の 4:2:0 は色差幅 4 なので通る。幅 12 は色差幅 6 で落ちる
    ok(color_model="ycbcr", subsampling="4:2:0", bit_depth=10,
       storage="mipi10", width=8, height=8)
    assert "4 の倍数" in ng(color_model="ycbcr", subsampling="4:2:0", bit_depth=10,
                            storage="mipi10", width=12, height=8)


def test_unknown_names_are_rejected():
    assert "未知のパターン" in ng(pattern="nosuch")
    assert "未知の storage" in ng(storage="nosuch")


def test_rgb_manifest_records_matrix_as_null():
    """RGB のままなら matrix は使っていない。null として残す."""
    assert Config(color_model="rgb").to_dict()["matrix"] is None
    assert Config(color_model="ycbcr", matrix="bt601").to_dict()["matrix"] == "bt601"


def test_describe_lists_only_combinations_that_validate():
    """数え上げた組み合わせが、実際に validate を通ること."""
    from vtp.config import Config, describe_combinations, validate
    from vtp.patterns import PATTERN_NAMES

    described = describe_combinations(PATTERN_NAMES)
    assert described["combinations"]

    for entry in described["combinations"]:
        cfg = Config(
            width=entry["width_multiple"] * 8,
            height=entry["height_multiple"] * 8,
            color_model=entry["color_model"],
            subsampling=entry["subsampling"],
            bit_depth=entry["bit_depth"],
            storage=entry["storage"],
            alignment=entry["alignment"],
            range=entry["range"],
        )
        validate(cfg, PATTERN_NAMES)  # 例外が出れば失敗


def test_describe_multiples_are_the_smallest_that_pass():
    """幅・高さの倍数が、それ未満では通らない値であること."""
    from vtp.config import Config, ConfigError, describe_combinations, validate
    from vtp.patterns import PATTERN_NAMES

    for entry in describe_combinations(PATTERN_NAMES)["combinations"]:
        wm, hm = entry["width_multiple"], entry["height_multiple"]
        if wm == 1 and hm == 1:
            continue

        common = dict(
            color_model=entry["color_model"], subsampling=entry["subsampling"],
            bit_depth=entry["bit_depth"], storage=entry["storage"],
            alignment=entry["alignment"], range=entry["range"],
        )
        # 倍数から 1 引いた幅・高さは通らない
        if wm > 1:
            with pytest.raises(ConfigError):
                validate(Config(width=wm * 8 - 1, height=hm * 8, **common), PATTERN_NAMES)
        if hm > 1:
            with pytest.raises(ConfigError):
                validate(Config(width=wm * 8, height=hm * 8 - 1, **common), PATTERN_NAMES)


def test_describe_covers_the_known_awkward_formats():
    """v210 と mipi10 の幅の条件が、表ではなく探索で出ていること."""
    from vtp.config import describe_combinations
    from vtp.patterns import PATTERN_NAMES

    combinations = describe_combinations(PATTERN_NAMES)["combinations"]
    by = {(c["storage"], c["subsampling"]): c for c in combinations}

    assert by[("v210", "4:2:2")]["width_multiple"] == 6
    assert by[("mipi10", "4:4:4")]["width_multiple"] == 4
    assert by[("mipi10", "4:2:0")]["width_multiple"] == 8   # 色差面が半分になるため
    assert by[("mipi10", "4:2:0")]["height_multiple"] == 2
    assert by[("nv12", "4:2:0")]["width_multiple"] == 2


def test_describe_excludes_combinations_that_cannot_exist():
    """成立しないものが表に載っていないこと."""
    from vtp.config import describe_combinations
    from vtp.patterns import PATTERN_NAMES

    combinations = describe_combinations(PATTERN_NAMES)["combinations"]

    # RGB に色差の間引きは無い
    assert not [c for c in combinations if c["color_model"] == "rgb" and c["subsampling"] != "4:4:4"]
    # RGB は full range のみ
    assert not [c for c in combinations if c["color_model"] == "rgb" and c["range"] != "full"]
    # p010 は上位詰めが定義
    assert not [c for c in combinations if c["storage"] == "p010" and c["alignment"] != "msb"]
    # nv12 は 8bit の 4:2:0 だけ
    assert not [c for c in combinations if c["storage"] == "nv12" and c["bit_depth"] != 8]

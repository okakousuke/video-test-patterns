"""パイプライン全体と CLI を確認する."""

import json

import numpy as np
import pytest

from vtp.cli import main
from vtp.config import Config
from vtp.pack import expected_size
from vtp.pipeline import build_frame, generate
from vtp.subsample import downsample, upsample


def test_generate_writes_three_outputs(tmp_path):
    cfg = Config(pattern="colorbar", width=32, height=16)
    res = generate(cfg, tmp_path / "cb")
    assert set(res.outputs) == {"raw", "png", "json"}
    assert (tmp_path / "cb.raw").stat().st_size == expected_size(cfg)
    assert res.roundtrip_ok


def test_manifest_contents(tmp_path):
    cfg = Config(pattern="grid", width=32, height=16, color_model="ycbcr",
                 subsampling="4:2:0", storage="nv12", range="limited", matrix="bt709")
    res = generate(cfg, tmp_path / "g")
    doc = json.loads((tmp_path / "g.manifest.json").read_text(encoding="utf-8"))

    assert doc["manifest_version"] == 1
    assert doc["parameters"]["matrix"] == "bt709"
    assert doc["parameters"]["storage"] == "nv12"
    assert doc["raw_bytes"] == expected_size(cfg)
    assert doc["roundtrip_verified"] is True
    kinds = {f["kind"] for f in doc["files"]}
    assert {"raw", "png"} <= kinds
    for f in doc["files"]:
        assert len(f["sha256"]) == 64
        assert not f["path"].startswith("/")
        assert "/" not in f["path"]
    assert doc["parameters_sha256"] == res.manifest["parameters_sha256"]


def test_same_config_gives_identical_raw(tmp_path):
    """条件が同じなら RAW はバイト単位で一致する（再現性）."""
    cfg = Config(pattern="circles", width=48, height=32)
    a = generate(cfg, tmp_path / "a").raw
    b = generate(cfg, tmp_path / "b").raw
    assert a == b


def test_changing_matrix_changes_raw(tmp_path):
    """matrix を変えれば結果も変わる（条件が効いていることの確認）."""
    base = dict(pattern="colorbar", width=32, height=16,
                color_model="ycbcr", subsampling="4:4:4", storage="planar")
    a = generate(Config(**base, matrix="bt601"), tmp_path / "a").raw
    b = generate(Config(**base, matrix="bt709"), tmp_path / "b").raw
    assert a != b


def test_subsampling_averages_not_drops():
    """1 画素幅のハッチは、間引きではなく平均になる（位相で結果が変わらない）."""
    plane = np.array([[0, 100, 0, 100]], dtype=np.uint16)
    assert list(downsample(plane, "4:2:2")[0]) == [50, 50]
    shifted = np.array([[100, 0, 100, 0]], dtype=np.uint16)
    assert list(downsample(shifted, "4:2:2")[0]) == [50, 50]


def test_upsample_restores_size():
    plane = np.array([[1, 2]], dtype=np.uint16)
    assert upsample(plane, "4:2:2").shape == (1, 4)
    assert upsample(plane, "4:2:0").shape == (2, 4)


def test_420_loses_chroma_detail_but_not_luma():
    """4:2:0 を通しても輝度は保たれ、色差だけがつぶれること."""
    cfg444 = Config(pattern="hatch", width=32, height=16, color_model="ycbcr",
                    subsampling="4:4:4", storage="planar")
    cfg420 = Config(**{**cfg444.__dict__, "subsampling": "4:2:0"})
    f444 = build_frame(cfg444)
    f420 = build_frame(cfg420)
    assert np.array_equal(f444.planes[0], f420.planes[0])          # Y は同じ
    assert f420.planes[1].shape == (8, 16)                          # 色差は 1/4


def test_colour_hatch_collapses_under_420():
    """赤・青の 1 画素縞は、4:2:0 を通すと色差が平均されて紫一色になる.

    白黒の縞では色差が一定なので、この劣化は現れません。
    「4:2:0 で色が消える」を再現するには色差の遠い 2 色が要る、という確認です。
    """
    base = dict(pattern="hatch", width=32, height=16, color_model="ycbcr",
                range="limited", matrix="bt709",
                pattern_options={"period": 2, "on": [1, 0, 0], "off": [0, 0, 1]})
    f444 = build_frame(Config(**base, subsampling="4:4:4", storage="planar"))
    f420 = build_frame(Config(**base, subsampling="4:2:0", storage="nv12"))

    # 4:4:4 では隣り合う色差が違う
    assert f444.planes[1][0, 0] != f444.planes[1][0, 1]
    # 4:2:0 では 1 行ぶんの色差がすべて同じ値に潰れる
    assert len(set(f420.planes[1][0].tolist())) == 1
    # 輝度は潰れていない（縞は残る）
    assert f420.planes[0][0, 0] != f420.planes[0][0, 1]


def test_limited_range_stays_within_bounds():
    cfg = Config(pattern="colorbar", width=32, height=16, color_model="ycbcr",
                 subsampling="4:4:4", storage="planar", range="limited")
    y = build_frame(cfg).planes[0]
    assert int(y.min()) >= 16 and int(y.max()) <= 235


# ---------------------------------------------------------------- CLI


def test_cli_success(tmp_path, capsys):
    rc = main(["--pattern", "colorbar", "--width", "32", "--height", "16",
               "--output", str(tmp_path / "x")])
    assert rc == 0
    assert (tmp_path / "x.raw").exists()
    assert "往復確認 OK" in capsys.readouterr().out


def test_cli_invalid_combination_returns_2(tmp_path, capsys):
    rc = main(["--color-model", "rgb", "--subsampling", "4:2:0",
               "--output", str(tmp_path / "x")])
    assert rc == 2
    assert not (tmp_path / "x.raw").exists()
    assert "エラー" in capsys.readouterr().err


def test_cli_dry_run_writes_nothing(tmp_path):
    rc = main(["--dry-run", "--width", "32", "--height", "16",
               "--output", str(tmp_path / "x")])
    assert rc == 0
    assert not list(tmp_path.iterdir())


def test_cli_config_file_then_override(tmp_path):
    cfgfile = tmp_path / "c.jsonc"
    cfgfile.write_text(
        '{ // テスト用\n "pattern": "grid", "width": 64, "height": 32 }\n',
        encoding="utf-8",
    )
    rc = main(["--config", str(cfgfile), "--width", "32",
               "--outputs", "raw", "--quiet", "--output", str(tmp_path / "x")])
    assert rc == 0
    cfg = Config(pattern="grid", width=32, height=32)
    assert (tmp_path / "x.raw").stat().st_size == expected_size(cfg)


def test_cli_pattern_option(tmp_path):
    rc = main(["--pattern", "graysteps", "--pattern-option", "steps=4",
               "--width", "32", "--height", "8", "--outputs", "raw", "--quiet",
               "--output", str(tmp_path / "x")])
    assert rc == 0


def test_cli_list_flags(capsys):
    assert main(["--list-patterns"]) == 0
    assert "colorbar" in capsys.readouterr().out
    assert main(["--list-storages"]) == 0
    assert "v210" in capsys.readouterr().out


@pytest.mark.parametrize(
    "args",
    [
        ["--storage", "v210", "--color-model", "ycbcr", "--subsampling", "4:2:2",
         "--bit-depth", "10", "--width", "24", "--height", "8"],
        ["--storage", "p010", "--color-model", "ycbcr", "--subsampling", "4:2:0",
         "--bit-depth", "10", "--alignment", "msb", "--width", "24", "--height", "8"],
        ["--storage", "mipi10", "--color-model", "ycbcr", "--subsampling", "4:2:0",
         "--bit-depth", "10", "--width", "24", "--height", "8"],
    ],
)
def test_cli_ten_bit_storages(tmp_path, args):
    rc = main([*args, "--quiet", "--output", str(tmp_path / "x")])
    assert rc == 0


def _luma_plane(path, width, height, bit_depth):
    """RAW の先頭にある Y' プレーンだけを読み出す（planar / nv12 とも先頭は Y'）."""
    dtype = np.uint8 if bit_depth == 8 else "<u2"
    return np.fromfile(path, dtype=dtype)[: width * height].reshape(height, width)


def test_digitalcard_lsb_band_reads_out_the_bit_depth(tmp_path):
    """3 番の帯が、経路に実際に残っているビット数を出すこと.

    10bit ぶんの刻みは、8bit の経路を通ると段にならずに潰れる。
    プレビューは RGB888 なのでどちらでも潰れて見えるが、
    RAW のコード値を見れば分かれる。ここで見ているのは RAW のほうです。
    """
    width, height = 800, 600
    row = 350
    eight_bit_side = slice(45, 375)
    ten_bit_side = slice(425, 755)

    generate(Config(pattern="digitalcard", width=width, height=height,
                    color_model="ycbcr", subsampling="4:4:4", bit_depth=10,
                    storage="planar", alignment="lsb", range="limited", matrix="bt709"),
             tmp_path / "ten")
    deep = _luma_plane(tmp_path / "ten.raw", width, height, 10)

    generate(Config(pattern="digitalcard", width=width, height=height,
                    color_model="ycbcr", subsampling="4:2:0", bit_depth=8,
                    storage="nv12", range="limited", matrix="bt709"),
             tmp_path / "eight")
    shallow = _luma_plane(tmp_path / "eight.raw", width, height, 8)

    # 8bit ぶんの刻みは、どちらの経路でも段として残る
    assert len(np.unique(deep[row, eight_bit_side])) == 8
    assert len(np.unique(shallow[row, eight_bit_side])) == 8

    # 10bit ぶんの刻みは、10bit の経路でだけ分かれる
    assert len(np.unique(deep[row, ten_bit_side])) >= 6
    assert len(np.unique(shallow[row, ten_bit_side])) <= 2


def test_digitalcard_chroma_stripes_collapse_in_420_but_luma_stripes_do_not(tmp_path):
    """1 番の帯で、色差の縞だけが 4:2:0 で潰れること."""
    width, height = 800, 600
    res = generate(Config(pattern="digitalcard", width=width, height=height,
                          color_model="ycbcr", subsampling="4:2:0", bit_depth=8,
                          storage="nv12", range="limited", matrix="bt709"),
                   tmp_path / "c")
    assert res.roundtrip_ok

    from PIL import Image
    img = np.asarray(Image.open(tmp_path / "c.preview.png").convert("RGB")).astype(int)

    luma = img[130, 40:56]
    chroma = img[130, 420:436]

    # 白黒の縞は残る（明るさの幅が広いまま）
    assert luma[:, 0].max() - luma[:, 0].min() > 200
    # 赤と青は混ざって紫になる。赤成分と青成分の差が小さくなっていること
    assert {tuple(px) for px in chroma} != {(255, 0, 0), (0, 0, 255)}
    for pixel in chroma:
        assert abs(int(pixel[0]) - int(pixel[2])) < 40

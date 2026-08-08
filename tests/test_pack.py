"""格納形式の詰め方を、バイト列そのもので確認する.

ビット詰めの誤りは絵を見ても気づけません。ここでは

1. 小さな既知の入力に対する **バイト列の期待値**
2. すべての形式についての **往復（pack → unpack）一致**

の 2 本立てで確認します。
"""

import numpy as np
import pytest

from vtp.config import Config
from vtp.pack import (
    Frame,
    expected_size,
    mipi10_pack_plane,
    mipi10_unpack_plane,
    pack,
    unpack,
    v210_row_stride,
)


def _frame(cfg: Config, seed: int = 0) -> Frame:
    rng = np.random.default_rng(seed)
    peak = (1 << cfg.bit_depth) - 1
    h, w = cfg.height, cfg.width
    if cfg.color_model == "rgb":
        shapes = [(h, w)] * 3
    elif cfg.subsampling == "4:4:4":
        shapes = [(h, w)] * 3
    elif cfg.subsampling == "4:2:2":
        shapes = [(h, w), (h, w // 2), (h, w // 2)]
    else:
        shapes = [(h, w), (h // 2, w // 2), (h // 2, w // 2)]
    planes = tuple(
        rng.integers(0, peak + 1, size=s, dtype=np.uint16) for s in shapes
    )
    return Frame(planes, cfg.color_model, cfg.subsampling, cfg.bit_depth)


CASES = [
    Config(color_model="rgb", subsampling="4:4:4", bit_depth=8, storage="planar"),
    Config(color_model="rgb", subsampling="4:4:4", bit_depth=8, storage="packed"),
    Config(color_model="rgb", subsampling="4:4:4", bit_depth=10, storage="planar"),
    Config(color_model="rgb", subsampling="4:4:4", bit_depth=10, storage="planar", alignment="msb"),
    Config(color_model="rgb", subsampling="4:4:4", bit_depth=10, storage="mipi10"),
    Config(color_model="ycbcr", subsampling="4:4:4", bit_depth=8, storage="planar"),
    Config(color_model="ycbcr", subsampling="4:4:4", bit_depth=8, storage="packed"),
    Config(color_model="ycbcr", subsampling="4:2:2", bit_depth=8, storage="planar"),
    Config(color_model="ycbcr", subsampling="4:2:2", bit_depth=8, storage="packed"),
    Config(color_model="ycbcr", subsampling="4:2:0", bit_depth=8, storage="planar"),
    Config(color_model="ycbcr", subsampling="4:2:0", bit_depth=8, storage="nv12"),
    Config(color_model="ycbcr", subsampling="4:2:0", bit_depth=10, storage="p010", alignment="msb"),
    Config(color_model="ycbcr", subsampling="4:2:2", bit_depth=10, storage="v210"),
    Config(color_model="ycbcr", subsampling="4:2:0", bit_depth=10, storage="mipi10"),
    Config(color_model="ycbcr", subsampling="4:2:2", bit_depth=10, storage="planar"),
]


def _ids(c: Config) -> str:
    return f"{c.color_model}-{c.subsampling}-{c.bit_depth}b-{c.storage}-{c.alignment}"


@pytest.mark.parametrize("cfg", CASES, ids=[_ids(c) for c in CASES])
def test_roundtrip(cfg: Config):
    """詰めて読み戻すと、元のプレーンと 1 ビットも違わないこと."""
    cfg = Config(**{**cfg.__dict__, "width": 24, "height": 8})
    frame = _frame(cfg)
    data = pack(frame, cfg)
    back = unpack(data, cfg)
    for a, b in zip(frame.planes, back.planes, strict=True):
        assert np.array_equal(a, b)


@pytest.mark.parametrize("cfg", CASES, ids=[_ids(c) for c in CASES])
def test_expected_size_matches(cfg: Config):
    """事前に計算した RAW サイズと、実際に詰めた長さが一致すること."""
    cfg = Config(**{**cfg.__dict__, "width": 24, "height": 8})
    assert len(pack(_frame(cfg), cfg)) == expected_size(cfg)


def test_planar_8bit_layout_is_plane_order():
    cfg = Config(color_model="ycbcr", subsampling="4:2:0", bit_depth=8,
                 storage="planar", width=4, height=2)
    y = np.array([[1, 2, 3, 4], [5, 6, 7, 8]], dtype=np.uint16)
    cb = np.array([[10, 11]], dtype=np.uint16)
    cr = np.array([[20, 21]], dtype=np.uint16)
    data = pack(Frame((y, cb, cr), "ycbcr", "4:2:0", 8), cfg)
    assert data == bytes([1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 20, 21])


def test_uyvy_component_order():
    """4:2:2 packed は Cb, Y0, Cr, Y1 の順に並ぶ."""
    cfg = Config(color_model="ycbcr", subsampling="4:2:2", bit_depth=8,
                 storage="packed", width=2, height=1)
    y = np.array([[100, 200]], dtype=np.uint16)
    cb = np.array([[10]], dtype=np.uint16)
    cr = np.array([[20]], dtype=np.uint16)
    data = pack(Frame((y, cb, cr), "ycbcr", "4:2:2", 8), cfg)
    assert data == bytes([10, 100, 20, 200])


def test_nv12_interleaves_chroma():
    cfg = Config(color_model="ycbcr", subsampling="4:2:0", bit_depth=8,
                 storage="nv12", width=2, height=2)
    y = np.array([[1, 2], [3, 4]], dtype=np.uint16)
    cb = np.array([[10]], dtype=np.uint16)
    cr = np.array([[20]], dtype=np.uint16)
    data = pack(Frame((y, cb, cr), "ycbcr", "4:2:0", 8), cfg)
    assert data == bytes([1, 2, 3, 4, 10, 20])


def test_alignment_changes_bytes_but_not_values():
    """lsb 寄せと msb 寄せでバイト列は変わるが、読み戻せば同じ値になる."""
    base = dict(color_model="ycbcr", subsampling="4:4:4", bit_depth=10,
                storage="planar", width=4, height=2)
    lsb = Config(**base, alignment="lsb")
    msb = Config(**base, alignment="msb")
    frame = _frame(lsb, seed=7)

    d_lsb = pack(frame, lsb)
    d_msb = pack(frame, msb)
    assert d_lsb != d_msb
    assert np.array_equal(unpack(d_lsb, lsb).planes[0], unpack(d_msb, msb).planes[0])

    # msb 寄せは 6bit 左シフト。1023 は 0xFFC0 になる
    one = np.full((2, 4), 1023, dtype=np.uint16)
    d = pack(Frame((one, one, one), "ycbcr", "4:4:4", 10), msb)
    assert d[:2] == bytes([0xC0, 0xFF])


def test_v210_row_stride_is_128_byte_aligned():
    assert v210_row_stride(6) == 128        # 16 バイト → 128 へ切り上げ
    assert v210_row_stride(48) == 128       # ちょうど 128
    assert v210_row_stride(1920) == 5120
    assert v210_row_stride(1920) % 128 == 0


def test_v210_word_layout():
    """先頭ワードは Cb0 | Y0<<10 | Cr0<<20 の順に詰まる."""
    cfg = Config(color_model="ycbcr", subsampling="4:2:2", bit_depth=10,
                 storage="v210", width=6, height=1)
    y = np.arange(1, 7, dtype=np.uint16).reshape(1, 6)
    cb = np.array([[100, 101, 102]], dtype=np.uint16)
    cr = np.array([[200, 201, 202]], dtype=np.uint16)
    data = pack(Frame((y, cb, cr), "ycbcr", "4:2:2", 10), cfg)
    word0 = int.from_bytes(data[0:4], "little")
    assert word0 & 0x3FF == 100
    assert (word0 >> 10) & 0x3FF == 1
    assert (word0 >> 20) & 0x3FF == 200
    assert word0 >> 30 == 0          # 上位 2bit は未使用
    assert len(data) == 128          # 行が 128 バイトへ揃えられている


def test_mipi10_five_bytes_per_four_samples():
    """4 サンプル 5 バイト。5 バイト目に下位 2bit が 4 つ分入る."""
    plane = np.array([[0x3FF, 0x000, 0x155, 0x2AA]], dtype=np.uint16)
    data = mipi10_pack_plane(plane)
    assert len(data) == 5
    assert data[0] == 0x3FF >> 2
    assert data[1] == 0x000 >> 2
    assert data[2] == 0x155 >> 2
    assert data[3] == 0x2AA >> 2
    lsb = (0x3FF & 3) | ((0x000 & 3) << 2) | ((0x155 & 3) << 4) | ((0x2AA & 3) << 6)
    assert data[4] == lsb
    assert np.array_equal(mipi10_unpack_plane(data, 1, 4), plane)


def test_mipi10_covers_full_10bit_range():
    plane = np.arange(1024, dtype=np.uint16).reshape(4, 256)
    back = mipi10_unpack_plane(mipi10_pack_plane(plane), 4, 256)
    assert np.array_equal(back, plane)

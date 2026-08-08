"""コード値のプレーンを、メモリ上のバイト列へ詰める／読み戻す.

このモジュールが扱うのは **並べ方だけ** です。色の意味には触れません。
すべての格納形式について ``pack`` と ``unpack`` を対で実装しています。
往復して元のプレーンに戻ることをテストで確認できるようにするためです
（ビット詰めの誤りは、絵を見ても気づけないことが多いためです）。

用語:

- プレーン ... 1 成分ぶんの 2 次元配列。``uint16`` のコード値で保持します
- ストライド ... 1 行あたりのバイト数。v210 のように行の末尾を詰める形式があります
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from .config import Config


@dataclass
class Frame:
    """成分プレーンの集合.

    RGB なら ``(R, G, B)``、Y'CbCr なら ``(Y, Cb, Cr)`` を保持します。
    色差プレーンは subsampling に応じて小さくなります。
    """

    planes: tuple[np.ndarray, np.ndarray, np.ndarray]
    color_model: str
    subsampling: str
    bit_depth: int

    @property
    def width(self) -> int:
        return int(self.planes[0].shape[1])

    @property
    def height(self) -> int:
        return int(self.planes[0].shape[0])


class PackError(ValueError):
    """格納形式として詰められない／読み戻せないときに送出される."""


# ---------------------------------------------------------------- 共通の道具


def _to_container(plane: np.ndarray, bit_depth: int, alignment: str) -> np.ndarray:
    """プレーンを、そのまま書き出せる整数型（uint8 / uint16 LE）へ変換する."""
    if bit_depth == 8:
        return plane.astype(np.uint8)
    if bit_depth == 10:
        v = plane.astype(np.uint16)
        if alignment == "msb":
            v = (v << 6).astype(np.uint16)
        return v.astype("<u2")
    raise PackError(f"未対応の bit_depth: {bit_depth}")


def _from_container(buf: np.ndarray, bit_depth: int, alignment: str) -> np.ndarray:
    if bit_depth == 8:
        return buf.astype(np.uint16)
    v = buf.astype(np.uint16)
    if alignment == "msb":
        v = (v >> 6).astype(np.uint16)
    return v


def _bytes_per_sample(bit_depth: int) -> int:
    return 1 if bit_depth == 8 else 2


def _chroma_shape(cfg: Config) -> tuple[int, int]:
    if cfg.subsampling == "4:4:4":
        return cfg.height, cfg.width
    if cfg.subsampling == "4:2:2":
        return cfg.height, cfg.width // 2
    return cfg.height // 2, cfg.width // 2


# ---------------------------------------------------------------- planar


def _pack_planar(frame: Frame, cfg: Config) -> bytes:
    out = bytearray()
    for p in frame.planes:
        out += _to_container(p, cfg.bit_depth, cfg.alignment).tobytes()
    return bytes(out)


def _unpack_planar(data: bytes, cfg: Config) -> Frame:
    bps = _bytes_per_sample(cfg.bit_depth)
    dt = np.uint8 if cfg.bit_depth == 8 else np.dtype("<u2")
    ch, cw = _chroma_shape(cfg)
    shapes = [(cfg.height, cfg.width)]
    shapes += [(cfg.height, cfg.width)] * 2 if cfg.color_model == "rgb" else [(ch, cw)] * 2

    planes = []
    off = 0
    for h, w in shapes:
        n = h * w
        chunk = np.frombuffer(data, dtype=dt, count=n, offset=off).reshape(h, w)
        planes.append(_from_container(chunk, cfg.bit_depth, cfg.alignment))
        off += n * bps
    return Frame(tuple(planes), cfg.color_model, cfg.subsampling, cfg.bit_depth)  # type: ignore[arg-type]


# ---------------------------------------------------------------- packed


def _pack_packed(frame: Frame, cfg: Config) -> bytes:
    if cfg.bit_depth != 8:
        raise PackError("storage=packed は 8bit のみです（10bit は v210 / mipi10 を使ってください）")

    a, b, c = frame.planes
    if cfg.subsampling == "4:4:4":
        # RGB24 (R,G,B) / YCbCr24 (Y,Cb,Cr)
        inter = np.stack([a, b, c], axis=-1).astype(np.uint8)
        return inter.tobytes()

    # 4:2:2 は UYVY（Cb, Y0, Cr, Y1）
    y, cb, cr = a, b, c
    h, w = y.shape
    out = np.empty((h, w // 2, 4), dtype=np.uint8)
    out[:, :, 0] = cb.astype(np.uint8)
    out[:, :, 1] = y[:, 0::2].astype(np.uint8)
    out[:, :, 2] = cr.astype(np.uint8)
    out[:, :, 3] = y[:, 1::2].astype(np.uint8)
    return out.tobytes()


def _unpack_packed(data: bytes, cfg: Config) -> Frame:
    h, w = cfg.height, cfg.width
    if cfg.subsampling == "4:4:4":
        arr = np.frombuffer(data, dtype=np.uint8, count=h * w * 3).reshape(h, w, 3)
        planes = tuple(arr[:, :, i].astype(np.uint16) for i in range(3))
        return Frame(planes, cfg.color_model, cfg.subsampling, cfg.bit_depth)  # type: ignore[arg-type]

    arr = np.frombuffer(data, dtype=np.uint8, count=h * (w // 2) * 4).reshape(h, w // 2, 4)
    y = np.empty((h, w), dtype=np.uint16)
    y[:, 0::2] = arr[:, :, 1]
    y[:, 1::2] = arr[:, :, 3]
    cb = arr[:, :, 0].astype(np.uint16)
    cr = arr[:, :, 2].astype(np.uint16)
    return Frame((y, cb, cr), cfg.color_model, cfg.subsampling, cfg.bit_depth)


# ---------------------------------------------------------------- nv12 / p010


def _pack_semi_planar(frame: Frame, cfg: Config) -> bytes:
    y, cb, cr = frame.planes
    align = "msb" if cfg.storage == "p010" else cfg.alignment
    out = bytearray(_to_container(y, cfg.bit_depth, align).tobytes())
    ch, cw = cb.shape
    inter = np.empty((ch, cw * 2), dtype=np.uint16)
    inter[:, 0::2] = cb
    inter[:, 1::2] = cr
    out += _to_container(inter, cfg.bit_depth, align).tobytes()
    return bytes(out)


def _unpack_semi_planar(data: bytes, cfg: Config) -> Frame:
    align = "msb" if cfg.storage == "p010" else cfg.alignment
    dt = np.uint8 if cfg.bit_depth == 8 else np.dtype("<u2")
    bps = _bytes_per_sample(cfg.bit_depth)
    h, w = cfg.height, cfg.width
    ch, cw = _chroma_shape(cfg)

    y = _from_container(
        np.frombuffer(data, dtype=dt, count=h * w).reshape(h, w), cfg.bit_depth, align
    )
    inter = _from_container(
        np.frombuffer(data, dtype=dt, count=ch * cw * 2, offset=h * w * bps).reshape(ch, cw * 2),
        cfg.bit_depth,
        align,
    )
    return Frame((y, inter[:, 0::2].copy(), inter[:, 1::2].copy()), cfg.color_model,
                 cfg.subsampling, cfg.bit_depth)


# ---------------------------------------------------------------- v210

#: v210 は 6 画素を 4 個の 32bit ワードへ詰め、行の先頭を 128 バイト境界へ揃える。
V210_ROW_ALIGN = 128


def v210_row_stride(width: int) -> int:
    """v210 の 1 行あたりのバイト数（128 バイト境界へ切り上げ）."""
    words = (width // 6) * 4
    raw = words * 4
    return ((raw + V210_ROW_ALIGN - 1) // V210_ROW_ALIGN) * V210_ROW_ALIGN


def _pack_v210(frame: Frame, cfg: Config) -> bytes:
    y, cb, cr = (p.astype(np.uint32) for p in frame.planes)
    h, w = cfg.height, cfg.width
    groups = w // 6
    stride = v210_row_stride(w)

    words = np.zeros((h, groups, 4), dtype=np.uint32)
    # 6 画素ぶんの Y と、3 組ぶんの Cb/Cr を取り出す
    yg = y.reshape(h, groups, 6)
    cbg = cb.reshape(h, groups, 3)
    crg = cr.reshape(h, groups, 3)

    words[:, :, 0] = cbg[:, :, 0] | (yg[:, :, 0] << 10) | (crg[:, :, 0] << 20)
    words[:, :, 1] = yg[:, :, 1] | (cbg[:, :, 1] << 10) | (yg[:, :, 2] << 20)
    words[:, :, 2] = crg[:, :, 1] | (yg[:, :, 3] << 10) | (cbg[:, :, 2] << 20)
    words[:, :, 3] = yg[:, :, 4] | (crg[:, :, 2] << 10) | (yg[:, :, 5] << 20)

    row_bytes = words.reshape(h, groups * 4).astype("<u4").tobytes()
    raw = groups * 16
    out = bytearray()
    for r in range(h):
        out += row_bytes[r * raw : (r + 1) * raw]
        out += b"\x00" * (stride - raw)
    return bytes(out)


def _unpack_v210(data: bytes, cfg: Config) -> Frame:
    h, w = cfg.height, cfg.width
    groups = w // 6
    stride = v210_row_stride(w)
    raw = groups * 16

    buf = bytearray()
    for r in range(h):
        buf += data[r * stride : r * stride + raw]
    words = np.frombuffer(bytes(buf), dtype="<u4").reshape(h, groups, 4)

    def f(word: np.ndarray, pos: int) -> np.ndarray:
        return ((word >> (10 * pos)) & 0x3FF).astype(np.uint16)

    y = np.empty((h, w), dtype=np.uint16)
    cb = np.empty((h, w // 2), dtype=np.uint16)
    cr = np.empty((h, w // 2), dtype=np.uint16)

    yg = y.reshape(h, groups, 6)
    cbg = cb.reshape(h, groups, 3)
    crg = cr.reshape(h, groups, 3)

    cbg[:, :, 0] = f(words[:, :, 0], 0)
    yg[:, :, 0] = f(words[:, :, 0], 1)
    crg[:, :, 0] = f(words[:, :, 0], 2)
    yg[:, :, 1] = f(words[:, :, 1], 0)
    cbg[:, :, 1] = f(words[:, :, 1], 1)
    yg[:, :, 2] = f(words[:, :, 1], 2)
    crg[:, :, 1] = f(words[:, :, 2], 0)
    yg[:, :, 3] = f(words[:, :, 2], 1)
    cbg[:, :, 2] = f(words[:, :, 2], 2)
    yg[:, :, 4] = f(words[:, :, 3], 0)
    crg[:, :, 2] = f(words[:, :, 3], 1)
    yg[:, :, 5] = f(words[:, :, 3], 2)

    return Frame((y, cb, cr), cfg.color_model, cfg.subsampling, cfg.bit_depth)


# ---------------------------------------------------------------- mipi10


def mipi10_pack_plane(plane: np.ndarray) -> bytes:
    """4 サンプルを 5 バイトへ詰める（上位 8bit×4 + 下位 2bit の寄せ集め 1 バイト）."""
    h, w = plane.shape
    if w % 4:
        raise PackError("mipi10 は幅が 4 の倍数である必要があります")
    s = plane.astype(np.uint16).reshape(h, w // 4, 4)
    out = np.empty((h, w // 4, 5), dtype=np.uint8)
    for i in range(4):
        out[:, :, i] = (s[:, :, i] >> 2).astype(np.uint8)
    out[:, :, 4] = (
        (s[:, :, 0] & 3)
        | ((s[:, :, 1] & 3) << 2)
        | ((s[:, :, 2] & 3) << 4)
        | ((s[:, :, 3] & 3) << 6)
    ).astype(np.uint8)
    return out.tobytes()


def mipi10_unpack_plane(data: bytes, height: int, width: int, offset: int = 0) -> np.ndarray:
    groups = width // 4
    n = height * groups * 5
    arr = np.frombuffer(data, dtype=np.uint8, count=n, offset=offset).reshape(height, groups, 5)
    out = np.empty((height, groups, 4), dtype=np.uint16)
    lsb = arr[:, :, 4].astype(np.uint16)
    for i in range(4):
        out[:, :, i] = (arr[:, :, i].astype(np.uint16) << 2) | ((lsb >> (2 * i)) & 3)
    return out.reshape(height, width)


def _pack_mipi10(frame: Frame, cfg: Config) -> bytes:
    return b"".join(mipi10_pack_plane(p) for p in frame.planes)


def _unpack_mipi10(data: bytes, cfg: Config) -> Frame:
    ch, cw = _chroma_shape(cfg)
    shapes = [(cfg.height, cfg.width)]
    shapes += [(cfg.height, cfg.width)] * 2 if cfg.color_model == "rgb" else [(ch, cw)] * 2

    planes = []
    off = 0
    for h, w in shapes:
        planes.append(mipi10_unpack_plane(data, h, w, offset=off))
        off += h * (w // 4) * 5
    return Frame(tuple(planes), cfg.color_model, cfg.subsampling, cfg.bit_depth)  # type: ignore[arg-type]


# ---------------------------------------------------------------- 入口

_PACKERS = {
    "planar": (_pack_planar, _unpack_planar),
    "packed": (_pack_packed, _unpack_packed),
    "nv12": (_pack_semi_planar, _unpack_semi_planar),
    "p010": (_pack_semi_planar, _unpack_semi_planar),
    "v210": (_pack_v210, _unpack_v210),
    "mipi10": (_pack_mipi10, _unpack_mipi10),
}


def pack(frame: Frame, cfg: Config) -> bytes:
    """プレーン群をバイト列へ詰める."""
    if cfg.storage not in _PACKERS:
        raise PackError(f"未知の storage: {cfg.storage}")
    return _PACKERS[cfg.storage][0](frame, cfg)


def unpack(data: bytes, cfg: Config) -> Frame:
    """バイト列をプレーン群へ読み戻す（``pack`` の逆）."""
    if cfg.storage not in _PACKERS:
        raise PackError(f"未知の storage: {cfg.storage}")
    return _PACKERS[cfg.storage][1](data, cfg)


def expected_size(cfg: Config) -> int:
    """その条件で生成される RAW のバイト数を、詰める前に計算する."""
    h, w = cfg.height, cfg.width
    ch, cw = _chroma_shape(cfg)

    if cfg.storage == "v210":
        return v210_row_stride(w) * h
    if cfg.storage == "mipi10":
        if cfg.color_model == "rgb":
            return 3 * h * (w // 4) * 5
        return h * (w // 4) * 5 + 2 * ch * (cw // 4) * 5
    if cfg.storage == "packed":
        return h * w * 3 if cfg.subsampling == "4:4:4" else h * (w // 2) * 4

    bps = _bytes_per_sample(cfg.bit_depth)
    if cfg.color_model == "rgb":
        return 3 * h * w * bps
    return (h * w + 2 * ch * cw) * bps

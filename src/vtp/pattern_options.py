"""パターン固有オプションの目録.

``patterns.py`` の各パターンは ``_opt(options, "steps", 11)`` の形で固有の
つまみを読みます。値そのものはそこで完結しますが、**それが何のつまみで、
どこまで動かせて、動かすと何が変わるのか**はコードから読み取れません。

ここはその情報だけを持ちます。用途は 2 つです。

1. 受け取った値を確かめる。``_opt`` は ``options.get`` なので、名前を
   打ち間違えても黙って既定値で通ります。``--pattern-option stpes=16`` が
   何事もなく成功してしまうのは、生成物を信じられなくします。
2. 画面へ出す。ビューアの生成パネルは ``--describe`` から読むだけで、
   どのパターンに何のつまみがあるかを自分では知りません。
   つまみを足したときに直す場所をここ 1 か所に閉じます。

``patterns.py`` の ``_opt`` 呼び出しとこの目録がずれると、
``tests/test_pattern_options.py`` が落ちます（構文木を読んで突き合わせます）。
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

__all__ = [
    "AutoRule",
    "Option",
    "PATTERN_OPTIONS",
    "auto_default",
    "options_for",
    "validate_pattern_options",
    "describe_pattern_options",
]


def _num(value: float, *, integer: bool) -> str:
    if integer:
        return str(int(value))
    return f"{value:.1f}" if float(value).is_integer() else str(value)


@dataclass(frozen=True)
class AutoRule:
    """既定が寸法から決まるときの求め方.

    対象の 12 個はすべて ``max(下限, 基準 ÷ 除数)`` の形で揃っています。
    文章ではなくこの形で持つのは、**同じ規則を 2 か所に書かないため**です。

    以前は ``patterns.py`` の式と、ここの説明文とに分かれていました。
    片方だけ直せば黙ってずれますし、画面側は文章から数を出せないので
    「格子の間隔」の欄が空のままでした。ここを唯一の出どころにして、
    ``patterns.py`` も ``--describe`` も同じものを読みます。
    """

    basis: str
    """``"width"`` なら幅だけで決めます。``"min"`` なら幅と高さの小さいほうです."""

    divisor: float
    """基準をいくつで割るか."""

    floor: float
    """これより小さくしません（1 画素未満の線などを作らないため）."""

    integer: bool = True
    """割り算を切り捨てて整数にするか."""

    def resolve(self, width: int, height: int) -> float:
        """その寸法での既定値."""
        base = width if self.basis == "width" else min(width, height)
        if self.integer:
            return int(max(self.floor, base // int(self.divisor)))
        return max(self.floor, base / self.divisor)

    def text(self) -> str:
        """画面のヒントに出す求め方."""
        base = "幅" if self.basis == "width" else "min(幅, 高さ)"
        op = "//" if self.integer else "/"
        return (
            f"max({_num(self.floor, integer=self.integer)}, "
            f"{base} {op} {_num(self.divisor, integer=True)})"
        )


def auto_default(pattern: str, name: str, width: int, height: int) -> float:
    """寸法から決まる既定値を返す（``patterns.py`` と画面の両方がここを読みます）."""
    for opt in options_for(pattern):
        if opt.name == name:
            if opt.auto is None:
                raise KeyError(f"{pattern}.{name} は寸法依存の既定ではありません")
            return opt.auto.resolve(width, height)
    raise KeyError(f"{pattern} に {name!r} というつまみはありません")


@dataclass(frozen=True)
class Option:
    """つまみ 1 つ分の説明."""

    name: str
    """``--pattern-option`` で使う名前。``_opt`` の第 2 引数と一致させます。"""

    label: str
    """画面に出す短い名前."""

    kind: str
    """``int`` / ``float`` / ``bool`` / ``choice`` / ``ints`` / ``floats`` / ``color``."""

    help: str
    """動かすと何が変わるか。選ぶときの目安まで書きます."""

    default: Any = None
    """既定値。``None`` は寸法から決まることを表し、``auto`` に求め方を書きます."""

    auto: AutoRule | None = None
    """既定が寸法依存のときの求め方。固定の既定値を持つものは ``None`` です."""

    minimum: float | None = None
    maximum: float | None = None
    choices: tuple[str, ...] = ()
    length: int | None = None
    """``ints`` / ``floats`` で個数が決まっているときの個数."""


def _frac(name: str, label: str, help_: str, default: float) -> Option:
    """0〜1 で表す割合のつまみ（明るさや位置の比）."""
    return Option(name, label, "float", help_, default, minimum=0.0, maximum=1.0)


_ORIENTATION = ("horizontal", "vertical")
_BACKGROUND = "地の明るさ。図の線と差を付けるために使います。0 が黒、1 が白です。"


PATTERN_OPTIONS: dict[str, tuple[Option, ...]] = {
    "colorbar": (
        _frac("level", "振幅", "バー全体の明るさ。0.75 にすると 75% カラーバーになります。", 1.0),
        Option("orientation", "並べる向き", "choice", "バーを横に並べるか縦に並べるかです。", "horizontal", choices=_ORIENTATION),
    ),
    "graysteps": (
        Option("steps", "段数", "int", "黒から白までを何段に割るか。11 なら 0%、10%、…、100% です。段を増やすと 1 段あたりの差が小さくなり、量子化の粗さが見えやすくなります。", 11, minimum=2, maximum=256),
    ),
    "frame": (
        Option("safe", "安全枠の比", "floats", "画面の内側に引く枠の大きさを、外側から順に割合で並べます。0.9 と 0.8 なら 90% と 80% の位置です。", [0.9, 0.8], minimum=0.0, maximum=1.0),
        Option("thickness", "線の太さ", "int", "枠線の太さ（画素）。細くするほど、表示側の拡大縮小で線が消えるかを試せます。", None, auto=AutoRule(basis="min", divisor=240, floor=1), minimum=1, maximum=64),
    ),
    "crosshair": (
        Option("thickness", "線の太さ", "int", "十字線の太さ（画素）です。", None, auto=AutoRule(basis="min", divisor=360, floor=1), minimum=1, maximum=64),
        Option("tick", "目盛の間隔", "int", "十字線に刻む目盛の間隔（画素）。表示側が伸縮していると目盛の間隔が崩れます。", None, auto=AutoRule(basis="min", divisor=20, floor=8), minimum=2, maximum=512),
    ),
    "grid": (
        Option("step", "格子の間隔", "int", "縦横の線の間隔（画素）です。", None, auto=AutoRule(basis="min", divisor=16, floor=8), minimum=2, maximum=1024),
        Option("thickness", "線の太さ", "int", "格子線の太さ（画素）です。", None, auto=AutoRule(basis="min", divisor=480, floor=1), minimum=1, maximum=64),
    ),
    "circles": (
        Option("step", "円の間隔", "int", "同心円の半径の刻み（画素）です。", None, auto=AutoRule(basis="min", divisor=16, floor=8), minimum=2, maximum=1024),
        Option("thickness", "線の太さ", "float", "円周の太さ（画素）。円は斜めの縁が続くので、細くすると縁の処理の差が出やすくなります。", None, auto=AutoRule(basis="min", divisor=480, floor=1.0, integer=False), minimum=0.5, maximum=64.0),
    ),
    "radial": (
        Option("spokes", "放射の本数", "int", "中心から伸びる線の本数。増やすほど中心付近が詰まり、間引きの限界が早く来ます。", 36, minimum=2, maximum=720),
    ),
    "hatch": (
        Option("period", "縞の周期", "int", "縞 1 往復の画素数。2 なら 1 画素おきで、これが最も細かい縞です。", 2, minimum=2, maximum=1024),
        Option("orientation", "縞の向き", "choice", "縦縞・横縞・格子（縦横の排他的論理和）を選びます。", "vertical", choices=("vertical", "horizontal", "both")),
        Option("on", "明るい側の色", "color", "縞の明るい側の RGB を 0〜1 で指定します。", [1.0, 1.0, 1.0], minimum=0.0, maximum=1.0, length=3),
        Option("off", "暗い側の色", "color", "縞の暗い側の RGB を 0〜1 で指定します。色差だけの縞にすると 4:2:0 の間引きがはっきり出ます。", [0.0, 0.0, 0.0], minimum=0.0, maximum=1.0, length=3),
    ),
    "dots": (
        Option("step", "点の間隔", "int", "点を置く間隔（画素）です。", 16, minimum=2, maximum=1024),
    ),
    "blocks": (
        Option("cols", "横の分割数", "int", "横に並べる区画の数です。", 8, minimum=1, maximum=256),
        Option("rows", "縦の分割数", "int", "縦に並べる区画の数です。", 6, minimum=1, maximum=256),
    ),
    "smptebars": (
        _frac("level", "振幅", "上段カラーバーの明るさ。放送で使う 75% が既定です。", 0.75),
        _frac("pluge", "PLUGE の差", "下段に置く黒近傍の段差。黒の沈み方を見るための差分で、小さいほど厳しい確認になります。", 0.04),
    ),
    "pluge": (
        _frac("delta", "黒からの差", "黒に対して付ける明暗の差。表示側の黒つぶれを見ます。", 0.06),
        Option("bars", "本数", "int", "並べる帯の本数です。", 6, minimum=2, maximum=64),
    ),
    "multiburst": (
        Option("periods", "周期の並び", "ints", "帯ごとの縞の周期（画素）を粗い順に並べます。2 が 1 画素おきで限界です。表示側の解像力がどこで落ちるかを見ます。", [16, 8, 4, 3, 2], minimum=2, maximum=1024),
    ),
    "window": (
        _frac("size", "窓の大きさ", "中央に置く四角の一辺を、画面に対する割合で指定します。", 0.25),
        _frac("level", "窓の明るさ", "窓の中の明るさ。地は黒です。明るい面積が変わると表示側の輝度が動く機種があります。", 1.0),
    ),
    "zoneplate": (
        Option("max_frequency", "最高周波数", "float", "画面端での縞の細かさ（1 画素あたりの周期数）。0.5 が 1 画素おきで、これ以上は元の絵として成立しません。", 0.5, minimum=0.01, maximum=0.5),
    ),
    "checker": (
        Option("cols", "横のます目", "int", "市松の横のます目の数です。", 8, minimum=1, maximum=512),
        Option("rows", "縦のます目", "int", "市松の縦のます目の数です。", 8, minimum=1, maximum=512),
    ),
    "pulsebar": (
        Option("pulse", "パルスの幅", "int", "細い縦線の幅（画素）。細いほど、表示側が信号をなまらせているかが出ます。", None, auto=AutoRule(basis="width", divisor=160, floor=1), minimum=1, maximum=512),
        _frac("bar", "バーの幅", "並べて置く太い帯の幅を、画面に対する割合で指定します。細い線と同じ高さで出るかを比べます。", 0.25),
    ),
    "splitbars": (
        _frac("top", "上段の振幅", "上半分のカラーバーの明るさです。", 1.0),
        _frac("bottom", "下段の振幅", "下半分のカラーバーの明るさ。上下で違う振幅を並べ、飽和の差を見ます。", 0.75),
    ),
    "rainbow": (
        Option("orientation", "並べる向き", "choice", "色相を横に回すか縦に回すかです。", "horizontal", choices=_ORIENTATION),
        _frac("saturation", "彩度", "色の濃さ。下げると白に寄ります。", 1.0),
        _frac("value", "明度", "全体の明るさです。", 1.0),
    ),
    "sweep": (
        Option("start", "始まりの周波数", "float", "左端（上端）の細かさ（1 画素あたりの周期数）です。", 0.02, minimum=0.001, maximum=0.5),
        Option("end", "終わりの周波数", "float", "右端（下端）の細かさ。0.5 が 1 画素おきの限界です。", 0.5, minimum=0.001, maximum=0.5),
        Option("orientation", "掃く向き", "choice", "周波数を横方向に上げるか縦方向に上げるかです。", "horizontal", choices=_ORIENTATION),
    ),
    "shallowramp": (
        _frac("center", "中心の明るさ", "傾斜の中央になる明るさです。", 0.5),
        Option("amplitude", "振れ幅", "float", "中心から上下へ振る幅。狭くするほど 1 段の差が小さくなり、8bit では段差（バンディング）が出ます。", 0.05, minimum=0.001, maximum=0.5),
        Option("orientation", "傾ける向き", "choice", "横方向に傾けるか縦方向に傾けるかです。", "horizontal", choices=_ORIENTATION),
    ),
    "triangleramp": (
        Option("orientation", "傾ける向き", "choice", "折り返す傾斜の向きです。", "horizontal", choices=_ORIENTATION),
    ),
    "square": (
        _frac("size", "四角の大きさ", "中央の四角の一辺を、画面に対する割合で指定します。", 0.6),
        Option("thickness", "線の太さ", "int", "四角の線の太さ（画素）です。", None, auto=AutoRule(basis="min", divisor=200, floor=1), minimum=1, maximum=64),
    ),
    "stepmatrix": (
        Option("cols", "横の段数", "int", "横に並べる段の数です。", 16, minimum=1, maximum=256),
        Option("rows", "縦の段数", "int", "縦に並べる段の数。縦横で 256 段まで並べると 8bit の全コードを敷き詰められます。", 16, minimum=1, maximum=256),
    ),
    "wedge": (
        Option("lines", "くさびの本数", "int", "1 つのくさびに入れる線の本数です。", 12, minimum=2, maximum=256),
        Option("direction", "置く向き", "choice", "縦横の両方に置くか、片方だけにするかです。", "all", choices=("all", "horizontal", "vertical")),
        _frac("inner", "内側の位置", "くさびの内端の位置を、画面に対する割合で指定します。", 0.05),
        _frac("outer", "外側の位置", "くさびの外端の位置です。内側との差が広いほど、細かさの変化がなだらかになります。", 0.46),
        _frac("background", "地の明るさ", _BACKGROUND, 0.5),
    ),
    "testcard": (
        _frac("background", "地の明るさ", _BACKGROUND, 0.5),
        Option("blocks", "外周の区画数", "int", "縁に並べる区画の数です。切れている辺があれば表示範囲が足りていません。", 16, minimum=4, maximum=256),
        Option("grid", "格子の間隔", "int", "背景の格子の間隔（画素）です。", None, auto=AutoRule(basis="min", divisor=12, floor=8), minimum=2, maximum=1024),
        Option("steps", "階調の段数", "int", "下部に置く濃淡の段数です。", 11, minimum=2, maximum=64),
        Option("wedge_lines", "くさびの本数", "int", "解像度くさびの線の本数です。", 10, minimum=2, maximum=128),
    ),
    "gamma": (
        Option("patches", "パッチ数", "int", "並べる濃淡パッチの数です。", 9, minimum=2, maximum=64),
        _frac("start", "始まりの明るさ", "いちばん暗いパッチの明るさです。", 0.35),
        _frac("end", "終わりの明るさ", "いちばん明るいパッチの明るさ。細かい縞と平坦面を並べ、表示側の応答の傾きを見ます。", 0.95),
    ),
    "colorramp": (
        Option("orientation", "傾ける向き", "choice", "色の傾斜の向きです。", "horizontal", choices=_ORIENTATION),
    ),
    "colormatrix": (
        Option("levels", "軸あたりの段数", "int", "R・G・B それぞれを何段に割るか。6 なら 216 色になります。増やすと 1 区画が小さくなります。", 6, minimum=2, maximum=16),
    ),
    "noise": (
        Option("seed", "並びの種", "int", "同じ数を入れれば毎回同じ並びになります。比較のときは変えないでください。", 1, minimum=0, maximum=2**31 - 1),
        Option("mono", "白黒にする", "bool", "切ると RGB それぞれに別の値が入り、色付きの粒になります。", True),
        _frac("center", "中心の明るさ", "粒がばらける中心の明るさです。", 0.5),
        Option("amplitude", "振れ幅", "float", "中心からのばらつきの幅。広げるほど粗い粒になります。", 0.5, minimum=0.0, maximum=0.5),
    ),
    "barshd": (
        _frac("level", "振幅", "カラーバーの明るさです。", 0.75),
        _frac("flank", "脇の明るさ", "バーの左右に置く帯の明るさです。", 0.4),
        _frac("pluge", "PLUGE の差", "黒近傍に付ける段差です。", 0.06),
    ),
    "splitsteps": (
        Option("steps", "段数", "int", "上下で向きを変えて並べる濃淡の段数です。隣り合う段の差を上下で見比べられます。", 11, minimum=2, maximum=256),
    ),
    "geometrycard": (
        Option("grid", "格子の間隔", "int", "背景の格子の間隔（画素）です。", None, auto=AutoRule(basis="min", divisor=16, floor=8), minimum=2, maximum=1024),
        Option("blocks", "外周の区画数", "int", "縁に並べる区画の数です。", 16, minimum=4, maximum=256),
    ),
    "resolutioncard": (
        Option("lines", "くさびの本数", "int", "解像度くさびの線の本数です。", 10, minimum=2, maximum=128),
        _frac("background", "地の明るさ", _BACKGROUND, 0.5),
        Option("periods", "周期の並び", "ints", "並べる縞の周期（画素）です。2 が 1 画素おきで限界になります。", [16, 8, 4, 3, 2], minimum=2, maximum=1024),
    ),
    "siemens": (
        Option("spokes", "放射の本数", "int", "星の羽根の数。中心は 1 周期が 2 画素を切るため、そこだけ地の色で伏せます。羽根を増やすほど伏せる円が大きくなります。", 36, minimum=4, maximum=720),
        _frac("background", "地の明るさ", _BACKGROUND, 0.5),
    ),
    "linepairs": (
        _frac("background", "地の明るさ", _BACKGROUND, 0.5),
        Option("widths", "線の太さの並び", "ints", "並べる線対の太さ（画素）です。1 が 1 画素幅で、表示側の解像力の限界を見ます。", [1, 2, 3, 4, 6, 8], minimum=1, maximum=256),
    ),
    "slantedge": (
        Option("angle", "傾き", "float", "縁の傾き（度）。わずかに傾けるのは、1 本の縁で画素より細かい応答を測るためです。0 度や 45 度では測れません。", 5.0, minimum=1.0, maximum=44.0),
        _frac("low", "暗い側", "縁の暗い側の明るさです。", 0.1),
        _frac("high", "明るい側", "縁の明るい側の明るさです。", 0.9),
    ),
    "raster": (
        Option("color", "色", "color", "画面全体を塗る RGB を 0〜1 で指定します。平坦な面は 4:4:4 でも 4:2:0 でも同じに出るはずで、違えば処理の側の問題です。", [1.0, 1.0, 1.0], minimum=0.0, maximum=1.0, length=3),
        _frac("level", "明るさ", "色に掛ける倍率です。", 1.0),
    ),
    "monoscope": (
        _frac("background", "地の明るさ", _BACKGROUND, 0.5),
        Option("blocks", "外周の区画数", "int", "縁に並べる区画の数です。", 20, minimum=4, maximum=256),
        Option("grid", "格子の間隔", "int", "背景の格子の間隔（画素）です。", None, auto=AutoRule(basis="min", divisor=12, floor=8), minimum=2, maximum=1024),
        Option("steps", "階調の段数", "int", "下部に置く濃淡の段数です。", 9, minimum=2, maximum=64),
        _frac("level", "カラーバーの振幅", "上部に置くカラーバーの明るさです。", 0.75),
        Option("periods", "周期の並び", "ints", "縞の周期（画素）です。添える数字は指定値ではなく、実際に置けた周期から求めます。丸めた結果がそのまま数字になります。", [12, 8, 6, 4, 3, 2], minimum=2, maximum=1024),
    ),
    "digitalcard": (
        _frac("background", "地の明るさ", _BACKGROUND, 0.5),
        Option("tick", "目盛の間隔", "int", "縁に刻む目盛の間隔（画素）です。", 10, minimum=2, maximum=512),
        Option("pitch", "市松の細かさ", "int", "画素単位の市松の周期。2 が 1 画素おきです。", 2, minimum=2, maximum=64),
        Option("steps", "階調の段数", "int", "濃淡の段数です。", 8, minimum=2, maximum=64),
        Option("chroma_on", "色差の明るい側", "color", "色差だけの市松の片側の RGB です。", [1.0, 0.0, 0.0], minimum=0.0, maximum=1.0, length=3),
        Option("chroma_off", "色差の暗い側", "color", "もう片側の RGB。明るさをそろえて色だけ変えると、4:2:0 で色が溶けるのが見えます。", [0.0, 0.0, 1.0], minimum=0.0, maximum=1.0, length=3),
    ),
}


def options_for(pattern: str) -> tuple[Option, ...]:
    """パターンのつまみを返す（無ければ空）."""
    return PATTERN_OPTIONS.get(pattern, ())


_LIST_KINDS = {"ints", "floats", "color"}


def validate_pattern_options(pattern: str, options: dict[str, Any]) -> None:
    """渡されたつまみを確かめる。おかしければ ``ValueError`` を投げる.

    ``_opt`` は ``options.get`` なので、ここで弾かないと打ち間違いが
    そのまま既定値の生成物になって出てきます。
    """
    if not options:
        return

    known = {opt.name: opt for opt in options_for(pattern)}

    for name, value in options.items():
        opt = known.get(name)
        if opt is None:
            if known:
                near = "、".join(sorted(known))
                raise ValueError(
                    f"{pattern} に {name!r} というオプションはありません。使えるのは {near} です。"
                )
            raise ValueError(f"{pattern} にオプションはありません（指定: {name!r}）。")

        _check_value(pattern, opt, value)


def _check_value(pattern: str, opt: Option, value: Any) -> None:
    where = f"{pattern} の {opt.name}"

    if opt.kind == "bool":
        if not isinstance(value, bool):
            raise ValueError(f"{where} は true か false です（指定: {value!r}）。")
        return

    if opt.kind == "choice":
        if value not in opt.choices:
            allowed = " / ".join(opt.choices)
            raise ValueError(f"{where} は {allowed} です（指定: {value!r}）。")
        return

    if opt.kind in _LIST_KINDS:
        if isinstance(value, (str, bytes)) or not isinstance(value, (list, tuple)):
            raise ValueError(f"{where} は数値の並びです（指定: {value!r}）。")
        if opt.length is not None and len(value) != opt.length:
            raise ValueError(f"{where} は {opt.length} 個です（指定: {len(value)} 個）。")
        if not value:
            raise ValueError(f"{where} が空です。")
        for item in value:
            _check_number(where, opt, item)
        return

    _check_number(where, opt, value)


def _check_number(where: str, opt: Option, value: Any) -> None:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{where} は数値です（指定: {value!r}）。")
    if opt.kind in ("int", "ints") and not float(value).is_integer():
        raise ValueError(f"{where} は整数です（指定: {value!r}）。")
    if opt.minimum is not None and value < opt.minimum:
        raise ValueError(f"{where} は {opt.minimum} 以上です（指定: {value}）。")
    if opt.maximum is not None and value > opt.maximum:
        raise ValueError(f"{where} は {opt.maximum} 以下です（指定: {value}）。")


def describe_pattern_options() -> dict[str, list[dict[str, Any]]]:
    """``--describe`` へ載せる形にする."""
    out: dict[str, list[dict[str, Any]]] = {}
    for pattern, opts in PATTERN_OPTIONS.items():
        rows: list[dict[str, Any]] = []
        for opt in opts:
            row: dict[str, Any] = {
                "name": opt.name,
                "label": opt.label,
                "kind": opt.kind,
                "help": opt.help,
                "default": opt.default,
            }
            if opt.auto is not None:
                # 文章は画面のヒント用、数は画面が既定値を出すため。
                # 求め方は 1 か所（AutoRule）から出しているので、ずれません。
                row["auto"] = opt.auto.text()
                row["auto_basis"] = opt.auto.basis
                row["auto_divisor"] = opt.auto.divisor
                row["auto_floor"] = opt.auto.floor
                row["auto_integer"] = opt.auto.integer
            if opt.minimum is not None:
                row["minimum"] = opt.minimum
            if opt.maximum is not None:
                row["maximum"] = opt.maximum
            if opt.choices:
                row["choices"] = list(opt.choices)
            if opt.length is not None:
                row["length"] = opt.length
            rows.append(row)
        out[pattern] = rows
    return out

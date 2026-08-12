# video-test-patterns

映像テストパターン生成器の **Python リファレンス実装** です。

カラーバー・グレースケール・格子・同心円といった人工パターンを、
色モデル・色差サンプリング・ビット深度・range・matrix・格納形式を明示して生成し、
RAW と PNG プレビュー、そして生成条件を記録した manifest JSON を一組で出力します。

このツールの目的は、きれいな絵を作ることではありません。
**映像処理の入力条件と期待結果を、後から再現できる形で残すこと**です。

```sh
vtp --pattern colorbar --width 1920 --height 1080 \
    --color-model ycbcr --subsampling 4:2:0 --bit-depth 8 \
    --range limited --matrix bt709 --storage nv12 \
    --output generated/colorbar_nv12
```

```text
json  generated/colorbar_nv12.manifest.json
png   generated/colorbar_nv12.preview.png
raw   generated/colorbar_nv12.raw
RAW サイズ: 3110400 バイト（往復確認 OK）
```

## これは何のためのものか

映像処理の不具合を自然画像で追うのは難しいところがあります。
色順の入れ替わり、黒つぶれ、白飛び、アスペクト比のずれ、レンズ歪みは、
入力画像のどこを見ればよいかが分からないためです。
色と形があらかじめ決まった人工パターンなら、確認すべき画素・線・色を先に決められます。

| 見たい現象 | 向くパターン |
| --- | --- |
| 色順・チャンネルの入れ替わり | `colorbar`, `colorbar75` |
| 黒つぶれ・白飛び・階調の欠落 | `graysteps`, `grayramp` |
| オーバースキャン・安全領域 | `frame` |
| 中心位置・アスペクト比のずれ | `crosshair`, `circles` |
| レンズ歪み・射影変換のずれ | `grid`, `circles`, `radial` |
| 4:2:2 / 4:2:0 の色差劣化 | `hatch` |
| ドット欠け・画素の欠落 | `dots` |
| 領域の重複・入れ替わり | `blocks`, `checker` |
| 黒レベルのずれ（黒浮き・黒つぶれ） | `pluge`, `smptebars` |
| 解像限界・周波数特性 | `multiburst`, `pulsebar` |
| 縮小・間引きでの折り返し（エイリアス） | `zoneplate` |
| 白の面積による明るさの変動 | `window` |
| 色相の連続性・色域 | `rainbow` |
| 量子化の段差（バンディング） | `shallowramp`, `stepmatrix` |
| 画素の縦横比 | `square`, `circles` |
| 向きごとの解像限界 | `wedge` |
| ひととおりまとめて | `testcard` |

### 作成できるパターン一覧

以下は、色変換やRAW格納形式を含まない、パターンそのもの（RGB888）のサンプルです。
画像はすべて640×480のPNGで、[サンプルフォルダ](samples/patterns/)からもまとめて確認できます。

| パターン | サンプル | 用途 |
| --- | --- | --- |
| `colorbar` | [colorbar.png](samples/patterns/colorbar.png) | 100%カラーバー、色順・チャンネル確認 |
| `colorbar75` | [colorbar75.png](samples/patterns/colorbar75.png) | 75%カラーバー、レベル確認 |
| `graysteps` | [graysteps.png](samples/patterns/graysteps.png) | 階調・黒つぶれ・白飛び確認 |
| `grayramp` | [grayramp.png](samples/patterns/grayramp.png) | 連続階調・バンディング確認 |
| `frame` | [frame.png](samples/patterns/frame.png) | オーバースキャン・安全領域確認 |
| `crosshair` | [crosshair.png](samples/patterns/crosshair.png) | 中心位置・アスペクト比確認 |
| `grid` | [grid.png](samples/patterns/grid.png) | レンズ歪み・射影変換確認 |
| `circles` | [circles.png](samples/patterns/circles.png) | 中心・アスペクト比・レンズ歪み確認 |
| `radial` | [radial.png](samples/patterns/radial.png) | 回転ずれ・放射方向の歪み確認 |
| `hatch` | [hatch.png](samples/patterns/hatch.png) | 色差サンプリング確認 |
| `dots` | [dots.png](samples/patterns/dots.png) | ドット欠け・画素欠落確認 |
| `blocks` | [blocks.png](samples/patterns/blocks.png) | 領域の重複・入れ替わり確認 |
| `smptebars` | [smptebars.png](samples/patterns/smptebars.png) | 3段構成。色順・青の再現・黒レベルをまとめて確認 |
| `pluge` | [pluge.png](samples/patterns/pluge.png) | 黒レベル合わせ（黒のすぐ上を刻んだ帯） |
| `multiburst` | [multiburst.png](samples/patterns/multiburst.png) | 解像限界・周波数特性確認 |
| `window` | [window.png](samples/patterns/window.png) | 白の面積を変えたときの明るさの追従確認 |
| `zoneplate` | [zoneplate.png](samples/patterns/zoneplate.png) | 折り返し（エイリアス）確認 |
| `checker` | [checker.png](samples/patterns/checker.png) | 画素の抜け・反転・領域のずれ確認 |
| `pulsebar` | [pulsebar.png](samples/patterns/pulsebar.png) | 急な変化とゆるやかな変化の崩れ方の違い |
| `splitbars` | [splitbars.png](samples/patterns/splitbars.png) | 振幅違いのカラーバーを上下に並べて比較 |
| `rainbow` | [rainbow.png](samples/patterns/rainbow.png) | 色相の連続性・色域確認 |
| `sweep` | [sweep.png](samples/patterns/sweep.png) | 解像度の落ち始める位置を連続で確認 |
| `shallowramp` | [shallowramp.png](samples/patterns/shallowramp.png) | 量子化の段差（バンディング）確認 |
| `triangleramp` | [triangleramp.png](samples/patterns/triangleramp.png) | 上がりと下がりで挙動が違わないか確認 |
| `square` | [square.png](samples/patterns/square.png) | 画素の縦横比確認 |
| `stepmatrix` | [stepmatrix.png](samples/patterns/stepmatrix.png) | 256階調を格子に並べて多階調を面で確認 |
| `wedge` | [wedge.png](samples/patterns/wedge.png) | 向きごとの解像限界（水平・垂直を分けて確認） |
| `testcard` | [testcard.png](samples/patterns/testcard.png) | 総合パターン。1枚で画角・縦横比・解像・色順・階調 |

サンプルを再生成する場合は、リポジトリのルートで次を実行します。

```sh
python tools/generate_pattern_samples.py
```

### 例: 4:2:0 で色が消えるのを見る

赤と青の 1 画素縞は、輝度がほぼ同じで色差だけが遠い組み合わせです。
これを 4:2:0 に落とすと、輝度の縞は残ったまま色差だけが平均され、紫一色になります。

```sh
vtp --pattern hatch --pattern-option 'on=[1,0,0]' --pattern-option 'off=[0,0,1]' \
    --width 384 --height 216 --color-model ycbcr --subsampling 4:2:0 \
    --range limited --matrix bt709 --storage nv12 --output generated/hatch420
```

白黒の縞では色差が一定なので、この劣化は現れません。
「色差サンプリングの確認には、輝度が近く色差が遠い 2 色が要る」ということです。

## インストール

Python 3.10 以上が必要です。

```sh
git clone https://github.com/okakousuke/video-test-patterns.git
cd video-test-patterns
pip install -e ".[dev]"
```

インストールせずに動かす場合は次のようにします。

```sh
PYTHONPATH=src python -m vtp --help
```

## 設計の柱

### 1. 「何を描くか」と「どう格納するか」を分ける

処理は次の順に一方向へ流れます。各段は前の段の結果しか見ません。

```text
patterns.py       R'G'B' float32 [0,1]        ← 絵だけを決める
    ↓
color_convert.py  Y'/Cb/Cr コード値 (uint16)  ← matrix と range をここで適用
    ↓
subsample.py      色差プレーンを 1/2 や 1/4 へ
    ↓
pack.py           バイト列（planar / packed / nv12 / p010 / v210 / mipi10）
    ↓
pack.unpack()     読み戻して元のプレーンと一致するか確認
    ↓
preview.py + manifest.py   PNG と生成条件
```

こう分けておくと、出た不具合が「絵のバグ」なのか「色変換のバグ」なのか
「ビット詰めのバグ」なのかを切り分けられます。
1 つの関数にまとめてしまうと、この切り分けができません。

### 2. 成立しない組み合わせは補正せずエラーにする

`color_model=rgb` に `--subsampling 4:2:0` を渡す、`p010` を `alignment=lsb` で使う、
v210 に 6 の倍数でない幅を渡す ―― こうした指定は、黙って直さずエラーで止めます。

```sh
$ vtp --color-model rgb --subsampling 4:2:0
エラー: color_model=rgb では subsampling は 4:4:4 のみです（指定: 4:2:0）。色差の間引きは ycbcr で行ってください
```

検証用のツールが気を利かせて補正すると、
「指定した条件で処理した結果」を比較できなくなります。

### 3. 詰めたデータは必ず読み戻して確認する

すべての格納形式に `pack` と `unpack` を対で実装してあり、
生成のたびに往復一致を確認して結果を manifest の `roundtrip_verified` に残します。
ビット詰めの誤り（16bit コンテナの寄せ方、v210 のワード内配置）は、
PNG プレビューを見ても気づけないためです。

### 4. 出力と条件を一組で残す

RAW ファイルだけが残ると、後から「どの条件で作ったのか」が分からなくなります。

```json
{
  "manifest_version": 1,
  "generator": { "name": "video-test-patterns", "version": "0.1.0" },
  "generated_at": "2026-08-07T21:25:01+00:00",
  "parameters": {
    "pattern": "hatch", "width": 1920, "height": 1080,
    "color_model": "ycbcr", "subsampling": "4:2:2", "bit_depth": 10,
    "range": "limited", "matrix": "bt709", "storage": "v210", "alignment": "lsb",
    "pattern_options": { "period": 2, "orientation": "vertical" }
  },
  "parameters_sha256": "059d4304d1dc16fb...",
  "raw_bytes": 5529600,
  "files": [
    { "kind": "raw", "path": "generated/hatch_bt709_10bit_v210.raw",
      "bytes": 5529600, "sha256": "7a9588fda40bcb72..." }
  ],
  "roundtrip_verified": true
}
```

人が編集する設定は JSONC（コメント付き JSON）、
機械が読み直す manifest はコメントなしの素の JSON、と役割を分けています。

## 使い方

### パターンと格納形式の一覧

```sh
vtp --list-patterns
vtp --list-storages
```

### 主なオプション

| オプション | 値 | 意味 |
| --- | --- | --- |
| `--pattern` | `colorbar` ほか | 生成するパターン |
| `--width` / `--height` | 整数 | 画素数 |
| `--color-model` | `rgb` / `ycbcr` | 色成分の表現 |
| `--subsampling` | `4:4:4` / `4:2:2` / `4:2:0` | 色差の間引き |
| `--bit-depth` | `8` / `10` | 1 成分のビット数 |
| `--range` | `full` / `limited` | コード値の使用範囲 |
| `--matrix` | `bt601` / `bt709` / `bt2020` | RGB との変換係数 |
| `--storage` | 下表参照 | メモリ上の並べ方 |
| `--alignment` | `lsb` / `msb` | 10bit を 16bit コンテナへ入れる寄せ方 |
| `--outputs` | `raw,png,json` | 出力の種類 |
| `--config` | パス | JSONC 設定ファイル |
| `--pattern-option` | `KEY=VALUE` | パターン固有の指定（複数可） |
| `--dry-run` | — | 検証と RAW サイズ計算だけ行う |

優先順位は **コマンドライン引数 > 設定ファイル > 既定値** です。

### 格納形式

| storage | color_model | subsampling | bit_depth | 内容 |
| --- | --- | --- | --- | --- |
| `planar` | rgb / ycbcr | 4:4:4 / 4:2:2 / 4:2:0 | 8 / 10 | 成分ごとに連続領域（I420 / I422 / I444 相当） |
| `packed` | rgb / ycbcr | 4:4:4 / 4:2:2 | 8 | RGB24 / YCbCr24 / UYVY |
| `nv12` | ycbcr | 4:2:0 | 8 | Y の後ろに CbCr を交互配置 |
| `p010` | ycbcr | 4:2:0 | 10 | NV12 と同配置で 16bit コンテナ上位詰め |
| `v210` | ycbcr | 4:2:2 | 10 | 6 画素を 4 個の 32bit ワードへ、行は 128 バイト境界 |
| `mipi10` | rgb / ycbcr | 4:4:4 / 4:2:2 / 4:2:0 | 10 | 各プレーンを 4 サンプル 5 バイトへ詰める |

各形式のバイト配置は [`docs/formats.md`](docs/formats.md) にまとめています。

### 設定ファイル

`configs/` に例を置いています。

```sh
vtp --config configs/hatch_bt709_10bit_v210.jsonc
vtp --config configs/colorbar_rgb8.jsonc --width 1280 --height 720   # 上書きも可
```

### サンプル一式の生成

全パターンを、格納形式とビット深度を散らした条件で1本ずつ作ります。

```sh
python tools/make_samples.py            # generated/samples/ へ出力
python tools/make_samples.py --seed 7   # 別のサイズ一式にする
```

画像サイズは、実際に使われている規格の解像度（QCIF / CIF / VGA / SD NTSC / SD PAL / SVGA / XGA /
HD 720p / SXGA / WXGA / HD+ / FHD / WUXGA / QHD / 4K UHD）から選びます。
適当な数字にすると「その幅だから起きた不具合」なのか「珍しい幅だから起きた不具合」なのか
区別が付きません。規格の解像度なら、同じ幅で実機を通したときと突き合わせられます。

どの解像度をどのパターンへ割り当てるかは乱数ですが**シードを固定**しているので、毎回同じになります。
v210 の幅は6の倍数、mipi10 は各プレーンの幅が4の倍数、というように格納形式ごとの条件があるので、
その条件を満たす解像度だけを割り当てます。

既定では WUXGA（1920×1200）までです。10bit の 4K は1本で50MB近くになるため、
使いたいときは `--max-pixels 8294400` を指定してください。

## テスト

```sh
pytest
```

テストは目視に頼らず、次の 2 方向から押さえています。

- **手計算できる数値** ― limited range の白は 235、黒は 16、
  純赤の輝度は `16 + 219 × Kr`、v210 の先頭ワードは `Cb0 | Y0<<10 | Cr0<<20`
- **往復一致** ― 全格納形式について `pack` → `unpack` が 1 ビットも違わないこと

## 対象外

初期版では次を扱いません。

- JPEG / H.264 / H.265 などの圧縮
- 規格適合性の保証、測定器としての用途
- インタレース、HDR の伝達関数（PQ / HLG）、色域変換
- v210 の幅が 6 で割り切れない場合の端数処理（実装ごとに揺れるため）
- 数字を描画するパターン（フォント依存を避けるため、`blocks` は二進マーカーを使います）

このツールは公開されている規格の数値条件を参照して実装していますが、
規格書の図版・メーカー資料・実機キャプチャは一切含みません。
生成されるパターンはすべてこのリポジトリのコードによる自作です。
規格への適合性や、測定器としての正確さを保証するものではありません。

## この先

C/C++ 版（実運用向けのフレーム処理・性能測定）は別に用意し、
この Python 版が生成した RAW と manifest を入力テストに使う想定です。
Python 側は期待値を作る役、C/C++ 側は速度と実装を担う役、と分けます。
両者を同じコードの書き換えにしないのは、同じ誤りを両方へ移さないためです。

## ライセンス

MIT License. [LICENSE](LICENSE) を参照してください。

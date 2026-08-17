# 参照用の RAW と manifest

**格納形式を網羅する**ための一式です。RAW と manifest をリポジトリへ同梱してあるので、
生成器を動かさなくても、読み手側の実装をこのまま試せます。

[`samples/patterns/`](../patterns/) の PNG がパターンの見た目を並べたものなのに対し、
こちらは**同じ絵を全形式で出したもの**です。絵を固定しているのは、形式ごとの違いだけを見るためです。
絵まで変わると、差が形式によるものか絵によるものか分かりません。

## 中身

| 項目 | 内容 |
| --- | --- |
| 画像サイズ | 192 × 144（全ファイル共通） |
| パターン | `colorbar`（形式比較）、`hatch`（色差確認）、用途別テンプレート 15 種 |
| ファイル数 | 36 組（RAW + manifest + プレビュー PNG） |
| 合計 | 約 2.8 MB |

192 × 144 なのは、全形式の条件を同時に満たす小さい値だからです。
v210 は幅が 6 の倍数、mipi10 は色差を間引くと幅が 8 の倍数、4:2:0 は幅も高さも偶数。
192 は 6 でも 8 でも割り切れ、144 は偶数です。

網羅している組み合わせ。

- **RGB 4:4:4** — planar（8bit / 10bit lsb / 10bit msb）、packed（8bit）、mipi10（10bit）
- **YCbCr 4:4:4** — planar（8bit limited / 8bit full / 10bit bt2020）、packed（8bit bt601）、mipi10（10bit）
- **YCbCr 4:2:2** — planar（8bit / 10bit）、packed = UYVY（8bit）、v210（10bit）、mipi10（10bit）
- **YCbCr 4:2:0** — planar（8bit / 10bit）、nv12（8bit）、p010（10bit）、mipi10（10bit）

`hatch_redblue_ycbcr420_8bit_nv12` だけは別の目的です。赤と青の 1 画素縞を 4:2:0 に落としてあり、
輝度の縞は残ったまま色差が平均されて紫一色になる様子が入っています。

## 用途別テンプレート

`template_` で始まる一式は、一般的な映像確認カテゴリを用途別に並べたものです。
機器固有の名称や実測値は含めず、次のような確認目的に対応させています。

| カテゴリ | 例 |
| --- | --- |
| レベル・色 | raster、colorbar、graysteps、pluge、window |
| 同期・位置合わせ | crosshair、grid、geometrycard |
| 周波数・解像 | multiburst、resolutioncard |
| 総合確認 | monoscope、digitalcard、hatch、colormatrix、gamma |

各ファイルは RAW と manifest とプレビュー PNG を一組で収録しています。サイズは
形式網羅セットと同じ 192 × 144 です。追加・再生成には次を使います。

```sh
python tools/make_template_raws.py
```

`make_reference_raws.py` が「同じ絵を全格納形式で比べる」ためのものなのに対し、
こちらは「用途ごとのひな形をビューアやデコーダの入力にする」ためのものです。

## 作り直す

```sh
python tools/make_reference_raws.py
```

同じ条件で作り直せます。パターンを変えたいときは `--pattern` を指定してください。

## 何のためにあるか

**読み手側の実装を、生成器なしで試せるようにするため**です。
manifest に生成条件が入っているので、RAW を正しく読めたかどうかを条件と突き合わせて判定できます。

実際にこの一式を作った時点で、ビューア側で **YCbCr 4:2:2 planar（I422）を UYVY として読んでいた**
不具合が見つかりました。それまでのサンプルに 4:2:2 planar が無く、その経路を一度も通していなかったためです。
形式を網羅する一式を持つ意味はここにあります。

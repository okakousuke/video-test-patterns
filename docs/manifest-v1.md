# manifest v1 共通仕様

Python生成器と、`apps/` 配下のビューアは、このファイルをRAWデータの共通契約として扱います。

現行のビューアは `apps/RawInspector`（WPF）です。`apps/RawManifestViewer`（Windows Forms）はこの契約に対応した状態で凍結してあり、新しい形式への追随は行いません。

## 基本原則

- manifestはコメントを含まないUTF-8 JSONとする。
- `manifest_version` が互換性の境界である。現在は `1` のみ対応する。
- 生成条件は `parameters` に残す。RAWの解釈に必要な条件を、ファイル名だけに依存させない。
- `files[].path` はmanifestファイルのあるディレクトリからの相対パスとする。絶対パスは記録しない。
- 未対応の格納形式でも、ビューアはmanifestを読み込み、対応可否を明示する。

## 必須項目

```json
{
  "manifest_version": 1,
  "generator": { "name": "video-test-patterns", "version": "0.1.0" },
  "generated_at": "2026-08-10T00:00:00+00:00",
  "parameters": {
    "pattern": "colorbar",
    "width": 1920,
    "height": 1080,
    "color_model": "rgb",
    "subsampling": "4:4:4",
    "bit_depth": 8,
    "range": "full",
    "matrix": null,
    "storage": "planar",
    "alignment": "lsb"
  },
  "raw_bytes": 6220800,
  "files": [
    { "kind": "raw", "path": "colorbar.raw", "bytes": 6220800, "sha256": "..." }
  ],
  "roundtrip_verified": true
}
```

## 読み方を差し替えた manifest（`derived_from`）

同じRAWを別の条件で読んだ状態は、**RAWではなく manifest 側に残す**。
表示条件を変えてもRAWのバイト列は1バイトも変わらないので、RAWの名前に `_bt601` のような印を付けると
「bt601 へ変換したRAW」があるように見える。実際にはそんなものは作っていない。

`apps/RawInspector` は、いまの読み方を **同じRAWを指したまま** 別の manifest として書き出せる。
生成器はこの項目を書かない。読む側は、知らなければ無視してよい（v1 の必須項目は変わらない）。

```json
{
  "parameters": { "matrix": "bt601", "range": "full" },
  "derived_from": {
    "manifest": "colorbar_ycbcr444_8bit_planar_bt709_limited.manifest.json",
    "tool": "RawInspector",
    "written_at": "2026-08-17T18:44:32+00:00",
    "changed": { "matrix": { "from": "bt709", "to": "bt601" } },
    "dropped_files": ["colorbar_....preview.png"],
    "note": "RAWのバイト列は元のものと同じです。…"
  }
}
```

守ること。

- 差し替えてよいのは `parameters.matrix` と `parameters.range` だけとする。
  成分の選択や表示の段は**書かない**。manifest はデータの条件を書くところであって、
  画面の見せ方を書くところではない。
- `files` は `kind: raw` だけを残す。プレビュー画像は**差し替える前の条件で描かれた絵**なので、
  新しい条件の manifest に付いていると「この条件で見るとこうなる」と読まれる。外したものは
  `dropped_files` に名前を残す。
- `parameters_sha256` は書き換えたあとの条件で計算し直す。ただし計算し直した値が
  生成器の数え方（`json.dumps(params, sort_keys=True, ensure_ascii=False)` の SHA-256）と
  一致することを、**書き換える前の値で確かめてから**にする。確かめられないときは項目ごと外す。
  合っていないハッシュを残すのは、無いより悪い。
- `generator` はそのままにする。RAWを作ったのはその生成器で、そこは変わっていない。
  manifest を書いたのが誰かは `derived_from.tool` に出る。
- 元の manifest と**同じディレクトリ**へ置く。`files[].path` はそこからの相対と決まっているので、
  別の場所へ置くとRAWを指せなくなる。

## ビューアの対応段階

| 段階 | 表示対象 | 状態 |
| --- | --- | --- |
| 1 | RGB 8bit planar / packed | プレビュー対応 |
| 2 | YCbCr 8bit 4:4:4 planar / packed | プレビュー対応 |
| 3 | YCbCr 8bit 4:2:2 packed（UYVY）/ NV12 | プレビュー対応 |
| 4 | RGB / YCbCr 10bit planar、P010 | プレビュー対応 |
| 5 | v210 / MIPI10 | プレビュー対応 |
| 6 | YCbCr planar 4:2:2 / 4:2:0（I422 / I420）8bit・10bit | プレビュー対応 |

新しい形式をPython側へ追加する場合は、生成、manifest記録、RAWサイズ検査、C#ビューアの対応可否表示を同じ変更単位で更新します。

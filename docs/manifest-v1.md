# manifest v1 共通仕様

Python生成器とRawManifestViewerは、このファイルをRAWデータの共通契約として扱います。

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

## ビューアの対応段階

| 段階 | 表示対象 | 状態 |
| --- | --- | --- |
| 1 | RGB 8bit planar / packed | プレビュー対応 |
| 2 | YCbCr 8bit 4:4:4 planar / packed | プレビュー対応 |
| 3 | YCbCr 8bit 4:2:2 packed（UYVY）/ NV12 | プレビュー対応 |
| 4 | RGB / YCbCr 10bit planar、P010 | プレビュー対応 |
| 5 | v210 / MIPI10 | 追加予定 |

新しい形式をPython側へ追加する場合は、生成、manifest記録、RAWサイズ検査、C#ビューアの対応可否表示を同じ変更単位で更新します。

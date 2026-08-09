# RawManifestViewer

`video-test-patterns` が生成したRAWデータを、マニフェストとセットで確認するためのWindows Formsアプリです。

## 現在の対応範囲

- .NET 8 / Windows Forms
- manifest v1（`parameters` / `files`）の読み込み
- `*.manifest.json` のフォルダ内検索
- マニフェストの生成条件・ファイル情報表示
- 8bit RGB 4:4:4 planar / packed、YCbCr 4:4:4・4:2:2 packed・NV12 RAWのプレビュー
- PNG保存

現在のプレビューは8bit RGB 4:4:4 planar / packedと、YCbCr 4:4:4 planar / packed・4:2:2 packed（UYVY）・4:2:0 NV12に対応しています。10bit、P010、v210などの対応外形式もマニフェスト情報は表示し、プレビュー欄には未対応理由を表示します。共通仕様は[`docs/manifest-v1.md`](../../docs/manifest-v1.md)を参照してください。

## ビルドと起動

```powershell
dotnet build .\apps\RawManifestViewer\RawManifestViewer.csproj
dotnet run --project .\apps\RawManifestViewer\RawManifestViewer.csproj
```

「フォルダを開く」で、RAWファイルとマニフェストが置かれたフォルダを指定してください。

## 今後の拡張

- YCbCr/RGB変換を利用したYCbCrプレビュー
- 10bitコンテナ形式の表示
- planar / NV12 / P010 / v210の読み込み
- Python側の生成結果を指定して開くCLIオプション

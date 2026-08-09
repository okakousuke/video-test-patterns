# RawManifestViewer

`video-test-patterns` が生成したRAWデータを、マニフェストとセットで確認するためのWindows Formsアプリです。

## 現在の対応範囲

- .NET 8 / Windows Forms
- manifest v1（`parameters` / `files`）の読み込み
- `*.manifest.json` のフォルダ内検索
- マニフェストの生成条件・ファイル情報表示
- 8bit RGB/BGR packed RAWのプレビュー
- PNG保存

現在のプレビューは8bit RGB packedのみ対応しています。YCbCr、10bit、planar、NV12、P010、v210などの対応外形式もマニフェスト情報は表示し、プレビュー欄には未対応理由を表示します。

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

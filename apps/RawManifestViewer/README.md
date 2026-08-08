# RawManifestViewer

`video-test-patterns` が生成したRAWデータを、マニフェストとセットで確認するためのWindows Formsアプリです。

## 現在の対応範囲

- .NET 8 / Windows Forms
- `*.manifest.json` のフォルダ内検索
- マニフェストのパラメータ表示
- 8bit RGB/BGR packed RAWのプレビュー
- PNG保存

現在のPython生成器が扱うYCbCr、10bit、planar、NV12、P010、v210などは、今後段階的に対応します。対応外のマニフェストも一覧には表示しますが、プレビュー時はエラーとして扱います。

## ビルドと起動

```powershell
dotnet build .\apps\RawManifestViewer\RawManifestViewer.csproj
dotnet run --project .\apps\RawManifestViewer\RawManifestViewer.csproj
```

「フォルダを開く」で、RAWファイルとマニフェストが置かれたフォルダを指定してください。

## 今後の拡張

- Python生成器のmanifest v1形式への対応
- YCbCr/RGB変換を利用したYCbCrプレビュー
- 10bitコンテナ形式の表示
- planar / NV12 / P010 / v210の読み込み
- Python側の生成結果を指定して開くCLIオプション

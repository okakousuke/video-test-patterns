# RawManifestViewer

`video-test-patterns` が生成したRAWデータを、マニフェストとセットで確認するためのWindows Formsアプリです。

## 現在の対応範囲

- .NET 8 / Windows Forms
- manifest v1（`parameters` / `files`）の読み込み
- パターン名ごとのmanifestツリー表示
- RGB / YUV系・画像サイズによる絞り込み
- マニフェストの生成条件・ファイル情報表示
- RAWファイル名で識別できるmanifest一覧
- 25%〜400%の表示倍率変更（アスペクト比維持、全体表示時は中央配置）
- 8bit RGB 4:4:4 planar / packed、YCbCr 4:4:4・4:2:2 packed・NV12 RAWのプレビュー
- プレビュー上の専用ボタンからPNG / JPEG / TIFF / BMP / GIF保存
- 読み込みフォルダを既定の出力先に設定し、出力先ラベルからエクスプローラーを開く
- 前回開いたフォルダを次回起動時に自動で復元
- 保存ボタンにはCtrl+1〜Ctrl+5のショートカットを表示し、キーボード操作でも保存可能
- manifestパラメータは横スクロールで長い値を確認可能。項目名の幅も自動調整
- ボタン、フィルタ、プレビュー操作部にはマウスオーバー説明を表示

現在のプレビューはRGB / YCbCr 4:4:4 planar（8bit・10bit）、YCbCr 4:4:4 packed、4:2:2 packed（UYVY / v210）、4:2:0 NV12 / P010、MIPI10に対応しています。対応外形式もマニフェスト情報は表示し、プレビュー欄には未対応理由を表示します。共通仕様は[`docs/manifest-v1.md`](../../docs/manifest-v1.md)を参照してください。

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

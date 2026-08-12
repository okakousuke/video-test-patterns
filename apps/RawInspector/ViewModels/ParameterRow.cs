using RawInspector.Models;

namespace RawInspector.ViewModels;

/// <summary>
/// manifestの生成条件を1行ぶん表したものです。
/// 値そのものより「その値を誤るとどう壊れるか」を <see cref="Help"/> に書きます。
/// </summary>
public sealed class ParameterRow
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required string Help { get; init; }

    /// <summary>英語名を併記したい項目だけ設定します（表の右端に薄く出します）。</summary>
    public string EnglishName { get; init; } = "";

    public static IReadOnlyList<ParameterRow> Build(ManifestInfo manifest, string manifestPath)
    {
        var raw = manifest.Raw;
        return
        [
            new ParameterRow
            {
                Name = "パターン名",
                Value = manifest.Pattern ?? "（未指定）",
                EnglishName = "pattern",
                Help = "生成したテストパターンの名前です。何を確認するための絵かがここで決まります。",
            },
            new ParameterRow
            {
                Name = "画像サイズ",
                Value = $"{manifest.Width} × {manifest.Height} px (W × H)",
                EnglishName = "width × height",
                Help = "RAWには画像サイズの情報が含まれません。幅または高さを誤ると行境界がずれ、復元結果全体が斜めに崩れます。",
            },
            new ParameterRow
            {
                Name = "色モデル",
                Value = manifest.ColorModel ?? "（未指定）",
                EnglishName = "color_model",
                Help = "画素値の色表現です。RGBは3原色、Y'CbCrは輝度Y'と色差Cb/Crで表します。どちらかでサンプルの解釈とRGB化の処理が変わります。",
            },
            new ParameterRow
            {
                Name = "色差サブサンプリング",
                Value = manifest.Subsampling ?? "（未指定）",
                EnglishName = "subsampling",
                Help = "4:4:4は各画素に色差を持ちます。4:2:2は水平方向を半分、4:2:0は水平・垂直とも半分に間引くため、色差面のサイズと読み出し位置が変わります。",
            },
            new ParameterRow
            {
                Name = "ビット深度",
                Value = $"{manifest.BitDepth} bit（最大コード値 {(1 << manifest.BitDepth) - 1}）",
                EnglishName = "bit_depth",
                Help = "1サンプルに割り当てる有効ビット数です。10bitは階調が細かい一方、16bitコンテナや専用パック形式のどちらで格納するかを別に決める必要があります。",
            },
            new ParameterRow
            {
                Name = "信号レンジ",
                Value = ValueOrNote(manifest.Range, "この色モデルでは未使用"),
                EnglishName = "range",
                Help = "コード値の使用範囲です。fullは全域、limitedは放送系の制限範囲（8bitで黒16・白235）です。Y'CbCrからRGBへ戻すときは、生成時と同じrangeを使わないと黒浮き・白飛びが出ます。",
            },
            new ParameterRow
            {
                Name = "色変換マトリクス",
                Value = ValueOrNote(manifest.Matrix, "この色モデルでは未使用"),
                EnglishName = "matrix",
                Help = "RGBとY'CbCrの変換係数です。bt601はSD系、bt709はHD系、bt2020はUHD系。異なるmatrixで戻すと、同じRAWでも色相と明るさが変わります。",
            },
            new ParameterRow
            {
                Name = "メモリ格納形式",
                Value = manifest.Storage ?? "（未指定）",
                EnglishName = "storage",
                Help = "RAW内でのサンプルの並び方です。planarは成分ごとに面を分け、packedは画素または画素対ごとに詰めます。NV12/P010はY面の後ろに色差を交互配置する4:2:0形式です。",
            },
            new ParameterRow
            {
                Name = "ビット配置",
                Value = ValueOrNote(manifest.Alignment, "この格納形式では未使用"),
                EnglishName = "alignment",
                Help = "10bitを16bitコンテナへ置くときの有効ビット位置です。lsbは下位10bit、msbは上位10bit。P010は通常msbです。v210とMIPI10はコンテナを使わない別のパック規則なので、この項目は効きません。",
            },
            new ParameterRow
            {
                Name = "チャンネル順序",
                Value = ValueOrNote(manifest.ChannelOrder, "この格納形式では未使用"),
                EnglishName = "channel_order",
                Help = "packed形式で、1画素内の成分がRGB順かBGR順かを示します。ここを誤ると赤と青が入れ替わります。planarでは通常使いません。",
            },
            new ParameterRow
            {
                Name = "RAWファイル",
                Value = raw.Path,
                EnglishName = "files[kind=raw].path",
                Help = "実際に読み込むRAWファイルです。manifestのあるフォルダからの相対パスで記録します。",
            },
            new ParameterRow
            {
                Name = "RAWファイルサイズ",
                Value = FormatBytes(manifest.RawBytes),
                EnglishName = "raw_bytes",
                Help = "RAWの総バイト数です。画像サイズ・サブサンプリング・ビット深度・格納形式から期待値を計算できるため、欠損や形式指定の取り違えをここで検出できます。",
            },
            new ParameterRow
            {
                Name = "往復確認",
                Value = manifest.RoundtripVerified switch
                {
                    true => "OK（pack → unpack が一致）",
                    false => "NG（一致しませんでした）",
                    null => "（記録なし）",
                },
                EnglishName = "roundtrip_verified",
                Help = "生成時に、詰めたバイト列を読み戻して元のプレーンと一致するか確認した結果です。ビット詰めの誤りはPNGプレビューでは気付けないため、この記録が要ります。",
            },
            new ParameterRow
            {
                Name = "RAW SHA-256",
                Value = raw.Sha256 ?? "（未指定）",
                EnglishName = "files[].sha256",
                Help = "RAWの内容から計算したハッシュ値です。生成後にファイルが意図せず変わっていないかを確認できます。",
            },
            new ParameterRow
            {
                Name = "生成日時",
                Value = manifest.GeneratedAt ?? "（未指定）",
                EnglishName = "generated_at",
                Help = "生成した時刻です。同じ条件で作り直したファイルを見分けるときに使います。",
            },
            new ParameterRow
            {
                Name = "生成器",
                Value = manifest.Generator is { } g ? $"{g.Name} {g.Version}" : "（未指定）",
                EnglishName = "generator",
                Help = "RAWを作ったツールとそのバージョンです。生成器を更新した前後で結果が変わったかを追えます。",
            },
            new ParameterRow
            {
                Name = "manifestパス",
                Value = manifestPath,
                EnglishName = "-",
                Help = "読み込んでいるmanifestファイルの場所です。",
            },
        ];
    }

    private static string ValueOrNote(string? value, string note) =>
        string.IsNullOrWhiteSpace(value) ? $"（{note}）" : value;

    private static string FormatBytes(long bytes)
    {
        const double kilobyte = 1024d;
        const double megabyte = kilobyte * 1024d;
        return bytes >= megabyte
            ? $"{bytes:N0} bytes ({bytes / megabyte:N2} MB)"
            : bytes >= kilobyte
                ? $"{bytes:N0} bytes ({bytes / kilobyte:N2} KB)"
                : $"{bytes:N0} bytes";
    }
}

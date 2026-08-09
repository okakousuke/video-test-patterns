using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;

namespace RawManifestViewer;

public sealed class ManifestInfo
{
    [JsonPropertyName("manifest_version")] public int ManifestVersion { get; set; }
    [JsonPropertyName("generated_at")] public string? GeneratedAt { get; set; }
    [JsonPropertyName("parameters")] public ManifestParameters Parameters { get; set; } = new();
    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = [];
    [JsonPropertyName("raw_bytes")] public long RawBytes { get; set; }
    [JsonPropertyName("roundtrip_verified")] public bool? RoundtripVerified { get; set; }

    [JsonIgnore] public string? Id => Parameters.Pattern;
    [JsonIgnore] public int Width => Parameters.Width;
    [JsonIgnore] public int Height => Parameters.Height;
    [JsonIgnore] public string? ColorModel => Parameters.ColorModel;
    [JsonIgnore] public string? ChannelOrder => Parameters.ChannelOrder;
    [JsonIgnore] public int BitDepth => Parameters.BitDepth;
    [JsonIgnore] public string? Storage => Parameters.Storage;
    [JsonIgnore] public string? Subsampling => Parameters.Subsampling;
    [JsonIgnore] public string? Range => Parameters.Range;
    [JsonIgnore] public string? Matrix => Parameters.Matrix;
    [JsonIgnore] public string? Alignment => Parameters.Alignment;

    public static ManifestInfo Load(string path)
    {
        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<ManifestInfo>(json)
            ?? throw new InvalidDataException("manifest is empty.");
        manifest.Validate();
        return manifest;
    }

    public ManifestFile Raw => Files.First(f => string.Equals(f.Kind, "raw", StringComparison.OrdinalIgnoreCase));

    public string ResolveRawPath(string manifestPath)
    {
        var relative = Raw.Path.Replace('/', Path.DirectorySeparatorChar);
        var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
        var candidate = Path.GetFullPath(Path.Combine(manifestDirectory, relative));
        if (File.Exists(candidate)) return candidate;

        // Also accept project-root-relative paths such as generated/foo.raw.
        var rootCandidate = Path.GetFullPath(Path.Combine(manifestDirectory, "..", relative));
        return File.Exists(rootCandidate) ? rootCandidate : candidate;
    }

    public bool SupportsPreview
    {
        get
        {
            if (string.Equals(ColorModel, "rgb", StringComparison.OrdinalIgnoreCase))
                return string.Equals(Subsampling, "4:4:4", StringComparison.OrdinalIgnoreCase)
                    && ((BitDepth == 8 && string.Equals(Storage, "packed", StringComparison.OrdinalIgnoreCase))
                        || ((BitDepth == 8 || BitDepth == 10) && string.Equals(Storage, "planar", StringComparison.OrdinalIgnoreCase))
                        || (BitDepth == 10 && string.Equals(Storage, "mipi10", StringComparison.OrdinalIgnoreCase)));

            if (!string.Equals(ColorModel, "ycbcr", StringComparison.OrdinalIgnoreCase)) return false;
            return (string.Equals(Subsampling, "4:4:4", StringComparison.OrdinalIgnoreCase)
                    && ((BitDepth == 8 && string.Equals(Storage, "packed", StringComparison.OrdinalIgnoreCase))
                        || ((BitDepth == 8 || BitDepth == 10) && string.Equals(Storage, "planar", StringComparison.OrdinalIgnoreCase))))
                || (string.Equals(Subsampling, "4:2:2", StringComparison.OrdinalIgnoreCase)
                    && BitDepth == 8 && string.Equals(Storage, "packed", StringComparison.OrdinalIgnoreCase))
                || (string.Equals(Subsampling, "4:2:0", StringComparison.OrdinalIgnoreCase)
                    && ((BitDepth == 8 && string.Equals(Storage, "nv12", StringComparison.OrdinalIgnoreCase))
                        || (BitDepth == 10 && string.Equals(Storage, "p010", StringComparison.OrdinalIgnoreCase))))
                || (BitDepth == 10 && string.Equals(Storage, "v210", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Subsampling, "4:2:2", StringComparison.OrdinalIgnoreCase))
                || (BitDepth == 10 && string.Equals(Storage, "mipi10", StringComparison.OrdinalIgnoreCase));
            
        }
    }

    private void Validate()
    {
        if (ManifestVersion != 1)
            throw new NotSupportedException($"manifest_version={ManifestVersion} is not supported (only 1 is supported).");
        if (Width <= 0 || Height <= 0)
            throw new InvalidDataException($"Invalid image size: {Width}x{Height}");
        if (Files.All(f => !string.Equals(f.Kind, "raw", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("manifest has no kind=raw file.");
    }
}

public sealed class ManifestParameters
{
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("color_model")] public string? ColorModel { get; set; }
    [JsonPropertyName("channel_order")] public string? ChannelOrder { get; set; }
    [JsonPropertyName("subsampling")] public string? Subsampling { get; set; }
    [JsonPropertyName("bit_depth")] public int BitDepth { get; set; }
    [JsonPropertyName("range")] public string? Range { get; set; }
    [JsonPropertyName("matrix")] public string? Matrix { get; set; }
    [JsonPropertyName("storage")] public string? Storage { get; set; }
    [JsonPropertyName("alignment")] public string? Alignment { get; set; }
}

public sealed class ManifestFile
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
}

public sealed class ManifestDisplay
{
    [Browsable(false)]
    [DisplayName("パターン名")]
    [Description("生成したテストパターンの名前です。manifestのparameters.patternに対応します。")]
    public string Id { get; init; } = "";
    [DisplayName("RAWファイル")]
    [Description("実際に読み込むRAWファイルのパスです。manifestのfilesからkind=rawを選択します。")]
    public string RawFile { get; init; } = "";
    [DisplayName("画像サイズ")]
    [Description("RAW画像の幅Wと高さHです。RAWにはヘッダーがないため、この値を誤ると行境界がずれて復元結果全体が崩れます。")]
    public string Size { get; init; } = "";
    [DisplayName("色モデル")]
    [Description("画素値の色表現です。RGBは3原色、Y'CbCrは輝度Y'と色差Cb/Crで表します。色モデルにより、各サンプルの解釈とRGB化の処理が変わります。")]
    public string ColorModel { get; init; } = "";
    [DisplayName("チャンネル順序 (Channel Order)")]
    [Description("RGB各チャンネルの並び順です。packed形式で使用します。")]
    [Browsable(false)]
    public string ChannelOrder { get; init; } = "";
    [DisplayName("色差サブサンプリング")]
    [Description("輝度Y'に対する色差Cb/Crのサンプル密度です。4:4:4は各画素、4:2:2は水平方向を半分、4:2:0は水平・垂直方向を半分にします。")]
    public string Subsampling { get; init; } = "";
    [DisplayName("ビット深度")]
    [Description("1サンプルあたりの有効ビット数です。10bitは8bitより細かい階調を表せますが、16bitコンテナや専用パック形式での格納方法も確認します。")]
    public string BitDepth { get; init; } = "";
    [DisplayName("信号レンジ")]
    [Description("量子化値の有効範囲です。fullは全域、limitedは放送系で一般的な制限範囲です。Y'CbCrをRGBへ戻す際はmatrixと合わせて一致させます。")]
    public string Range { get; init; } = "";
    [DisplayName("色変換マトリクス")]
    [Description("RGBとY'CbCrの相互変換に使う係数です。bt601はSD系、bt709はHD系、bt2020はUHD系の代表例です。指定が違うと色相や明るさが変わります。")]
    public string Matrix { get; init; } = "";
    [DisplayName("メモリ格納形式 (Storage)")]
    [Description("RAW内でのサンプルの並び方です。planarは成分ごとに面を分け、packedは画素または画素対ごとに詰めます。NV12/P010はY面の後に色差面を置く4:2:0形式です。")]
    [Browsable(false)]
    public string Storage { get; init; } = "";
    [DisplayName("ビット配置 (Alignment)")]
    [Description("10bitを16bitコンテナに置く場合の有効ビット位置です。lsbは下位10bit、msbは上位10bitを使います。P010は通常msb配置です。")]
    [Browsable(false)]
    public string Alignment { get; init; } = "";
    [DisplayName("RAWファイルサイズ")]
    [Description("RAWの総バイト数です。画像サイズ・色差サブサンプリング・ビット深度・格納形式から期待値を見積もり、欠損や形式指定の不一致を検出できます。")]
    public string RawBytes { get; init; } = "";
    [DisplayName("RAW SHA-256")]
    [Description("RAWファイル内容のハッシュ値です。ファイル同一性の確認に使います。")]
    public string Sha256 { get; init; } = "";

    [DisplayName("チャンネル順序")]
    [Description("英語名: Channel Order。packed形式でRGBまたはBGRなど、各画素内の成分の並びを示します。planar形式では通常は使いません。")]
    public string ChannelOrderDisplay { get; init; } = "";

    [DisplayName("ビット配置")]
    [Description("英語名: Alignment。10bitを16bitコンテナに置く場合の有効ビット位置です。lsbは下位10bit、msbは上位10bitを使います。")]
    public string AlignmentDisplay { get; init; } = "";

    [DisplayName("メモリ格納形式")]
    [Description("英語名: Storage。RAW内でのサンプルの並び方です。planar、packed、NV12、P010、v210、mipi10などで読み出し位置が変わります。")]
    public string StorageDisplay { get; init; } = "";
}

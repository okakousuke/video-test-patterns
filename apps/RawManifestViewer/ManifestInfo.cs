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

    public bool SupportsPreview =>
        BitDepth == 8 && string.Equals(Subsampling, "4:4:4", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(ColorModel, "rgb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ColorModel, "ycbcr", StringComparison.OrdinalIgnoreCase))
        && (string.Equals(Storage, "packed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Storage, "planar", StringComparison.OrdinalIgnoreCase));

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
    [DisplayName("パターン名")]
    [Description("生成したテストパターンの名前です。manifestのparameters.patternに対応します。")]
    public string Id { get; init; } = "";
    [DisplayName("RAWファイル")]
    [Description("実際に読み込むRAWファイルのパスです。manifestのfilesからkind=rawを選択します。")]
    public string RawFile { get; init; } = "";
    [DisplayName("画像サイズ")]
    [Description("RAW画像の幅と高さです。")]
    public string Size { get; init; } = "";
    [DisplayName("色モデル")]
    [Description("画素値の色表現です。現在はRGB 8bitのプレビューに対応しています。")]
    public string ColorModel { get; init; } = "";
    [DisplayName("チャンネル順")]
    [Description("RGB各チャンネルの並び順です。packed形式で使用します。")]
    public string ChannelOrder { get; init; } = "";
    [DisplayName("色差サブサンプリング")]
    [Description("輝度と色差のサンプル数の比率です。4:4:4、4:2:2、4:2:0などで表します。")]
    public string Subsampling { get; init; } = "";
    [DisplayName("ビット深度")]
    [Description("1サンプルあたりの有効ビット数です。8bitや10bitなどで表します。")]
    public string BitDepth { get; init; } = "";
    [DisplayName("レンジ")]
    [Description("画素値の使用範囲です。fullは全域、limitedは映像信号向けの制限範囲です。")]
    public string Range { get; init; } = "";
    [DisplayName("マトリクス")]
    [Description("RGBとY'CbCrの相互変換に使う係数の規格名です。")]
    public string Matrix { get; init; } = "";
    [DisplayName("格納形式")]
    [Description("RAWファイル内でのサンプルの並び方です。planar、packed、NV12などがあります。")]
    public string Storage { get; init; } = "";
    [DisplayName("アライメント")]
    [Description("10bitなどをコンテナへ格納するときのビット詰め方向です。")]
    public string Alignment { get; init; } = "";
    [DisplayName("RAWバイト数")]
    [Description("生成されたRAWファイルのサイズです。")]
    public string RawBytes { get; init; } = "";
    [DisplayName("RAW SHA-256")]
    [Description("RAWファイル内容のハッシュ値です。ファイル同一性の確認に使います。")]
    public string Sha256 { get; init; } = "";
}

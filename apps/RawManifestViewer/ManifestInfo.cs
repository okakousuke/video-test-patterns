using System.Text.Json;
using System.Text.Json.Serialization;

namespace RawManifestViewer;

public sealed class ManifestInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("raw_file")]
    public string? RawFile { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("color_model")]
    public string? ColorModel { get; set; }

    [JsonPropertyName("channel_order")]
    public string? ChannelOrder { get; set; }

    [JsonPropertyName("bit_depth")]
    public int BitDepth { get; set; }

    [JsonPropertyName("storage")]
    public string? Storage { get; set; }

    [JsonPropertyName("stride_bytes")]
    public int? StrideBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    public static ManifestInfo Load(string path)
    {
        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<ManifestInfo>(json)
            ?? throw new InvalidDataException("manifestが空です。");

        manifest.Validate(path);
        return manifest;
    }

    public string ResolveRawPath(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(RawFile))
            throw new InvalidDataException("manifestにraw_fileがありません。");

        return Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(manifestPath)!, RawFile));
    }

    public int EffectiveStrideBytes => StrideBytes ?? checked(Width * 3);

    private void Validate(string manifestPath)
    {
        if (Width <= 0 || Height <= 0)
            throw new InvalidDataException($"画像サイズが不正です: {Width}x{Height}");
        if (BitDepth != 8)
            throw new NotSupportedException("最小版はbit_depth=8だけに対応しています。");
        if (!string.Equals(ColorModel, "rgb", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("最小版はcolor_model=rgbだけに対応しています。");
        if (!string.Equals(Storage, "packed", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("最小版はstorage=packedだけに対応しています。");
        if (!string.Equals(ChannelOrder, "RGB", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ChannelOrder, "BGR", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("channel_orderはRGBまたはBGRを指定してください。");
        if (EffectiveStrideBytes < Width * 3)
            throw new InvalidDataException("stride_bytesが1行の必要バイト数より小さいです。");
        _ = ResolveRawPath(manifestPath);
    }
}

public sealed class ManifestDisplay
{
    public string Id { get; init; } = "";
    public string RawFile { get; init; } = "";
    public string Size { get; init; } = "";
    public string ColorModel { get; init; } = "";
    public string ChannelOrder { get; init; } = "";
    public string BitDepth { get; init; } = "";
    public string Storage { get; init; } = "";
    public string StrideBytes { get; init; } = "";
    public string Sha256 { get; init; } = "";
}

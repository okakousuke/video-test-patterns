using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RawInspector.Models;

/// <summary>
/// manifest v1 の読み込みと検証を行います。仕様は docs/manifest-v1.md を正とします。
/// </summary>
public sealed class ManifestInfo
{
    [JsonPropertyName("manifest_version")] public int ManifestVersion { get; set; }
    [JsonPropertyName("generated_at")] public string? GeneratedAt { get; set; }
    [JsonPropertyName("generator")] public ManifestGenerator? Generator { get; set; }
    [JsonPropertyName("parameters")] public ManifestParameters Parameters { get; set; } = new();
    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = [];
    [JsonPropertyName("raw_bytes")] public long RawBytes { get; set; }
    [JsonPropertyName("roundtrip_verified")] public bool? RoundtripVerified { get; set; }

    [JsonIgnore] public string? Pattern => Parameters.Pattern;
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

    [JsonIgnore] public bool IsYcbcr => Same(ColorModel, "ycbcr");

    public ManifestFile Raw => Files.First(f => Same(f.Kind, "raw"));

    public static ManifestInfo Load(string path)
    {
        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<ManifestInfo>(json)
            ?? throw new InvalidDataException("manifestが空です。");
        manifest.Validate();
        return manifest;
    }

    public string ResolveRawPath(string manifestPath)
    {
        var relative = Raw.Path.Replace('/', Path.DirectorySeparatorChar);
        var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
        var candidate = Path.GetFullPath(Path.Combine(manifestDirectory, relative));
        if (File.Exists(candidate)) return candidate;

        // generated/foo.raw のようにプロジェクト直下起点で書かれた場合も受け付けます。
        var rootCandidate = Path.GetFullPath(Path.Combine(manifestDirectory, "..", relative));
        return File.Exists(rootCandidate) ? rootCandidate : candidate;
    }

    /// <summary>
    /// プレビュー可能な組み合わせかを返します。対応段階は docs/manifest-v1.md の表に対応します。
    /// </summary>
    public bool SupportsPreview
    {
        get
        {
            if (Same(ColorModel, "rgb"))
                return Same(Subsampling, "4:4:4")
                    && ((BitDepth == 8 && Same(Storage, "packed"))
                        || ((BitDepth == 8 || BitDepth == 10) && Same(Storage, "planar"))
                        || (BitDepth == 10 && Same(Storage, "mipi10")));

            if (!IsYcbcr) return false;

            // planar は I444 / I422 / I420 のいずれも読めます。
            if (Same(Storage, "planar")) return BitDepth is 8 or 10;

            return (Same(Subsampling, "4:4:4") && BitDepth == 8 && Same(Storage, "packed"))
                || (Same(Subsampling, "4:2:2") && BitDepth == 8 && Same(Storage, "packed"))
                || (Same(Subsampling, "4:2:0")
                    && ((BitDepth == 8 && Same(Storage, "nv12")) || (BitDepth == 10 && Same(Storage, "p010"))))
                || (BitDepth == 10 && Same(Storage, "v210") && Same(Subsampling, "4:2:2"))
                || (BitDepth == 10 && Same(Storage, "mipi10"));
        }
    }

    /// <summary>
    /// プレビューできない理由を、読者が次に何を見ればよいか分かる形で返します。
    /// </summary>
    public string UnsupportedReason =>
        $"この組み合わせはまだプレビューに対応していません（色モデル {ColorModel} / 色差サブサンプリング {Subsampling} / {BitDepth}bit / 格納形式 {Storage}）。"
        + "manifestに記録された生成条件は左の一覧で確認できます。";

    private void Validate()
    {
        if (ManifestVersion != 1)
            throw new NotSupportedException($"manifest_version={ManifestVersion} には対応していません（対応は1のみです）。");
        if (Width <= 0 || Height <= 0)
            throw new InvalidDataException($"画像サイズが不正です: {Width}x{Height}");
        if (Files.All(f => !Same(f.Kind, "raw")))
            throw new InvalidDataException("manifestに kind=raw のファイルがありません。");
    }

    /// <summary>manifestの値は大文字小文字が揺れうるため、比較は常にここを通します。</summary>
    internal static bool Same(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}

public sealed class ManifestGenerator
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
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

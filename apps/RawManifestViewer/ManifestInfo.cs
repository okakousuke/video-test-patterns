using System.Text.Json;
using System.Text.Json.Serialization;

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

    public bool SupportsRgb8Preview =>
        BitDepth == 8 && string.Equals(ColorModel, "rgb", StringComparison.OrdinalIgnoreCase)
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
    public string Id { get; init; } = "";
    public string RawFile { get; init; } = "";
    public string Size { get; init; } = "";
    public string ColorModel { get; init; } = "";
    public string ChannelOrder { get; init; } = "";
    public string Subsampling { get; init; } = "";
    public string BitDepth { get; init; } = "";
    public string Range { get; init; } = "";
    public string Matrix { get; init; } = "";
    public string Storage { get; init; } = "";
    public string Alignment { get; init; } = "";
    public string RawBytes { get; init; } = "";
    public string Sha256 { get; init; } = "";
}

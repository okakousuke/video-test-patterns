using RawInspector.Models;

namespace RawInspector.Decoding;

/// <summary>
/// Y'CbCrをRGBへ戻すときの解釈です。manifestの値を初期値としますが、
/// 別の解釈で読み直せるように独立した型にしてあります
/// （「間違ったmatrixで見るとどうなるか」を出せるようにするため）。
/// </summary>
public readonly record struct ColorInterpretation(string Matrix, string Range)
{
    public static ColorInterpretation FromManifest(ManifestInfo manifest) =>
        new(NormalizeMatrix(manifest.Matrix), NormalizeRange(manifest.Range));

    public bool IsLimited => ManifestInfo.Same(Range, "limited");

    /// <summary>BT.601 / BT.709 / BT.2020 の輝度係数 Kr・Kb です。</summary>
    public (double Kr, double Kb) Coefficients => Matrix.ToLowerInvariant() switch
    {
        "bt601" => (0.299, 0.114),
        "bt2020" => (0.2627, 0.0593),
        _ => (0.2126, 0.0722), // 未指定はbt709として扱います。
    };

    private static string NormalizeMatrix(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "bt709" : value.ToLowerInvariant();

    private static string NormalizeRange(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "full" : value.ToLowerInvariant();
}

namespace RawInspector.Decoding;

/// <summary>
/// 1画素ぶんの読み取り結果です。RAWから取り出した生のコード値と、
/// それを表示用RGB8へ変換した結果の両方を持ちます。
/// 「絵は正しいのに数値が違う」「数値は合っているのに絵が違う」を切り分けるため、
/// 片方だけを返さずに必ず対で扱います。
/// </summary>
public readonly record struct PixelSample(
    int X,
    int Y,
    string FirstLabel,
    string SecondLabel,
    string ThirdLabel,
    int First,
    int Second,
    int Third,
    int MaxCode,
    byte R,
    byte G,
    byte B,
    bool ChromaInterpolated = false)
{
    /// <summary>
    /// 色差が補間値かどうかの注記です。バイリニアで表示しているときは、
    /// 表示に使った色差はRAWに格納された値そのものではありません。
    /// </summary>
    public string ChromaNote => ChromaInterpolated ? "（色差は補間値）" : "";

    /// <summary>`#RRGGBB`。表示RGB8の値です。</summary>
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>`R, G, B`。表計算やコードへそのまま貼れる形にします。</summary>
    public string RgbText => $"{R}, {G}, {B}";

    /// <summary>RAWから読んだ生のコード値です。Y'CbCrならY'/Cb/Cr、RGBならR/G/B。</summary>
    public string CodeText => $"{FirstLabel}={First}, {SecondLabel}={Second}, {ThirdLabel}={Third}";

    /// <summary>`x, y`。</summary>
    public string PositionText => $"{X}, {Y}";

    /// <summary>座標・生コード値・表示RGBを1行にまとめたものです。</summary>
    public string FullText =>
        $"({X}, {Y})  {FirstLabel}={First} {SecondLabel}={Second} {ThirdLabel}={Third}"
        + $"  (最大 {MaxCode})  →  {Hex}  rgb({RgbText})";
}

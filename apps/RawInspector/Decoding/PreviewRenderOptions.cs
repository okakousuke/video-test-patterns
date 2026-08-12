namespace RawInspector.Decoding;

/// <summary>どの成分を出すかです。</summary>
public enum ChannelView
{
    /// <summary>3成分をまとめてRGBへ変換します。</summary>
    All,

    /// <summary>第1成分だけをグレースケールで出します（Y'CbCrならY'、RGBならR）。</summary>
    First,

    /// <summary>第2成分だけ（Cb または G）。</summary>
    Second,

    /// <summary>第3成分だけ（Cr または B）。</summary>
    Third,
}

/// <summary>
/// 間引かれた色差を輝度と同じ密度へ戻すときのやり方です。
///
/// 生成側（src/vtp/subsample.py）は色差を**平均**で間引き、**最近傍複製**で戻しています。
/// 最近傍はその戻し方をそのまま再現するもので、「色差が間引かれた事実」が四角いブロックとして見えます。
/// バイリニアは平均の逆にあたる位置合わせで補間するため、境界がなだらかになります。
///
/// どちらが正しいという話ではなく、**同じRAWでも戻し方で見え方が変わる**ことを示すための切り替えです。
/// </summary>
public enum ChromaUpsample
{
    /// <summary>格納されている色差サンプルをそのまま複製します（生成側と同じ）。</summary>
    Nearest,

    /// <summary>隣り合う色差サンプルの間を線形に補間します。</summary>
    Bilinear,
}

/// <summary>
/// プレビューの作り方一式です。manifestの値を初期値としますが、
/// あとから別の条件で読み直せるように独立させています
/// （「間違ったmatrixで見るとどうなるか」を出せるようにするため）。
/// </summary>
public readonly record struct PreviewRenderOptions(
    ColorInterpretation Interpretation,
    ChannelView Channel,
    ChromaUpsample Upsample)
{
    public static PreviewRenderOptions Default(ColorInterpretation interpretation) =>
        new(interpretation, ChannelView.All, ChromaUpsample.Nearest);
}

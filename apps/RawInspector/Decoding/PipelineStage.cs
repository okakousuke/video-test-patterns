namespace RawInspector.Decoding;

/// <summary>
/// 変換のどの段で止めた値を画面に出すかです。
///
/// 画面の右下には、コード値からRGBまでの手順を文章で出しています。ただし文章は
/// 「そう書いてある」だけで、<b>どの段でどう変わったのかは絵になっていません</b>。
/// 色がずれているとき、それが色差を戻した段の話なのか、正規化の段の話なのか、
/// matrix の段の話なのかは、途中を出さないかぎり切り分けられません。
///
/// そこで各段の値をそのまま絵にします。段を選ぶと、そこで止めた値が画面に出ます。
/// </summary>
public enum PipelineStage
{
    /// <summary>格納形式から取り出したコード値。色差はまだ間引かれたままです。</summary>
    Codes,

    /// <summary>間引かれた色差を輝度と同じ密度へ戻した直後。</summary>
    Chroma,

    /// <summary>range に従って正規化した直後（Y は 0-1、Cb・Cr は ±0.5）。</summary>
    Normalized,

    /// <summary>matrix でRGBへ戻した直後。まだ 0-1 に丸めていません。</summary>
    Rgb,

    /// <summary>0-1 に丸めて 8bit へ量子化したもの。これが既定の表示です。</summary>
    Display,
}

/// <summary>段ひとつぶんの説明です。表示名と、その段の値をどう絵にしたかを持ちます。</summary>
public readonly record struct PipelineStageOption(PipelineStage Stage, string Label, string Mapping)
{
    /// <summary>ComboBox はこれを見て文字を出します。</summary>
    public override string ToString() => Label;
}

/// <summary>
/// 段の一覧です。<b>選んだRAWによって、存在する段が違います。</b>
/// 4:4:4 に「色差を戻す段」はありませんし、RGB には正規化と matrix の段がありません
/// （コード値をそのまま 0-1 に写して量子化するだけです）。
/// 無い段を選べるようにすると、切り替えても絵が変わらない段が並ぶことになります。
/// </summary>
public static class PipelineStages
{
    private const string CodeMapping =
        "この段の値を絵にする方法: 成分を1つだけ選んでいるときは、その面のコード値をそのまま濃淡にします"
        + "（明るさ = コード値 / 最大コード値）。2つ以上のときは、3つのコード値を R・G・B の位置へ置いています。"
        + "色としての意味はありません。面ごとの値の並びと、色差が間引かれている範囲を見るためのものです。";

    public static PipelineStageOption Option(PipelineStage stage) => stage switch
    {
        PipelineStage.Codes => new(stage, "1. コード値（格納されたまま）", CodeMapping),
        PipelineStage.Chroma => new(stage, "2. 色差を戻したあと",
            CodeMapping
            + "／最近傍のままなら1段目と同じ絵です。色差の値を複製しているだけで、数が変わらないためです。"
            + "差が出るのはバイリニアのときだけで、そこで初めて「戻し方」が絵に効きます。"),
        PipelineStage.Normalized => new(stage, "3. 正規化したあと",
            "この段の値を絵にする方法: Y をそのまま濃淡、Cb・Cr は ±0.5 の振れなので +0.5 してから濃淡にし、"
            + "Y→R、Cb→G、Cr→B の位置へ置いています。色としての意味はありません。"
            + "range を取り違えていると、この段で 0-1 の外へ出ます（「範囲外」で色が付きます）。"),
        PipelineStage.Rgb => new(stage, "4. RGBへ戻した直後（丸める前）",
            "この段の値を絵にする方法: 出すときには結局 0-1 へ丸めるので、"
            + "絵そのものは5段目と同じです。違いは「範囲外」を出したときに見えます。"
            + "matrix や range を取り違えると、ここで 0-1 の外へ出た値が丸められて潰れます。"
            + "潰れた場所を知るための段です。"),
        _ => new(PipelineStage.Display, "5. 8bitへ量子化（既定）",
            "この段の値を絵にする方法: 0-1 に丸めてから 255 倍して四捨五入した、そのままの値です。"),
    };

    /// <summary>そのRAWに存在する段だけを返します。</summary>
    public static IReadOnlyList<PipelineStageOption> For(bool isYcbcr, bool hasSubsampledChroma)
    {
        if (!isYcbcr) return [Option(PipelineStage.Display)];

        var stages = new List<PipelineStageOption> { Option(PipelineStage.Codes) };
        if (hasSubsampledChroma) stages.Add(Option(PipelineStage.Chroma));
        stages.Add(Option(PipelineStage.Normalized));
        stages.Add(Option(PipelineStage.Rgb));
        stages.Add(Option(PipelineStage.Display));
        return stages;
    }

    /// <summary>保存名へ足す短い名前です。既定の段では何も足しません。</summary>
    public static string FileToken(PipelineStage stage) => stage switch
    {
        PipelineStage.Codes => "codes",
        PipelineStage.Chroma => "chroma",
        PipelineStage.Normalized => "norm",
        PipelineStage.Rgb => "rgb",
        _ => "",
    };

    /// <summary>
    /// 「範囲外」を出せる段かどうかです。
    /// 1・2段目のコード値は、格納できる範囲の中にしか入りません
    /// （0-255 や 0-1023 を超えるコード値は、そもそも書き込めません）。
    /// 出せないものに色を付ける口を残すと、「範囲外が無い」のか「機能が効いていない」のかが
    /// 区別できなくなります。
    /// </summary>
    public static bool SupportsRangeMarking(PipelineStage stage) =>
        stage is PipelineStage.Normalized or PipelineStage.Rgb or PipelineStage.Display;
}

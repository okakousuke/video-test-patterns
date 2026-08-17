using System.Numerics;

namespace RawInspector.Decoding;

/// <summary>
/// どの成分を残すかです。組み合わせて選べます。
///
/// 「成分を足す」操作はビット単位のORではありません。コード値をORしても意味のある数になりません。
/// ここでやるのは<b>選ばなかった成分を中立値へ置き換えてから、いつもどおり色変換する</b>ことです。
///
/// 中立値は成分によって違い、そこを取り違えると絵が嘘をつきます。
///
/// - RGB は加算なので、選ばなかった成分は 0 です。R と G だけなら黄色寄りの絵になります
/// - 色差（Cb / Cr）の中立は 0 ではなく 128 &lt;&lt; (bit-8) です。
///   0 を入れると「色差が大きく振れている」ことになり、強い色かぶりになります
/// - 輝度（Y'）の中立は、そのrangeで 0.5 にあたるコード値です。
///   0 を入れると真っ黒になり、色差だけを見たいときに何も見えません
/// </summary>
[Flags]
public enum ChannelMask
{
    /// <summary>どれも残しません（すべて中立値になるので、平坦な絵になります）。</summary>
    None = 0,

    /// <summary>第1成分（Y'CbCrならY'、RGBならR）。</summary>
    First = 1,

    /// <summary>第2成分（Cb または G）。</summary>
    Second = 2,

    /// <summary>第3成分（Cr または B）。</summary>
    Third = 4,

    /// <summary>3成分すべて。</summary>
    All = First | Second | Third,
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
    ChannelMask Channels,
    ChromaUpsample Upsample,
    bool RawCodeGray = false,
    PipelineStage Stage = PipelineStage.Display,
    bool MarkOutOfRange = false)
{
    public static PreviewRenderOptions Default(ColorInterpretation interpretation) =>
        new(interpretation, ChannelMask.All, ChromaUpsample.Nearest);

    /// <summary>選んだ成分の数です。</summary>
    public int SelectedCount => BitOperations.PopCount((uint)Channels);

    /// <summary>
    /// コード値をそのまま濃淡にするかどうかです。
    ///
    /// 成分を1つだけ選んだときに限って意味を持ちます。
    /// 色変換を通すと range のぶん伸縮するため、画面の明るさと成分のコード値が一致しなくなります。
    /// 「この面はいくつか」を見たいときは変換を通さないほうが読めるので、別の切り替えにしてあります。
    /// 2つ以上選んでいるときは、どの成分の値を濃淡にするのか決まらないので使いません。
    ///
    /// 途中の段で止めているときも使いません。<b>段そのものが「どの段の値を見るか」を決めている</b>ので、
    /// そこへ「色変換を通さない」という別の指定を重ねると、何を見ているのか言えなくなります。
    /// 1・2段目で成分を1つだけ選んだときの絵は、この指定と同じ（コード値の濃淡）になります。
    /// </summary>
    public bool UseRawCodeGray => RawCodeGray && SelectedCount == 1 && Stage == PipelineStage.Display;
}

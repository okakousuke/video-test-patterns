namespace RawInspector.ViewModels;

/// <summary>プレビュー上でマウスがどう振る舞うかです。</summary>
public enum PreviewTool
{
    /// <summary>通常のカーソルです。ドラッグもホイールも意味を変えません。</summary>
    Arrow,

    /// <summary>ドラッグで表示位置を動かします。ホイールは通常のスクロールです。</summary>
    Hand,

    /// <summary>ホイールだけで拡大縮小します（Ctrlを押さなくてよい）。</summary>
    Zoom,
}

/// <summary>項目名と値の組です。画面では色を分けて出します。</summary>
public sealed record SummaryItem(string Label, string Value);

/// <summary>
/// 保存形式の選択肢です。
///
/// <c>Encoding</c> には、実際に書き出されるファイルの中身を書きます。
/// 「PNGで保存した」だけでは、あとから別のソフトで開き直して
/// 品質・間引き・ビット数を調べる羽目になります。押す前にここで分かるようにします。
///
/// 値は書き出したファイルのヘッダから読んだものです（推測ではありません）。
/// エンコーダの既定が変わったら、ここも実物で確かめ直してください。
/// </summary>
public sealed record ImageFormatOption(string Label, string Extension)
{
    /// <summary>書き出されるファイルの中身です。</summary>
    public required string Encoding { get; init; }

    /// <summary>
    /// 押す前に知らせたい損失です。無いなら空にします。
    /// 毎回同じ注意が全形式に付いていると、本当に効く形式のときも読み飛ばされます。
    /// </summary>
    public string Caution { get; init; } = "";

    public override string ToString() => Label;
}

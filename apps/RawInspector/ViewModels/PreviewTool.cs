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

/// <summary>保存形式の選択肢です。</summary>
public sealed record ImageFormatOption(string Label, string Extension)
{
    public override string ToString() => Label;
}

namespace RawInspector.ViewModels;

/// <summary>
/// 並びのつまみ（`widths=[1, 2, 3, 4, 6, 8]` など）の 1 要素ぶんです。
///
/// 並び全体を1つの欄に打たせると、区切りと括弧の作法を覚える必要があり、
/// 1つ直したいだけでも全部を打ち直すことになります。
/// ここは「数を1つ持つ欄」で、組み立ては <see cref="PatternOptionRow"/> がやります。
/// </summary>
public sealed class PatternOptionPart : ObservableObject
{
    private readonly Action _changed;

    public PatternOptionPart(string label, Action changed)
    {
        Label = label;
        _changed = changed;
    }

    /// <summary>欄の上に出す見出しです。色なら R / G / B、それ以外は通し番号です。</summary>
    public string Label { get; }

    private string _text = "";
    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value ?? "")) _changed(); }
    }

    /// <summary>
    /// 値を入れますが、組み立ては呼びません。
    /// まとめて入れ替えるときに、1つ動かすたびに組み立て直さないためです。
    /// </summary>
    public void SetWithoutNotify(string text) => Set(ref _text, text ?? "", nameof(Text));
}

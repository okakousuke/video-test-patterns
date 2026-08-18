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
    private readonly bool _isInteger;
    private readonly double? _minimum;
    private readonly double? _maximum;

    public PatternOptionPart(string label, Action changed, bool isInteger, double? minimum, double? maximum)
    {
        Label = label;
        _changed = changed;
        _isInteger = isInteger;
        _minimum = minimum;
        _maximum = maximum;
        StepUpCommand = new RelayCommand(() => Bump(1), () => CanBump(1));
        StepDownCommand = new RelayCommand(() => Bump(-1), () => CanBump(-1));
    }

    /// <summary>欄の上に出す見出しです。色なら R / G / B、それ以外は通し番号です。</summary>
    public string Label { get; }
    public RelayCommand StepUpCommand { get; }
    public RelayCommand StepDownCommand { get; }

    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? "")) return;
            StepUpCommand.RaiseCanExecuteChanged();
            StepDownCommand.RaiseCanExecuteChanged();
            _changed();
        }
    }

    /// <summary>
    /// 値を入れますが、組み立ては呼びません。
    /// まとめて入れ替えるときに、1つ動かすたびに組み立て直さないためです。
    /// </summary>
    public void SetWithoutNotify(string text)
    {
        if (!Set(ref _text, text ?? "", nameof(Text))) return;
        StepUpCommand.RaiseCanExecuteChanged();
        StepDownCommand.RaiseCanExecuteChanged();
    }

    /// <summary>その向きへまだ動かせるかどうかです。端では押せなくします。</summary>
    private bool CanBump(int direction)
    {
        if (!double.TryParse(_text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var value))
            return true;

        return direction > 0
            ? _maximum is not double max || value < max
            : _minimum is not double min || value > min;
    }

    private void Bump(int direction)
    {
        if (!double.TryParse(_text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var value))
            value = _minimum ?? 0;
        value += (_isInteger ? 1 : 0.05) * direction;
        if (_minimum is double min) value = Math.Max(min, value);
        if (_maximum is double max) value = Math.Min(max, value);
        Text = _isInteger
            ? ((long)Math.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Math.Round(value, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    }
}

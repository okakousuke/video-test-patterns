using System.Globalization;
using RawInspector.Models;

namespace RawInspector.ViewModels;

/// <summary>
/// 生成画面に出す、パターン固有のつまみ 1 行分です。
///
/// もとは `KEY=VALUE` を1行ずつ打ち込む欄でした。名前も範囲も覚えていないと
/// 書けず、打ち間違えても（当時は）黙って既定値で通っていました。
/// ここは生成器が渡してきた説明どおりの入力欄を出すだけの入れ物です。
///
/// **空欄は「触っていない」という意味です。** 既定値を打ち直す必要はありません。
/// 触っていないつまみはコマンドに載せないので、生成器側の既定が変わったときに
/// 古い値を固定してしまうことがありません。
/// </summary>
public sealed class PatternOptionRow : ObservableObject
{
    private readonly PatternOption _option;
    private readonly Action _changed;

    public PatternOptionRow(PatternOption option, Action changed)
    {
        _option = option;
        _changed = changed;
        _flag = DefaultFlag;
    }

    public string Name => _option.Name;
    public string Label => _option.Label;
    public string Help => _option.Help;
    public string Hint => _option.Hint();

    /// <summary>選択肢から選ぶつまみです。</summary>
    public bool IsChoice => _option.Kind == "choice";

    /// <summary>入り切りのつまみです。</summary>
    public bool IsFlag => _option.Kind == "bool";

    /// <summary>数値、または数値の並びを打ち込むつまみです。</summary>
    public bool IsText => !IsChoice && !IsFlag;

    /// <summary>色（RGB 3 つ）かどうか。入力欄の幅を変えるために使います。</summary>
    public bool IsColor => _option.Kind == "color";

    public IReadOnlyList<string> Choices => _option.Choices;

    /// <summary>選択肢のつまみで「既定のまま」を表す見出しです。</summary>
    public const string KeepDefault = "（既定のまま）";

    public IReadOnlyList<string> ChoiceItems =>
        IsChoice ? new[] { KeepDefault }.Concat(_option.Choices).ToList() : [];

    private bool DefaultFlag =>
        _option.Default.ValueKind == System.Text.Json.JsonValueKind.True;

    // --- 入力された値 ---

    private string _text = "";
    /// <summary>数値・並びの入力。空欄なら触っていないことになります。</summary>
    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value ?? "")) Notify(); }
    }

    private string _choice = KeepDefault;
    public string Choice
    {
        get => _choice;
        set { if (Set(ref _choice, value ?? KeepDefault)) Notify(); }
    }

    private bool _flag;
    public bool Flag
    {
        get => _flag;
        set { if (Set(ref _flag, value)) Notify(); }
    }

    private void Notify()
    {
        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
        Raise(nameof(IsChanged));
        _changed();
    }

    /// <summary>既定から動かしたかどうかです。動かした行だけコマンドに載ります。</summary>
    public bool IsChanged => IsFlag
        ? _flag != DefaultFlag
        : IsChoice
            ? _choice != KeepDefault
            : _text.Trim().Length > 0;

    /// <summary>
    /// 打ち込まれた値の言い分です。問題なければ空になります。
    ///
    /// これは早く気付くための確認で、正解ではありません。**最後に判定するのは生成器です。**
    /// ここを通っても生成器が弾くことはあり、そのときは生成器の言い分をそのまま出します。
    /// </summary>
    public string Problem
    {
        get
        {
            if (!IsText || !IsChanged) return "";
            var values = Parse(_text, out var bad);
            if (bad is not null) return bad;

            if (_option.IsList)
            {
                if (_option.Length is int n && values.Count != n)
                    return $"{n} 個で指定してください（いまは {values.Count} 個）。";
            }
            else if (values.Count != 1)
            {
                return "値は 1 つです。";
            }

            foreach (var v in values)
            {
                if (_option.IsInteger && v != Math.Floor(v)) return "整数で指定してください。";
                if (_option.Minimum is double lo && v < lo) return $"{Show(lo)} 以上にしてください。";
                if (_option.Maximum is double hi && v > hi) return $"{Show(hi)} 以下にしてください。";
            }
            return "";
        }
    }

    public bool HasProblem => Problem.Length > 0;

    /// <summary>
    /// `--pattern-option` へ渡す `名前=値` を返します。触っていなければ null です。
    ///
    /// 値は生成器側が JSON として読むので、並びは角括弧、真偽は小文字で渡します。
    /// 選択肢は JSON として読めないため文字列として届きます（生成器の想定どおりです）。
    /// </summary>
    public string? Argument()
    {
        if (!IsChanged || HasProblem) return null;
        if (IsFlag) return $"{Name}={(_flag ? "true" : "false")}";
        if (IsChoice) return $"{Name}={_choice}";

        var values = Parse(_text, out var bad);
        if (bad is not null) return null;

        var text = string.Join(", ", values.Select(v =>
            _option.IsInteger ? ((long)v).ToString(CultureInfo.InvariantCulture)
                              : v.ToString("0.###########", CultureInfo.InvariantCulture)));

        return _option.IsList ? $"{Name}=[{text}]" : $"{Name}={text}";
    }

    /// <summary>入力を空にして、既定へ戻します。</summary>
    public void Reset()
    {
        Text = "";
        Choice = KeepDefault;
        Flag = DefaultFlag;
    }

    /// <summary>
    /// `1, 2, 3` でも `[1, 2, 3]` でも受け取ります。
    /// 角括弧を必須にすると、1 つの数を打つときにも書かされて煩わしいからです。
    /// </summary>
    private static List<double> Parse(string text, out string? problem)
    {
        problem = null;
        var body = text.Trim().TrimStart('[').TrimEnd(']');
        var parts = body.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var values = new List<double>(parts.Length);
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                problem = $"数値として読めません: {part}";
                return values;
            }
            values.Add(v);
        }

        if (values.Count == 0) problem = "値を入れてください。";
        return values;
    }

    private static string Show(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
}

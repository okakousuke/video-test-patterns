using System.Collections.ObjectModel;
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

        Presets = BuildPresets(option);
        StepUpCommand = new RelayCommand(() => Bump(+1));
        StepDownCommand = new RelayCommand(() => Bump(-1));
        AddPartCommand = new RelayCommand(AddPart, () => IsVariableLength);
        RemovePartCommand = new RelayCommand(RemovePart, () => IsVariableLength && Parts.Count > 1);
        FillDefaultCommand = new RelayCommand(FillPartsWithDefault, () => option.DefaultText().Length > 0);

        if (_option.IsList) BuildParts();
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

    /// <summary>数値を1つ打つつまみです（並びではないもの）。</summary>
    public bool IsNumber => IsText && !_option.IsList;

    // --- よく使う値 ---
    //
    // 範囲を読んで自分で数を決めるのは、毎回やるには重い作業です。
    // かといって刻みを細かく並べると、選ぶこと自体が探し物になります。
    // 「これだろう」という値だけを、多くならない数で出します。

    /// <summary>選べる値の候補です。無ければ空になります（打ち込みは常にできます）。</summary>
    public IReadOnlyList<string> Presets { get; }

    public bool HasPresets => Presets.Count > 0;

    private static IReadOnlyList<string> BuildPresets(PatternOption option)
    {
        if (option.IsList || option.Kind is "bool" or "choice") return [];

        // 明るさや位置の比（0〜1）は 25% 刻みで足ります。
        // カラーバーの level もここに入ります。
        if (!option.IsInteger && option.Minimum is 0 && option.Maximum is 1)
            return ["0", "0.25", "0.5", "0.75", "1"];

        if (!option.IsInteger) return [];

        var low = (int)Math.Max(option.Minimum ?? 1, 1);

        if (option.Maximum is double max)
        {
            var high = (int)max;

            // 上限が 2 の階乗なら、2 の階乗だけを出します。
            // チェッカやハッチのように「4×4 / 8×8 / 16×16 / 32×32」で語る種類のつまみです。
            if (IsPowerOfTwo(high))
            {
                var powers = new List<string>();
                for (var v = 1; v <= high; v *= 2)
                    if (v >= low) powers.Add(v.ToString(CultureInfo.InvariantCulture));
                return powers;
            }

            // それ以外は端と、間を等間隔で。8 個を超えないようにします。
            var span = high - low;
            if (span <= 0) return [low.ToString(CultureInfo.InvariantCulture)];
            var stride = Math.Max(1, (int)Math.Ceiling(span / 7.0));
            var values = new List<string>();
            for (var v = low; v <= high; v += stride) values.Add(v.ToString(CultureInfo.InvariantCulture));
            if (values[^1] != high.ToString(CultureInfo.InvariantCulture))
                values.Add(high.ToString(CultureInfo.InvariantCulture));
            return values;
        }

        // 上限が無いもの（段数や本数）は、よく使う切りのいい数を並べます。
        // 既定値も混ぜて、いま何になっているかを選び直せるようにします。
        var ladder = new List<int> { 2, 4, 5, 8, 10, 16, 20, 32, 64 };
        if (int.TryParse(option.DefaultText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
            ladder.Add(d);
        return ladder.Where(v => v >= low).Distinct().Order()
                     .Select(v => v.ToString(CultureInfo.InvariantCulture)).ToList();
    }

    private static bool IsPowerOfTwo(int value) => value >= 2 && (value & (value - 1)) == 0;

    // --- 1つずつ動かす ---

    public RelayCommand StepUpCommand { get; }
    public RelayCommand StepDownCommand { get; }

    /// <summary>▲▼ で動かす幅です。整数は 1、比は 0.05 にします。</summary>
    private double Step =>
        _option.IsInteger ? 1
        : _option.Minimum is 0 && _option.Maximum is 1 ? 0.05
        : 0.1;

    /// <summary>
    /// いまの値を1段ぶん動かします。空欄なら既定値から始めます。
    /// 打ち込むより、少しずつ動かして絵を見るほうが早い場面のためのものです。
    /// </summary>
    private void Bump(int direction)
    {
        var current = _text.Trim();
        if (current.Length == 0) current = _option.DefaultText();

        if (!double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            value = _option.Minimum ?? 0;

        value += Step * direction;
        if (_option.Minimum is double lo) value = Math.Max(lo, value);
        if (_option.Maximum is double hi) value = Math.Min(hi, value);

        Text = _option.IsInteger
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            // 0.05 を足し引きすると 0.30000000000000004 が出ます。丸めてから見せます。
            : Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
    }

    // --- 並びは1つずつの欄に分ける ---
    //
    // `[1, 2, 3, 4, 6, 8]` を1つの欄に打たせると、区切りと括弧の作法を覚える必要があり、
    // 1つ直したいだけでも全部を打ち直すことになります。欄を分ければ、
    // 直したい数だけを動かせます。組み立て（角括弧とカンマ）はこちらでやります。

    public ObservableCollection<PatternOptionPart> Parts { get; } = [];

    public bool HasParts => Parts.Count > 0;

    /// <summary>個数が決まっていない並びかどうか（決まっていれば増減させません）。</summary>
    public bool IsVariableLength => _option.IsList && _option.Length is null;

    public RelayCommand AddPartCommand { get; }
    public RelayCommand RemovePartCommand { get; }

    /// <summary>既定の並びを欄へ入れて、そこから直せるようにします。</summary>
    public RelayCommand FillDefaultCommand { get; }

    /// <summary>色は R/G/B、それ以外は番号を見出しにします。</summary>
    private static readonly string[] ColorLabels = ["R", "G", "B"];

    private void BuildParts()
    {
        var defaults = Parse(_option.DefaultText(), out _);
        var count = _option.Length ?? Math.Max(defaults.Count, 1);

        for (var i = 0; i < count; i++)
        {
            var label = IsColor && i < ColorLabels.Length ? ColorLabels[i] : (i + 1).ToString(CultureInfo.InvariantCulture);
            Parts.Add(new PatternOptionPart(label, ComposeFromParts));
        }
        Raise(nameof(HasParts));
    }

    private void AddPart()
    {
        Parts.Add(new PatternOptionPart((Parts.Count + 1).ToString(CultureInfo.InvariantCulture), ComposeFromParts));
        RemovePartCommand.RaiseCanExecuteChanged();
        ComposeFromParts();
    }

    private void RemovePart()
    {
        if (Parts.Count <= 1) return;
        Parts.RemoveAt(Parts.Count - 1);
        RemovePartCommand.RaiseCanExecuteChanged();
        ComposeFromParts();
    }

    /// <summary>
    /// 欄の中身を1本の文字列へ組み立てます。1つでも空いていれば「触っていない」ままにします。
    /// 途中まで打ったものを送ると、生成器には個数違いとして届くだけだからです。
    /// </summary>
    private void ComposeFromParts()
    {
        var values = Parts.Select(p => p.Text.Trim()).ToList();
        Text = values.Any(v => v.Length == 0) ? "" : string.Join(", ", values);
    }

    /// <summary>並びの欄へ既定値を入れます（「既定を入れる」を押したとき）。</summary>
    public void FillPartsWithDefault()
    {
        var defaults = Parse(_option.DefaultText(), out var bad);
        if (bad is not null || defaults.Count == 0) return;

        while (IsVariableLength && Parts.Count < defaults.Count) AddPart();
        for (var i = 0; i < Parts.Count; i++)
            Parts[i].SetWithoutNotify(i < defaults.Count ? Show(defaults[i]) : "");
        ComposeFromParts();
    }

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
            : _text.Trim().Length > 0 && !SameAsDefault();

    /// <summary>
    /// 打ち込まれた値が既定と同じかどうかです。同じならコマンドに載せません。
    ///
    /// 「既定を入れる」で埋めてから1つだけ直す、という使い方をしても、
    /// 触っていない値まで固定してしまわないようにするためです
    /// （固定すると、生成器側で既定を変えたときに古い値へ釘付けになります）。
    /// </summary>
    private bool SameAsDefault()
    {
        var defaults = Parse(_option.DefaultText(), out var badDefault);
        if (badDefault is not null || defaults.Count == 0) return false;

        var values = Parse(_text, out var bad);
        if (bad is not null || values.Count != defaults.Count) return false;

        return !values.Where((v, i) => Math.Abs(v - defaults[i]) > 1e-9).Any();
    }

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
        foreach (var part in Parts) part.SetWithoutNotify("");
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

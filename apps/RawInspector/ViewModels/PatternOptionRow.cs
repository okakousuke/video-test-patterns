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

    /// <summary>
    /// いまの寸法です。格子の間隔や線の太さの既定は、幅と高さから決まります。
    /// 行が寸法を知らないと、その手のつまみは既定値を出せません。
    /// </summary>
    private readonly Func<(int Width, int Height)> _size;

    public PatternOptionRow(PatternOption option, Action changed, Func<(int Width, int Height)> size)
    {
        _option = option;
        _changed = changed;
        _size = size;
        _flag = DefaultFlag;

        Presets = BuildPresets(option, DefaultText());
        StepUpCommand = new RelayCommand(() => Bump(+1), () => CanBump(+1));
        StepDownCommand = new RelayCommand(() => Bump(-1), () => CanBump(-1));
        AddPartCommand = new RelayCommand(AddPart, () => IsVariableLength);
        RemovePartCommand = new RelayCommand(RemovePart, () => IsVariableLength && Parts.Count > 1);
        FillDefaultCommand = new RelayCommand(FillPartsWithDefault, () => DefaultText().Length > 0);

        if (_option.IsList)
        {
            BuildParts();
            FillPartsWithDefault();
        }
        else if (IsNumber) { _text = DefaultText(); _shownAuto = _text; }
        else if (IsChoice && _option.Choices.Contains(DefaultText())) _choice = DefaultText();
    }

    /// <summary>いまの寸法での既定値です。寸法に依らないものはそのまま返ります。</summary>
    private string DefaultText() => _option.DefaultText(_size().Width, _size().Height);

    /// <summary>いま欄に入れてある「寸法から決めた既定値」です。触られたかの判定に使います。</summary>
    private string _shownAuto = "";

    /// <summary>
    /// 寸法が変わったので、寸法から決まる既定値を出し直します。
    ///
    /// 入れ替えるのは**触っていない行だけ**です。打ち込んだ値まで書き換えると、
    /// 寸法をいじった拍子に指定が消えます。
    ///
    /// 入れ替えても「触っていない」ままなので、コマンドには載りません。
    /// 載せてしまうと、そのあと寸法を変えても数が付いてこなくなります
    /// （生成器側の自動計算を、その時点の数で固定することになります）。
    /// </summary>
    public void RefreshForSize()
    {
        Raise(nameof(Hint));
        if (!_option.HasAuto || !IsNumber) return;

        if (_text != _shownAuto) return;

        _shownAuto = DefaultText();
        if (!Set(ref _text, _shownAuto, nameof(Text))) return;
        Raise(nameof(IsChanged));
        StepUpCommand.RaiseCanExecuteChanged();
        StepDownCommand.RaiseCanExecuteChanged();
    }

    public string Name => _option.Name;
    public string Label => _option.Label;
    public string Help => _option.Help;
    public string Hint => _option.Hint(_size().Width, _size().Height);

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

    /// <summary>
    /// 端から端まで刻んで見せる価値がある、範囲の広さの上限です。
    ///
    /// noise の seed は 2<sup>31</sup>-1 まで許されます。そこを 8 等分しても
    /// 3 億刻みの数が並ぶだけで、「これだろう」という値にはなりません。
    /// これより広いものは桁で並べます。
    /// </summary>
    private const double WidestRangeWorthSpacing = 4096;

    /// <summary>既定値であることの注記です。選んだあと <see cref="Text"/> が落とします。</summary>
    private const string DefaultMark = "（規定）";

    private static IReadOnlyList<string> BuildPresets(PatternOption option, string defaultText)
    {
        if (option.IsList || option.Kind is "bool" or "choice") return [];

        var values = Candidates(option);
        if (values.Count == 0) return [];

        // 既定が刻みから出てこないことがあります（傾き 5.0、暗い側 0.17 など）。
        // 選べないと「元は何だったか」へ戻せないので、順番どおりの位置へ入れます。
        if (!double.TryParse(defaultText, NumberStyles.Float, CultureInfo.InvariantCulture, out var def))
            return values.Select(Show).ToList();

        if (!values.Any(v => Near(v, def)))
        {
            var at = values.FindIndex(v => v > def);
            values.Insert(at < 0 ? values.Count : at, def);
        }
        return values.Select(v => Near(v, def) ? Show(v) + DefaultMark : Show(v)).ToList();
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    /// <summary>刻んだ値そのものです（既定の注記を付ける前）。</summary>
    private static List<double> Candidates(PatternOption option)
    {
        if (!option.IsInteger)
        {
            // 明るさや位置の比（0〜1）は 25% 刻みで足ります。
            if (option.Minimum is 0 && option.Maximum is 1) return [0, 0.25, 0.5, 0.75, 1];

            // 傾き（1〜44 度）や最高周波数（0.01〜0.5）のように、
            // 0〜1 に収まらない小数にも候補は要ります。
            return option.Minimum is double flo && option.Maximum is double fhi
                ? NiceSteps(flo, fhi, integer: false)
                : [];
        }

        var low = Math.Max(option.Minimum ?? 1, 1);

        // 上限が無いもの（段数や本数）は、よく使う切りのいい数を並べます。
        if (option.Maximum is not double high)
            return new List<double> { 2, 4, 5, 8, 10, 16, 20, 32, 64 }.Where(v => v >= low).ToList();

        // 桁違いに広いものは、刻んでも読めません。桁で並べて、最後を上限にします。
        if (high - low > WidestRangeWorthSpacing) return Decades(low, high);

        // 上限が 2 の階乗なら、2 の階乗だけを出します。
        // チェッカやハッチのように「4×4 / 8×8 / 16×16 / 32×32」で語る種類のつまみです。
        //
        // 刻みは long で持ちます。int だと最後の一歩で上限を跨いだときに
        // 負へ回り込み、条件が永遠に成り立ってしまいます。
        if (IsPowerOfTwo((long)high))
        {
            var powers = new List<double>();
            for (var v = 1L; v <= (long)high; v *= 2)
                if (v >= low) powers.Add(v);
            return powers;
        }

        return NiceSteps(low, high, integer: true);
    }

    /// <summary>
    /// 下限と上限のあいだを、切りのいい刻みで並べます。
    ///
    /// 端は必ず入れます。上限が選べないと、いちばん端の振る舞いを試せません。
    /// 途中は刻みの倍数から始めます（4, 107, 210… ではなく 4, 100, 200… にするためです）。
    /// </summary>
    private static List<double> NiceSteps(double low, double high, bool integer)
    {
        var values = new List<double> { low };
        var step = NiceStep((high - low) / 8.0, integer);
        if (step > 0)
        {
            for (var v = Math.Ceiling(low / step) * step; v < high; v += step)
                if (v > low) values.Add(integer ? Math.Round(v) : Math.Round(v, 6));
        }
        if (high > low) values.Add(high);
        return values.Distinct().OrderBy(v => v).ToList();
    }

    /// <summary>1・2・2.5・5 の 10 の階乗倍のうち、求めた刻み以上でいちばん小さいものです。</summary>
    private static double NiceStep(double raw, bool integer)
    {
        if (raw <= 0) return 0;
        var scale = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var m in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
        {
            var step = m * scale;
            if (step >= raw) return integer ? Math.Max(1, Math.Round(step)) : step;
        }
        return integer ? Math.Max(1, Math.Round(10 * scale)) : 10 * scale;
    }

    /// <summary>桁で並べます（1, 10, 100, …）。最後は必ず上限です。</summary>
    private static List<double> Decades(double low, double high)
    {
        var values = new List<double> { low };
        for (var v = Math.Pow(10, Math.Ceiling(Math.Log10(Math.Max(low, 1)))); v < high; v *= 10)
            if (v > low) values.Add(v);
        values.Add(high);
        return values.Distinct().OrderBy(v => v).ToList();
    }

    private static bool IsPowerOfTwo(long value) => value >= 2 && (value & (value - 1)) == 0;

    // --- 1つずつ動かす ---

    public RelayCommand StepUpCommand { get; }
    public RelayCommand StepDownCommand { get; }

    /// <summary>▲▼ で動かす幅です。整数は 1、比は 0.05 にします。</summary>
    private double Step =>
        _option.IsInteger ? 1
        : _option.Minimum is 0 && _option.Maximum is 1 ? 0.05
        : 0.1;

    /// <summary>
    /// その向きへまだ動かせるかどうかです。
    /// 端に着いても押せたままだと、押しても何も起きない状態が続きます。
    /// </summary>
    private bool CanBump(int direction)
    {
        if (!IsNumber) return false;

        var current = _text.Trim();
        if (current.Length == 0) current = DefaultText();

        // 読めないうちは塞ぎません。打ち直す前に押して直せる余地を残します。
        if (!double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return true;

        return direction > 0
            ? _option.Maximum is not double hi || value < hi
            : _option.Minimum is not double lo || value > lo;
    }

    /// <summary>
    /// いまの値を1段ぶん動かします。空欄なら既定値から始めます。
    /// 打ち込むより、少しずつ動かして絵を見るほうが早い場面のためのものです。
    /// </summary>
    private void Bump(int direction)
    {
        var current = _text.Trim();
        if (current.Length == 0) current = DefaultText();

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

    // --- 色を名前で入れる ---
    //
    // R・G・B を 3 つ打つのは、白や黄のような「決まった色」を入れたいだけのときには
    // 手間なだけです。カラーバーに出てくる 8 色は各成分が 0 か 1 かのどちらかなので、
    // 名前から入れられます。輝度の高い順に並べます（カラーバーの並び順です）。
    //
    // ここに置いてよいのは、これが**生成器の規則ではない**からです。
    // 「白は R=G=B=1」は色の決まりであって、生成器が決めていることではありません。

    private static readonly (string Name, double R, double G, double B)[] NamedColors =
    [
        ("白", 1, 1, 1),
        ("黄", 1, 1, 0),
        ("シアン", 0, 1, 1),
        ("緑", 0, 1, 0),
        ("マゼンタ", 1, 0, 1),
        ("赤", 1, 0, 0),
        ("青", 0, 0, 1),
        ("黒", 0, 0, 0),
    ];

    /// <summary>名前で入れられる色です。色のつまみ以外では空になります。</summary>
    public IReadOnlyList<string> ColorPresets =>
        IsColor ? NamedColors.Select(c => c.Name).ToList() : [];

    public bool HasColorPresets => IsColor;

    /// <summary>
    /// いまの R/G/B に当たる色の名前です。当てはまらなければ null（未選択）になります。
    ///
    /// 選ぶと 3 つの欄が埋まります。手で打ち直したときは、名前のほうを空にします。
    /// 名前を残すと、赤を選んでから R を 0.5 にしたときに「赤」と出たままになり、
    /// 欄に書いてあることと食い違います。
    /// </summary>
    public string? ColorPreset
    {
        get
        {
            if (!IsColor || Parts.Count < 3) return null;

            var values = new double[3];
            for (var i = 0; i < 3; i++)
                if (!double.TryParse(Parts[i].Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return null;

            foreach (var c in NamedColors)
                if (Near(values[0], c.R) && Near(values[1], c.G) && Near(values[2], c.B)) return c.Name;
            return null;
        }
        set
        {
            if (value is null || !IsColor || Parts.Count < 3) return;

            var hit = NamedColors.FirstOrDefault(c => c.Name == value);
            if (hit.Name is null) return;

            Parts[0].SetWithoutNotify(Show(hit.R));
            Parts[1].SetWithoutNotify(Show(hit.G));
            Parts[2].SetWithoutNotify(Show(hit.B));
            ComposeFromParts();
        }
    }

    private void BuildParts()
    {
        var defaults = Parse(DefaultText(), out _);
        var count = _option.Length ?? Math.Max(defaults.Count, 1);

        for (var i = 0; i < count; i++)
        {
            var label = IsColor && i < ColorLabels.Length ? ColorLabels[i] : (i + 1).ToString(CultureInfo.InvariantCulture);
            Parts.Add(new PatternOptionPart(label, ComposeFromParts, _option.IsInteger, _option.Minimum, _option.Maximum));
        }
        Raise(nameof(HasParts));
    }

    private void AddPart()
    {
        // 空のまま増やすと、その並びは「1つでも空いていれば触っていない」の扱いになり、
        // 増やしたのに何も送られません。値を入れて、そこから直せるようにします。
        var part = new PatternOptionPart((Parts.Count + 1).ToString(CultureInfo.InvariantCulture), ComposeFromParts,
                                         _option.IsInteger, _option.Minimum, _option.Maximum);
        part.SetWithoutNotify(SeedForNewPart());
        Parts.Add(part);
        RemovePartCommand.RaiseCanExecuteChanged();
        ComposeFromParts();
    }

    /// <summary>増やした欄へ最初に入れる値です。直前の欄、無ければ既定の最後を使います。</summary>
    private string SeedForNewPart()
    {
        if (Parts.Count > 0 && Parts[^1].Text.Trim().Length > 0) return Parts[^1].Text.Trim();
        var defaults = Parse(DefaultText(), out _);
        return Show(defaults.Count > 0 ? defaults[^1] : _option.Minimum ?? 1);
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
        // 欄を手で直したら、色の名前も付け直します（当てはまらなければ空になります）。
        if (IsColor) Raise(nameof(ColorPreset));
    }

    /// <summary>並びの欄へ既定値を入れます（「既定を入れる」を押したとき）。</summary>
    public void FillPartsWithDefault()
    {
        var defaults = Parse(DefaultText(), out var bad);
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
        set
        {
            var clean = Strip(value);
            if (Set(ref _text, clean)) Notify();
            // 「5（規定）」を選ぶと、落とした「5」を欄へ返さないと注記が残ります。
            else if (clean != value) Raise(nameof(Text));
        }
    }

    /// <summary>
    /// プルダウンの「5（規定）」から、数の部分だけを取り出します。
    /// 見出しは選ぶときの手がかりで、値ではありません。
    /// </summary>
    private static string Strip(string? value)
    {
        var text = value ?? "";
        var at = text.IndexOf(DefaultMark, StringComparison.Ordinal);
        return at < 0 ? text : text[..at];
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
        StepUpCommand.RaiseCanExecuteChanged();
        StepDownCommand.RaiseCanExecuteChanged();
        _changed();
    }

    /// <summary>
    /// 既定から動かしたかどうかです。動かした行だけコマンドに載ります。
    ///
    /// 選択肢は「（既定のまま）」だけでなく、**既定と同じものを選び直した場合も
    /// 動かしていない**と見ます。欄には最初から既定が入っているので、
    /// ここを名前だけで見ると、開いた瞬間に全部が「動かした」になってしまいます。
    /// </summary>
    public bool IsChanged => IsFlag
        ? _flag != DefaultFlag
        : IsChoice
            ? _choice != KeepDefault && _choice != DefaultText()
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
        var defaults = Parse(DefaultText(), out var badDefault);
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
        if (_option.IsList) FillPartsWithDefault();
        else if (IsNumber) { Text = DefaultText(); _shownAuto = _text; }
        else Text = "";
        Choice = IsChoice && _option.Choices.Contains(DefaultText())
            ? DefaultText() : KeepDefault;
        Flag = DefaultFlag;
    }

    public void Restore(string text, string choice, bool flag)
    {
        if (_option.IsList)
        {
            var values = Parse(text, out _);
            for (var i = 0; i < Parts.Count; i++)
                Parts[i].SetWithoutNotify(i < values.Count ? Show(values[i]) : "");
            ComposeFromParts();
        }
        else Text = text;
        Choice = choice;
        Flag = flag;
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

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using RawInspector.Models;

namespace RawInspector.ViewModels;

/// <summary>
/// 生成パネルの状態です。
///
/// 選べる値は生成器の `--describe` から取ります。規則をここへ写すと、
/// 生成器側を直したときに黙ってずれます（GUI では作れるのに生成器が弾く、など）。
///
/// 組み合わせを**選べなくする**のではなく、選んだうえで**成立しない理由を出す**方針です。
/// 選択肢が黙って消えると、なぜ消えたのかが分かりません。
/// 「v210 は 4:2:2 の 10bit だけ」と書いてあれば、次に何を変えればよいかが読めます。
/// </summary>
public sealed class GeneratorViewModel : ObservableObject
{
    private readonly Action<string> _onGenerated;

    public GeneratorViewModel(Action<string> onGenerated)
    {
        _onGenerated = onGenerated;
        GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
        CopyCommandCommand = new RelayCommand(CopyCommandLine, () => !string.IsNullOrEmpty(CommandLine));
    }

    public RelayCommand GenerateCommand { get; }
    public RelayCommand CopyCommandCommand { get; }

    // --- 生成器の呼び出し方 ---

    private string _generatorCommand = "python -m vtp";
    public string GeneratorCommand
    {
        get => _generatorCommand;
        set { if (Set(ref _generatorCommand, value)) Revalidate(); }
    }

    private GeneratorCatalog? _catalog;

    public ObservableCollection<string> Patterns { get; } = [];
    public ObservableCollection<string> ColorModels { get; } = [];
    public ObservableCollection<string> Subsamplings { get; } = [];
    public ObservableCollection<int> BitDepths { get; } = [];
    public ObservableCollection<string> Ranges { get; } = [];
    public ObservableCollection<string> Matrices { get; } = [];
    public ObservableCollection<string> Storages { get; } = [];
    public ObservableCollection<string> Alignments { get; } = [];

    /// <summary>生成器の一覧を読み込みます。読めなければ理由を残して、生成は止めます。</summary>
    public async Task LoadCatalogAsync()
    {
        IsBusy = true;
        StatusText = "生成器へ問い合わせています…";
        try
        {
            _catalog = await GeneratorCatalog.LoadAsync(_generatorCommand);
            CatalogError = null;

            Fill(Patterns, _catalog.Patterns);
            Fill(ColorModels, _catalog.Combinations.Select(c => c.ColorModel).Distinct());
            Fill(Subsamplings, _catalog.Combinations.Select(c => c.Subsampling).Distinct());
            Fill(BitDepths, _catalog.Combinations.Select(c => c.BitDepth).Distinct().OrderBy(b => b));
            Fill(Ranges, _catalog.Combinations.Select(c => c.Range).Distinct());
            Fill(Matrices, _catalog.Matrices);
            Fill(Storages, _catalog.Combinations.Select(c => c.Storage).Distinct());
            Fill(Alignments, _catalog.Combinations.Select(c => c.Alignment).Distinct());

            StatusText = $"{_catalog.Generator} に接続しました（成立する組み合わせ {_catalog.Combinations.Count} 通り）。";
        }
        catch (Exception ex)
        {
            _catalog = null;
            CatalogError = ex.Message;
            StatusText = "生成器へ接続できませんでした。";
        }
        finally
        {
            IsBusy = false;
            Revalidate();
        }
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private string? _catalogError;
    public string? CatalogError
    {
        get => _catalogError;
        private set { if (Set(ref _catalogError, value)) Raise(nameof(HasCatalogError)); }
    }

    public bool HasCatalogError => !string.IsNullOrEmpty(_catalogError);

    // --- 生成条件 ---

    private string _pattern = "colorbar";
    public string Pattern { get => _pattern; set { if (Set(ref _pattern, value)) Revalidate(); } }

    private int _width = 1920;
    public int Width { get => _width; set { if (Set(ref _width, value)) Revalidate(); } }

    private int _height = 1080;
    public int Height { get => _height; set { if (Set(ref _height, value)) Revalidate(); } }

    private string _colorModel = "ycbcr";
    public string ColorModel { get => _colorModel; set { if (Set(ref _colorModel, value)) Revalidate(); } }

    private string _subsampling = "4:2:0";
    public string Subsampling { get => _subsampling; set { if (Set(ref _subsampling, value)) Revalidate(); } }

    private int _bitDepth = 8;
    public int BitDepth { get => _bitDepth; set { if (Set(ref _bitDepth, value)) Revalidate(); } }

    private string _range = "limited";
    public string Range { get => _range; set { if (Set(ref _range, value)) Revalidate(); } }

    private string _matrix = "bt709";
    public string Matrix { get => _matrix; set { if (Set(ref _matrix, value)) Revalidate(); } }

    private string _storage = "nv12";
    public string Storage { get => _storage; set { if (Set(ref _storage, value)) Revalidate(); } }

    private string _alignment = "lsb";
    public string Alignment { get => _alignment; set { if (Set(ref _alignment, value)) Revalidate(); } }

    /// <summary>``KEY=VALUE`` を1行に1つ。パターン固有の指定です。</summary>
    private string _patternOptions = "";
    public string PatternOptions { get => _patternOptions; set { if (Set(ref _patternOptions, value)) Revalidate(); } }

    private string _outputFolder = "";
    public string OutputFolder { get => _outputFolder; set { if (Set(ref _outputFolder, value)) Revalidate(); } }

    private string _outputName = "";
    public string OutputName { get => _outputName; set { if (Set(ref _outputName, value)) Revalidate(); } }

    // --- 判定と表示 ---

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _problem = "";
    public string Problem
    {
        get => _problem;
        private set { if (Set(ref _problem, value)) Raise(nameof(HasProblem)); }
    }

    public bool HasProblem => !string.IsNullOrEmpty(_problem);

    private string _sizeNote = "";
    public string SizeNote { get => _sizeNote; private set => Set(ref _sizeNote, value); }

    private string _commandLine = "";
    public string CommandLine { get => _commandLine; private set => Set(ref _commandLine, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(CanGenerate));
            GenerateCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanGenerate => !_isBusy && !HasProblem && !HasCatalogError && _catalog is not null;

    private string _log = "";
    public string Log
    {
        get => _log;
        private set { if (Set(ref _log, value)) Raise(nameof(HasLog)); }
    }

    public bool HasLog => !string.IsNullOrWhiteSpace(_log);

    /// <summary>
    /// 条件を見直して、成立しない理由と実行するコマンドを作り直します。
    ///
    /// ここで見るのは生成器から受け取った表だけです。判定そのものは持ちません。
    /// </summary>
    private void Revalidate()
    {
        CommandLine = BuildCommandLine();
        Raise(nameof(CanGenerate));

        if (_catalog is null)
        {
            Problem = HasCatalogError ? "生成器へ接続できていません。" : "";
            SizeNote = "";
            GenerateCommand.RaiseCanExecuteChanged();
            CopyCommandCommand.RaiseCanExecuteChanged();
            return;
        }

        var combination = _catalog.Find(_colorModel, _subsampling, _bitDepth, _storage, _alignment, _range);
        if (combination is null)
        {
            // どこが合わないのかを、同じ格納形式で成立する条件から言い当てます。
            var sameStorage = _catalog.Combinations.Where(c => c.Storage == _storage).ToList();
            Problem = sameStorage.Count == 0
                ? $"格納形式 {_storage} で成立する組み合わせがありません。"
                : $"この組み合わせは成立しません。{_storage} で使えるのは "
                  + $"色モデル {string.Join(" / ", sameStorage.Select(c => c.ColorModel).Distinct())}、"
                  + $"色差 {string.Join(" / ", sameStorage.Select(c => c.Subsampling).Distinct())}、"
                  + $"{string.Join(" / ", sameStorage.Select(c => c.BitDepth).Distinct().OrderBy(b => b))}bit、"
                  + $"詰め {string.Join(" / ", sameStorage.Select(c => c.Alignment).Distinct())}、"
                  + $"range {string.Join(" / ", sameStorage.Select(c => c.Range).Distinct())} です。";
            SizeNote = "";
        }
        else if (_width <= 0 || _height <= 0)
        {
            Problem = "幅と高さは 1 以上にしてください。";
            SizeNote = "";
        }
        else if (_width % combination.WidthMultiple != 0 || _height % combination.HeightMultiple != 0)
        {
            Problem = $"この条件では幅が {combination.WidthMultiple} の倍数、"
                      + $"高さが {combination.HeightMultiple} の倍数である必要があります"
                      + $"（指定: {_width} x {_height}）。"
                      + $"近い値は {Nearest(_width, combination.WidthMultiple)} x {Nearest(_height, combination.HeightMultiple)} です。";
            SizeNote = "";
        }
        else if (string.IsNullOrWhiteSpace(_outputFolder))
        {
            Problem = "出力先のフォルダを指定してください。";
            SizeNote = "";
        }
        else
        {
            Problem = "";
            SizeNote = $"幅は {combination.WidthMultiple} の倍数、高さは {combination.HeightMultiple} の倍数。"
                       + $"  {ResolutionNames.Describe(_width, _height)}";
        }

        GenerateCommand.RaiseCanExecuteChanged();
        CopyCommandCommand.RaiseCanExecuteChanged();
    }

    private static int Nearest(int value, int multiple) =>
        multiple <= 1 ? value : Math.Max(multiple, (int)Math.Round(value / (double)multiple) * multiple);

    /// <summary>出力ファイルの基準パスです（拡張子は生成器が付けます）。</summary>
    public string OutputBasePath =>
        Path.Combine(_outputFolder, string.IsNullOrWhiteSpace(_outputName) ? DefaultName() : _outputName.Trim());

    /// <summary>名前を書かなかったときの既定です。条件がそのまま名前になります。</summary>
    private string DefaultName()
    {
        var name = $"{_pattern}_{_colorModel}{_subsampling.Replace(":", "")}_{_bitDepth}bit_{_storage}";
        return name;
    }

    internal List<string> BuildArguments()
    {
        var arguments = new List<string>
        {
            "--pattern", _pattern,
            "--width", _width.ToString(),
            "--height", _height.ToString(),
            "--color-model", _colorModel,
            "--subsampling", _subsampling,
            "--bit-depth", _bitDepth.ToString(),
            "--range", _range,
            "--storage", _storage,
            "--output", OutputBasePath,
        };

        // matrix は Y'CbCr のときだけ意味を持ちます。RGB では生成器が使いません。
        if (_colorModel == "ycbcr") { arguments.Add("--matrix"); arguments.Add(_matrix); }
        // alignment は 16bit コンテナへ 10bit を入れるときだけ効きます。
        if (_bitDepth == 10) { arguments.Add("--alignment"); arguments.Add(_alignment); }

        foreach (var line in ParsePatternOptions())
        {
            arguments.Add("--pattern-option");
            arguments.Add(line);
        }
        return arguments;
    }

    internal IEnumerable<string> ParsePatternOptions() =>
        (_patternOptions ?? "")
        .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => !line.StartsWith('#'));

    private string BuildCommandLine()
    {
        // 貼ってそのまま動く形にします。中身を隠さないほうが、あとから追いやすくなります。
        static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
        return _generatorCommand + " " + string.Join(" ", BuildArguments().Select(Quote));
    }

    private void CopyCommandLine()
    {
        try
        {
            Clipboard.SetText(CommandLine);
            StatusText = "コマンドをコピーしました。";
        }
        catch (Exception ex)
        {
            StatusText = $"コピーできませんでした: {ex.Message}";
        }
    }

    private async Task GenerateAsync()
    {
        if (!CanGenerate) return;

        IsBusy = true;
        StatusText = "生成しています…";
        Log = "";
        try
        {
            Directory.CreateDirectory(_outputFolder);
            var (exitCode, stdout, stderr) = await GeneratorCatalog.RunAsync(_generatorCommand, BuildArguments(), null);
            Log = string.Join("\n", new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));

            if (exitCode == 0)
            {
                StatusText = $"生成しました: {Path.GetFileName(OutputBasePath)}";
                _onGenerated(OutputBasePath + ".manifest.json");
            }
            else
            {
                // 生成器が断った理由をそのまま出します。言い換えると、
                // 生成器が実際に何を嫌がったのかが分からなくなります。
                StatusText = $"生成できませんでした（終了コード {exitCode}）。";
            }
        }
        catch (Exception ex)
        {
            StatusText = "生成できませんでした。";
            Log = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

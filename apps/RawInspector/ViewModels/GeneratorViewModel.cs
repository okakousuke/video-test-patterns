using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using RawInspector.Models;

namespace RawInspector.ViewModels;

/// <summary>下の帯に出す「いまの条件」1 行ぶんです。</summary>
public sealed record ConditionItem(string Label, string Value);

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
    private readonly Func<string, Task> _onGenerated;
    private readonly Action<bool, string> _onGenerationStateChanged;

    public GeneratorViewModel(Func<string, Task> onGenerated,
                              Action<bool, string> onGenerationStateChanged,
                              int initialWidth = 1920, int initialHeight = 1080)
    {
        _onGenerated = onGenerated;
        _onGenerationStateChanged = onGenerationStateChanged;
        _width = initialWidth > 0 ? initialWidth : 1920;
        _height = initialHeight > 0 ? initialHeight : 1080;
        GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
        CopyCommandCommand = new RelayCommand(CopyCommandLine, () => !string.IsNullOrEmpty(CommandLine));
        // 出ている絵といまの条件が同じあいだは押せません。押しても同じ絵しか出ないためです。
        // 押せない＝いま見えているものが、いまの条件の姿だ、という意味になります。
        PreviewCommand = new RelayCommand(async () => await PreviewAsync(automatic: false),
                                          () => !_isBusy && !HasProblem && _catalog is not null && IsPreviewStale);
        _ = ShowDefaultThumbnailAsync();
    }

    public RelayCommand GenerateCommand { get; }
    public RelayCommand CopyCommandCommand { get; }
    public RelayCommand PreviewCommand { get; }

    // --- 生成器の呼び出し方 ---

    private string _generatorCommand = "python -m vtp";
    public string GeneratorCommand
    {
        get => _generatorCommand;
        set { if (Set(ref _generatorCommand, value)) Revalidate(); }
    }

    private GeneratorCatalog? _catalog;

    public ObservableCollection<PatternChoice> Patterns { get; } = [];
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

            Fill(Patterns, _catalog.Patterns.Select(name => new PatternChoice(name)));
            Fill(ColorModels, _catalog.Combinations.Select(c => c.ColorModel).Distinct());
            Fill(Subsamplings, _catalog.Combinations.Select(c => c.Subsampling).Distinct());
            Fill(BitDepths, _catalog.Combinations.Select(c => c.BitDepth).Distinct().OrderBy(b => b));
            Fill(Ranges, _catalog.Combinations.Select(c => c.Range).Distinct());
            Fill(Matrices, _catalog.Matrices);
            Fill(Storages, _catalog.Combinations.Select(c => c.Storage).Distinct());
            Fill(Alignments, _catalog.Combinations.Select(c => c.Alignment).Distinct());

            RebuildPatternOptions();
            StatusText = $"{_catalog.Generator} に接続しました"
                         + $"（成立する組み合わせ {_catalog.Combinations.Count} 通り、"
                         + $"つまみを持つパターン {_catalog.PatternOptions.Count} 種）。";
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
            QueueAutomaticPreview();
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
    private readonly Dictionary<string, Dictionary<string, (string Text, string Choice, bool Flag)>> _patternState = new(StringComparer.OrdinalIgnoreCase);
    public string Pattern
    {
        get => _pattern;
        set
        {
            SavePatternState(_pattern);
            SavePreview(_pattern);
            if (!Set(ref _pattern, value)) return;
            RebuildPatternOptions();
            RestorePatternState(_pattern);
            // つまみと同じで、絵も置いてきた状態のまま戻します。
            // 別のパターンの絵を出したまま条件だけ変わる、という状態は作りません。
            ShowPreviewFor(_pattern);
            Revalidate();
            CancelSizePreviewDelay();
            QueueAutomaticPreview();
        }
    }

    private int _width = 1920;
    public int Width
    {
        get => _width;
        set
        {
            if (!Set(ref _width, value)) return;
            Raise(nameof(SizePreset));
            RefreshOptionSizes();
            Revalidate();
            QueueAutomaticPreviewAfterSizeInput();
        }
    }

    /// <summary>
    /// よく使う大きさです。幅と高さを毎回打たせると、桁を1つ間違えても気付けません。
    /// 通称の付いている大きさは選べるようにしておきます（打ち込みも従来どおりできます）。
    /// </summary>
    public IReadOnlyList<string> SizePresets { get; } =
        ResolutionNames.Presets.Select(p => p.Label).ToList();

    /// <summary>いまの寸法に当たる選択肢です。表に無い大きさなら null（未選択）になります。</summary>
    public string? SizePreset
    {
        get => ResolutionNames.Presets
            .FirstOrDefault(p => p.Width == _width && p.Height == _height).Label;
        set
        {
            if (value is null) return;
            var hit = ResolutionNames.Presets.FirstOrDefault(p => p.Label == value);
            if (hit.Label is null) return;

            // 片方ずつ入れると、途中の組み合わせで判定が走って赤くなります。まとめて入れます。
            Set(ref _width, hit.Width, nameof(Width));
            Set(ref _height, hit.Height, nameof(Height));
            Raise(nameof(SizePreset));
            RefreshOptionSizes();
            Revalidate();
            CancelSizePreviewDelay();
            QueueAutomaticPreview();
        }
    }

    private int _height = 1080;
    public int Height
    {
        get => _height;
        set
        {
            if (!Set(ref _height, value)) return;
            Raise(nameof(SizePreset));
            RefreshOptionSizes();
            Revalidate();
            QueueAutomaticPreviewAfterSizeInput();
        }
    }

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

    /// <summary>
    /// いま選んでいるパターンのつまみです。パターンを変えると総入れ替えになります。
    ///
    /// 中身は生成器の `--describe` が渡してきたものだけで、ここには表がありません。
    /// 生成器へつまみを足せば、この画面は直さなくても出るようになります。
    /// </summary>
    public ObservableCollection<PatternOptionRow> PatternOptionRows { get; } = [];

    public bool HasPatternOptions => PatternOptionRows.Count > 0;

    /// <summary>つまみが 1 つも無いパターンのときに出す一言です。</summary>
    public string PatternOptionNote => _catalog is null
        ? "生成器へ接続すると、パターンごとの設定がここに出ます。"
        : $"{_pattern} に固有の設定はありません。";

    /// <summary>パターンが変わったので、つまみを入れ替えます。</summary>
    private void RebuildPatternOptions()
    {
        PatternOptionRows.Clear();
        if (_catalog is not null)
        {
            foreach (var option in _catalog.OptionsFor(_pattern))
                PatternOptionRows.Add(new PatternOptionRow(option, Revalidate, () => (_width, _height)));
        }
        Raise(nameof(HasPatternOptions));
        Raise(nameof(PatternOptionNote));
    }

    /// <summary>
    /// 寸法から決まる既定値（格子の間隔・線の太さ・パルスの幅）を出し直します。
    /// 触っていない欄だけが入れ替わります。
    /// </summary>
    private void RefreshOptionSizes()
    {
        foreach (var row in PatternOptionRows) row.RefreshForSize();
    }

    private void SavePatternState(string pattern)
    {
        if (PatternOptionRows.Count == 0) return;
        _patternState[pattern] = PatternOptionRows.ToDictionary(
            row => row.Name, row => (row.Text, row.Choice, row.Flag), StringComparer.OrdinalIgnoreCase);
    }

    private void RestorePatternState(string pattern)
    {
        if (!_patternState.TryGetValue(pattern, out var state)) return;
        foreach (var row in PatternOptionRows)
            if (state.TryGetValue(row.Name, out var saved)) row.Restore(saved.Text, saved.Choice, saved.Flag);
        Revalidate();
    }

    /// <summary>触ったつまみをすべて既定へ戻します。</summary>
    public void ResetPatternOptions()
    {
        foreach (var row in PatternOptionRows) row.Reset();
        Revalidate();
    }

    // --- 参考図 ---
    //
    // 押す前に「どんな形の絵か」を出します。既定の姿は exe へ埋め込んだ静止画で、
    // 選んだ瞬間に出ます（生成器は動きません）。つまみを触ったときだけ、
    // その条件で実際に描かせて差し替えます。
    //
    // 静止画は実寸で描いたものを長辺480へ縮めてあります。**実物ではありません。**
    // 画素単位の線・折り返し・ドットバイドットはこの大きさでは出せません。
    // そこは割り切って、形と配置と密度の比だけを見るものにしています。

    private BitmapSource? _previewImage;
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set { if (Set(ref _previewImage, value)) Raise(nameof(HasPreviewImage)); }
    }

    public bool HasPreviewImage => _previewImage is not null;

    /// <summary>いま出ている絵が、生成器に描かせたものかどうかです（参考図なら false）。</summary>
    private bool _isPreviewLive;
    public bool IsPreviewLive { get => _isPreviewLive; private set => Set(ref _isPreviewLive, value); }

    // 参考図を描いた条件です（tools/make_pattern_thumbnails.py と対になっています）。
    private const int ThumbnailWidth = 1920;
    private const int ThumbnailHeight = 1080;

    /// <summary>
    /// いま画面に出ている絵が、どの条件で描かれたものかです。
    ///
    /// 「押せる／押せない」はこれと今の条件を突き合わせて決めます。
    /// **既定から動かしたかどうかでは決めません。**
    /// 既定へ戻したときも、出ている絵が別の条件で描かれたものなら描き直しが要ります
    /// （「既定へ戻す」を押しても押せないままだったのは、そこを見ていなかったためです）。
    /// </summary>
    private string _shownSignature = "";

    // どの欄がずれているのかを名指しできるよう、寸法だけは別にも控えます。
    // 「点滅しているが理由が分からない」を無くすためのものです。
    private int _shownWidth;
    private int _shownHeight;

    /// <summary>
    /// 絵が一致しているかを見るためだけの、条件の写しです。
    ///
    /// パターン名・寸法・既定から動かしたつまみを並べます。つまみはパターンごとに
    /// 違うので、パターン名が入っている時点で「別のパターンの絵が出たままだ」も
    /// 同じ判定で拾えます。つまみの状態自体はパターンごとに持っている
    /// （<see cref="_patternState"/>）ので、行き来しても各パターンの言い分は変わりません。
    /// </summary>
    // 条件の区切り。値の中に出てこない文字なら何でもよいので、制御文字を使います。
    private const string Separator = "\u001F";

    private static string SignatureOf(string pattern, int width, int height, IEnumerable<string> options) =>
        string.Join(Separator, new[] { pattern, $"{width}x{height}" }
                              .Concat(options.OrderBy(o => o, StringComparer.Ordinal)));

    private string CurrentSignature() =>
        SignatureOf(_pattern, _width, _height, ChangedPatternOptions());

    /// <summary>
    /// 静的参考図が表している条件です。
    ///
    /// 埋め込み画像はFHDから縮めて作っていますが、用途は配置と形の案内です。
    /// 出力寸法の正確なプレビューではないため、寸法は署名へ含めません。
    /// パターン固有のつまみを変えたときだけ、参考図と現在条件が違うと判定します。
    /// </summary>
    private static string ReferenceSignature(string pattern, IEnumerable<string> options) =>
        SignatureOf(pattern, 0, 0, options);

    /// <summary>出ている絵が、いまの条件のものではないかどうかです。</summary>
    public bool IsPreviewStale => _isPreviewLive
        ? _shownSignature != CurrentSignature()
        : _shownSignature != ReferenceSignature(_pattern, ChangedPatternOptions());

    /// <summary>
    /// 寸法が、出ている絵のものと違うかどうかです。
    ///
    /// 「押せる」だけでは、なぜ押せるのかが分かりません。固有のつまみを触っていなくても
    /// 寸法を変えれば絵は古くなるので、**どこを変えたから古いのか**を欄の側でも示します。
    /// </summary>
    public bool IsSizeChanged =>
        HasPreviewImage && _isPreviewLive && (_width != _shownWidth || _height != _shownHeight);

    /// <summary>
    /// コマンドの下に出す一言です。
    ///
    /// 直前にやったこと（StatusText）だけを出していると、描いたあとに条件を変えても
    /// 「いまの条件で描きました」と出たままになり、画面が嘘をつきます。
    /// 古くなっているあいだは、そちらを先に言います。
    /// </summary>
    public string ConditionNote =>
        !IsPreviewStale ? _statusText
        : IsSizeChanged && HasChangedPatternOptions
            ? "条件とパターンの設定が変わっています。「この条件で描いてみる」で描き直せます。"
        : IsSizeChanged
            ? $"寸法が変わっています（出ている絵は {_shownWidth}×{_shownHeight}）。「この条件で描いてみる」で描き直せます。"
        : HasChangedPatternOptions
            ? "パターンの設定が変わっています。「この条件で描いてみる」で描き直せます。"
            : "条件が変わっています。「この条件で描いてみる」で描き直せます。";

    private bool HasChangedPatternOptions => PatternOptionRows.Any(r => r.IsChanged);

    /// <summary>
    /// いまの条件を、読める形で並べたものです。
    ///
    /// 参考図は**形の図**なので、色モデルや格納形式を変えても絵は変わりません。
    /// 絵に出ないものを絵で報せることはできませんし、点滅させると
    /// 「押しても見た目が変わらないのに点滅している」ことになります。
    /// 変えたことはここに出します。ビューアの「RAWに記録された生成条件」と
    /// 同じ並び・同じ言葉にして、作るときと見るときで読み替えが要らないようにします。
    /// </summary>
    public IReadOnlyList<ConditionItem> ConditionSummary
    {
        get
        {
            var name = SizePreset;
            var size = string.IsNullOrEmpty(name) ? $"{_width}×{_height}" : $"{_width}×{_height} {name}";

            var items = new List<ConditionItem>
            {
                new("パターン名", _pattern),
                new("画像サイズ", size),
                new("画素数の比", AspectRatio.Describe(_width, _height)),
                new("色モデル", _colorModel),
                new("色差サブサンプリング", _subsampling),
                new("ビット深度", $"{_bitDepth} bit"),
                new("信号レンジ", _range),
                new("色変換マトリクス",
                    _colorModel == "rgb" ? "（この色モデルでは未使用）" : _matrix),
                new("メモリ格納形式", _storage),
                new("ビット配置",
                    _bitDepth > 8 ? _alignment : "（8bit では未使用）"),
            };
            return items;
        }
    }

    /// <summary>絵の下に出す一言です。いま何を見ているのかを言い切ります。</summary>
    public string PreviewNote =>
        !HasPreviewImage ? ""
        : !IsPreviewStale
            ? _isPreviewLive
                ? "いまの条件で描いた絵です。実寸を縮めてあります。"
                : $"形を見るための参考図です。出力は {_width}×{_height} で生成します。"
            : _isPreviewLive
                ? "1つ前の条件で描いた絵です。いまの条件とは違います。"
                : $"既定・{ThumbnailWidth}×{ThumbnailHeight} の姿です。いまの条件とは違います。";

    public PatternGuide Guide => PatternGuide.For(_pattern);

    /// <summary>
    /// パターンごとに、最後に見ていた絵と、その絵を描いた条件です。
    ///
    /// つまみだけを覚えて絵を覚えないと、行き来したときに
    /// 「つまみは触ったまま・絵は既定の姿」という食い違いが必ず起きます。
    /// そうなると戻るたびに点滅し、本当に描き直しが要るときの合図が薄れます。
    /// 置いてきた状態へそのまま戻せるよう、絵の側も持っておきます。
    /// </summary>
    private readonly Dictionary<string, (BitmapSource? Image, bool Live, string Signature, int Width, int Height)> _previewByPattern =
        new(StringComparer.OrdinalIgnoreCase);

    private void SavePreview(string pattern)
    {
        if (_previewImage is null) return;
        _previewByPattern[pattern] = (_previewImage, _isPreviewLive, _shownSignature, _shownWidth, _shownHeight);
    }

    /// <summary>そのパターンで最後に見ていた絵へ戻します。無ければ参考図を出します。</summary>
    private void ShowPreviewFor(string pattern)
    {
        if (!_previewByPattern.TryGetValue(pattern, out var saved))
        {
            _ = ShowDefaultThumbnailAsync();
            return;
        }

        PreviewImage = saved.Image;
        IsPreviewLive = saved.Live;
        _shownSignature = saved.Signature;
        _shownWidth = saved.Width;
        _shownHeight = saved.Height;
        RaisePreviewState();
    }

    private async Task ShowDefaultThumbnailAsync()
    {
        var pattern = _pattern;

        // 読み込みを待つ前に、これから出る絵の条件を控えます。
        // 待っているあいだだけ「古い」に見えると、パターンを選ぶたびに
        // ボタンが一瞬光ってしまい、本当に古いときの合図が薄れます。
        _shownSignature = ReferenceSignature(pattern, []);
        _shownWidth = 0;
        _shownHeight = 0;
        RaisePreviewState();

        var image = await Task.Run(() => PatternThumbnails.For(pattern));
        if (!string.Equals(pattern, _pattern, StringComparison.OrdinalIgnoreCase)) return;
        PreviewImage = image;
        IsPreviewLive = false;
        // 参考図が出せなかったときは、何も出ていないので常に描き直せる状態にします。
        if (image is null) _shownSignature = "";
        RaisePreviewState();
    }

    private void RaisePreviewState()
    {
        Raise(nameof(PreviewNote));
        Raise(nameof(IsPreviewStale));
        Raise(nameof(IsSizeChanged));
        Raise(nameof(ConditionNote));
        Raise(nameof(Guide));
        PreviewCommand.RaiseCanExecuteChanged();
    }

    private bool HasExactLivePreview =>
        _isPreviewLive && _shownSignature == CurrentSignature();

    private bool _automaticPreviewPending;
    private CancellationTokenSource? _sizePreviewDelay;

    private void CancelSizePreviewDelay()
    {
        _sizePreviewDelay?.Cancel();
        _sizePreviewDelay?.Dispose();
        _sizePreviewDelay = null;
    }

    /// <summary>
    /// 幅と高さの直接入力は、1文字ごとに生成器を起動せず、入力が止まってから1回だけ描きます。
    /// </summary>
    private void QueueAutomaticPreviewAfterSizeInput()
    {
        CancelSizePreviewDelay();
        var delay = new CancellationTokenSource();
        _sizePreviewDelay = delay;
        _ = WaitForSizeInputAsync(delay);
    }

    private async Task WaitForSizeInputAsync(CancellationTokenSource delay)
    {
        try
        {
            await Task.Delay(600, delay.Token);
            if (!delay.IsCancellationRequested) QueueAutomaticPreview();
        }
        catch (OperationCanceledException)
        {
            // 次の文字が入っただけなので、何も表示しません。
        }
        finally
        {
            if (ReferenceEquals(_sizePreviewDelay, delay))
            {
                _sizePreviewDelay.Dispose();
                _sizePreviewDelay = null;
            }
        }
    }

    /// <summary>
    /// パターンを選んだときだけ、現在の共通解像度で実物のプレビューを用意します。
    /// 連続して選んだ場合は最後の1件だけを待ち行列に残します。
    /// </summary>
    private void QueueAutomaticPreview()
    {
        if (_catalog is null || HasProblem) return;
        if (HasExactLivePreview)
        {
            _automaticPreviewPending = false;
            return;
        }
        if (_isBusy)
        {
            _automaticPreviewPending = true;
            return;
        }

        _automaticPreviewPending = false;
        _ = PreviewAsync(automatic: true);
    }

    /// <summary>
    /// いまの条件のまま、絵だけを一時フォルダへ描かせて差し替えます。
    ///
    /// `--outputs png` にしてRAWは書きません。4K の RAW は数百MBあり、
    /// 見るためだけに作る理由がありません（絵は同じものが出ます）。
    /// 生成器は決定的なので、ここで見た絵と本番の絵は一致します。
    /// </summary>
    private async Task PreviewAsync(bool automatic)
    {
        if (_catalog is null) return;
        if (_isBusy)
        {
            if (automatic) _automaticPreviewPending = true;
            return;
        }

        IsBusy = true;
        IsPreviewBusy = true;
        StatusText = automatic ? "選んだパターンを現在の解像度で描いています…" : "この条件で描いています…";
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "RawInspector", "preview");
            Directory.CreateDirectory(folder);
            var basePath = Path.Combine(folder, "current");

            // 描き始める前の条件を控えます。描いているあいだにつまみを触られたら、
            // 出来上がった絵はもう古いので、そのまま「描き直せる」に戻します。
            var drawn = CurrentSignature();
            var drawnPattern = _pattern;
            var drawnWidth = _width;
            var drawnHeight = _height;

            // 出力先だけ差し替えます。ほかは本番と同じ引数です。
            var arguments = BuildArguments();
            var output = arguments.IndexOf("--output");
            if (output >= 0) arguments[output + 1] = basePath;
            arguments.Add("--outputs");
            arguments.Add("png");

            var (exitCode, stdout, stderr) = await GeneratorCatalog.RunAsync(_generatorCommand, arguments, null);
            if (exitCode != 0)
            {
                Log = string.Join("\n", new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));
                LogIsFailure = true;
                StatusText = $"この条件では描けませんでした（終了コード {exitCode}）。";
                return;
            }

            // 生成器が付ける名前です（pipeline.py が .preview.png にします）。
            if (PatternThumbnails.FromFile(basePath + ".preview.png") is { } image)
            {
                _previewByPattern[drawnPattern] = (image, true, drawn, drawnWidth, drawnHeight);

                // 描いているあいだに別のパターンへ移った場合、結果はキャッシュだけして
                // いま見ている画面へ割り込ませません。
                if (string.Equals(drawnPattern, _pattern, StringComparison.OrdinalIgnoreCase)
                    && drawn == CurrentSignature())
                {
                    PreviewImage = image;
                    IsPreviewLive = true;
                    _shownSignature = drawn;
                    _shownWidth = drawnWidth;
                    _shownHeight = drawnHeight;
                    StatusText = "いまの条件で描きました。";
                }
            }
            else
            {
                StatusText = "描いた絵を読み込めませんでした。";
            }
        }
        catch (Exception ex)
        {
            StatusText = "この条件では描けませんでした。";
            Log = ex.Message;
            LogIsFailure = true;
        }
        finally
        {
            IsPreviewBusy = false;
            IsBusy = false;
            RaisePreviewState();
            if (_automaticPreviewPending) QueueAutomaticPreview();
        }
    }

    private string _outputFolder = "";
    public string OutputFolder { get => _outputFolder; set { if (Set(ref _outputFolder, value)) Revalidate(); } }

    private string _outputName = "";

    /// <summary>
    /// ファイル名の欄が、こちらで入れたままかどうかです。
    ///
    /// 条件を変えるたびに名前を作り直しますが、打ち直された名前まで書き換えると、
    /// 付けた名前が黙って消えます。手が入ったらそこで追従をやめます。
    /// 空に戻せば、また条件から作った名前が入ります（元へ戻す手立てが要ります）。
    /// </summary>
    private bool _outputNameIsAutomatic = true;

    public string OutputName
    {
        get => _outputName;
        set
        {
            if (!Set(ref _outputName, value)) return;
            _outputNameIsAutomatic = string.IsNullOrWhiteSpace(value);
            Revalidate();
        }
    }

    // --- 判定と表示 ---

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set { if (Set(ref _statusText, value)) Raise(nameof(ConditionNote)); }
    }

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

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        private set => Set(ref _isGenerating, value);
    }

    private bool _isPreviewBusy;
    public bool IsPreviewBusy
    {
        get => _isPreviewBusy;
        private set => Set(ref _isPreviewBusy, value);
    }

    private string _log = "";
    public string Log
    {
        get => _log;
        private set { if (Set(ref _log, value)) Raise(nameof(HasLog)); }
    }

    public bool HasLog => !string.IsNullOrWhiteSpace(_log);

    /// <summary>
    /// いま出ているログが、断られた理由かどうかです。
    ///
    /// ログは畳んで置きますが、**失敗したときだけは開いた状態で出します。**
    /// 断られた理由を畳んでしまうと、「生成できませんでした」の一行だけが残り、
    /// 何が起きたのかは自分で開きに行かないと分かりません。
    /// </summary>
    private bool _logIsFailure;
    public bool LogIsFailure { get => _logIsFailure; private set => Set(ref _logIsFailure, value); }

    /// <summary>
    /// 生成し終わったら、この窓を畳むかどうかです。
    ///
    /// この窓はメイン画面に所有させています（Owner）。所有された窓は必ず所有者より前面に来るので、
    /// メイン画面をいくら前面へ出しても、この窓の下からは出られません。
    /// 作った絵をすぐ見たいなら、こちらを畳むのが確実です。
    /// </summary>
    private bool _minimizeAfterGenerate = true;
    public bool MinimizeAfterGenerate
    {
        get => _minimizeAfterGenerate;
        set => Set(ref _minimizeAfterGenerate, value);
    }

    /// <summary>
    /// 窓を畳んでほしい、という合図です。
    /// 窓そのものを触るのは画面側の仕事なので、ここでは頼むだけにします。
    /// </summary>
    public Action? RequestMinimize { get; set; }

    /// <summary>
    /// 条件を見直して、成立しない理由と実行するコマンドを作り直します。
    ///
    /// ここで見るのは生成器から受け取った表だけです。判定そのものは持ちません。
    /// </summary>
    private void Revalidate()
    {
        // 名前を先に決めます。あとにすると、下に出るコマンドが1手ぶん古い名前を指します。
        RefreshOutputName();
        CommandLine = BuildCommandLine();
        Raise(nameof(CanGenerate));
        Raise(nameof(ConditionSummary));
        // つまみや寸法を触ると、出ている絵が「いまの条件」から外れます。そこを言い直します。
        RaisePreviewState();

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
        else if (PatternOptionRows.FirstOrDefault(r => r.HasProblem) is { } badRow)
        {
            // どの欄かを名前で言います。行の下にも同じ理由が出ますが、
            // 実行できない理由は 1 か所にまとまっていたほうが探さずに済みます。
            Problem = $"{badRow.Label}（{badRow.Name}）: {badRow.Problem}";
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
        Path.Combine(_outputFolder, string.IsNullOrWhiteSpace(_outputName) ? DerivedName() : _outputName.Trim());

    /// <summary>
    /// 条件から決まる名前です。
    ///
    /// **絵が変わる条件は名前にも出します。** 名前に出ない条件だけを変えて生成すると、
    /// 同じ名前になって前のものが消えます（寸法だけ変えたときに上書きされていたのは、
    /// 寸法が名前に入っていなかったためです）。
    ///
    /// 名前に出すものの選び方は <see cref="BuildArguments"/> と揃えます。
    /// matrix は Y'CbCr のときだけ、詰めは 10bit のときだけ効くので、
    /// 効かない条件は名前にも出しません。使われない値を名前に書くと嘘になります。
    ///
    /// パターン固有のつまみまでは入れません。数も長さも決まっていないので、
    /// 入れると名前が読めなくなります。そちらは同名を避ける (n) のほうで受けます。
    /// </summary>
    private string DerivedName()
    {
        var parts = new List<string>
        {
            _pattern,
            $"{_width}x{_height}",
            $"{_colorModel}{_subsampling.Replace(":", "")}",
            $"{_bitDepth}bit",
            _storage,
            _range,
        };
        if (_colorModel == "ycbcr") parts.Add(_matrix);
        if (_bitDepth == 10) parts.Add(_alignment);
        return string.Join("_", parts);
    }

    /// <summary>
    /// 生成器が作るファイルです。どれか1つでも残っていれば、その名前は使われているとみなします。
    /// RAWだけを消して manifest が残っている、という状態でも同じ名前を避けたいためです。
    /// </summary>
    private static readonly string[] GeneratedExtensions = [".raw", ".manifest.json", ".preview.png"];

    private bool NameIsTaken(string name)
    {
        if (string.IsNullOrWhiteSpace(_outputFolder)) return false;
        try
        {
            return GeneratedExtensions.Any(ext => File.Exists(Path.Combine(_outputFolder, name + ext)));
        }
        catch
        {
            // 場所が読めないなら、空いているかどうかも言えません。
            // ここで止めるより、生成そのものの失敗として理由を出したほうが分かります。
            return false;
        }
    }

    /// <summary>同じ名前が残っていたら、(1) から順に空いている番号を探します。</summary>
    private string UnusedName(string name)
    {
        if (!NameIsTaken(name)) return name;

        // 上限を置くのは、場所が読めないなどで必ず「使われている」と答える状態になったとき、
        // ここで回り続けないためです。見つからなければ元の名前を返し、判断は生成器へ渡します。
        for (var index = 1; index < 10000; index++)
        {
            var candidate = $"{name} ({index})";
            if (!NameIsTaken(candidate)) return candidate;
        }
        return name;
    }

    /// <summary>
    /// ファイル名の欄を、いまの条件から決まる名前に揃えます。
    ///
    /// 欄を空にしておいて中で名前を作ると、何ができるのかは押すまで分かりません。
    /// 出るものをそのまま出しておきます。
    /// 手が入っている欄には触りません（<see cref="_outputNameIsAutomatic"/>）。
    /// </summary>
    private void RefreshOutputName()
    {
        if (!_outputNameIsAutomatic) return;

        var name = UnusedName(DerivedName());
        if (name == _outputName) return;

        // 名乗り出るのは PropertyChanged だけにします。setter を通すと、
        // こちらが入れた名前を「手で直された」と数えてしまいます。
        _outputName = name;
        Raise(nameof(OutputName));
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

        // 触ったつまみだけを載せます。既定のままのものまで書き出すと、
        // 生成器側で既定を変えたときに古い値へ釘付けしてしまいます。
        foreach (var argument in ChangedPatternOptions())
        {
            arguments.Add("--pattern-option");
            arguments.Add(argument);
        }
        return arguments;
    }

    /// <summary>既定から動かしたつまみを ``名前=値`` の形で返します。</summary>
    internal IEnumerable<string> ChangedPatternOptions() =>
        PatternOptionRows.Select(row => row.Argument()).OfType<string>();

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

        // 押した時点で名前を確定させます。欄に入れたあとに同じ名前ができていることがあります
        // （前回の生成、別の窓、エクスプローラでの複製）。そのまま書くと前のものが黙って消えます。
        // 打ち直された名前でも、上書きだけはしません。
        var settled = UnusedName(string.IsNullOrWhiteSpace(_outputName) ? DerivedName() : _outputName.Trim());
        if (settled != _outputName)
        {
            // 実際に作る名前を欄にも出します。できたものと欄が食い違っていると、
            // どれが今作ったものなのかが分かりません。
            _outputName = settled;
            Raise(nameof(OutputName));
            CommandLine = BuildCommandLine();
        }

        IsBusy = true;
        IsGenerating = true;
        StatusText = "画像を生成中です…";
        _onGenerationStateChanged(true, StatusText);
        Log = "";
        LogIsFailure = false;
        try
        {
            Directory.CreateDirectory(_outputFolder);
            var (exitCode, stdout, stderr) = await GeneratorCatalog.RunAsync(_generatorCommand, BuildArguments(), null);
            Log = string.Join("\n", new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));
            LogIsFailure = exitCode != 0;

            if (exitCode == 0)
            {
                StatusText = $"生成しました: {Path.GetFileName(OutputBasePath)}";
                // メイン画面がRAWを読み込み、WPFの描画キューへ画像を渡し終えるまで待ちます。
                // 生成器プロセスの終了だけでプログレスを閉じると、空白のキャンバスが数秒残ります。
                await _onGenerated(OutputBasePath + ".manifest.json");

                // 畳むのは**絵が出てから**です。先に畳むと、下から現れるのは前の絵のままで、
                // 作ったものが出るところを見られません。
                // 失敗したときは畳みません。理由がこの窓にあるのに隠すことになります。
                if (_minimizeAfterGenerate) RequestMinimize?.Invoke();
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
            LogIsFailure = true;
        }
        finally
        {
            IsGenerating = false;
            IsBusy = false;
            // いま作ったものが場所を埋めたので、次に出る名前を出し直します。
            // 同じ条件で続けて押しても、前のものを踏まずに (1) (2) と増えます。
            Revalidate();
            _onGenerationStateChanged(false, StatusText);
        }
    }
}

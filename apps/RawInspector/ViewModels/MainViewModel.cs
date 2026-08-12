using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RawInspector.Decoding;
using RawInspector.Models;

namespace RawInspector.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    // 上のほうを厚くしてあります。ここで見たいものは 1 画素の単位で決まっているためです。
    // hatch の 1 画素縞、linepairs の線幅 1、digitalcard の目盛りと 1 画素おきの縞、
    // そして 4:2:0 の色差ブロック（2 x 2）。100% では画面の 1 点なので、
    // 出ているのか出ていないのかを目で確かめられません。
    // 1600% なら 1 画素が 16 点、色差ブロックが 32 点角になり、そこで初めて数えられます。
    // マウスで 1 画素を狙うときも、的が 1 点か 16 点かで確実さが変わります。
    private static readonly double[] ScaleSteps =
    [
        10, 20, 25, 30, 40, 50, 75, 100, 125, 150, 175, 200, 250, 300, 350, 400,
        500, 600, 800, 1000, 1200, 1600,
    ];

    private const double MinScale = 10;
    private const double MaxScale = 1600;

    private static readonly string LastFolderFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RawInspector", "last-folder.txt");

    private readonly List<ManifestEntryViewModel> _entries = [];

    private RawImage? _rawImage;
    private string? _currentManifestPath;
    private string? _currentRawPath;

    public MainViewModel()
    {
        OpenFolderCommand = new RelayCommand(OpenFolder);
        RefreshFolderCommand = new RelayCommand(RefreshFolder, () => _currentFolder is not null);
        SelectOutputFolderCommand = new RelayCommand(SelectOutputFolder);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        SaveImageCommand = new RelayCommand<string>(SaveImage, _ => HasPreview);
        SaveRawCopyCommand = new RelayCommand(SaveRawCopy, () => _currentRawPath is not null);
        ZoomInCommand = new RelayCommand(() => ScalePercent = NextStep(ScalePercent));
        ZoomOutCommand = new RelayCommand(() => ScalePercent = PreviousStep(ScalePercent));
        ActualSizeCommand = new RelayCommand(() => ScalePercent = 100);
        CopySampleCommand = new RelayCommand<string>(CopySample, _ => HasSample);
        SaveSelectedFormatCommand = new RelayCommand(SaveSelectedFormat, () => HasPreview);
        SaveAllFormatsCommand = new RelayCommand(SaveAllFormats, () => HasPreview);
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        ResetInterpretationCommand = new RelayCommand(ResetInterpretation, () => HasPreview);
        Dashboard = new DashboardViewModel(folder =>
        {
            LoadFolder(folder);
            ShowDashboard = false;
        });
        ShowDashboardCommand = new RelayCommand(OpenDashboard);
        ToggleFullScreenCommand = new RelayCommand(() => IsFullScreen = !IsFullScreen, () => HasPreview);
        ExitFullScreenCommand = new RelayCommand(() => IsFullScreen = false, () => IsFullScreen);
        ExpandAllCommand = new RelayCommand(() => SetAllGroupsExpanded(true));
        CollapseAllCommand = new RelayCommand(() => SetAllGroupsExpanded(false));
        _selectedImageFormat = ImageFormats[0];

        ColorModelFilters = ["すべて", "RGB", "YUV / YCbCr"];
        SizeFilters = ["すべて"];
        _colorModelFilter = ColorModelFilters[0];
        _sizeFilter = SizeFilters[0];
    }

    // --- 最初に出す画面 ---
    //
    // フォルダを開いていないうちは、一覧もプレビューも空のままです。
    // そこで「何がどこにあるか」「生成器へ繋がっているか」を先に出します。
    // 一覧を読み込んだら引っ込めますが、状態を見たくなるのは起動時とはかぎらないので、
    // ホームでいつでも戻れるようにします。戻っても読み込んだ一覧は消しません。

    public DashboardViewModel Dashboard { get; }

    private bool _showDashboard = true;
    public bool ShowDashboard
    {
        get => _showDashboard;
        private set => Set(ref _showDashboard, value);
    }

    public RelayCommand ShowDashboardCommand { get; }
    public RelayCommand ToggleFullScreenCommand { get; }
    public RelayCommand ExitFullScreenCommand { get; }

    // --- プレビューの全画面 ---
    //
    // 一覧も条件欄もツールバーも畳んで、絵だけにします。
    // 4K を等倍で見たいときや、パターン同士を並べて見比べたいときに、
    // 周りの枠が要らなくなります。F11 と Esc で出入りします。

    private bool _isFullScreen;
    public bool IsFullScreen
    {
        get => _isFullScreen;
        set
        {
            if (!Set(ref _isFullScreen, value)) return;
            Raise(nameof(IsNotFullScreen));
            ExitFullScreenCommand.RaiseCanExecuteChanged();
            StatusText = value ? "全画面表示です。F11 か Esc で戻ります。" : "全画面表示を終了しました。";
        }
    }

    public bool IsNotFullScreen => !_isFullScreen;

    /// <summary>最初の画面を出します。数え直しもここで走らせます。</summary>
    private void OpenDashboard()
    {
        ShowDashboard = true;
        _ = Dashboard.RefreshAsync(_currentFolder ?? LoadLastFolder());
    }

    // --- コマンド ---

    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand RefreshFolderCommand { get; }
    public RelayCommand SelectOutputFolderCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand<string> SaveImageCommand { get; }
    public RelayCommand SaveRawCopyCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ActualSizeCommand { get; }
    public RelayCommand<string> CopySampleCommand { get; }
    public RelayCommand SaveSelectedFormatCommand { get; }
    public RelayCommand SaveAllFormatsCommand { get; }
    public RelayCommand ResetFiltersCommand { get; }
    public RelayCommand ResetInterpretationCommand { get; }
    public RelayCommand ExpandAllCommand { get; }
    public RelayCommand CollapseAllCommand { get; }

    // --- プレビューの操作モード ---

    /// <summary>
    /// 矢印は通常のカーソル、手のひらはドラッグで移動、虫眼鏡はホイールだけで拡大縮小します
    /// （PDFビューアと同じ考え方）。既定を矢印にしているのは、
    /// ドラッグやホイールの意味が勝手に変わっていない状態から始めるためです。
    /// </summary>
    private PreviewTool _tool = PreviewTool.Arrow;
    public PreviewTool Tool
    {
        get => _tool;
        set
        {
            if (!Set(ref _tool, value)) return;
            Raise(nameof(IsArrowTool));
            Raise(nameof(IsHandTool));
            Raise(nameof(IsZoomTool));
        }
    }

    public bool IsArrowTool
    {
        get => _tool == PreviewTool.Arrow;
        set { if (value) Tool = PreviewTool.Arrow; }
    }

    public bool IsHandTool
    {
        get => _tool == PreviewTool.Hand;
        set { if (value) Tool = PreviewTool.Hand; }
    }

    public bool IsZoomTool
    {
        get => _tool == PreviewTool.Zoom;
        set { if (value) Tool = PreviewTool.Zoom; }
    }

    // --- 圧縮画像の保存形式 ---

    public IReadOnlyList<ImageFormatOption> ImageFormats { get; } =
    [
        new("PNG（可逆・既定）", "png"),
        new("JPEG（非可逆）", "jpg"),
        new("TIFF（可逆）", "tiff"),
        new("BMP（無圧縮）", "bmp"),
        new("GIF（256色）", "gif"),
    ];

    private ImageFormatOption? _selectedImageFormat;
    public ImageFormatOption? SelectedImageFormat
    {
        get => _selectedImageFormat;
        set => Set(ref _selectedImageFormat, value);
    }

    // --- 表示条件（manifestの値を初期値に、あとから変えられる） ---
    //
    // 「間違ったmatrixで見るとどう狂うか」「色差の戻し方で見え方がどう変わるか」を
    // 同じRAWのまま出せるようにするための切り替えです。
    // 変えるのは**読み方**だけで、RAWには一切触りません。

    public IReadOnlyList<string> MatrixOptions { get; } = ["bt601", "bt709", "bt2020"];
    public IReadOnlyList<string> RangeOptions { get; } = ["full", "limited"];

    private string _selectedMatrix = "bt709";
    public string SelectedMatrix
    {
        get => _selectedMatrix;
        set { if (Set(ref _selectedMatrix, value)) Rerender(); }
    }

    private string _selectedRange = "full";
    public string SelectedRange
    {
        get => _selectedRange;
        set { if (Set(ref _selectedRange, value)) Rerender(); }
    }

    private ChannelMask _channels = ChannelMask.All;
    public ChannelMask Channels
    {
        get => _channels;
        set
        {
            if (!Set(ref _channels, value)) return;
            Raise(nameof(ShowFirstChannel));
            Raise(nameof(ShowSecondChannel));
            Raise(nameof(ShowThirdChannel));
            Raise(nameof(CanUseRawCodeGray));
            Rerender();
        }
    }

    private void SetChannel(ChannelMask flag, bool on) =>
        Channels = on ? _channels | flag : _channels & ~flag;

    public bool ShowFirstChannel
    {
        get => _channels.HasFlag(ChannelMask.First);
        set => SetChannel(ChannelMask.First, value);
    }

    public bool ShowSecondChannel
    {
        get => _channels.HasFlag(ChannelMask.Second);
        set => SetChannel(ChannelMask.Second, value);
    }

    public bool ShowThirdChannel
    {
        get => _channels.HasFlag(ChannelMask.Third);
        set => SetChannel(ChannelMask.Third, value);
    }

    /// <summary>
    /// コード値をそのまま濃淡にするかどうかです。
    /// 成分を1つだけ選んでいるときにしか意味を持たないので、それ以外では使えなくします。
    /// 黙って別の意味に切り替わるより、押せないほうが誤解がありません。
    /// </summary>
    private bool _rawCodeGray;
    public bool RawCodeGray
    {
        get => _rawCodeGray;
        set { if (Set(ref _rawCodeGray, value)) Rerender(); }
    }

    public bool CanUseRawCodeGray => BitOperations.PopCount((uint)_channels) == 1;

    /// <summary>成分ボタンの表示名です。色モデルで呼び名が変わります。</summary>
    private string _firstChannelLabel = "R";
    public string FirstChannelLabel { get => _firstChannelLabel; private set => Set(ref _firstChannelLabel, value); }

    private string _secondChannelLabel = "G";
    public string SecondChannelLabel { get => _secondChannelLabel; private set => Set(ref _secondChannelLabel, value); }

    private string _thirdChannelLabel = "B";
    public string ThirdChannelLabel { get => _thirdChannelLabel; private set => Set(ref _thirdChannelLabel, value); }

    private ChromaUpsample _upsample = ChromaUpsample.Nearest;
    public ChromaUpsample Upsample
    {
        get => _upsample;
        set
        {
            if (!Set(ref _upsample, value)) return;
            Raise(nameof(IsNearestUpsample));
            Raise(nameof(IsBilinearUpsample));
            Rerender();
        }
    }

    public bool IsNearestUpsample { get => _upsample == ChromaUpsample.Nearest; set { if (value) Upsample = ChromaUpsample.Nearest; } }
    public bool IsBilinearUpsample { get => _upsample == ChromaUpsample.Bilinear; set { if (value) Upsample = ChromaUpsample.Bilinear; } }

    /// <summary>Y'CbCr のときだけ matrix / range / 色差の戻し方が効きます。</summary>
    private bool _isYcbcrSelected;
    public bool IsYcbcrSelected { get => _isYcbcrSelected; private set => Set(ref _isYcbcrSelected, value); }

    /// <summary>色差が間引かれている形式のときだけ、戻し方の切り替えに意味があります。</summary>
    private bool _hasSubsampledChroma;
    public bool HasSubsampledChroma { get => _hasSubsampledChroma; private set => Set(ref _hasSubsampledChroma, value); }

    /// <summary>表示条件がmanifestの記録どおりかどうか。違うときは画面で警告します。</summary>
    public bool IsInterpretationOverridden =>
        _rawImage is not null && IsYcbcrSelected
        && (!ManifestInfo.Same(_selectedMatrix, _rawImage.DefaultInterpretation.Matrix)
            || !ManifestInfo.Same(_selectedRange, _rawImage.DefaultInterpretation.Range));

    public string OverrideWarning => IsInterpretationOverridden
        ? $"manifestの記録（{_rawImage!.DefaultInterpretation.Matrix} / {_rawImage.DefaultInterpretation.Range}）とは"
          + $"違う条件で表示しています。この見え方はRAWの中身ではなく、読み方の違いによるものです。"
        : "";

    // --- プレビューの作り方 ---
    //
    // 画面に出ている絵はRAWそのものではありません。コード値を変換した結果です。
    // どの条件でどう変換したのかを書いておかないと、
    // 「RAWがこう見える」と読み違えます。

    private string _previewRecipe = "";
    public string PreviewRecipe { get => _previewRecipe; private set => Set(ref _previewRecipe, value); }

    /// <summary>
    /// どのRAWでも変わらない注意なので、作り方の説明ではなくステータスバーへ常時出します。
    /// 毎回同じ文が説明の末尾にあると、その手前の「今回だけの条件」まで読み飛ばされます。
    /// </summary>
    public string BitDepthNote =>
        "表示は常に8bit。10bitのRAWは画面で階調が落ちます（元の階調はコード値で確認）";

    private void UpdatePreviewRecipe()
    {
        if (_rawImage is null || _currentManifest is null)
        {
            PreviewRecipe = "RAWを選ぶと、その絵をどう作っているかをここに出します。";
            return;
        }

        var m = _currentManifest;
        var lines = new List<string>
        {
            "画面に出ているのはRAWそのものではありません。",
            "RAWのコード値を次の順で変換した結果を表示しています。",
            "",
            $"1. 格納形式 {m.Storage} から {(m.IsYcbcr ? "Y' / Cb / Cr" : "R / G / B")} のコード値を取り出す"
            + $"（{m.BitDepth}bit: 0-{_rawImage.MaxCode}）",
        };

        var step = 2;

        if (_rawImage.HasSubsampledChroma)
        {
            var how = _upsample == ChromaUpsample.Nearest
                ? "最近傍（格納されている値をそのまま複製）"
                : "バイリニア（隣り合うサンプルの間を線形補間）";
            lines.Add($"{step++}. 間引かれた色差を輝度と同じ密度へ戻す（{m.Subsampling} / {how}）");
        }

        if (m.IsYcbcr)
        {
            var shift = 1 << (_rawImage.BitDepth - 8);
            if (ManifestInfo.Same(_selectedRange, "limited"))
            {
                lines.Add($"{step++}. range=limited として正規化する");
                lines.Add($"      Y  = (Y' - {16 * shift}) / {219 * shift}");
                lines.Add($"      Cb = (Cb - {128 * shift}) / {224 * shift}");
                lines.Add($"      Cr = (Cr - {128 * shift}) / {224 * shift}");
            }
            else
            {
                lines.Add($"{step++}. range=full として正規化する");
                lines.Add($"      Y  = Y' / {_rawImage.MaxCode}");
                lines.Add($"      Cb = (Cb - {128 * shift}) / {_rawImage.MaxCode}");
                lines.Add($"      Cr = (Cr - {128 * shift}) / {_rawImage.MaxCode}");
            }

            var (kr, kb) = new ColorInterpretation(_selectedMatrix, _selectedRange).Coefficients;
            var kg = 1.0 - kr - kb;
            lines.Add($"{step++}. matrix={_selectedMatrix} でRGBへ戻す"
                + $"（Kr={kr}, Kb={kb}, Kg={kg:0.####}）");
            lines.Add("      R = Y + 2(1-Kr)・Cr");
            lines.Add("      B = Y + 2(1-Kb)・Cb");
            lines.Add("      G = (Y - Kr・R - Kb・B) / Kg");
        }
        else
        {
            lines.Add($"{step++}. コード値を 0-1 へ正規化する（値 / {_rawImage.MaxCode}）");
        }

        lines.Add($"{step++}. 0-1 に丸めてから 8bit（0-255）へ量子化し、画面へ出す");

        if (_channels != ChannelMask.All)
        {
            var kept = ChannelNames(_channels);
            var dropped = ChannelNames(ChannelMask.All & ~_channels);
            lines.Add("");
            if (CurrentOptions.UseRawCodeGray)
            {
                lines.Add($"※ 成分「{kept}」のコード値を、そのまま濃淡にしています。");
                lines.Add("   色変換は通していません。画面の明るさ = コード値 / 最大コード値です。");
            }
            else if (_channels == ChannelMask.None)
            {
                lines.Add("※ 成分をひとつも選んでいないので、すべて中立値です（平坦な絵になります）。");
            }
            else
            {
                lines.Add($"※ 成分「{kept}」だけを使い、「{dropped}」は中立値に置き換えてから変換しています。");
                lines.Add($"   中立値: {NeutralNote()}");
                lines.Add("   成分どうしの関係を保ったまま抜くため、落とさずに置き換えています。");
            }
        }

        // 縮小しているときだけ出します。等倍より上は画素が増えるだけなので、
        // 見えているものと入っているものはずれません。
        if (_scalePercent < 100)
        {
            lines.Add("");
            lines.Add($"※ いまは {_scalePercent:0.#}% で縮小して出しています。");
            lines.Add("   最近傍で間引いて表示するので、1画素の線や点は画面から消えることがあります。");
            lines.Add("   消えているのは表示のせいで、RAWから無くなったわけではありません。等倍以上で確かめてください。");
        }

        if (IsInterpretationOverridden)
        {
            lines.Add("");
            lines.Add($"※ matrix / range は manifest の記録"
                + $"（{_rawImage.DefaultInterpretation.Matrix} / {_rawImage.DefaultInterpretation.Range}）"
                + "とは違う値にしています。");
        }

        PreviewRecipe = string.Join("\n", lines);
    }

    /// <summary>選ばれている成分の呼び名を並べます（色モデルで呼び名が変わります）。</summary>
    private string ChannelNames(ChannelMask mask)
    {
        var names = new List<string>();
        if (mask.HasFlag(ChannelMask.First)) names.Add(FirstChannelLabel);
        if (mask.HasFlag(ChannelMask.Second)) names.Add(SecondChannelLabel);
        if (mask.HasFlag(ChannelMask.Third)) names.Add(ThirdChannelLabel);
        return names.Count == 0 ? "なし" : string.Join(" + ", names);
    }

    /// <summary>中立値が成分ごとに違うので、実際に入れている値を書き出します。</summary>
    private string NeutralNote()
    {
        if (_rawImage is null) return "-";
        if (!IsYcbcrSelected) return "RGBは加算なので 0";

        var shift = 1 << (_rawImage.BitDepth - 8);
        var luma = _selectedRange == "limited"
            ? (int)Math.Round(16.0 * shift + 219.0 * shift * 0.5)
            : (int)Math.Round(_rawImage.MaxCode * 0.5);
        return $"Y'は{luma}（0.5にあたるコード値）、Cb/Crは{128 * shift}（振れ0の中央）";
    }

    private ManifestInfo? _currentManifest;

    /// <summary>
    /// RAWに記録されている主要な条件です。項目名と値を分けて持ちます。
    /// 1本の文字列にすると、どこまでが項目名でどこからが値なのかが読み取れないためです。
    /// </summary>
    public ObservableCollection<SummaryItem> RawSummary { get; } = [];

    private void BuildRawSummary(ManifestInfo manifest)
    {
        RawSummary.Clear();
        RawSummary.Add(new SummaryItem("色モデル", manifest.ColorModel ?? "-"));
        RawSummary.Add(new SummaryItem("色差サブサンプリング", manifest.Subsampling ?? "-"));
        RawSummary.Add(new SummaryItem("ビット深度", $"{manifest.BitDepth}bit"));
        RawSummary.Add(new SummaryItem("格納形式", manifest.Storage ?? "-"));
        RawSummary.Add(new SummaryItem("画像サイズ", ResolutionNames.Describe(manifest.Width, manifest.Height)));
        // 画素数の比です。映したときの形の比ではありません（画素が正方形のときだけ一致します）。
        RawSummary.Add(new SummaryItem("画素数の比", AspectRatio.Describe(manifest.Width, manifest.Height)));
    }

    private PreviewRenderOptions CurrentOptions =>
        new(new ColorInterpretation(_selectedMatrix, _selectedRange), _channels, _upsample, _rawCodeGray);

    /// <summary>表示条件を manifest の記録へ戻します。</summary>
    private void ResetInterpretation()
    {
        if (_rawImage is null) return;
        ApplyDefaultsFrom(_rawImage);
        Rerender();
        StatusText = "表示条件をmanifestの記録どおりに戻しました。";
    }

    private void ApplyDefaultsFrom(RawImage image)
    {
        var defaults = image.DefaultInterpretation;
        _selectedMatrix = defaults.Matrix;
        _selectedRange = defaults.Range;
        _channels = ChannelMask.All;
        _rawCodeGray = false;
        _upsample = ChromaUpsample.Nearest;

        var (first, second, third) = image.ChannelLabels;
        FirstChannelLabel = first;
        SecondChannelLabel = second;
        ThirdChannelLabel = third;
        IsYcbcrSelected = image.ChannelLabels.First == "Y'";
        HasSubsampledChroma = image.HasSubsampledChroma;

        Raise(nameof(SelectedMatrix));
        Raise(nameof(SelectedRange));
        Raise(nameof(ChromaBlockWidth));
        Raise(nameof(ChromaBlockHeight));
        Raise(nameof(IsPixelGridVisible));
        Raise(nameof(Channels));
        Raise(nameof(ShowFirstChannel));
        Raise(nameof(ShowSecondChannel));
        Raise(nameof(ShowThirdChannel));
        Raise(nameof(RawCodeGray));
        Raise(nameof(CanUseRawCodeGray));
        Raise(nameof(IsNearestUpsample));
        Raise(nameof(IsBilinearUpsample));
    }

    /// <summary>表示条件を変えたときに、同じRAWから絵を作り直します。</summary>
    private void Rerender()
    {
        Raise(nameof(IsInterpretationOverridden));
        Raise(nameof(OverrideWarning));
        UpdatePreviewRecipe();

        if (_rawImage is null) return;

        var pixels = _rawImage.ToBgra32(CurrentOptions);
        var bitmap = BitmapSource.Create(
            _rawImage.Width, _rawImage.Height, 96, 96, PixelFormats.Bgra32, null, pixels, _rawImage.Width * 4);
        bitmap.Freeze();

        // 倍率とスクロール位置は保ったまま中身だけ差し替えます。
        // 条件を切り替えて見比べるので、そのたびに全体表示へ戻ると比較になりません。
        PreviewImage = bitmap;
    }

    // --- パターンの意図 ---

    private PatternGuide _patternGuide = PatternGuide.For(null);
    public PatternGuide PatternGuide { get => _patternGuide; private set => Set(ref _patternGuide, value); }

    /// <summary>ビューへ「全体表示に戻したい」と伝えます（表示領域の大きさはビューしか知らないため）。</summary>
    public event EventHandler? FitRequested;

    public void RequestFit() => FitRequested?.Invoke(this, EventArgs.Empty);

    // --- 一覧 ---

    public ObservableCollection<PatternGroupViewModel> Groups { get; } = [];

    public IReadOnlyList<string> ColorModelFilters { get; }

    public ObservableCollection<string> SizeFilters { get; }

    private string _colorModelFilter;
    public string ColorModelFilter
    {
        get => _colorModelFilter;
        set { if (Set(ref _colorModelFilter, value)) RebuildGroups(); }
    }

    private string _sizeFilter;
    public string SizeFilter
    {
        get => _sizeFilter;
        set { if (Set(ref _sizeFilter, value)) RebuildGroups(); }
    }

    private ManifestEntryViewModel? _selectedEntry;
    public ManifestEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set { if (Set(ref _selectedEntry, value)) LoadSelected(); }
    }

    // --- フォルダ ---

    private string _folderText = "フォルダ未選択";
    public string FolderText { get => _folderText; private set => Set(ref _folderText, value); }

    private string? _outputFolder;
    public string? OutputFolder
    {
        get => _outputFolder;
        private set { if (Set(ref _outputFolder, value)) Raise(nameof(OutputFolderText)); }
    }

    public string OutputFolderText => _outputFolder is null ? "出力先: 未指定" : $"出力先: {_outputFolder}";

    // --- 表示 ---

    private string _patternBadge = "パターン名: 未選択";
    public string PatternBadge { get => _patternBadge; private set => Set(ref _patternBadge, value); }

    private string _previewTitle = "RAWファイルを選択してください";
    public string PreviewTitle { get => _previewTitle; private set => Set(ref _previewTitle, value); }

    private string _statusText = "「フォルダを開く」で、RAWとmanifestのあるフォルダを指定してください。";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private BitmapSource? _previewImage;
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (!Set(ref _previewImage, value)) return;
            Raise(nameof(HasPreview));
            Raise(nameof(PreviewPixelWidth));
            Raise(nameof(PreviewPixelHeight));
            Raise(nameof(ScaledWidth));
            Raise(nameof(ScaledHeight));
            SaveImageCommand.RaiseCanExecuteChanged();
            SaveSelectedFormatCommand.RaiseCanExecuteChanged();
            SaveAllFormatsCommand.RaiseCanExecuteChanged();
            SaveRawCopyCommand.RaiseCanExecuteChanged();
            ResetInterpretationCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasPreview => _previewImage is not null;

    public int PreviewPixelWidth => _previewImage?.PixelWidth ?? 0;
    public int PreviewPixelHeight => _previewImage?.PixelHeight ?? 0;

    private string _unsupportedMessage = "";
    public string UnsupportedMessage { get => _unsupportedMessage; private set => Set(ref _unsupportedMessage, value); }

    // --- 倍率 ---

    private double _scalePercent = 100;
    public double ScalePercent
    {
        get => _scalePercent;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 2), MinScale, MaxScale);
            if (!Set(ref _scalePercent, clamped)) return;
            Raise(nameof(ScaledWidth));
            Raise(nameof(ScaledHeight));
            Raise(nameof(ScaleText));
            Raise(nameof(PointsPerPixel));
            Raise(nameof(CanShowPixelGrid));
            Raise(nameof(IsPixelGridVisible));
            // 縮小しているときは絵から画素が落ちます。作り方の説明にもそれを出します。
            UpdatePreviewRecipe();
        }
    }

    public string ScaleText => $"{_scalePercent:0.#} %";

    public double ScaledWidth => PreviewPixelWidth * _scalePercent / 100.0;
    public double ScaledHeight => PreviewPixelHeight * _scalePercent / 100.0;

    // --- 画素グリッド ---
    //
    // 拡大しても、隣り合う画素が同じ値だと境目が見えません。
    // 「1画素の線」なのか「2画素の帯」なのかは、境目が引かれて初めて数えられます。
    //
    // 色差ブロックの線を別に引くのは、そちらのほうが読み取りたいことが多いためです。
    // 4:2:0 なら 2 x 2 の画素が同じ色差を共有しています。その枠を出せば、
    // 「間引かれた」という言葉が画面の上のどの範囲を指すのかが目で分かります。

    /// <summary>1画素が画面で何点になるかです。</summary>
    public double PointsPerPixel => _scalePercent / 100.0;

    /// <summary>
    /// グリッドを引けるだけ拡大されているかです。
    /// 1画素が細かいうちに線を引くと、線のほうが画素より太くなって絵が読めなくなります。
    /// </summary>
    public bool CanShowPixelGrid => PointsPerPixel >= PixelGrid.MinPointsPerPixel;

    private bool _showPixelGrid;
    public bool ShowPixelGrid
    {
        get => _showPixelGrid;
        set { if (Set(ref _showPixelGrid, value)) Raise(nameof(IsPixelGridVisible)); }
    }

    /// <summary>実際に線を出すかどうかです（入れていても、拡大が足りなければ出しません）。</summary>
    public bool IsPixelGridVisible => _showPixelGrid && CanShowPixelGrid;

    public int ChromaBlockWidth => _rawImage?.ChromaBlockWidth ?? 1;
    public int ChromaBlockHeight => _rawImage?.ChromaBlockHeight ?? 1;

    // --- ピクセルプローブ ---

    // 表示は項目ごとに別のプロパティへ分けています。
    // 1本の文字列にすると、カーソルを動かすたびに文字数が変わって桁位置や帯の高さが動き、
    // その上のプレビュー枠までずれてしまうためです。
    //
    // Hover* と Sample* は役割が違います。
    //   Hover*  … いまカーソルの下にある値。下のバーに出し、プレビューから外れたら消します。
    //   Sample* … 最後に指した値。右クリックメニューに出し、外れても残します。
    // 分けているのは、右クリックでメニューへマウスを移した瞬間に MouseLeave が飛ぶためです。
    // 1組にすると、メニューを開いた瞬間に中身が全部「—」へ戻ってしまいます。

    private const string Placeholder = "—";

    private string _hoverPositionText = Placeholder;
    public string HoverPositionText { get => _hoverPositionText; private set => Set(ref _hoverPositionText, value); }

    private string _hoverCodeText = Placeholder;
    public string HoverCodeText { get => _hoverCodeText; private set => Set(ref _hoverCodeText, value); }

    private string _hoverCodeRangeText = "";
    public string HoverCodeRangeText { get => _hoverCodeRangeText; private set => Set(ref _hoverCodeRangeText, value); }

    private string _hoverHexText = Placeholder;
    public string HoverHexText { get => _hoverHexText; private set => Set(ref _hoverHexText, value); }

    private string _hoverRgbText = Placeholder;
    public string HoverRgbText { get => _hoverRgbText; private set => Set(ref _hoverRgbText, value); }

    private Brush _hoverSwatch = Brushes.Transparent;
    public Brush HoverSwatch { get => _hoverSwatch; private set => Set(ref _hoverSwatch, value); }

    private string _samplePositionText = Placeholder;
    public string SamplePositionText { get => _samplePositionText; private set => Set(ref _samplePositionText, value); }

    /// <summary>RAWから読んだ生のコード値です。変換していません。</summary>
    private string _sampleCodeText = Placeholder;
    public string SampleCodeText { get => _sampleCodeText; private set => Set(ref _sampleCodeText, value); }

    /// <summary>matrixとrangeを適用し、8bitへ丸めた変換後の値です。</summary>
    private string _sampleHexText = Placeholder;
    public string SampleHexText { get => _sampleHexText; private set => Set(ref _sampleHexText, value); }

    private string _sampleRgbText = Placeholder;
    public string SampleRgbText { get => _sampleRgbText; private set => Set(ref _sampleRgbText, value); }

    private string _sampleFullText = Placeholder;
    public string SampleFullText { get => _sampleFullText; private set => Set(ref _sampleFullText, value); }

    /// <summary>直近に指した画素です。右クリックからのコピーはこれを使います。</summary>
    private PixelSample? _lastSample;

    public bool HasSample => _lastSample is not null;

    /// <summary>プレビュー上の画素を指したときに呼びます。</summary>
    public void UpdateProbe(int x, int y)
    {
        if (_rawImage is null || x < 0 || y < 0 || x >= _rawImage.Width || y >= _rawImage.Height)
        {
            ClearHover();
            return;
        }

        var sample = _rawImage.Sample(x, y, CurrentOptions);
        _lastSample = sample;
        Raise(nameof(HasSample));
        CopySampleCommand.RaiseCanExecuteChanged();

        HoverPositionText = sample.PositionText;
        HoverCodeText = sample.CodeText;
        HoverCodeRangeText = $"0-{sample.MaxCode}";
        HoverHexText = sample.Hex;
        HoverRgbText = sample.RgbText;
        HoverSwatch = new SolidColorBrush(Color.FromRgb(sample.R, sample.G, sample.B));

        SamplePositionText = sample.PositionText;
        SampleCodeText = sample.CodeText;
        SampleHexText = sample.Hex;
        SampleRgbText = sample.RgbText;
        SampleFullText = sample.FullText;
    }

    /// <summary>
    /// カーソルがプレビューから外れたときに呼びます。下のバーの表示だけを消します。
    /// Sample*（コピー対象）は残します。右クリックでメニューへマウスを移すとここへ来るためです。
    /// </summary>
    public void ClearHover()
    {
        HoverPositionText = Placeholder;
        HoverCodeText = Placeholder;
        HoverCodeRangeText = "";
        HoverHexText = Placeholder;
        HoverRgbText = Placeholder;
        HoverSwatch = Brushes.Transparent;
    }

    /// <summary>
    /// 直近の画素を忘れます。呼ぶのは別のmanifestを開いたときだけです。
    /// </summary>
    private void ForgetSample()
    {
        _lastSample = null;
        SamplePositionText = Placeholder;
        SampleCodeText = Placeholder;
        SampleHexText = Placeholder;
        SampleRgbText = Placeholder;
        SampleFullText = Placeholder;
        ClearHover();
        Raise(nameof(HasSample));
        CopySampleCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 直近に指した画素の値をクリップボードへ入れます。
    /// 見て確かめた値をそのまま記事やテストコードへ持っていけるようにするためです。
    /// </summary>
    private void CopySample(string kind)
    {
        if (_lastSample is not { } s) return;

        // 文字列の組み立ては PixelSample 側に置いてあります。
        // WPFに依存しないので、コピーされる中身をそのまま検証できます。
        var text = kind switch
        {
            "hex" => s.Hex,
            "rgb" => s.RgbText,
            "code" => s.CodeText,
            "xy" => s.PositionText,
            _ => s.FullText,
        };

        try
        {
            Clipboard.SetText(text);
            StatusText = $"コピーしました: {text}";
        }
        catch (Exception ex)
        {
            // 他のアプリがクリップボードを掴んでいると失敗することがあります。
            StatusText = $"クリップボードへコピーできませんでした: {ex.Message}";
        }
    }

    // --- パラメータ ---

    public ObservableCollection<ParameterRow> Parameters { get; } = [];

    private ParameterRow? _selectedParameter;
    public ParameterRow? SelectedParameter
    {
        get => _selectedParameter;
        set { if (Set(ref _selectedParameter, value)) Raise(nameof(ParameterHelp)); }
    }

    public string ParameterHelp => _selectedParameter?.Help
        ?? "項目を選ぶと、RAWの解釈にその値がどう効くかを表示します。";

    // --- 起動時 ---

    /// <summary>前回のフォルダの場所だけを返します（読み込みません）。</summary>
    private static string? LoadLastFolder()
    {
        try
        {
            if (!File.Exists(LastFolderFile)) return null;
            var folder = File.ReadAllText(LastFolderFile).Trim();
            return Directory.Exists(folder) ? folder : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 起動時の処理です。前回のフォルダは読み込んでおきますが、
    /// 最初に見せるのは最初の画面のほうです。
    /// いきなり一覧が出ても、その素材が何なのか・全部揃っているのかが分かりません。
    /// </summary>
    public void RestoreLastFolder()
    {
        var folder = LoadLastFolder();
        try
        {
            if (folder is not null) LoadFolder(folder);
        }
        catch (Exception ex)
        {
            StatusText = $"前回のフォルダを復元できませんでした: {ex.Message}";
        }

        ShowDashboard = true;
        _ = Dashboard.RefreshAsync(folder);
    }

    private void OpenFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "RAWとmanifestのあるフォルダを選んでください",
            InitialDirectory = _outputFolder ?? "",
        };
        if (dialog.ShowDialog() != true) return;
        LoadFolder(dialog.FolderName);
        ShowDashboard = false;
    }

    /// <summary>
    /// いま開いているフォルダを読み直します。
    ///
    /// 生成し直すたびに開き直すのは手数が多いので、開いたまま更新できるようにします。
    /// 前と同じRAWを選び直し、表示倍率も保ちます。作り直す前後で見比べるための機能なので、
    /// 倍率が戻ってしまうと比較になりません。
    /// </summary>
    private void RefreshFolder()
    {
        if (_currentFolder is null) return;

        var keepPath = _currentManifestPath;
        var keepScale = _scalePercent;
        LoadFolder(_currentFolder, keepPath, keepScale);
    }

    private string? _currentFolder;

    /// <summary>true のあいだ、RAWを開いても全体表示へ戻しません。</summary>
    private bool _keepScaleOnLoad;

    /// <summary>
    /// 生成の窓へ渡す既定の出力先です。
    /// いま開いているフォルダをそのまま使います。作ったものがその場で一覧へ並ぶためです。
    /// まだ何も開いていなければ、出力先か作業フォルダにします。
    /// </summary>
    public string GeneratedFolder =>
        _currentFolder ?? OutputFolder ?? Environment.CurrentDirectory;

    /// <summary>
    /// 生成された manifest を一覧へ取り込みます。
    ///
    /// 開いているフォルダの中なら読み直して選び、外なら**そのフォルダへ移ります**。
    /// 作ったものが見えないままだと、生成できたのかどうかが分かりません。
    /// </summary>
    public void AdoptGenerated(string manifestPath)
    {
        var folder = Path.GetDirectoryName(manifestPath);
        if (folder is null) return;

        ShowDashboard = false;
        var sameFolder = _currentFolder is not null
            && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(_currentFolder), StringComparison.OrdinalIgnoreCase);

        LoadFolder(sameFolder ? _currentFolder! : folder, manifestPath, sameFolder ? _scalePercent : null);
        StatusText = $"生成したものを開きました: {Path.GetFileName(manifestPath)}";
    }

    /// <summary>フォルダを読み込みます（ダイアログを出さずに指定できるよう公開しています）。</summary>
    public void LoadFolder(string folder, string? selectPath = null, double? keepScale = null)
    {
        _entries.Clear();
        ClearSelection();
        FolderText = folder;
        _currentFolder = folder;
        RefreshFolderCommand.RaiseCanExecuteChanged();

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.manifest.json", SearchOption.AllDirectories))
                _entries.Add(ManifestEntryViewModel.Load(path));
        }
        catch (Exception ex)
        {
            StatusText = $"フォルダを読み込めませんでした: {ex.Message}";
            return;
        }

        RefreshPatternFilters();
        RefreshAspectFilters();
        RefreshSizeFilters();
        RebuildGroups();

        OutputFolder ??= folder;
        SaveLastFolder(folder);

        // 読み直しのときは前と同じRAWを選びます。無くなっていれば先頭に戻します。
        var wanted = selectPath is null
            ? null
            : _entries.FirstOrDefault(e => string.Equals(e.Path, selectPath, StringComparison.OrdinalIgnoreCase));

        // 空のキャンバスのまま止まらないよう、プレビューできるものを1件だけ開いておきます。
        var target = wanted ?? _entries.FirstOrDefault(e => e.SupportsPreview) ?? _entries.FirstOrDefault();
        if (target is not null)
        {
            _keepScaleOnLoad = keepScale is not null && wanted is not null;
            target.IsSelected = true;
            _keepScaleOnLoad = false;
            if (keepScale is not null && wanted is not null) ScalePercent = keepScale.Value;
        }

        var broken = _entries.Count(e => !e.IsLoaded);
        var reloaded = selectPath is not null;
        var head = reloaded ? "読み直しました。manifest" : "manifest";
        StatusText = broken == 0
            ? $"{head} {_entries.Count} 件。一覧から選ぶとプレビューします。"
            : $"{head} {_entries.Count} 件（うち読み込み不可 {broken} 件）。読み込み不可のものも一覧に残しています。";
    }

    // 画素数の粗い区分。フォルダに数十件あると実サイズだけの一覧は選びにくいので、
    // 先に「だいたいこのくらい」で絞れるようにします。判定は幅と高さの両方で行います。
    private static readonly (string Label, int Width, int Height)[] SizeBuckets =
    [
        ("〜HD (1280×720 以下)", 1280, 720),
        ("〜FHD (1920×1080 以下)", 1920, 1080),
        ("〜4K (3840×2160 以下)", 3840, 2160),
    ];

    private const string OverFourKLabel = "4K 超";

    // パターンが 42 まで増えたので、見出しを畳んでも目的のものを探すのに時間がかかります。
    // 名前で絞れるようにします。中身は開いているフォルダにあるものだけです。
    // 「あるはずのものが選べない」より「選べるのに1件も出ない」ほうが分かりにくいためです。
    public ObservableCollection<string> PatternFilters { get; } = ["すべて"];

    private string _patternFilter = "すべて";
    public string PatternFilter
    {
        get => _patternFilter;
        set { if (Set(ref _patternFilter, value)) RebuildGroups(); }
    }

    private void RefreshPatternFilters()
    {
        var current = _patternFilter;
        PatternFilters.Clear();
        PatternFilters.Add("すべて");

        foreach (var name in _entries
                     .Where(e => e.IsLoaded)
                     .Select(e => e.GroupName)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            PatternFilters.Add(name);

        _patternFilter = PatternFilters.Contains(current) ? current : "すべて";
        Raise(nameof(PatternFilter));
    }

    // 同じ比のものだけを並べたい場面があります（16:9 だけを見比べる、など）。
    // サイズでの絞り込みは1つの解像度に決め打ちになるので、比のほうは別に持ちます。
    public ObservableCollection<string> AspectFilters { get; } = ["すべて"];

    private string _aspectFilter = "すべて";
    public string AspectFilter
    {
        get => _aspectFilter;
        set { if (Set(ref _aspectFilter, value)) RebuildGroups(); }
    }

    /// <summary>絞り込みに使う比の表記です。表示用の「16:9（1.778）」とは別に、短い形を使います。</summary>
    private static string AspectKey(ManifestInfo manifest) =>
        AspectRatio.Key(manifest.Width, manifest.Height);

    private void RefreshAspectFilters()
    {
        var current = _aspectFilter;
        AspectFilters.Clear();
        AspectFilters.Add("すべて");

        // 横長のものから並べます。文字の並びだと 11:9 と 16:9 の前後が読めません。
        foreach (var key in _entries
                     .Where(e => e.IsLoaded)
                     .Select(e => (Key: AspectKey(e.Manifest!), Value: AspectRatio.Value(e.Manifest!.Width, e.Manifest!.Height)))
                     .GroupBy(pair => pair.Key)
                     .OrderByDescending(group => group.First().Value)
                     .Select(group => group.Key))
            AspectFilters.Add(key);

        _aspectFilter = AspectFilters.Contains(current) ? current : "すべて";
        Raise(nameof(AspectFilter));
    }

    private void RefreshSizeFilters()
    {
        var current = _sizeFilter;
        var loaded = _entries.Where(e => e.IsLoaded).Select(e => e.Manifest!).ToArray();

        SizeFilters.Clear();
        SizeFilters.Add("すべて");

        // 該当するものが1件も無い区分は出しません。選べない項目が並ぶと探しにくくなるためです。
        foreach (var (label, width, height) in SizeBuckets)
            if (loaded.Any(m => m.Width <= width && m.Height <= height))
                SizeFilters.Add(label);

        if (loaded.Any(m => m.Width > 3840 || m.Height > 2160))
            SizeFilters.Add(OverFourKLabel);

        // 実サイズは画素数の小さい順に並べます。文字として並べると 176x144 が
        // 1280x720 より後ろへ行き、探すときに見当が付きません。
        foreach (var size in loaded
                     .Select(m => (m.Width, m.Height))
                     .Distinct()
                     .OrderBy(s => (long)s.Width * s.Height)
                     .ThenBy(s => s.Width)
                     .Select(s => ResolutionNames.Describe(s.Width, s.Height)))
            SizeFilters.Add(size);

        _sizeFilter = SizeFilters.Contains(current) ? current : "すべて";
        Raise(nameof(SizeFilter));
    }

    // パターンが増えると一覧が縦に伸びるので、まとめて開閉できるようにします。
    // 絞り込みを変えると見出しを作り直すため、直前の開閉をここで覚えて引き継ぎます。
    // 覚えていないと、閉じたつもりが絞り込みのたびに開き直ります。
    private bool _groupsExpanded = true;

    private void SetAllGroupsExpanded(bool expanded)
    {
        _groupsExpanded = expanded;
        foreach (var group in Groups) group.IsExpanded = expanded;
        StatusText = expanded ? "すべて展開しました。" : "すべて閉じました。";
    }

    /// <summary>絞り込みを初期状態へ戻します。絞ったまま「件数が合わない」と悩まないためです。</summary>
    private void ResetFilters()
    {
        _colorModelFilter = ColorModelFilters[0];
        _sizeFilter = "すべて";
        _patternFilter = "すべて";
        _aspectFilter = "すべて";
        Raise(nameof(ColorModelFilter));
        Raise(nameof(SizeFilter));
        Raise(nameof(PatternFilter));
        Raise(nameof(AspectFilter));
        RebuildGroups();
        StatusText = "絞り込みを解除しました。";
    }

    // --- 並び順 ---
    //
    // 既定はファイル名です。ただし名前で並べると 1280x720 が 176x144 より前に来ます。
    // 文字として比べているので「1」「1」「7」の順で決まってしまうためです。
    // 同じパターンを解像度違いで並べたときは、これだと大小の見当が付きません。

    public const string SortByName = "名前順";
    public const string SortBySizeAscending = "解像度順（小さい順）";
    public const string SortBySizeDescending = "解像度順（大きい順）";

    public IReadOnlyList<string> SortOptions { get; } = [SortByName, SortBySizeAscending, SortBySizeDescending];

    private string _sortOrder = SortByName;
    public string SortOrder
    {
        get => _sortOrder;
        set { if (Set(ref _sortOrder, value)) RebuildGroups(); }
    }

    /// <summary>
    /// 解像度の大小は画素数で見ます。1280x720 と 1600x900 のように
    /// 幅だけでは決まらない組み合わせがあるためです。
    /// 画素数が同じものは幅・高さ・名前の順で決めて、並びが毎回変わらないようにします。
    /// 読み込めなかったものは最後にまとめます（大きさが分からないので比べようがありません）。
    /// </summary>
    private static IOrderedEnumerable<ManifestEntryViewModel> SortEntries(
        IEnumerable<ManifestEntryViewModel> entries, string order)
    {
        if (order == SortByName)
            return entries.OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase);

        var descending = order == SortBySizeDescending;
        return entries
            .OrderBy(e => e.Manifest is null)
            .ThenBy(e => descending ? -Pixels(e) : Pixels(e))
            .ThenBy(e => descending ? -(e.Manifest?.Width ?? 0) : e.Manifest?.Width ?? 0)
            .ThenBy(e => descending ? -(e.Manifest?.Height ?? 0) : e.Manifest?.Height ?? 0)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase);
    }

    private static long Pixels(ManifestEntryViewModel entry) =>
        entry.Manifest is null ? 0 : (long)entry.Manifest.Width * entry.Manifest.Height;

    private void RebuildGroups()
    {
        Groups.Clear();

        // 見出しはパターン名なので、並び順を変えても見出し自体は名前順のままにします。
        // 見出しの位置まで動くと、探していたパターンを毎回追い直すことになります。
        foreach (var entry in SortEntries(_entries.Where(Matches), _sortOrder))
        {
            var group = Groups.FirstOrDefault(g => string.Equals(g.Name, entry.GroupName, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                group = new PatternGroupViewModel { Name = entry.GroupName, IsExpanded = _groupsExpanded };
                Groups.Add(group);
            }
            group.Entries.Add(entry);
        }

        var ordered = Groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Groups.Clear();
        foreach (var group in ordered) Groups.Add(group);
    }

    private bool Matches(ManifestEntryViewModel entry)
    {
        if (entry.Manifest is null) return _colorModelFilter == "すべて" && _sizeFilter == "すべて";

        var colorMatches = _colorModelFilter == "すべて"
            || (_colorModelFilter == "YUV / YCbCr" && entry.Manifest.IsYcbcr)
            || ManifestInfo.Same(entry.Manifest.ColorModel, _colorModelFilter);
        var patternMatches = _patternFilter == "すべて"
            || string.Equals(entry.GroupName, _patternFilter, StringComparison.OrdinalIgnoreCase);
        var aspectMatches = _aspectFilter == "すべて" || AspectKey(entry.Manifest) == _aspectFilter;
        return colorMatches && patternMatches && aspectMatches && MatchesSize(entry.Manifest);
    }

    private bool MatchesSize(ManifestInfo manifest)
    {
        if (_sizeFilter == "すべて") return true;
        if (_sizeFilter == OverFourKLabel) return manifest.Width > 3840 || manifest.Height > 2160;

        foreach (var (label, width, height) in SizeBuckets)
            if (_sizeFilter == label)
                return manifest.Width <= width && manifest.Height <= height;

        // 一覧に出している表記そのものと突き合わせます。片方だけ書式を変えると
        // 選べるのに1件も出ない、という状態になります。
        return _sizeFilter == ResolutionNames.Describe(manifest.Width, manifest.Height);
    }

    // --- 選択 ---

    private void ClearSelection()
    {
        Groups.Clear();
        Parameters.Clear();
        SelectedParameter = null;
        PreviewImage = null;
        _rawImage = null;
        _currentManifestPath = null;
        _currentRawPath = null;
        UnsupportedMessage = "";
        _currentManifest = null;
        RawSummary.Clear();
        UpdatePreviewRecipe();
        PatternGuide = PatternGuide.For(null);
        PatternBadge = "パターン名: 未選択";
        PreviewTitle = "RAWファイルを選択してください";
        ForgetSample();
    }

    private void LoadSelected()
    {
        var entry = _selectedEntry;
        if (entry is null) return;

        _currentManifestPath = entry.Path;
        _currentManifest = null;
        RawSummary.Clear();
        Parameters.Clear();
        SelectedParameter = null;
        PreviewImage = null;
        _rawImage = null;
        _currentRawPath = null;
        UnsupportedMessage = "";
        ForgetSample();

        if (entry.Manifest is null)
        {
            PatternBadge = "パターン名: 読み込み不可";
            PreviewTitle = Path.GetFileName(entry.Path);
            UnsupportedMessage = $"manifestを読み込めませんでした。\n\n{entry.Error}";
            StatusText = $"読み込み不可: {entry.Error}";
            SaveRawCopyCommand.RaiseCanExecuteChanged();
            return;
        }

        var manifest = entry.Manifest;
        _currentManifest = manifest;
        BuildRawSummary(manifest);
        foreach (var row in ParameterRow.Build(manifest, entry.Path)) Parameters.Add(row);
        PatternGuide = PatternGuide.For(manifest.Pattern);
        PatternBadge = "パターン名: " + (manifest.Pattern ?? "未指定");
        PreviewTitle = FormatPrimaryParameters(manifest);

        if (!manifest.SupportsPreview)
        {
            UnsupportedMessage = manifest.UnsupportedReason;
            StatusText = $"読み込み済み（プレビュー未対応）: {manifest.ColorModel}, {manifest.BitDepth}bit, {manifest.Storage}";
            SaveRawCopyCommand.RaiseCanExecuteChanged();
            return;
        }

        try
        {
            var rawPath = manifest.ResolveRawPath(entry.Path);
            var image = RawImage.Load(rawPath, manifest);

            _rawImage = image;
            _currentRawPath = rawPath;

            // 別のRAWを開いたら、表示条件はmanifestの記録へ戻します。
            // 前のファイルで変えた条件が残っていると、条件を変えたことを忘れて読み違えます。
            ApplyDefaultsFrom(image);

            var pixels = image.ToBgra32(CurrentOptions);
            var bitmap = BitmapSource.Create(
                image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, image.Width * 4);
            bitmap.Freeze();

            PreviewImage = bitmap;
            Raise(nameof(IsInterpretationOverridden));
            Raise(nameof(OverrideWarning));
            UpdatePreviewRecipe();
            StatusText = "読み込みました。";
            // 読み直しのときは倍率を保ちます。作り直す前後を見比べる操作なので、
            // ここで全体表示へ戻すと比較にならなくなります。
            if (!_keepScaleOnLoad) RequestFit();
        }
        catch (Exception ex)
        {
            UnsupportedMessage = $"RAWを読み込めませんでした。\n\n{ex.Message}";
            StatusText = $"RAW読み込みエラー: {ex.Message}";
        }

        SaveRawCopyCommand.RaiseCanExecuteChanged();
    }

    private static string FormatPrimaryParameters(ManifestInfo manifest) =>
        $"色モデル: {manifest.ColorModel} / 色差サブサンプリング: {manifest.Subsampling} / "
        + $"ビット深度: {manifest.BitDepth}bit / 格納形式: {manifest.Storage} / 画像サイズ: {manifest.Width} x {manifest.Height}";

    // --- 保存 ---

    /// <summary>
    /// 表示条件を名前へ付け足す部分です。既定のままなら空です。
    ///
    /// 同じRAWから matrix 違い・成分違いの絵を何枚も出すので、
    /// 名前が同じだと後から見分けが付きません。上書きも起きます。
    /// 条件を変えたときだけ付けるので、既定で出したものは名前が伸びません。
    /// </summary>
    public string ViewSuffix
    {
        get
        {
            var parts = new List<string>();

            if (IsInterpretationOverridden)
            {
                if (IsYcbcrSelected) parts.Add(_selectedMatrix);
                parts.Add(_selectedRange);
            }

            if (_channels != ChannelMask.All)
                parts.Add(_channels == ChannelMask.None ? "none" : ChannelNames(_channels).Replace(" + ", "").Replace("'", ""));

            if (CurrentOptions.UseRawCodeGray) parts.Add("code");
            if (_upsample == ChromaUpsample.Bilinear) parts.Add("bilinear");

            return parts.Count == 0 ? "" : "_" + string.Join("-", parts);
        }
    }

    /// <summary>保存するときの既定のファイル名（拡張子なし）です。</summary>
    private string SuggestedBaseName()
    {
        var name = Path.GetFileNameWithoutExtension(_currentManifestPath ?? "preview");
        if (name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
            name = name[..^".manifest".Length];
        return name + ViewSuffix;
    }

    private void SaveSelectedFormat()
    {
        if (_selectedImageFormat is { } format) SaveImage(format.Extension);
    }

    /// <summary>
    /// 対応する全形式を、出力先フォルダへ一度に書き出します。
    /// 形式ごとの見え方（JPEGの劣化、GIFの減色）を並べて比べるとき用なので、
    /// 保存ダイアログは出さずにまとめて出します。
    /// </summary>
    private void SaveAllFormats()
    {
        if (_previewImage is null) return;

        var folder = _outputFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            StatusText = "出力先フォルダが未指定です。「出力先...」で指定してください。";
            return;
        }

        var baseName = SuggestedBaseName();

        var saved = new List<string>();
        foreach (var format in ImageFormats)
        {
            var path = Path.Combine(folder, $"{baseName}.{format.Extension}");
            try
            {
                var (encoder, _) = EncoderFor(format.Extension);
                encoder.Frames.Add(BitmapFrame.Create(_previewImage));
                using var stream = File.Create(path);
                encoder.Save(stream);
                saved.Add(format.Extension);
            }
            catch (Exception ex)
            {
                StatusText = $"{format.Extension} の保存に失敗しました: {ex.Message}";
                return;
            }
        }

        StatusText = $"{saved.Count} 形式を保存しました（{string.Join(" / ", saved)}）: {folder}";
    }

    private void SaveImage(string extension)
    {
        if (_previewImage is null) return;

        var (encoder, filter) = EncoderFor(extension);
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = SuggestedBaseName() + "." + extension,
            InitialDirectory = _outputFolder ?? "",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            encoder.Frames.Add(BitmapFrame.Create(_previewImage));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            OutputFolder = Path.GetDirectoryName(dialog.FileName) ?? _outputFolder;
            StatusText = $"保存しました: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存に失敗しました: {ex.Message}";
        }
    }

    private static (BitmapEncoder Encoder, string Filter) EncoderFor(string extension) => extension switch
    {
        "png" => (new PngBitmapEncoder(), "PNG画像 (*.png)|*.png"),
        "jpg" => (new JpegBitmapEncoder(), "JPEG画像 (*.jpg)|*.jpg"),
        "tiff" => (new TiffBitmapEncoder(), "TIFF画像 (*.tiff)|*.tiff"),
        "bmp" => (new BmpBitmapEncoder(), "BMP画像 (*.bmp)|*.bmp"),
        "gif" => (new GifBitmapEncoder(), "GIF画像 (*.gif)|*.gif"),
        _ => (new PngBitmapEncoder(), "PNG画像 (*.png)|*.png"),
    };

    private void SaveRawCopy()
    {
        if (_currentRawPath is null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "RAWデータ (*.raw)|*.raw|すべてのファイル (*.*)|*.*",
            FileName = Path.GetFileName(_currentRawPath),
            InitialDirectory = _outputFolder ?? "",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.Copy(_currentRawPath, dialog.FileName, overwrite: true);
            OutputFolder = Path.GetDirectoryName(dialog.FileName) ?? _outputFolder;
            StatusText = $"RAWをコピーしました: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"RAWのコピーに失敗しました: {ex.Message}";
        }
    }

    private void SelectOutputFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "保存先のフォルダを選んでください",
            InitialDirectory = _outputFolder ?? "",
        };
        if (dialog.ShowDialog() != true) return;
        OutputFolder = dialog.FolderName;
    }

    private void OpenOutputFolder()
    {
        if (_outputFolder is null || !Directory.Exists(_outputFolder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_outputFolder}\"") { UseShellExecute = true });
    }

    private static void SaveLastFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastFolderFile)!);
            File.WriteAllText(LastFolderFile, folder);
        }
        catch
        {
            // 次回起動時の利便のための保存なので、失敗しても操作は続行します。
        }
    }

    private static double NextStep(double current) =>
        ScaleSteps.FirstOrDefault(step => step > current + 0.001, ScaleSteps[^1]);

    private static double PreviousStep(double current) =>
        ScaleSteps.LastOrDefault(step => step < current - 0.001, ScaleSteps[0]);
}

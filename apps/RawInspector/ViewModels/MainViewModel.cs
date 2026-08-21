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
        ClearPatternSearchCommand = new RelayCommand(() => PatternSearchText = "");
        ResetInterpretationCommand = new RelayCommand(ResetInterpretation, () => HasPreview);
        // 記録どおりのまま書き出しても、元と同じものが1つ増えるだけです。
        // 変えているときにだけ押せるようにし、押せない理由はツールチップに書きます。
        SaveInterpretationManifestCommand = new RelayCommand(
            SaveInterpretationManifest, () => IsInterpretationOverridden);
        Dashboard = new DashboardViewModel(
            folder =>
            {
                LoadFolder(folder);
                ShowDashboard = false;
            },
            key =>
            {
                // 生成の窓は Window を作る話なので、画面側に任せます。
                if (key == "generator") { RequestGenerator?.Invoke(); return; }
                // 「RAWを見る」には開く先がまだ無いので、場所を訊きます。
                // HOME を畳むのは OpenFolder が選ばれたあとにやります。
                // 先に畳むと、選ぶのをやめたときに空のビューアだけが残ります。
                OpenFolder();
            },
            document => RequestHelp?.Invoke(document));
        ShowDashboardCommand = new RelayCommand(OpenDashboard);
        // F1 とツールバーから呼びます。HOME とビューアは別の画面なので、出す説明も分けます。
        ShowHelpCommand = new RelayCommand(() =>
            RequestHelp?.Invoke(ShowDashboard ? HelpLibrary.Launcher : HelpLibrary.Viewer));
        ShowScopeCommand = new RelayCommand(() => RequestScope?.Invoke(), () => HasPreview);
        ShowCompareCommand = new RelayCommand(() => RequestCompare?.Invoke(), () => HasPreview);
        ToggleFullScreenCommand = new RelayCommand(() => IsFullScreen = !IsFullScreen, () => HasPreview);
        ExitFullScreenCommand = new RelayCommand(() => IsFullScreen = false, () => IsFullScreen);
        ExpandAllCommand = new RelayCommand(() => SetAllGroupsExpanded(true));
        CollapseAllCommand = new RelayCommand(() => SetAllGroupsExpanded(false));
        _selectedImageFormat = ImageFormats[0];

        ColorModelFilters = ["すべて", "RGB", "YUV / YCbCr"];
        SizeFilters = ["すべて"];
        PatternCategoryFilters = ["すべて"];
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
        private set { if (Set(ref _showDashboard, value)) Raise(nameof(IsViewerChromeVisible)); }
    }

    /// <summary>
    /// ビューアのツールバーと下段の値表示を出すかどうかです。
    ///
    /// HOME は別の画面であって、ビューアの一部ではありません。
    /// 同じ枠を着せると「いま何を見ているのか」が曖昧になり、
    /// 開いてもいないRAWの座標や値の欄が出たままになります。
    /// 全画面のときも同じ理由で外します。
    /// </summary>
    public bool IsViewerChromeVisible => !_isFullScreen && !_showDashboard;

    /// <summary>
    /// 生成の窓を出してほしい、という合図です。窓を作るのは画面側の仕事なので、
    /// ここでは頼むだけにします（ビューモデルから Window を触らないためです）。
    /// </summary>
    public Action? RequestGenerator { get; set; }

    /// <summary>
    /// 使い方を出してほしい、という合図です。生成の窓と同じで、窓を作るのは画面側の仕事です。
    /// 引数は `docs/` からの相対パスで、どの画面の説明を先に出すかを指定します。
    /// </summary>
    public Action<string>? RequestHelp { get; set; }

    /// <summary>分布の窓を出してほしい、という合図です。</summary>
    public Action? RequestScope { get; set; }

    /// <summary>
    /// 分布の窓へ渡す相手です。<b>いま選んでいるRAWと、いまの読み方を対で渡します。</b>
    /// 別々に渡せるようにすると、あるRAWの数字を別の条件の説明と一緒に出せてしまいます。
    /// </summary>
    public InspectionTarget? CurrentTarget => _rawImage is null || _currentManifest is null
        ? null
        : new InspectionTarget(_rawImage, ScopeTitle(), CurrentOptions);

    /// <summary>比較の窓を出してほしい、という合図です。</summary>
    public Action? RequestCompare { get; set; }

    /// <summary>
    /// 突き合わせる相手の候補です。<b>大きさが同じものだけ</b>並べます。
    ///
    /// 先頭には「同じRAWを manifest の記録どおりに読んだもの」を置きます。
    /// 表示条件を変えて見ているときに、その差がどれだけなのかを数えるためです。
    /// 選べるのに必ず断られる項目を並べても、断りの文面を読むまで理由が分かりません。
    /// </summary>
    public IReadOnlyList<CompareCandidate> BuildCompareCandidates()
    {
        var candidates = new List<CompareCandidate>();
        if (_currentManifest is null || _currentManifestPath is null) return candidates;

        candidates.Add(new CompareCandidate("同じRAW（manifest の記録どおりに読んだもの）", null));

        foreach (var entry in _entries
                     .Where(e => e.IsLoaded && e.Manifest!.SupportsPreview)
                     .Where(e => e.Manifest!.Width == _currentManifest.Width
                                 && e.Manifest.Height == _currentManifest.Height)
                     .Where(e => !string.Equals(e.Path, _currentManifestPath, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase))
            candidates.Add(new CompareCandidate(
                $"{Path.GetFileNameWithoutExtension(entry.Path).Replace(".manifest", "")}"
                + $"（{entry.Manifest!.ColorModel} {entry.Manifest.Subsampling} / "
                + $"{entry.Manifest.BitDepth}bit / {entry.Manifest.Storage}）",
                entry.Path));

        return candidates;
    }

    /// <summary>
    /// 候補を読み込みます。<b>相手は manifest の記録どおりに読みます。</b>
    /// 相手側の条件まで変えられるようにすると、出てきた差がどちらの条件のせいなのか言えなくなります。
    /// </summary>
    public InspectionTarget? LoadCompareCandidate(CompareCandidate candidate)
    {
        if (_rawImage is null || _currentManifest is null) return null;

        if (candidate.ManifestPath is null)
            return new InspectionTarget(_rawImage,
                ScopeTitle() + "［manifest の記録どおり］",
                PreviewRenderOptions.Default(_rawImage.DefaultInterpretation));

        try
        {
            var manifest = ManifestInfo.Load(candidate.ManifestPath);
            var image = RawImage.Load(manifest.ResolveRawPath(candidate.ManifestPath), manifest);
            return new InspectionTarget(image, candidate.Title,
                PreviewRenderOptions.Default(image.DefaultInterpretation));
        }
        catch (Exception ex)
        {
            StatusText = $"比較する相手を読み込めませんでした: {ex.Message}";
            return null;
        }
    }

    private string ScopeTitle() =>
        $"{Path.GetFileName(_currentRawPath ?? _currentManifestPath ?? "")}"
        + $"（{_currentManifest!.Width} × {_currentManifest.Height} / {_currentManifest.ColorModel} "
        + $"{_currentManifest.Subsampling} / {_currentManifest.BitDepth}bit / {_currentManifest.Storage}）";

    public RelayCommand ShowDashboardCommand { get; }
    public RelayCommand ShowHelpCommand { get; }
    public RelayCommand ShowScopeCommand { get; }
    public RelayCommand ShowCompareCommand { get; }
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
            Raise(nameof(IsViewerChromeVisible));
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
    public RelayCommand ClearPatternSearchCommand { get; }
    public RelayCommand ResetInterpretationCommand { get; }
    public RelayCommand SaveInterpretationManifestCommand { get; }
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

    // 3つのうち必ず1つが選ばれている、という組です。だから「入れる」だけを受け取り、
    // 「外す」は受け取りません。道具を1つも持っていない状態は無いためです。
    //
    // ただし**黙って無視はできません。** 右クリックの献立は、いま選ばれている項目を
    // もう一度押すと自分で印を外し、こちらの値が変わらなければそのまま外れて見えます
    // （RadioButton は自分で戻すので、この手当てが要るのは献立の側です）。
    // 外そうとされたら、変わっていないことをこちらから言い直して印を戻します。

    public bool IsArrowTool
    {
        get => _tool == PreviewTool.Arrow;
        set { if (value) Tool = PreviewTool.Arrow; else Raise(nameof(IsArrowTool)); }
    }

    public bool IsHandTool
    {
        get => _tool == PreviewTool.Hand;
        set { if (value) Tool = PreviewTool.Hand; else Raise(nameof(IsHandTool)); }
    }

    public bool IsZoomTool
    {
        get => _tool == PreviewTool.Zoom;
        set { if (value) Tool = PreviewTool.Zoom; else Raise(nameof(IsZoomTool)); }
    }

    // --- 圧縮画像の保存形式 ---

    // Encoding の中身は、このアプリが実際に書き出したファイルのヘッダから読んだ値です。
    // 圧縮の細かい条件を選べるようにするのはこの先の話で、いまは既定のまま書き出します。
    // 選べないからこそ、何になるのかは押す前に見えている必要があります。
    public IReadOnlyList<ImageFormatOption> ImageFormats { get; } =
    [
        new("PNG（可逆・既定）", "png")
        {
            Encoding = "可逆。Deflate（zlib）／8bit RGBA（アルファ込み）／フィルタ方式0／インターレースなし",
        },
        new("JPEG（非可逆）", "jpg")
        {
            Encoding = "非可逆。品質 75（固定）／色差 4:2:0／8bit 3成分／ベースライン（JFIF）／量子化テーブル2種",
            Caution = "JPEGは保存のときに色差を 4:2:0 へ間引きます。"
                      + "4:4:4 や 4:2:2 のRAWを見ていても、画面のとおりの色差はファイルに残りません。"
                      + "色差の違いを残したいときは PNG か TIFF にしてください。",
        },
        new("TIFF（可逆）", "tiff")
        {
            Encoding = "可逆。LZW／8bit RGBA（ExtraSamples でアルファを保持）",
        },
        new("BMP（無圧縮）", "bmp")
        {
            Encoding = "無圧縮。32bpp BI_RGB（幅×高さ×4バイト＋ヘッダ54バイト）",
        },
        new("GIF（256色）", "gif")
        {
            Encoding = "パレット方式（最大256色）／LZW",
            Caution = "GIFは色をパレットへ落とします。階調やランプのパターンでは、"
                      + "圧縮そのものは可逆でも色数の段階で情報が落ちます。",
        },
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

    /// <summary>
    /// 成分が1つのときにしか意味を持たず、途中の段で止めているときも使いません。
    /// 段そのものが「どの段の値を見るか」を決めているので、そこへ
    /// 「色変換を通さない」という別の指定を重ねると、何を見ているのか言えなくなります。
    /// 1・2段目で成分を1つに絞れば、同じ絵（コード値の濃淡）になります。
    /// </summary>
    public bool CanUseRawCodeGray =>
        BitOperations.PopCount((uint)_channels) == 1 && _selectedStage.Stage == PipelineStage.Display;

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

    // --- 変換の段 ---
    //
    // 右下には変換の手順を文章で出しています。ただし文章は「そう書いてある」だけで、
    // どの段で何が変わったのかは絵になっていません。色がずれているとき、それが
    // 色差を戻した段の話なのか、正規化の段なのか、matrix の段なのかは、
    // 途中を出さないかぎり切り分けられません。段を選ぶと、そこで止めた値が画面に出ます。
    //
    // 段の並びは**選んだRAWによって変わります**。4:4:4 に「色差を戻す段」はありませんし、
    // RGB には正規化と matrix の段がありません。切り替えても絵が変わらない段を並べると、
    // 「効いていない」のか「そういう段」なのかが区別できなくなります。

    public ObservableCollection<PipelineStageOption> Stages { get; } = [];

    private PipelineStageOption _selectedStage = PipelineStages.Option(PipelineStage.Display);
    public PipelineStageOption SelectedStage
    {
        get => _selectedStage;
        set
        {
            if (!Set(ref _selectedStage, value)) return;
            Raise(nameof(IsStagePartial));
            Raise(nameof(StageWarning));
            Raise(nameof(StageMapping));
            Raise(nameof(CanUseRawCodeGray));
            Raise(nameof(CanMarkOutOfRange));
            Rerender();
        }
    }

    /// <summary>Y'CbCr のときだけ段の切り替えに意味があります（RGB は取り出して量子化するだけです）。</summary>
    public bool HasStages => Stages.Count > 1;

    /// <summary>既定（最後の段）以外で止めているかどうか。止めているあいだは画面で警告します。</summary>
    public bool IsStagePartial => _rawImage is not null && _selectedStage.Stage != PipelineStage.Display;

    public string StageWarning => IsStagePartial
        ? $"変換を「{_selectedStage.Label}」で止めた値を出しています。"
          + "これは最後まで通した絵ではありません。色として読まないでください。"
        : "";

    /// <summary>その段の値をどう絵にしたのか。段ごとに写し方が違うので、選ぶたびに出します。</summary>
    public string StageMapping => _selectedStage.Mapping;

    /// <summary>
    /// 範囲外（0-1 の外）に出た画素を色で示すかどうかです。
    ///
    /// 丸めたあとの絵では、0 未満も 1 超も同じ黒・同じ白になります。
    /// 「もともとその値だった」のか「潰れた結果そう見えている」のかは、
    /// 丸める前を見ないかぎり区別できません。
    /// </summary>
    private bool _markOutOfRange;
    public bool MarkOutOfRange
    {
        get => _markOutOfRange;
        set { if (Set(ref _markOutOfRange, value)) Rerender(); }
    }

    /// <summary>
    /// 1・2段目のコード値は、格納できる範囲の中にしか入りません
    /// （0-255 や 0-1023 を超える値は書き込めません）。範囲外が出ようがない段では押せなくします。
    /// </summary>
    public bool CanMarkOutOfRange => PipelineStages.SupportsRangeMarking(_selectedStage.Stage);

    /// <summary>段の一覧を、開いたRAWに存在するものへ入れ替えます。</summary>
    private void RebuildStages(RawImage image)
    {
        Stages.Clear();
        foreach (var option in image.Stages) Stages.Add(option);
        _selectedStage = Stages[^1]; // 既定は最後の段（＝いつもの表示）です。
        Raise(nameof(Stages));
        Raise(nameof(HasStages));
        Raise(nameof(SelectedStage));
        Raise(nameof(IsStagePartial));
        Raise(nameof(StageWarning));
        Raise(nameof(StageMapping));
        Raise(nameof(CanMarkOutOfRange));
    }

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

        // 手順の何行目がどの段にあたるかを覚えておきます。
        // 止めている段を、説明の中のその行そのものへ書き足すためです。
        // 末尾にまとめて書くと、手順を読んでいる目の動きと、止めた場所が離れます。
        var stageLine = new Dictionary<PipelineStage, int> { [PipelineStage.Codes] = lines.Count - 1 };

        var step = 2;

        if (_rawImage.HasSubsampledChroma)
        {
            stageLine[PipelineStage.Chroma] = lines.Count;
            var how = _upsample == ChromaUpsample.Nearest
                ? "最近傍（格納されている値をそのまま複製）"
                : "バイリニア（隣り合うサンプルの間を線形補間）";
            lines.Add($"{step++}. 間引かれた色差を輝度と同じ密度へ戻す（{m.Subsampling} / {how}）");
        }

        if (m.IsYcbcr)
        {
            stageLine[PipelineStage.Normalized] = lines.Count;
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
            stageLine[PipelineStage.Rgb] = lines.Count;
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

        stageLine[PipelineStage.Display] = lines.Count;
        lines.Add($"{step++}. 0-1 に丸めてから 8bit（0-255）へ量子化し、画面へ出す");

        // 止めている段は、手順のその行に印を付けます。どこまで通したのかが、
        // 手順を読んでいる目の位置でそのまま分かります。
        if (IsStagePartial && stageLine.TryGetValue(_selectedStage.Stage, out var marked))
        {
            lines[marked] += "　◀ ここで止めています";
            lines.Add("");
            lines.Add($"※ 「{_selectedStage.Label}」で止めた値を出しています。この先の段は通していません。");
            foreach (var part in _selectedStage.Mapping.Split('／'))
                lines.Add("   " + part.Trim());
        }

        if (_markOutOfRange && CanMarkOutOfRange)
        {
            lines.Add("");
            lines.Add("※ 範囲外に出た画素を色で示しています。");
            lines.Add("   絵のほうは無彩色にしています。元の色を残したまま色を重ねると、");
            lines.Add("   もともと赤い画素と、範囲外だから赤くした画素が見分けられないためです。");
            lines.Add("   赤 = 上へ外れた、青 = 下へ外れた、マゼンタ = 成分によって上下どちらへも出た。");
        }

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
        new(new ColorInterpretation(_selectedMatrix, _selectedRange), _channels, _upsample, _rawCodeGray,
            _selectedStage.Stage, _markOutOfRange);

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
        _markOutOfRange = false;
        RebuildStages(image);

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
        Raise(nameof(MarkOutOfRange));
    }

    /// <summary>表示条件を変えたときに、同じRAWから絵を作り直します。</summary>
    private void Rerender()
    {
        Raise(nameof(IsInterpretationOverridden));
        Raise(nameof(OverrideWarning));
        SaveInterpretationManifestCommand.RaiseCanExecuteChanged();
        Raise(nameof(IsStagePartial));
        Raise(nameof(StageWarning));
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
    public PatternGuide PatternGuide
    {
        get => _patternGuide;
        private set { if (Set(ref _patternGuide, value)) RaiseScaleWarning(); }
    }

    // --- 縮小表示そのものが出している嘘 ---
    //
    // プレビューの拡大縮小は最近傍に固定です（画素を作らないため）。
    // つまり 100% 未満は単純な間引きで、細かい縞を持つ絵はその場で折り返します。
    // **データは正しいのに画面には渦やモアレが出ます。**
    // 周波数を見るためのパターンでこれを黙っていると、受け取った側の不具合と読み違えます。
    //
    // 出す場所を2つに分けています。ステータスの行は常に目に入るかわりに短く、
    // 絵のすぐ上の帯は場所を取るかわりに理由まで書きます。
    // 折り返しは「説明を読みに行こう」と思う前に誤解が終わってしまうので、
    // 絵の隣に置いて、読まなくても目に入るようにします。

    public bool HasScaleWarning => HasPreview && _patternGuide.ScaleSensitive && _scalePercent < 100;

    public string ScaleWarningShort => $"表示 {_scalePercent:0.#}% は画面側で折り返しています";

    public string ScaleWarningText =>
        $"いまは {_scalePercent:0.#}% の縮小表示です。プレビューは画素を作らない拡大縮小（最近傍）なので、"
        + "縮小のあいだは画面が画素を間引いています。"
        + "見えている渦・モアレ・消えた縞は画面側で起きたもので、RAWの中身とは別です。"
        + "このパターンは細かい縞を持つので、判断は等倍（Ctrl+0）でしてください。";

    private void RaiseScaleWarning()
    {
        Raise(nameof(HasScaleWarning));
        Raise(nameof(ScaleWarningShort));
        Raise(nameof(ScaleWarningText));
    }

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
    public string PatternBadge
    {
        get => _patternBadge;
        private set { if (Set(ref _patternBadge, value)) Raise(nameof(FullScreenInfo)); }
    }

    /// <summary>
    /// 全画面のとき、絵の左上に小さく出す1行です。
    ///
    /// 周りの枠をすべて畳むので、「何を・どの大きさで・どの倍率で見ているか」が
    /// 画面のどこにも残りません。パターンを何枚か行き来したあとだと、
    /// 絵だけでは自分がどれを見ているのか言えなくなります。
    /// バッジの接頭辞は外します。ここは項目名を書くほどの場所ではありません。
    /// </summary>
    public string FullScreenInfo =>
        $"{_patternBadge.Replace("パターン名: ", "")} / {PreviewPixelWidth}×{PreviewPixelHeight} / 表示 {ScaleText}";

    public string FullScreenContextInfo =>
        $"{PreviewPixelWidth}×{PreviewPixelHeight} / {_currentManifest?.BitDepth ?? 0}bit / " +
        $"{_currentManifest?.ColorModel ?? "-"} {_currentManifest?.Subsampling ?? ""} / {_currentManifest?.Storage ?? "-"}";

    public string FullScreenInterpretationInfo =>
        $"表示変換: matrix={_selectedMatrix}, range={_selectedRange}, " +
        $"色差={(_upsample == ChromaUpsample.Nearest ? "最近傍" : "バイリニア")}";

    private string _previewTitle = "RAWファイルを選択してください";
    public string PreviewTitle { get => _previewTitle; private set => Set(ref _previewTitle, value); }

    private string _statusText = "「フォルダを開く」で、RAWとmanifestのあるフォルダを指定してください。";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        private set => Set(ref _isGenerating, value);
    }

    /// <summary>
    /// 別窓の生成処理を本体にも伝えます。生成中は中央キャンバスと下部ステータスの
    /// 両方へ出し、失敗した場合は別窓を見ていなくても理由が残るようにします。
    /// </summary>
    public void SetGenerationState(bool isGenerating, string status)
    {
        IsGenerating = isGenerating;
        if (isGenerating || status.StartsWith("生成できませんでした", StringComparison.Ordinal))
            StatusText = status;
    }

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
            Raise(nameof(FullScreenInfo));
            Raise(nameof(FullScreenContextInfo));
            Raise(nameof(FullScreenInterpretationInfo));
            SaveImageCommand.RaiseCanExecuteChanged();
            SaveSelectedFormatCommand.RaiseCanExecuteChanged();
            SaveAllFormatsCommand.RaiseCanExecuteChanged();
            SaveRawCopyCommand.RaiseCanExecuteChanged();
            ResetInterpretationCommand.RaiseCanExecuteChanged();
            ShowScopeCommand.RaiseCanExecuteChanged();
            ShowCompareCommand.RaiseCanExecuteChanged();
            RaiseScaleWarning();
            // 絵が入るまで全画面にする意味はないので HasPreview を条件にしています。
            // ここで知らせないと、この RelayCommand は CommandManager を見ていないので
            // 条件を確かめ直す機会が無く、絵を読み込んでもボタンは灰色のままになります。
            ToggleFullScreenCommand.RaiseCanExecuteChanged();
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
            Raise(nameof(FullScreenInfo));
            Raise(nameof(FullScreenInterpretationInfo));
            Raise(nameof(PointsPerPixel));
            Raise(nameof(CanShowPixelGrid));
            Raise(nameof(IsPixelGridVisible));
            RaiseScaleWarning();
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

    /// <summary>
    /// いまカーソルが画素の上にあるかどうかです。
    ///
    /// 通常画面の下段は幅を固定するために出しっぱなしにしますが、
    /// 全画面では絵に重ねるので、指していないあいだ「—」だけの札が
    /// 絵の上に残ることになります。指している時だけ出すために使います。
    /// </summary>
    private bool _isProbeActive;
    public bool IsProbeActive { get => _isProbeActive; private set => Set(ref _isProbeActive, value); }

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
        IsProbeActive = true;

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
        IsProbeActive = false;
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
            var folder = FolderPath.Normalize(File.ReadAllText(LastFolderFile));
            return folder is not null && Directory.Exists(folder) ? folder : null;
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

    /// <summary>
    /// ファイル選択の窓を「フォルダを開く窓」として使うための置き文字です。
    ///
    /// 何も選ばずに「開く」を押せるようにするには、名前の欄が空でないことが要ります。
    /// ここに出た文字はそのまま利用者に見えるので、押すと何が起きるかを書いておきます。
    /// </summary>
    private const string FolderPickerCue = "このフォルダを開く";

    /// <summary>
    /// 読み込むフォルダを選びます。
    ///
    /// 選ぶ対象はフォルダですが、フォルダ選択の窓（OpenFolderDialog）は使いません。
    /// あれは仕様上ファイルを一切表示しないので、manifest が入っているのかどうかは
    /// 開いて空の一覧が出るまで分からず、名前だけで当てることになります。
    ///
    /// 代わりに、ファイル選択の窓をフォルダ選択として使います。
    /// 中の manifest が見えたまま、その場所を開けます。
    ///   ・ValidateNames と CheckFileExists を外す … 実在するファイル名を選ばなくても押せます
    ///   ・名前の欄に置き文字を入れる           … 何も選ばずに「開く」を押せるようにします
    ///
    /// 押したときに返る文字列は3通りあり、どれもフォルダに行き着きます（ResolveFolder）。
    ///   1. 何も選ばずに押した → いま見えているフォルダ ＋ 置き文字
    ///   2. manifest を選んで押した → そのファイルのフルパス
    ///   3. 欄にフォルダのパスを打って押した → そのフォルダのパス
    /// どのやり方でも同じ場所が開くので、利用者は使い分けを覚える必要がありません。
    /// 読み込むのはそのフォルダで、従来どおり下のサブフォルダも走査します。
    /// </summary>
    private void OpenFolder()
    {
        var dialog = new OpenFileDialog
        {
            Title = "RAWとmanifestのあるフォルダを開いてください（中の manifest が見えます）",
            Filter = "manifest (*.manifest.json)|*.manifest.json|JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
            InitialDirectory = FolderPath.ForDialog(_outputFolder),
            FileName = FolderPickerCue,
            ValidateNames = false,
            CheckFileExists = false,
            CheckPathExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;

        if (ResolveFolder(dialog.FileName) is not { } folder)
        {
            StatusText = "開く先のフォルダが分かりませんでした。";
            return;
        }

        LoadFolder(folder);
        ShowDashboard = false;
    }

    /// <summary>
    /// 窓が返した文字列から、実際に読み込むフォルダを決めます。
    /// フォルダそのものを先に見るのは、打ち込まれたときに親へ遡ってしまわないためです。
    /// </summary>
    private static string? ResolveFolder(string chosen)
    {
        if (chosen.Length == 0) return null;
        if (Directory.Exists(chosen)) return chosen;

        var parent = Path.GetDirectoryName(chosen);
        return parent is { Length: > 0 } && Directory.Exists(parent) ? parent : null;
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
    public bool AdoptGenerated(string manifestPath)
    {
        var folder = Path.GetDirectoryName(manifestPath);
        if (folder is null) return false;

        ShowDashboard = false;
        var sameFolder = _currentFolder is not null
            && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(_currentFolder), StringComparison.OrdinalIgnoreCase);

        LoadFolder(sameFolder ? _currentFolder! : folder, manifestPath, sameFolder ? _scalePercent : null);
        // RAWのデコードに失敗した場合は LoadSelected が残した理由を上書きしません。
        // 成功時だけ「開きました」とし、呼び出し側は実描画を待ってから進捗表示を閉じます。
        if (HasPreview)
            StatusText = $"生成したものを開きました: {Path.GetFileName(manifestPath)}";
        return HasPreview;
    }

    /// <summary>フォルダを読み込みます（ダイアログを出さずに指定できるよう公開しています）。</summary>
    public void LoadFolder(string folder, string? selectPath = null, double? keepScale = null)
    {
        // 区切りをここで揃えます。`/` のまま持ち回ると、あとでダイアログへ渡したときに落ちます。
        folder = FolderPath.Normalize(folder) ?? folder;

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

            // 一覧の見た目と、実際に開く操作の両方をここでやります。
            //
            // IsSelected は TreeViewItem と結んだ印で、**器が作られて初めて** TreeView へ伝わります。
            // 一覧を作り直した直後はまだ器が無いので、これだけでは SelectedItemChanged が飛びません。
            // 飛ばなければ SelectedEntry は前のままで、絵も前のままです
            // （生成したものを取り込んでも、しばらく古い絵が出ていたのはこれが理由です）。
            // 開くのは一覧の都合ではないので、器を待たずにここで開きます。
            target.IsSelected = true;
            SelectedEntry = target;

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

    public ObservableCollection<string> PatternCategoryFilters { get; }

    private string _patternSearchText = "";
    public string PatternSearchText
    {
        get => _patternSearchText;
        set { if (Set(ref _patternSearchText, value ?? "")) RebuildGroups(); }
    }

    private string _patternCategoryFilter = "すべて";
    public string PatternCategoryFilter
    {
        get => _patternCategoryFilter;
        set { if (Set(ref _patternCategoryFilter, value)) RebuildGroups(); }
    }

    private string _visibleManifestSummary = "0件";
    public string VisibleManifestSummary
    {
        get => _visibleManifestSummary;
        private set => Set(ref _visibleManifestSummary, value);
    }

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

        var currentCategory = _patternCategoryFilter;
        PatternCategoryFilters.Clear();
        PatternCategoryFilters.Add("すべて");
        foreach (var category in _entries
                     .Where(e => e.IsLoaded)
                     .Select(e => PatternChoice.CategoryOf(e.GroupName))
                     .Distinct()
                     .OrderBy(CategoryOrder))
            PatternCategoryFilters.Add(category);
        _patternCategoryFilter = PatternCategoryFilters.Contains(currentCategory) ? currentCategory : "すべて";
        Raise(nameof(PatternCategoryFilter));
    }

    private static int CategoryOrder(string category) => category switch
    {
        "階調・色・レベル" => 0,
        "画面・位置・幾何" => 1,
        "解像度・周波数" => 2,
        "放送・総合カード" => 3,
        _ => 4,
    };

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
        _patternCategoryFilter = "すべて";
        _patternSearchText = "";
        _aspectFilter = "すべて";
        Raise(nameof(ColorModelFilter));
        Raise(nameof(SizeFilter));
        Raise(nameof(PatternFilter));
        Raise(nameof(PatternCategoryFilter));
        Raise(nameof(PatternSearchText));
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

        var ordered = Groups
            .OrderBy(g => CategoryOrder(g.Category))
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Groups.Clear();
        foreach (var group in ordered) Groups.Add(group);

        var visibleCount = Groups.Sum(group => group.Entries.Count);
        VisibleManifestSummary = $"{Groups.Count}パターン / {visibleCount}件";
    }

    private bool Matches(ManifestEntryViewModel entry)
    {
        if (entry.Manifest is null) return _colorModelFilter == "すべて" && _sizeFilter == "すべて";

        var colorMatches = _colorModelFilter == "すべて"
            || (_colorModelFilter == "YUV / YCbCr" && entry.Manifest.IsYcbcr)
            || ManifestInfo.Same(entry.Manifest.ColorModel, _colorModelFilter);
        var patternMatches = _patternFilter == "すべて"
            || string.Equals(entry.GroupName, _patternFilter, StringComparison.OrdinalIgnoreCase);
        var categoryMatches = _patternCategoryFilter == "すべて"
            || string.Equals(PatternChoice.CategoryOf(entry.GroupName), _patternCategoryFilter, StringComparison.Ordinal);
        var searchMatches = SearchMatches(entry);
        var aspectMatches = _aspectFilter == "すべて" || AspectKey(entry.Manifest) == _aspectFilter;
        return colorMatches && patternMatches && categoryMatches && searchMatches
            && aspectMatches && MatchesSize(entry.Manifest);
    }

    private bool SearchMatches(ManifestEntryViewModel entry)
    {
        var words = _patternSearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return true;

        var manifest = entry.Manifest;
        var searchable = manifest is null
            ? $"{entry.GroupName} {entry.Path} {entry.Error}"
            : $"{entry.GroupName} {manifest.ColorModel} {manifest.Storage} {manifest.BitDepth}bit "
              + $"{manifest.Width}x{manifest.Height} {ResolutionNames.Describe(manifest.Width, manifest.Height)} "
              + $"{manifest.Raw.Path} {entry.Path}";
        return words.All(word => searchable.Contains(word, StringComparison.OrdinalIgnoreCase));
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
        // 一覧を作り直すと、いま持っている選択は捨てた項目を指したままになります。
        // 忘れずに残すと、同じRAWを選び直したときに「もう選んである」と見なして開きません
        // （作り直しても項目は別のインスタンスなので、実際には別物です）。
        _selectedEntry = null;
        Groups.Clear();
        Stages.Clear();
        Raise(nameof(HasStages));
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
            SaveInterpretationManifestCommand.RaiseCanExecuteChanged();
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

            // 途中の段で止めた絵は、最後まで通した絵とは別物です。名前が同じだと、
            // あとから見て「色が変」としか言えないものが混ざります。
            if (PipelineStages.FileToken(_selectedStage.Stage) is { Length: > 0 } stage) parts.Add(stage);
            if (_markOutOfRange && CanMarkOutOfRange) parts.Add("range");

            return parts.Count == 0 ? "" : "_" + string.Join("-", parts);
        }
    }

    /// <summary>
    /// manifest として書き出すときに名前へ足す部分です。
    ///
    /// 画像の保存に使う <see cref="ViewSuffix"/> とは別に持ちます。あちらには成分や段まで入りますが、
    /// manifest に書くのは matrix と range だけです。**名前と中身は揃っている必要があります。**
    /// `_Y-norm` の付いた manifest があると、成分や段まで記録されていると読まれます。
    /// </summary>
    private string InterpretationSuffix
    {
        get
        {
            if (!IsInterpretationOverridden) return "";
            var parts = new List<string>();
            if (IsYcbcrSelected) parts.Add(_selectedMatrix);
            parts.Add(_selectedRange);
            return "_" + string.Join("-", parts);
        }
    }

    /// <summary>
    /// いま画面に効いている表示条件を、そのまま読める形で並べます。
    /// RAWコピーの確認で「これは反映されません」と示すために使います。
    /// 既定のままなら「manifest のとおり」と言い切ります。
    /// </summary>
    private string ViewSummaryForCopy()
    {
        var parts = new List<string>();

        if (IsInterpretationOverridden)
        {
            if (IsYcbcrSelected) parts.Add($"変換係数 {_selectedMatrix}");
            parts.Add(_selectedRange == "limited" ? "限定レンジ" : "フルレンジ");
        }

        if (_channels != ChannelMask.All)
            parts.Add(_channels == ChannelMask.None ? "成分をすべて伏せた状態" : $"{ChannelNames(_channels)} のみ");

        if (CurrentOptions.UseRawCodeGray) parts.Add("コード値をそのまま濃淡へ");
        if (_upsample == ChromaUpsample.Bilinear) parts.Add("色差を線形補間");

        // 途中の段で止めているなら、それがいちばん先に言うべきことです。
        // 「色が違う画像」ではなく「最後まで通していない画像」が出てきます。
        if (IsStagePartial) parts.Insert(0, $"変換を「{_selectedStage.Label}」で止めた値");
        if (_markOutOfRange && CanMarkOutOfRange) parts.Add("範囲外を色で表示（絵は無彩色）");

        return parts.Count == 0 ? "manifest のとおりの解釈" : string.Join("・", parts);
    }

    /// <summary>
    /// 圧縮画像として書き出すときの、ビット深度についての一言です。
    ///
    /// 書き出しの元は画面に出している絵で、それは 8bit（Bgra32）です。
    /// **形式を選んでも変わりません。** PNG も TIFF も、可逆なのは「その8bitを可逆に詰める」
    /// という意味であって、10bit のRAWから 10bit の画像が出るわけではありません。
    /// ここを取り違えると、階調を確かめるつもりで 8bit に落ちた絵を渡すことになります。
    /// </summary>
    /// <param name="label">項目名です。桁を揃えたいので、呼ぶ側の見出しの幅に合わせて渡します。</param>
    private string BitDepthLineForSave(string label) =>
        _rawImage is { BitDepth: > 8 }
            ? $"{label}: 8bit へ落ちます（このRAWは {_rawImage.BitDepth}bit です）\n"
            : $"{label}: 8bit（このRAWも 8bit なので、ここでは落ちません）\n";

    /// <summary>
    /// 8bit へ落ちる理由の説明です。
    ///
    /// 欄の中へ折り返して入れると、メッセージの幅で改行されて桁が崩れます。
    /// 長い文は形式ごとの注意（※）と同じ場所へ、続きの文として出します。
    /// </summary>
    private string BitDepthCautionForSave() =>
        _rawImage is { BitDepth: > 8 }
            ? "※ 書き出しの元は画面に出ている8bitの絵です。"
              + $"形式を変えても {_rawImage.BitDepth}bit にはなりません（可逆の PNG・TIFF でも同じです）。"
              + "元の階調が要るときは「RAWコピー」でRAWごと複製してください。\n"
            : "";

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

        // 5つまとめて書くので、保存ダイアログは出しません。
        // そのぶん、どこへ何が出るのかはここで全部見せます。
        // 形式ごとの見え方を比べるための機能なので、比べる相手の中身が分からないと意味がありません。
        if (MessageBox.Show(
                $"いま画面に出している絵を、{ImageFormats.Count} 形式まとめて書き出します。\n\n"
                + $"出力先　　: {folder}\n"
                + $"寸法　　　: {PreviewPixelWidth} × {PreviewPixelHeight}（表示倍率に関わらず等倍）\n"
                + BitDepthLineForSave("ビット深度")
                + $"反映　　　: {ViewSummaryForCopy()}（表示倍率と格子線は入りません）\n\n"
                + string.Join("\n", ImageFormats.Select(f =>
                    $"{baseName}.{f.Extension}\n    {f.Encoding}"))
                + "\n\n"
                + BitDepthCautionForSave()
                + "\n同じ名前のファイルがあれば上書きします。書き出しますか？",
                "全形式で保存",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information) != MessageBoxResult.OK) return;

        var saved = new List<string>();
        foreach (var format in ImageFormats)
        {
            var path = Path.Combine(folder, $"{baseName}.{format.Extension}");
            try
            {
                var encoder = EncoderFor(format.Extension);
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

        var format = ImageFormats.FirstOrDefault(f => f.Extension == extension) ?? ImageFormats[0];
        var fileName = SuggestedBaseName() + "." + extension;

        // 何が焼き込まれ、何が焼き込まれないのかを先に言います。
        // 画面には表示条件を通した結果が出ていますが、倍率と格子は画面だけのものです。
        // 「見えているとおりに保存される」と思ったまま押すと、
        // 400% で見ていた絵が等倍で出てきて、格子も入っていない、ということになります。
        if (MessageBox.Show(
                "いま画面に出している絵を、そのまま焼き込んで保存します。\n\n"
                + $"ファイル名　　　: {fileName}\n"
                + $"寸法　　　　　　: {PreviewPixelWidth} × {PreviewPixelHeight}（表示倍率に関わらず等倍）\n"
                + BitDepthLineForSave("ビット深度　　　")
                + $"反映されるもの　: {ViewSummaryForCopy()}\n"
                + "反映されないもの: 表示倍率、画素の格子線\n\n"
                + $"形式　　　　　　: {format.Label}\n"
                + $"書き出す中身　　: {format.Encoding}\n"
                + (BitDepthCautionForSave() is { Length: > 0 } depth ? "\n" + depth : "")
                + (format.Caution.Length > 0 ? $"\n※ {format.Caution}\n" : "")
                + "\n保存しますか？",
                "画像を保存",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information) != MessageBoxResult.OK) return;

        var encoder = EncoderFor(extension);
        var dialog = new SaveFileDialog
        {
            // 拡張子の絞り込みは出しません。形式は上のコンボで決まっているので、
            // ここで選び直させると、選んだ形式と違うものを選べるように見えてしまいます。
            // 付け忘れだけは DefaultExt で補います。
            DefaultExt = extension,
            AddExtension = true,
            FileName = fileName,
            InitialDirectory = FolderPath.ForDialog(_outputFolder),
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

    /// <summary>
    /// 形式ごとのエンコーダです。設定はどれも既定のままにしています。
    /// 何になるのかは <see cref="ImageFormatOption.Encoding"/> に書いてあり、
    /// 保存の前にダイアログで出します。
    /// </summary>
    private static BitmapEncoder EncoderFor(string extension) => extension switch
    {
        "png" => new PngBitmapEncoder(),
        "jpg" => new JpegBitmapEncoder(),
        "tiff" => new TiffBitmapEncoder(),
        "bmp" => new BmpBitmapEncoder(),
        "gif" => new GifBitmapEncoder(),
        _ => new PngBitmapEncoder(),
    };

    private void SaveRawCopy()
    {
        // manifest が無いなら複製しません。RAWだけ渡しても中身を読み取れないので、
        // 組にできないときは何も作らないほうが親切です。
        if (_currentRawPath is null || _currentManifestPath is null) return;

        // 何が出るのかを先に言います。
        //
        // 画面には表示条件（マトリクス・レンジ・成分・補間）を通した「解釈した結果」が
        // 出ています。RAWコピーはそれを書き出しません。**元のバイトをそのまま複製します。**
        //
        // ここを取り違えると、画面で色を直したつもりのRAWを渡してしまいます。
        // 表示のとおりの絵が要るときは「画像を保存」を使ってください（PNG に焼き込みます）。
        var answer = MessageBox.Show(
            "元のRAWファイルをバイト単位でそのまま複製します。\n\n"
            + "RAWと manifest を、同じ名前で1組にして書き出します。\n"
            + "RAWのバイト列には寸法もビット深度も格納形式も入っていないので、\n"
            + "manifest と離すと、そのファイルが何なのかを誰も読み取れなくなります。\n\n"
            + $"RAW　　　 : {Path.GetFileName(_currentRawPath)}（次の画面で名前を決められます）\n"
            + "manifest  : RAWと同じ名前で隣に置きます\n"
            + $"条件　　　: {_currentManifest?.Width} × {_currentManifest?.Height} / "
            + $"{_currentManifest?.ColorModel} {_currentManifest?.Subsampling} / "
            + $"{_currentManifest?.BitDepth}bit / {_currentManifest?.Storage}\n\n"
            + $"いま画面に出している解釈（{ViewSummaryForCopy()}）は反映されません。\n"
            + "表示のとおりの絵が必要なときは「画像を保存」を使ってください。\n\n"
            + "複製しますか？",
            "RAWコピー",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;

        var dialog = new SaveFileDialog
        {
            // 画像の保存と同じで、絞り込みは出しません。複製するファイルはもう決まっています。
            DefaultExt = "raw",
            AddExtension = true,
            FileName = Path.GetFileName(_currentRawPath),
            InitialDirectory = FolderPath.ForDialog(_outputFolder),
        };
        if (dialog.ShowDialog() != true) return;

        // 上書きの確認を保存ダイアログがやってくれるのは、そこで選んだRAWの分だけです。
        // manifest は名前が決まってから隣へ置くので、訊かれません。こちらで訊きます。
        // 黙って上書きすると、別のRAWの条件が消えます。
        var manifestTarget = CopiedManifest.PathFor(dialog.FileName);
        if (File.Exists(manifestTarget)
            && MessageBox.Show(
                $"manifest がすでにあります。上書きしますか？\n\n{manifestTarget}\n\n"
                + "「いいえ」を選ぶと、RAWも複製しません（組にならないためです）。",
                "上書きの確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            File.Copy(_currentRawPath, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            StatusText = $"RAWのコピーに失敗しました: {ex.Message}";
            return;
        }

        try
        {
            File.WriteAllText(manifestTarget, CopiedManifest.Build(
                File.ReadAllText(_currentManifestPath),
                Path.GetFileName(_currentManifestPath),
                Path.GetFileName(dialog.FileName),
                DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            // RAWだけが残った状態です。黙って「コピーしました」と言うと、
            // 条件の分からないRAWを渡してしまいます。何が起きたかをそのまま出します。
            StatusText = $"RAWは複製しましたが、manifest を書けませんでした: {ex.Message}"
                         + "（RAWだけでは寸法も格納形式も分かりません）";
            return;
        }

        OutputFolder = Path.GetDirectoryName(dialog.FileName) ?? _outputFolder;
        StatusText = $"RAWと manifest を1組でコピーしました: {Path.GetFileName(dialog.FileName)}";
    }

    /// <summary>
    /// いまの読み方を、manifest として書き出します。
    ///
    /// 表示条件を変えてもRAWのバイト列は変わらないので、RAWコピーの名前に条件を付けても嘘になります
    /// （「bt601 へ変換したRAW」があるように見えます）。読み方を残す場所は manifest のほうです。
    /// <b>同じRAWを指したまま</b>、matrix と range だけが違う manifest を添えます。
    /// </summary>
    private void SaveInterpretationManifest()
    {
        if (_currentManifestPath is null || _currentManifest is null || _rawImage is null) return;

        var target = DerivedManifest.SuggestPath(_currentManifestPath, InterpretationSuffix);
        var matrix = IsYcbcrSelected ? _selectedMatrix : null;

        var dropped = _currentManifest.Files.Count(f => !ManifestInfo.Same(f.Kind, "raw"));
        var ignored = new List<string>();
        if (_channels != ChannelMask.All) ignored.Add("成分の選択");
        if (CurrentOptions.UseRawCodeGray) ignored.Add("コード値表示");
        if (_upsample == ChromaUpsample.Bilinear) ignored.Add("色差の戻し方");
        if (IsStagePartial) ignored.Add("段");
        if (_markOutOfRange) ignored.Add("範囲外の表示");

        if (MessageBox.Show(
                "いまの読み方を manifest として書き出します。RAWは作りません。\n\n"
                + $"書き出す先　　: {target}\n"
                + $"指すRAW　　　 : {_currentManifest.Raw.Path}（元のものと同じファイルです）\n"
                + $"書き換える条件: matrix {(matrix is null ? "—" : $"{_rawImage.DefaultInterpretation.Matrix} → {matrix}")}"
                + $" / range {_rawImage.DefaultInterpretation.Range} → {_selectedRange}\n"
                + (dropped > 0
                    ? $"外すもの　　　: RAW以外のファイル {dropped} 件（元の条件で作られた絵なので、この条件では合いません）\n"
                    : "")
                + (ignored.Count > 0
                    ? $"書かないもの　: {string.Join("・", ignored)}\n"
                      + "　　　　　　　  manifest はデータの条件を書くところで、画面の見せ方を書くところではありません。\n"
                    : "")
                + "\n書き出しますか？",
                "読み方を manifest に書き出す",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information) != MessageBoxResult.OK) return;

        if (File.Exists(target)
            && MessageBox.Show($"すでにあります。上書きしますか？\n\n{target}", "上書きの確認",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        try
        {
            var json = DerivedManifest.Build(
                File.ReadAllText(_currentManifestPath),
                Path.GetFileName(_currentManifestPath),
                matrix,
                _selectedRange,
                DateTimeOffset.Now);
            File.WriteAllText(target, json);
        }
        catch (Exception ex)
        {
            StatusText = $"manifest を書き出せませんでした: {ex.Message}";
            return;
        }

        // 書いたものがその場で一覧に並ぶよう読み直し、書いたほうを選びます。
        // 書けたのかどうかを、フォルダを開き直して確かめさせないためです。
        LoadFolder(_currentFolder ?? Path.GetDirectoryName(target)!, target, _scalePercent);
        StatusText = $"読み方を manifest に書き出しました: {Path.GetFileName(target)}";
    }

    private void SelectOutputFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "保存先のフォルダを選んでください",
            InitialDirectory = FolderPath.ForDialog(_outputFolder),
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

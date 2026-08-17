using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawInspector.Decoding;

namespace RawInspector.ViewModels;

/// <summary>
/// 突き合わせる相手の候補です。
/// </summary>
/// <param name="ManifestPath">
/// null なら「同じRAWを manifest の記録どおりに読んだもの」です。
/// 表示条件を変えて見ているとき、その差がどれだけなのかを数えるために使います。
/// </param>
public sealed record CompareCandidate(string Title, string? ManifestPath)
{
    public override string ToString() => Title;
}

/// <summary>差の表の1行です。</summary>
public sealed class DiffRow
{
    public DiffRow(DiffChannel channel, ComparisonResult result)
    {
        Label = channel.Label;
        Max = channel.Max.ToString();
        Mean = $"{channel.Mean:0.000}";
        Different = channel.Different == 0
            ? "0"
            : $"{channel.Different:N0}（{channel.Different * 100.0 / result.Pixels:0.##}%）";
        Where = channel.MaxX < 0 ? "—" : $"({channel.MaxX}, {channel.MaxY})";
    }

    public string Label { get; }

    /// <summary>いちばん大きかった差です。</summary>
    public string Max { get; }

    public string Mean { get; }

    /// <summary>差のある画素の数です。最大差だけでは、1画素なのか全面なのかが分かりません。</summary>
    public string Different { get; }

    /// <summary>最大差が出た座標です。本体で拡大して見に行けるように出します。</summary>
    public string Where { get; }
}

/// <summary>
/// 2枚を突き合わせる窓のビューモデルです。
///
/// 並べて見るだけでは「同じに見える」までしか言えません。1コード値の差は目で見えませんし、
/// 見えないからといって無いわけでもありません。<b>差は数えるものです。</b>
///
/// 左は本体でいま見ているRAWと、いまの表示条件。右は選んだRAWを
/// <b>manifest の記録どおりに</b>読んだものです。右まで条件を変えられるようにすると、
/// 出てきた差がどちらの条件のせいなのか言えなくなります。
/// </summary>
public sealed class CompareViewModel : ObservableObject
{
    private readonly Func<InspectionTarget?> _leftProvider;
    private readonly Func<IReadOnlyList<CompareCandidate>> _candidateProvider;
    private readonly Func<CompareCandidate, InspectionTarget?> _loader;

    private InspectionTarget? _left;
    private InspectionTarget? _right;
    private RawComparison? _comparison;
    private ComparisonResult? _result;

    public CompareViewModel(
        Func<InspectionTarget?> leftProvider,
        Func<IReadOnlyList<CompareCandidate>> candidateProvider,
        Func<CompareCandidate, InspectionTarget?> loader)
    {
        _leftProvider = leftProvider;
        _candidateProvider = candidateProvider;
        _loader = loader;

        CompareCommand = new RelayCommand(() => Compare(), () => !_isBusy && _selectedCandidate is not null);
        ReloadCandidatesCommand = new RelayCommand(ReloadCandidates);
    }

    public RelayCommand CompareCommand { get; }
    public RelayCommand ReloadCandidatesCommand { get; }

    // --- 相手 ---

    public ObservableCollection<CompareCandidate> Candidates { get; } = [];

    private CompareCandidate? _selectedCandidate;
    public CompareCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (!Set(ref _selectedCandidate, value)) return;
            CompareCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 候補を集め直します。<b>大きさが同じものだけ</b>を並べます。
    /// 選べるのに必ず断られる項目が並んでいると、断りの文面を読むまで理由が分かりません。
    /// </summary>
    public void ReloadCandidates()
    {
        var wanted = _selectedCandidate;
        Candidates.Clear();
        foreach (var candidate in _candidateProvider()) Candidates.Add(candidate);

        _selectedCandidate = Candidates.FirstOrDefault(c => c == wanted) ?? Candidates.FirstOrDefault();
        Raise(nameof(SelectedCandidate));
        Raise(nameof(HasCandidates));
        Raise(nameof(NoCandidateNote));
        CompareCommand.RaiseCanExecuteChanged();
    }

    public bool HasCandidates => Candidates.Count > 0;

    public string NoCandidateNote =>
        "同じ大きさのRAWが、開いているフォルダにありません。"
        + "大きさが違うものは比べません（拡大縮小して揃えると、画素は元のどちらとも違う値になります）。";

    // --- 何で比べるか ---

    public IReadOnlyList<CompareDomain> Domains { get; } = [CompareDomain.Display, CompareDomain.Codes];

    private CompareDomain _domain = CompareDomain.Display;
    public CompareDomain Domain
    {
        get => _domain;
        set { if (Set(ref _domain, value)) Compare(); }
    }

    public bool IsDisplayDomain
    {
        get => _domain == CompareDomain.Display;
        set { if (value) Domain = CompareDomain.Display; }
    }

    public bool IsCodesDomain
    {
        get => _domain == CompareDomain.Codes;
        set { if (value) Domain = CompareDomain.Codes; }
    }

    /// <summary>コード値で比べられない組み合わせでは押せなくします。理由は下に出します。</summary>
    public bool CanCompareCodes => _comparison?.CanCompareCodes ?? true;

    private string _domainNote = "";
    public string DomainNote { get => _domainNote; private set => Set(ref _domainNote, value); }

    // --- 差の倍率 ---

    public IReadOnlyList<int> Amplifications { get; } = [1, 2, 4, 8, 16, 32, 64];

    private int _amplification = 8;
    public int Amplification
    {
        get => _amplification;
        set { if (Set(ref _amplification, value)) { DrawDiff(); Raise(nameof(AmplificationNote)); } }
    }

    /// <summary>
    /// 倍率を掛けたことは、絵の隣に出し続けます。
    /// 掛けたことを忘れると、8倍した差を「これだけずれている」と読みます。
    /// </summary>
    public string AmplificationNote => _result is null
        ? ""
        : _amplification == 1
            ? "差をそのまま出しています（倍率 1）。1コード値の差は 1/255 の明るさなので、ほぼ黒です。"
            : $"差を {_amplification} 倍して出しています。"
              + $"画面の明るさ 255 は、実際には差 {255.0 / _amplification / 255 * _result.Scale:0.##} にあたります。";

    // --- 状態 ---

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) CompareCommand.RaiseCanExecuteChanged(); }
    }

    private string _status = "突き合わせる相手を選んでください。";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _leftTitle = "—";
    public string LeftTitle { get => _leftTitle; private set => Set(ref _leftTitle, value); }

    private string _rightTitle = "—";
    public string RightTitle { get => _rightTitle; private set => Set(ref _rightTitle, value); }

    private string _leftReading = "";
    public string LeftReading { get => _leftReading; private set => Set(ref _leftReading, value); }

    private string _rightReading = "";
    public string RightReading { get => _rightReading; private set => Set(ref _rightReading, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>1バイトも違わないときは、そう言い切ります。</summary>
    private bool _isIdentical;
    public bool IsIdentical { get => _isIdentical; private set => Set(ref _isIdentical, value); }

    public ObservableCollection<DiffRow> Rows { get; } = [];

    private BitmapSource? _leftImage;
    public BitmapSource? LeftImage { get => _leftImage; private set => Set(ref _leftImage, value); }

    private BitmapSource? _rightImage;
    public BitmapSource? RightImage { get => _rightImage; private set => Set(ref _rightImage, value); }

    private BitmapSource? _diffImage;
    public BitmapSource? DiffImage { get => _diffImage; private set => Set(ref _diffImage, value); }

    // --- 突き合わせ ---

    public async void Compare()
    {
        if (_isBusy) return;
        if (_selectedCandidate is not { } candidate) return;

        if (_leftProvider() is not { } left)
        {
            Status = "本体でRAWが選ばれていません。";
            return;
        }

        var right = _loader(candidate);
        if (right is null)
        {
            Status = $"{candidate.Title} を読み込めませんでした。";
            return;
        }

        IsBusy = true;
        _left = left;
        _right = right;
        LeftTitle = left.Title;
        RightTitle = right.Title;
        LeftReading = "左は本体の表示条件で読んでいます: " + Describe(left);
        RightReading = "右は manifest の記録どおりに読んでいます: " + Describe(right);

        var comparison = new RawComparison(left.Image, left.Options, right.Image, right.Options);
        _comparison = comparison;
        Raise(nameof(CanCompareCodes));

        if (!comparison.SameSize)
        {
            Reset();
            Status = comparison.SizeMismatch;
            IsBusy = false;
            return;
        }

        // コード値で比べられない組み合わせでは、黙って表示RGBへ落とさずに理由を出してから移ります。
        var domain = _domain;
        if (domain == CompareDomain.Codes && !comparison.CanCompareCodes)
        {
            domain = CompareDomain.Display;
            _domain = domain;
            Raise(nameof(IsDisplayDomain));
            Raise(nameof(IsCodesDomain));
        }

        DomainNote = comparison.CodesUnavailableReason ?? DescribeDomain(domain, comparison);

        Status = "突き合わせています…";

        try
        {
            var result = await Task.Run(() => comparison.Analyze(domain));
            _result = result;

            LeftImage = Render(left);
            RightImage = Render(right);
            DrawDiff();
            BuildRows(result);

            Status = $"{result.Pixels:N0} 画素を突き合わせました。";
        }
        catch (Exception ex)
        {
            Status = $"突き合わせできませんでした: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Reset()
    {
        _result = null;
        LeftImage = null;
        RightImage = null;
        DiffImage = null;
        Rows.Clear();
        Summary = "";
        IsIdentical = false;
    }

    private void BuildRows(ComparisonResult result)
    {
        Rows.Clear();
        foreach (var channel in result.Channels) Rows.Add(new DiffRow(channel, result));

        IsIdentical = result.IsIdentical;
        var domain = result.Domain == CompareDomain.Display ? "表示RGB（0-255）" : $"コード値（0-{result.Scale}）";

        Summary = result.IsIdentical
            ? $"{domain}で、{result.Pixels:N0} 画素すべてが完全に一致しました（最大差 0）。"
            : $"{domain}で、{result.DifferentPixels:N0} 画素（{result.DifferentPixels * 100.0 / result.Pixels:0.##}%）"
              + $"に差があります。いちばん大きい差は {result.MaxDifference} です"
              + $"（比べた値の最大は {result.Scale}）。";

        Raise(nameof(AmplificationNote));
    }

    private void DrawDiff()
    {
        if (_comparison is not { } comparison || _result is not { } result) return;

        var pixels = comparison.RenderDiffBgra(result.Domain, _amplification);
        DiffImage = Freeze(pixels, result.Width, result.Height);
    }

    private static BitmapSource Render(InspectionTarget target) =>
        Freeze(target.Image.ToBgra32(target.Options), target.Image.Width, target.Image.Height);

    private static BitmapSource Freeze(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static string Describe(InspectionTarget target)
    {
        var parts = new List<string>();
        if (target.Image.ChannelLabels.First == "Y'")
        {
            parts.Add($"matrix {target.Options.Interpretation.Matrix}");
            parts.Add($"range {target.Options.Interpretation.Range}");
            if (target.Image.HasSubsampledChroma)
                parts.Add(target.Options.Upsample == ChromaUpsample.Nearest ? "色差は最近傍" : "色差はバイリニア");
        }
        else
        {
            parts.Add("RGB");
        }

        if (target.Options.Stage != PipelineStage.Display)
            parts.Add($"段は「{PipelineStages.Option(target.Options.Stage).Label}」");
        if (target.Options.Channels != ChannelMask.All) parts.Add("成分を絞っている");

        return string.Join(" / ", parts);
    }

    private static string DescribeDomain(CompareDomain domain, RawComparison comparison) =>
        domain == CompareDomain.Display
            ? "表示RGB（8bit へ変換したあとの値）で比べています。"
              + "格納形式やビット深度が違っていても比べられますが、8bit へ丸めたあとなので、"
              + "それより細かい差は消えています。"
            : $"コード値（RAWから読んだ生の値、0-{comparison.ScaleOf(CompareDomain.Codes)}）で比べています。"
              + "変換を通していないので、8bit へ丸める前の違いがそのまま出ます。"
              + "色差が間引かれている形式では、戻したあとの値で比べます。";
}

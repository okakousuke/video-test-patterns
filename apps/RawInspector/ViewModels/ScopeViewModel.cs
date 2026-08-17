using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawInspector.Decoding;

namespace RawInspector.ViewModels;

/// <summary>
/// 分布の窓のビューモデルです。
///
/// 絵を眺めても「1コード値だけずれている」「上のほうが少しだけ潰れている」は分かりません。
/// かといって画素を1つずつ拾うのも現実的ではないので、全画素を数えて分布にします。
///
/// 集計そのものは <see cref="RawImage.Analyze"/>（WPFに依存しません）が持っています。
/// ここがやるのは、数えた結果を絵にすることと、
/// <b>いつの条件で数えたのかを言い続けること</b>です。
/// </summary>
public sealed class ScopeViewModel : ObservableObject
{
    private readonly Func<InspectionTarget?> _provider;

    /// <summary>いま出している数字が、どのRAWをどの条件で数えたものか。</summary>
    private InspectionTarget? _measured;

    public ScopeViewModel(Func<InspectionTarget?> provider)
    {
        _provider = provider;
        RefreshCommand = new RelayCommand(() => Refresh(), () => !_isBusy);
    }

    private string _title = "RAWが選ばれていません";
    public string Title { get => _title; private set => Set(ref _title, value); }

    public RelayCommand RefreshCommand { get; }

    /// <summary>Y'CbCr のときだけ、色差の平面に意味があります。</summary>
    public bool HasVector => _statistics?.IsYcbcr ?? false;

    public string VectorUnavailable =>
        "この形式には色差がありません（R・G・B はそれぞれ独立した量で、平面に置く意味がありません）。";

    // --- 数えた結果 ---

    private ScopeStatistics? _statistics;

    public ObservableCollection<ChannelStatRow> Rows { get; } = [];

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private string _status = "数えています…";
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>どの条件で数えたか。画面の条件と違ってきたら、それが分かるようにするためです。</summary>
    private string _measuredUnder = "";
    public string MeasuredUnder { get => _measuredUnder; private set => Set(ref _measuredUnder, value); }

    /// <summary>
    /// 数えたあとに、本体で別のRAWが選ばれたか、表示条件が変わったかどうかです。
    ///
    /// <b>自動では数え直しません。</b> 4K なら 800 万画素を1周するので、
    /// 一覧を矢印キーで送るたびに走ると、本体の操作まで重くなります。
    /// ただし黙って古い数字を出し続けるほうが悪いので、ずれていることは必ず出します。
    /// </summary>
    public bool IsStale
    {
        get
        {
            if (_measured is null) return false;
            var now = _provider();
            if (now is null) return true;
            return !ReferenceEquals(now.Image, _measured.Image) || !SameReading(now.Options, _measured.Options);
        }
    }

    public string StaleWarning
    {
        get
        {
            var now = _provider();
            if (_measured is null || now is null) return "本体でRAWが選ばれていません。";
            return !ReferenceEquals(now.Image, _measured.Image)
                ? $"本体では別のRAW（{now.Title}）を見ています。"
                  + $"この数字は {_measured.Title} を数えたものです。「取り直す」でいまのRAWを数えます。"
                : "この数字は、いまの表示条件で数えたものではありません。「取り直す」で数え直せます。";
        }
    }

    /// <summary>
    /// 集計に効く条件だけを比べます。成分の選択と段は集計に効かないので、
    /// そこが変わっただけで「古い」と言うと、意味のない警告が出続けます。
    /// </summary>
    private static bool SameReading(PreviewRenderOptions a, PreviewRenderOptions b) =>
        a.Interpretation == b.Interpretation && a.Upsample == b.Upsample;

    /// <summary>本体で条件やRAWが変わったときに呼びます（数え直しはしません）。</summary>
    public void NotifyTargetChanged()
    {
        Raise(nameof(IsStale));
        Raise(nameof(StaleWarning));
    }

    // --- 波形に出す成分 ---
    //
    // 成分の呼び名はRAWで変わる（Y'CbCr か R・G・B か）ので、数えたときに作り直します。

    public ObservableCollection<WaveformChannelOption> WaveformChannels { get; } = [];

    private WaveformChannelOption? _waveformChannel;
    public WaveformChannelOption? WaveformChannel
    {
        get => _waveformChannel;
        set { if (Set(ref _waveformChannel, value) && value is not null && !_isBusy) Refresh(); }
    }

    /// <summary>
    /// 縦を対数にするかどうかです。既定は対数にしています。
    ///
    /// テストパターンは一様な面を持つものが多く、そこだけ何万画素も積み上がります。
    /// 線形の目盛りだと、その山以外は床に貼り付いて見えません。
    /// 「1画素だけ違う値がある」を見つけたいので、既定は対数にします。
    /// </summary>
    private bool _logScale = true;
    public bool LogScale
    {
        get => _logScale;
        set { if (Set(ref _logScale, value)) Draw(); }
    }

    // --- 絵 ---

    private BitmapSource? _firstHistogram;
    public BitmapSource? FirstHistogram { get => _firstHistogram; private set => Set(ref _firstHistogram, value); }

    private BitmapSource? _secondHistogram;
    public BitmapSource? SecondHistogram { get => _secondHistogram; private set => Set(ref _secondHistogram, value); }

    private BitmapSource? _thirdHistogram;
    public BitmapSource? ThirdHistogram { get => _thirdHistogram; private set => Set(ref _thirdHistogram, value); }

    private BitmapSource? _waveform;
    public BitmapSource? Waveform { get => _waveform; private set => Set(ref _waveform, value); }

    private BitmapSource? _vector;
    public BitmapSource? Vector { get => _vector; private set => Set(ref _vector, value); }

    public string FirstLabel => _statistics?.Channels[0].Label ?? "—";
    public string SecondLabel => _statistics?.Channels[1].Label ?? "—";
    public string ThirdLabel => _statistics?.Channels[2].Label ?? "—";

    private string _waveformNote = "";
    public string WaveformNote { get => _waveformNote; private set => Set(ref _waveformNote, value); }

    private string _vectorNote = "";
    public string VectorNote { get => _vectorNote; private set => Set(ref _vectorNote, value); }

    private string _clipNote = "";
    public string ClipNote { get => _clipNote; private set => Set(ref _clipNote, value); }

    // --- 集計 ---

    /// <summary>
    /// 数え直します。画素数ぶん1周するので、別のスレッドでやります。
    /// 4K なら 800 万回まわるので、画面と同じスレッドで走らせると窓が固まります。
    /// </summary>
    public async void Refresh()
    {
        if (_isBusy) return;

        if (_provider() is not { } target)
        {
            Status = "本体でRAWが選ばれていません。RAWを1本選んでから「取り直す」を押してください。";
            NotifyTargetChanged();
            return;
        }

        IsBusy = true;
        Title = target.Title;
        Status = $"{target.Image.Width} × {target.Image.Height} = "
            + $"{(long)target.Image.Width * target.Image.Height:N0} 画素を数えています…";

        // 成分の選択肢は、開いているRAWの呼び名で作り直します。
        // Y'CbCr のRAWから RGB のRAWへ移ったときに、Cb・Cr のまま残っていると選べません。
        var (l1, l2, l3) = target.Image.ChannelLabels;
        if (WaveformChannels.Count != 3 || WaveformChannels[0].Label != l1)
        {
            var wanted = _waveformChannel?.Channel ?? ChannelMask.First;
            WaveformChannels.Clear();
            WaveformChannels.Add(new WaveformChannelOption(ChannelMask.First, l1));
            WaveformChannels.Add(new WaveformChannelOption(ChannelMask.Second, l2));
            WaveformChannels.Add(new WaveformChannelOption(ChannelMask.Third, l3));
            _waveformChannel = WaveformChannels.FirstOrDefault(o => o.Channel == wanted) ?? WaveformChannels[0];
            Raise(nameof(WaveformChannel));
        }

        var channel = _waveformChannel?.Channel ?? ChannelMask.First;

        try
        {
            var stats = await Task.Run(() => target.Image.Analyze(target.Options, channel));
            _statistics = stats;
            _measured = target with { Options = stats.Options };
            MeasuredUnder = DescribeReading(stats);
            Status = $"{stats.Pixels:N0} 画素を数えました。";
            BuildRows(stats);
            Draw();
            Raise(nameof(HasVector));
            Raise(nameof(FirstLabel));
            Raise(nameof(SecondLabel));
            Raise(nameof(ThirdLabel));
        }
        catch (Exception ex)
        {
            Status = $"集計できませんでした: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyTargetChanged();
        }
    }

    private static string DescribeReading(ScopeStatistics stats)
    {
        var parts = new List<string>();
        if (stats.IsYcbcr)
        {
            parts.Add($"matrix {stats.Options.Interpretation.Matrix}");
            parts.Add($"range {stats.Options.Interpretation.Range}");
            parts.Add(stats.Options.Upsample == ChromaUpsample.Nearest ? "色差は最近傍" : "色差はバイリニア");
        }
        else
        {
            parts.Add("RGB（matrix と range は効きません）");
        }

        return "この条件で数えました: " + string.Join(" / ", parts)
            + "。成分の選択と段は集計に効きません（RAWに入っている値そのものを数えます）。";
    }

    private void BuildRows(ScopeStatistics stats)
    {
        Rows.Clear();
        foreach (var channel in stats.Channels) Rows.Add(new ChannelStatRow(channel, stats));

        var clipped = stats.ClippedTotal;
        ClipNote = clipped == 0
            ? $"変換後に 0-1 の外へ出る画素はありません（{stats.Pixels:N0} 画素中 0）。"
            : $"変換後に 0-1 の外へ出る画素が {clipped:N0} 個あります"
              + $"（{stats.Pixels:N0} 画素の {clipped * 100.0 / stats.Pixels:0.##}%／"
              + $"上へ {stats.ClippedOver:N0}・下へ {stats.ClippedUnder:N0}・両方へ {stats.ClippedBoth:N0}）。"
              + "画面へ出すときに丸められるので、この画素は元の値へは戻せません。";

        WaveformNote = stats.PixelsPerColumn == 1
            ? $"横は画素の位置そのものです（{stats.WaveformColumns} 列）。"
            : $"横は {stats.PixelsPerColumn} 画素ぶんを1列に束ねています"
              + $"（{stats.Width} 画素 → {stats.WaveformColumns} 列）。細い縦線は隣と混ざります。";
        WaveformNote += stats.BitDepth == 8
            ? " 縦はコード値 0-255 です。"
            : $" 縦はコード値 0-{stats.MaxCode} を 256 段へ束ねています。";

        VectorNote = "中心（Cb=128, Cr=128）が無彩色です。十字は中心と規定範囲の境目、"
            + "小さな印は、いまの matrix と range で計算した原色・補色の位置です"
            + "（規格の図版ではなく、この場で計算しています）。印から外れていれば、"
            + "その matrix で読むのは正しくありません。";
    }

    // --- 描画 ---
    //
    // 目盛りの数字までは描き込みません。文字を絵に焼くと、拡大したときに読めなくなるためです。
    // 代わりに、意味のある位置（規定範囲の境目、無彩色の中心）だけへ線を引き、
    // 数字は下の表のほうに出します。

    private void Draw()
    {
        if (_statistics is not { } stats) return;

        FirstHistogram = DrawHistogram(stats, 0);
        SecondHistogram = DrawHistogram(stats, 1);
        ThirdHistogram = DrawHistogram(stats, 2);
        Waveform = DrawWaveform(stats);
        Vector = HasVector ? DrawVector(stats) : null;
    }

    private const int HistogramWidth = 512;
    private const int HistogramHeight = 120;

    /// <summary>成分の色です。どの絵がどの成分かを、並び順ではなく色でも分かるようにします。</summary>
    private (byte R, byte G, byte B) ChannelColor(int channel) => HasVector
        ? channel switch
        {
            0 => ((byte)235, (byte)235, (byte)235), // Y'
            1 => ((byte)120, (byte)170, (byte)255), // Cb（青寄り）
            _ => ((byte)255, (byte)140, (byte)140), // Cr（赤寄り）
        }
        : channel switch
        {
            0 => ((byte)255, (byte)120, (byte)120),
            1 => ((byte)130, (byte)230, (byte)130),
            _ => ((byte)130, (byte)160, (byte)255),
        };

    private BitmapSource DrawHistogram(ScopeStatistics stats, int channel)
    {
        var pixels = NewCanvas(HistogramWidth, HistogramHeight);
        var counts = stats.Histogram[channel];
        var peak = Math.Max(1, stats.PeakCount(channel));
        var (r, g, b) = ChannelColor(channel);

        // 規定範囲の境目に縦線を引きます。limited のRAWで
        // 「16 より下に画素がある」ことは、目で見るより先に位置で分かります。
        var stat = stats.Channels[channel];
        if (stat.NominalLow > 0) DrawColumn(pixels, HistogramWidth, HistogramHeight, BinToX(stat.NominalLow, stats), 90, 90, 110);
        if (stat.NominalHigh < stats.MaxCode) DrawColumn(pixels, HistogramWidth, HistogramHeight, BinToX(stat.NominalHigh, stats), 90, 90, 110);

        // 1列に複数のコード値が入るので、その列の最大を採ります。
        // 平均にすると、1画素だけの値が隣に薄められて消えます。
        var columnPeak = new int[HistogramWidth];
        for (var code = 0; code < counts.Length; code++)
        {
            var x = BinToX(code, stats);
            columnPeak[x] = Math.Max(columnPeak[x], counts[code]);
        }

        for (var x = 0; x < HistogramWidth; x++)
        {
            if (columnPeak[x] == 0) continue;
            var height = (int)Math.Round(Normalize(columnPeak[x], peak) * (HistogramHeight - 1));
            for (var y = HistogramHeight - 1; y >= HistogramHeight - 1 - height; y--)
                SetPixel(pixels, HistogramWidth, x, y, r, g, b);
        }

        return Freeze(pixels, HistogramWidth, HistogramHeight);
    }

    private static int BinToX(int code, ScopeStatistics stats) =>
        Math.Clamp(code * (HistogramWidth - 1) / stats.MaxCode, 0, HistogramWidth - 1);

    private BitmapSource DrawWaveform(ScopeStatistics stats)
    {
        var width = stats.WaveformColumns;
        var height = ScopeStatistics.WaveformLevels;
        var pixels = NewCanvas(width, height);
        var peak = Math.Max(1, stats.WaveformPeak());

        // 規定範囲の境目に横線を引きます。波形で見たいのは、まさにここを割っているかどうかです。
        var stat = stats.Channels[ChannelIndex(stats.WaveformChannel)];
        if (stat.NominalLow > 0) DrawRow(pixels, width, height, LevelToY(stat.NominalLow, stats), 90, 90, 110);
        if (stat.NominalHigh < stats.MaxCode) DrawRow(pixels, width, height, LevelToY(stat.NominalHigh, stats), 90, 90, 110);

        var (r, g, b) = ChannelColor(ChannelIndex(stats.WaveformChannel));

        for (var column = 0; column < width; column++)
        {
            for (var level = 0; level < height; level++)
            {
                var count = stats.Waveform[column * height + level];
                if (count == 0) continue;
                var intensity = Normalize(count, peak);
                // 上が明るい値になるように、縦は反転します（波形モニタと同じ向き）。
                SetPixel(pixels, width, column, height - 1 - level,
                    Scale(r, intensity), Scale(g, intensity), Scale(b, intensity));
            }
        }

        return Freeze(pixels, width, height);
    }

    private static int ChannelIndex(ChannelMask mask) => mask switch
    {
        ChannelMask.Second => 1,
        ChannelMask.Third => 2,
        _ => 0,
    };

    private static int LevelToY(int code, ScopeStatistics stats) =>
        ScopeStatistics.WaveformLevels - 1
        - Math.Clamp((int)((long)code * (ScopeStatistics.WaveformLevels - 1) / stats.MaxCode),
            0, ScopeStatistics.WaveformLevels - 1);

    private BitmapSource DrawVector(ScopeStatistics stats)
    {
        const int size = ScopeStatistics.VectorSize;
        var pixels = NewCanvas(size, size);
        var peak = Math.Max(1, stats.VectorPeak());

        // 無彩色の位置（Cb=Cr=128）に十字を引きます。ここから離れているほど色が付いています。
        DrawColumn(pixels, size, size, 128, 70, 70, 90);
        DrawRow(pixels, size, size, size - 1 - 128, 70, 70, 90);

        for (var cr = 0; cr < size; cr++)
        {
            for (var cb = 0; cb < size; cb++)
            {
                var count = stats.Vector[cr * size + cb];
                if (count == 0) continue;
                var intensity = Normalize(count, peak);
                var value = Scale(235, intensity);
                // Cr は上へ伸ばします（ベクトルスコープの慣習に合わせます）。
                SetPixel(pixels, size, cb, size - 1 - cr, value, value, value);
            }
        }

        foreach (var (cb, cr, color, _) in ColorTargets(stats))
            DrawCross(pixels, size, size, cb, size - 1 - cr, color);

        return Freeze(pixels, size, size);
    }

    /// <summary>
    /// 原色・補色が来るはずの位置です。<b>規格の図版ではなく、その場で計算しています。</b>
    /// いまの matrix と range で、振幅いっぱいの R・G・B・C・M・Y を Y'CbCr へ変換した値です。
    /// 印から外れていれば、その matrix で読むのは正しくありません。
    /// </summary>
    private IEnumerable<(int Cb, int Cr, (byte R, byte G, byte B) Color, string Name)> ColorTargets(ScopeStatistics stats)
    {
        var (kr, kb) = stats.Options.Interpretation.Coefficients;
        var kg = 1.0 - kr - kb;
        var limited = stats.Options.Interpretation.IsLimited;

        (double R, double G, double B, string Name)[] corners =
        [
            (1, 0, 0, "R"), (1, 1, 0, "Y"), (0, 1, 0, "G"),
            (0, 1, 1, "C"), (0, 0, 1, "B"), (1, 0, 1, "M"),
        ];

        foreach (var (r, g, b, name) in corners)
        {
            var y = kr * r + kg * g + kb * b;
            var cb = (b - y) / (2.0 * (1.0 - kb));
            var cr = (r - y) / (2.0 * (1.0 - kr));

            // コード値へ戻します。8bit 相当で置くので、10bit でも同じ位置になります。
            var cbCode = (int)Math.Round(limited ? cb * 224.0 + 128.0 : cb * 255.0 + 128.0);
            var crCode = (int)Math.Round(limited ? cr * 224.0 + 128.0 : cr * 255.0 + 128.0);

            yield return (Math.Clamp(cbCode, 0, ScopeStatistics.VectorSize - 1),
                Math.Clamp(crCode, 0, ScopeStatistics.VectorSize - 1),
                ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)),
                name);
        }
    }

    /// <summary>対数にするかどうかで、山の見え方がまるで変わります。既定は対数です。</summary>
    private double Normalize(int count, int peak) => _logScale
        ? Math.Log(1 + count) / Math.Log(1 + peak)
        : count / (double)peak;

    private static byte Scale(int value, double intensity) =>
        (byte)Math.Clamp((int)Math.Round(value * intensity), 0, 255);

    private static byte[] NewCanvas(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 24;
            pixels[i + 1] = 24;
            pixels[i + 2] = 28;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static void SetPixel(byte[] pixels, int width, int x, int y, byte r, byte g, byte b)
    {
        var index = (y * width + x) * 4;
        if (index < 0 || index + 3 >= pixels.Length) return;
        pixels[index] = b;
        pixels[index + 1] = g;
        pixels[index + 2] = r;
        pixels[index + 3] = 255;
    }

    private static void DrawColumn(byte[] pixels, int width, int height, int x, byte r, byte g, byte b)
    {
        for (var y = 0; y < height; y++) SetPixel(pixels, width, x, y, r, g, b);
    }

    private static void DrawRow(byte[] pixels, int width, int height, int y, byte r, byte g, byte b)
    {
        for (var x = 0; x < width; x++) SetPixel(pixels, width, x, y, r, g, b);
    }

    private static void DrawCross(byte[] pixels, int width, int height, int x, int y, (byte R, byte G, byte B) color)
    {
        for (var d = -3; d <= 3; d++)
        {
            SetPixel(pixels, width, Math.Clamp(x + d, 0, width - 1), y, color.R, color.G, color.B);
            SetPixel(pixels, width, x, Math.Clamp(y + d, 0, height - 1), color.R, color.G, color.B);
        }
    }

    private static BitmapSource Freeze(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>波形に出す成分の選択肢です。</summary>
public sealed record WaveformChannelOption(ChannelMask Channel, string Label)
{
    public override string ToString() => Label;
}

/// <summary>数値の表の1行ぶんです。</summary>
public sealed class ChannelStatRow
{
    public ChannelStatRow(ChannelStat stat, ScopeStatistics stats)
    {
        Label = stat.Label;
        Range = $"{stat.Min} – {stat.Max}";
        Mean = $"{stat.Mean:0.0}";
        Distinct = $"{stat.Distinct:N0} 種";
        Nominal = stat.NominalLow == 0 && stat.NominalHigh == stats.MaxCode
            ? "—"
            : $"{stat.NominalLow} – {stat.NominalHigh}";
        Outside = !stat.HasOutside
            ? "0"
            : $"下 {stat.BelowNominal:N0} / 上 {stat.AboveNominal:N0}";
        HasOutside = stat.HasOutside;
    }

    public string Label { get; }

    /// <summary>実際に出てきたコード値の幅です。</summary>
    public string Range { get; }

    public string Mean { get; }

    /// <summary>使われているコード値の種類数です。階調の粗さがここに出ます。</summary>
    public string Distinct { get; }

    /// <summary>規定の範囲（limited のときだけ内側になります）。</summary>
    public string Nominal { get; }

    /// <summary>規定の範囲を外れた画素数です。0 でないことに意味があります。</summary>
    public string Outside { get; }

    public bool HasOutside { get; }
}

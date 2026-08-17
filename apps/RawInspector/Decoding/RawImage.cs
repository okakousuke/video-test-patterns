using System.IO;
using RawInspector.Models;

namespace RawInspector.Decoding;

/// <summary>
/// RAWバイト列を保持し、画素ごとのコード値読み出しと表示用ビットマップ生成を担当します。
///
/// プレビュー生成とピクセルプローブは、どちらも <see cref="ReadCodes"/> を通ります。
/// デコード済みの画素値を別に持たせると、表示と数値がずれても気付けないためです。
/// </summary>
public sealed class RawImage
{
    private readonly byte[] _data;
    private readonly ManifestInfo _manifest;

    private readonly bool _isYcbcr;
    private readonly bool _isPlanar;
    private readonly bool _isPacked;
    private readonly bool _isNv12;
    private readonly bool _isP010;
    private readonly bool _isV210;
    private readonly bool _isMipi10;
    private readonly bool _is422;
    private readonly bool _isBgr;
    private readonly int _bytesPerSample;
    private readonly int _chromaWidth;
    private readonly int _chromaHeight;

    public int Width { get; }
    public int Height { get; }
    public int BitDepth { get; }
    public int MaxCode { get; }

    /// <summary>manifestに記録された解釈です。プローブと初期表示の既定値になります。</summary>
    public ColorInterpretation DefaultInterpretation { get; }

    private RawImage(byte[] data, ManifestInfo manifest)
    {
        _data = data;
        _manifest = manifest;

        Width = manifest.Width;
        Height = manifest.Height;
        BitDepth = manifest.BitDepth;
        MaxCode = (1 << manifest.BitDepth) - 1;

        _isYcbcr = manifest.IsYcbcr;
        _isPlanar = ManifestInfo.Same(manifest.Storage, "planar");
        _isPacked = ManifestInfo.Same(manifest.Storage, "packed");
        _isNv12 = ManifestInfo.Same(manifest.Storage, "nv12");
        _isP010 = ManifestInfo.Same(manifest.Storage, "p010");
        _isV210 = ManifestInfo.Same(manifest.Storage, "v210");
        _isMipi10 = ManifestInfo.Same(manifest.Storage, "mipi10");
        _is422 = ManifestInfo.Same(manifest.Subsampling, "4:2:2");
        _isBgr = ManifestInfo.Same(manifest.ChannelOrder, "BGR");
        _bytesPerSample = manifest.BitDepth == 10 ? 2 : 1;
        (_chromaWidth, _chromaHeight) = ChromaPlaneSize(manifest);

        DefaultInterpretation = ColorInterpretation.FromManifest(manifest);
    }

    public static RawImage Load(string rawPath, ManifestInfo manifest)
    {
        if (!File.Exists(rawPath))
            throw new FileNotFoundException("manifestが指すRAWファイルがありません。", rawPath);

        var expected = ExpectedMinimumBytes(manifest);
        var fileLength = new FileInfo(rawPath).Length;
        if (fileLength < expected)
            throw new InvalidDataException(
                $"RAWサイズが不足しています: 実ファイル {fileLength:N0} バイト < 必要最小 {expected:N0} バイト。"
                + "画像サイズ・ビット深度・格納形式の指定が実データと合っているか確認してください。");

        return new RawImage(File.ReadAllBytes(rawPath), manifest);
    }

    /// <summary>
    /// 色差プレーンの大きさを返します。4:4:4 は輝度と同じ、4:2:2 は幅が半分、
    /// 4:2:0 は幅も高さも半分です。
    /// </summary>
    public static (int Width, int Height) ChromaPlaneSize(ManifestInfo manifest)
    {
        var width = ManifestInfo.Same(manifest.Subsampling, "4:4:4") ? manifest.Width : manifest.Width / 2;
        var height = ManifestInfo.Same(manifest.Subsampling, "4:2:0") ? manifest.Height / 2 : manifest.Height;
        return (width, height);
    }

    /// <summary>
    /// 格納形式から求まる、RAWに最低限必要なバイト数です。
    /// これを下回るファイルは行境界がずれるため、読み出す前に弾きます。
    /// </summary>
    public static int ExpectedMinimumBytes(ManifestInfo manifest)
    {
        var bytesPerSample = manifest.BitDepth == 10 ? 2 : 1;

        if (ManifestInfo.Same(manifest.Storage, "v210"))
            return checked(V210RowStride(manifest.Width) * manifest.Height);

        if (ManifestInfo.Same(manifest.Storage, "mipi10"))
            return Mipi10ExpectedBytes(manifest);

        if (ManifestInfo.Same(manifest.Storage, "nv12") || ManifestInfo.Same(manifest.Storage, "p010"))
            return checked(manifest.Width * manifest.Height * 3 / 2 * bytesPerSample);

        if (ManifestInfo.Same(manifest.Storage, "planar"))
        {
            // I444 / I422 / I420。色差プレーンはサブサンプリングぶん小さくなります。
            var (cw, ch) = ChromaPlaneSize(manifest);
            return checked(manifest.Width * manifest.Height * bytesPerSample + cw * ch * bytesPerSample * 2);
        }

        // packed。4:2:2 は UYVY（2画素4バイト）、4:4:4 は1画素3バイトです。
        if (manifest.IsYcbcr && ManifestInfo.Same(manifest.Subsampling, "4:2:2"))
            return checked(manifest.Width * manifest.Height * 2);

        return checked(manifest.Width * manifest.Height * 3 * bytesPerSample);
    }

    /// <summary>
    /// 指定画素の生のコード値を返します。Y'CbCrなら (Y', Cb, Cr)、RGBなら (R, G, B) の順です。
    /// packed形式で channel_order が BGR の場合も、ここで R・G・B の順へ揃えます。
    /// </summary>
    public (int First, int Second, int Third) ReadCodes(int x, int y)
    {
        if (_isV210) return ReadV210(x, y);
        if (_isMipi10) return ReadMipi10(x, y);

        var pixel = y * Width + x;

        if (_isNv12 || _isP010)
        {
            var ySize = Width * Height;
            var alignment = _isP010 ? "msb" : _manifest.Alignment;
            var chroma = ySize * _bytesPerSample
                + (y / 2) * Width * _bytesPerSample
                + (x / 2) * 2 * _bytesPerSample;
            return (ReadCode(pixel * _bytesPerSample, alignment),
                ReadCode(chroma, alignment),
                ReadCode(chroma + _bytesPerSample, alignment));
        }

        // UYVY は packed のときだけです。4:2:2 でも planar なら下の I422 として読みます。
        // ここで storage を見ていないと、4:2:2 planar を UYVY として読んでしまいます。
        if (_isYcbcr && _is422 && _isPacked)
        {
            // UYVY: 2画素で Cb Y0 Cr Y1 の4バイト。
            var source = (y * Width + x / 2 * 2) * 2;
            return (_data[source + (x % 2 == 0 ? 1 : 3)], _data[source], _data[source + 2]);
        }

        if (_isPlanar)
        {
            // I444 / I422 / I420。輝度プレーンの後ろに、間引いた色差プレーンが2枚続きます。
            var alignment = _manifest.Alignment;
            var yPlane = Width * Height * _bytesPerSample;
            var chromaPlane = _chromaWidth * _chromaHeight * _bytesPerSample;
            var cx = _chromaWidth == Width ? x : x / 2;
            var cy = _chromaHeight == Height ? y : y / 2;
            var chromaOffset = (cy * _chromaWidth + cx) * _bytesPerSample;
            return (ReadCode(pixel * _bytesPerSample, alignment),
                ReadCode(yPlane + chromaOffset, alignment),
                ReadCode(yPlane + chromaPlane + chromaOffset, alignment));
        }

        var packed = pixel * 3;
        return (_data[packed + (_isBgr ? 2 : 0)], _data[packed + 1], _data[packed + (_isBgr ? 0 : 2)]);
    }

    /// <summary>色差が間引かれている（＝アップサンプル方式が結果に効く）形式かどうか。</summary>
    public bool HasSubsampledChroma => _isYcbcr && !ManifestInfo.Same(_manifest.Subsampling, "4:4:4");

    /// <summary>
    /// 色差サンプル1つが受け持つ画素の数です（4:2:0 なら 2 x 2、4:2:2 なら 2 x 1）。
    /// 間引きが無ければ 1 x 1 です。この範囲の画素は同じ色差を共有しているので、
    /// 「どこまでが同じ色か」の境目がここになります。
    /// </summary>
    public int ChromaBlockWidth => _chromaWidth == 0 ? 1 : Width / _chromaWidth;

    public int ChromaBlockHeight => _chromaHeight == 0 ? 1 : Height / _chromaHeight;

    /// <summary>成分の呼び名です。Y'CbCr なら Y'/Cb/Cr、RGB なら R/G/B。</summary>
    public (string First, string Second, string Third) ChannelLabels =>
        _isYcbcr ? ("Y'", "Cb", "Cr") : ("R", "G", "B");

    /// <summary>そのRAWに存在する変換の段です。色モデルと色差の間引きで変わります。</summary>
    public IReadOnlyList<PipelineStageOption> Stages => PipelineStages.For(_isYcbcr, HasSubsampledChroma);

    /// <summary>
    /// 指定された段が、このRAWに存在するかどうかです。
    /// 別のRAWを開いたときに、前のRAWにしか無い段が選ばれたままになるのを防ぎます。
    /// </summary>
    public bool HasStage(PipelineStage stage) => Stages.Any(option => option.Stage == stage);

    /// <summary>
    /// 実際に使う段です。存在しない段を指定されたら、いちばん近い意味の段へ寄せます。
    /// 黙って既定へ戻すと、RAWを選び直すたびに段が外れて理由が分かりません。
    /// </summary>
    private PipelineStage EffectiveStage(PreviewRenderOptions options)
    {
        if (!_isYcbcr) return PipelineStage.Display;
        // 4:4:4 に「色差を戻す段」はありません。戻す相手が無いので1段目と同じ値です。
        if (options.Stage == PipelineStage.Chroma && !HasSubsampledChroma) return PipelineStage.Codes;
        return options.Stage;
    }

    /// <summary>
    /// その段で使う色差の戻し方です。
    /// 1段目は「格納されたまま」なので、バイリニアを選んでいても補間しません。
    /// ここで補間してしまうと、1段目と2段目の差（＝戻し方が絵に効く量）が消えます。
    /// </summary>
    private static ChromaUpsample StageUpsample(PipelineStage stage, PreviewRenderOptions options) =>
        stage == PipelineStage.Codes ? ChromaUpsample.Nearest : options.Upsample;

    /// <summary>
    /// 画面に出す1画素です。<b>プレビューもピクセルプローブもここを通ります。</b>
    /// 段ごとに別の描き方を持たせると、絵と数値が食い違っても気付けません。
    /// </summary>
    public (byte R, byte G, byte B) RenderPixel(int x, int y, PreviewRenderOptions options)
    {
        var stage = EffectiveStage(options);
        var (first, second, third) = ReadCodes(x, y, StageUpsample(stage, options));
        return ToRgb(first, second, third, options, stage);
    }

    /// <summary>指定画素の、コード値と表示RGBを対にして返します。</summary>
    public PixelSample Sample(int x, int y, PreviewRenderOptions options)
    {
        var stage = EffectiveStage(options);
        var upsample = StageUpsample(stage, options);
        var (first, second, third) = ReadCodes(x, y, upsample);
        var (r, g, b) = ToRgb(first, second, third, options, stage);
        var (l1, l2, l3) = ChannelLabels;
        var interpolated = upsample == ChromaUpsample.Bilinear && HasSubsampledChroma;
        return new PixelSample(x, y, l1, l2, l3, first, second, third, MaxCode, r, g, b, interpolated);
    }

    /// <summary>
    /// WPFのBgra32ビットマップへそのまま渡せるバイト列を作ります（1画素4バイト、B G R A の順）。
    /// </summary>
    public byte[] ToBgra32(PreviewRenderOptions options)
    {
        var buffer = new byte[checked(Width * Height * 4)];
        var stride = Width * 4;
        var stage = EffectiveStage(options);
        var upsample = StageUpsample(stage, options);

        for (var y = 0; y < Height; y++)
        {
            var rowStart = y * stride;
            for (var x = 0; x < Width; x++)
            {
                var (first, second, third) = ReadCodes(x, y, upsample);
                var (r, g, b) = ToRgb(first, second, third, options, stage);
                var target = rowStart + x * 4;
                buffer[target] = b;
                buffer[target + 1] = g;
                buffer[target + 2] = r;
                buffer[target + 3] = 255;
            }
        }

        return buffer;
    }

    /// <summary>
    /// アップサンプル方式を指定してコード値を読みます。
    /// 最近傍のときは <see cref="ReadCodes(int,int)"/> と同じで、格納された値をそのまま返します。
    /// </summary>
    public (int First, int Second, int Third) ReadCodes(int x, int y, ChromaUpsample upsample)
    {
        if (upsample == ChromaUpsample.Nearest || !HasSubsampledChroma) return ReadCodes(x, y);

        var (luma, _, _) = ReadCodes(x, y);
        return (luma, InterpolateChroma(x, y, second: true), InterpolateChroma(x, y, second: false));
    }

    /// <summary>
    /// 隣り合う色差サンプルの間を線形に補間します。
    ///
    /// 生成側は色差を「並んだ画素の平均」で間引いています。つまり色差サンプル j の中心は、
    /// 輝度の座標でいうと j*s + (s-1)/2 の位置にあります（s は間引きの比）。
    /// 逆に輝度の位置 x に対応する色差の連続座標は (x - (s-1)/2) / s です。
    /// この位置合わせを外すと、補間しただけで絵が半画素ずれます。
    /// </summary>
    private int InterpolateChroma(int x, int y, bool second)
    {
        var scaleX = Width / _chromaWidth;
        var scaleY = Height / _chromaHeight;

        var cx = (x - (scaleX - 1) / 2.0) / scaleX;
        var cy = (y - (scaleY - 1) / 2.0) / scaleY;

        var x0 = (int)Math.Floor(cx);
        var y0 = (int)Math.Floor(cy);
        var fx = cx - x0;
        var fy = cy - y0;

        var v00 = ChromaAt(x0, y0, second);
        var v10 = ChromaAt(x0 + 1, y0, second);
        var v01 = ChromaAt(x0, y0 + 1, second);
        var v11 = ChromaAt(x0 + 1, y0 + 1, second);

        var top = v00 + (v10 - v00) * fx;
        var bottom = v01 + (v11 - v01) * fx;
        return (int)Math.Round(top + (bottom - top) * fy);
    }

    /// <summary>色差プレーンから1サンプル読みます。端は外挿せず、いちばん外の値を使います。</summary>
    private int ChromaAt(int cx, int cy, bool second)
    {
        cx = Math.Clamp(cx, 0, _chromaWidth - 1);
        cy = Math.Clamp(cy, 0, _chromaHeight - 1);

        // 色差の格納位置は形式ごとに違うため、輝度の座標へ戻してから既存の読み出しを使います。
        var lumaX = Math.Min(cx * (Width / _chromaWidth), Width - 1);
        var lumaY = Math.Min(cy * (Height / _chromaHeight), Height - 1);
        var (_, cb, cr) = ReadCodes(lumaX, lumaY);
        return second ? cb : cr;
    }

    /// <summary>
    /// 全画素を数えて、分布と内訳を返します。
    ///
    /// <b>読み出しはここでも <see cref="ReadCodes(int,int,ChromaUpsample)"/> の1本です。</b>
    /// 集計用に別の読み方を書くと、絵と数字が食い違っても気付けません。
    ///
    /// 効かせるのは matrix・range・色差の戻し方だけです。成分の選択と段は無視します。
    /// ここで見たいのはRAWに入っている値そのものであって、いま出している絵ではないためです。
    /// </summary>
    public ScopeStatistics Analyze(PreviewRenderOptions options, ChannelMask waveformChannel)
    {
        // 集計に使う条件を、絵の条件から切り離しておきます。
        var reading = options with { Channels = ChannelMask.All, RawCodeGray = false, Stage = PipelineStage.Display };

        var histogram = new[] { new int[MaxCode + 1], new int[MaxCode + 1], new int[MaxCode + 1] };
        var sums = new double[3];
        var mins = new[] { int.MaxValue, int.MaxValue, int.MaxValue };
        var maxs = new[] { int.MinValue, int.MinValue, int.MinValue };

        var columns = Math.Min(Width, ScopeStatistics.MaxWaveformColumns);
        // 端数を捨てないよう切り上げます。捨てると右端の列がまるごと数から漏れます。
        var pixelsPerColumn = (Width + columns - 1) / columns;
        columns = (Width + pixelsPerColumn - 1) / pixelsPerColumn;
        var waveform = new int[columns * ScopeStatistics.WaveformLevels];

        var vector = _isYcbcr ? new int[ScopeStatistics.VectorSize * ScopeStatistics.VectorSize] : [];
        var shift = 1 << (BitDepth - 8);

        int over = 0, under = 0, both = 0;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (first, second, third) = ReadCodes(x, y, reading.Upsample);

                histogram[0][Math.Clamp(first, 0, MaxCode)]++;
                histogram[1][Math.Clamp(second, 0, MaxCode)]++;
                histogram[2][Math.Clamp(third, 0, MaxCode)]++;
                sums[0] += first;
                sums[1] += second;
                sums[2] += third;
                mins[0] = Math.Min(mins[0], first);
                mins[1] = Math.Min(mins[1], second);
                mins[2] = Math.Min(mins[2], third);
                maxs[0] = Math.Max(maxs[0], first);
                maxs[1] = Math.Max(maxs[1], second);
                maxs[2] = Math.Max(maxs[2], third);

                var picked = waveformChannel switch
                {
                    ChannelMask.Second => second,
                    ChannelMask.Third => third,
                    _ => first,
                };
                var level = (int)((long)Math.Clamp(picked, 0, MaxCode) * (ScopeStatistics.WaveformLevels - 1) / MaxCode);
                waveform[x / pixelsPerColumn * ScopeStatistics.WaveformLevels + level]++;

                if (_isYcbcr)
                {
                    var cb = Math.Clamp(second / shift, 0, ScopeStatistics.VectorSize - 1);
                    var cr = Math.Clamp(third / shift, 0, ScopeStatistics.VectorSize - 1);
                    vector[cr * ScopeStatistics.VectorSize + cb]++;
                }

                var (isOver, isUnder) = RangeFlags(first, second, third, reading);
                if (isOver && isUnder) both++;
                else if (isOver) over++;
                else if (isUnder) under++;
            }
        }

        var (l1, l2, l3) = ChannelLabels;
        var labels = new[] { l1, l2, l3 };
        var stats = new ChannelStat[3];
        for (var i = 0; i < 3; i++)
        {
            var (low, high) = NominalRange(i, options.Interpretation);
            var distinct = 0;
            var below = 0;
            var above = 0;
            for (var code = 0; code <= MaxCode; code++)
            {
                var count = histogram[i][code];
                if (count == 0) continue;
                distinct++;
                if (code < low) below += count;
                if (code > high) above += count;
            }

            stats[i] = new ChannelStat(labels[i], mins[i], maxs[i], sums[i] / (Width * (double)Height),
                distinct, low, high, below, above);
        }

        return new ScopeStatistics
        {
            Width = Width,
            Height = Height,
            BitDepth = BitDepth,
            MaxCode = MaxCode,
            IsYcbcr = _isYcbcr,
            IsLimited = options.Interpretation.IsLimited,
            Options = reading,
            Histogram = histogram,
            Channels = stats,
            Waveform = waveform,
            WaveformColumns = columns,
            PixelsPerColumn = pixelsPerColumn,
            WaveformChannel = waveformChannel,
            Vector = vector,
            ClippedOver = over,
            ClippedUnder = under,
            ClippedBoth = both,
        };
    }

    /// <summary>
    /// その成分の「規定の範囲」です。limited のときだけ、格納できる範囲より内側になります。
    /// 輝度は 16-235、色差は 16-240（いずれも8bit換算）で、この外は放送の規定では使いません。
    /// full range と RGB は格納できる範囲がそのまま規定の範囲です。
    /// </summary>
    private (int Low, int High) NominalRange(int channel, ColorInterpretation interpretation)
    {
        if (!_isYcbcr || !interpretation.IsLimited) return (0, MaxCode);
        var shift = 1 << (BitDepth - 8);
        return channel == 0 ? (16 * shift, 235 * shift) : (16 * shift, 240 * shift);
    }

    /// <summary>
    /// 丸める前の値が 0-1 の外へ出ているかどうかです。
    /// プレビューの「範囲外」表示と<b>同じ判定を通します</b>。数と色が食い違わないようにするためです。
    /// </summary>
    private (bool Over, bool Under) RangeFlags(int first, int second, int third, PreviewRenderOptions options)
    {
        var (_, _, _, over, under) = StageValues(first, second, third, options, PipelineStage.Rgb);
        return (over, under);
    }

    /// <summary>選ばなかった成分に入れる値です。成分ごとに「無いこと」の表し方が違います。</summary>
    private int NeutralCode(int index, PreviewRenderOptions options)
    {
        var shift = 1 << (BitDepth - 8);

        // RGB は加算なので、無い成分は 0 です。
        if (!_isYcbcr) return 0;

        // 色差の中立は 0 ではなく中央です。0 は「振り切っている」という意味になります。
        if (index != 0) return 128 * shift;

        // 輝度の中立は、そのrangeで 0.5 にあたるコード値です。
        // 0 にすると真っ黒になり、色差だけを見たいときに何も見えません。
        return options.Interpretation.IsLimited
            ? (int)Math.Round(16.0 * shift + 219.0 * shift * 0.5)
            : (int)Math.Round(MaxCode * 0.5);
    }

    private (byte R, byte G, byte B) ToRgb(int first, int second, int third, PreviewRenderOptions options, PipelineStage stage)
    {
        // 成分を1つだけ選び、かつコード値のまま見る指定のときは、色変換を通しません。
        // 通すと range のぶん伸縮して、見たい成分の値と画面の明るさが一致しなくなるためです。
        if (options.UseRawCodeGray)
        {
            var code = options.Channels switch
            {
                ChannelMask.First => first,
                ChannelMask.Second => second,
                _ => third,
            };
            var gray = ToByte(code / (double)MaxCode);
            return (gray, gray, gray);
        }

        // 選ばなかった成分は中立値へ置き換えてから、いつもどおり変換します。
        // 落とすのではなく置き換えるのは、成分どうしの関係を保ったまま1つだけ抜くためです。
        // 置き換えは段によらず先にやります。どの段の値を見るときも、
        // 「抜いた成分は中立値だった」という前提は同じだからです。
        if (!options.Channels.HasFlag(ChannelMask.First)) first = NeutralCode(0, options);
        if (!options.Channels.HasFlag(ChannelMask.Second)) second = NeutralCode(1, options);
        if (!options.Channels.HasFlag(ChannelMask.Third)) third = NeutralCode(2, options);

        // 1・2段目はコード値そのものです。色変換を通していないので、色として読んではいけません。
        if (stage is PipelineStage.Codes or PipelineStage.Chroma)
        {
            // 成分を1つに絞っているなら、その面の値をそのまま濃淡にします
            // （「コード値」の指定と同じ絵です。どちらの入口から来ても同じ値を出します）。
            if (options.SelectedCount == 1)
            {
                var only = options.Channels switch
                {
                    ChannelMask.First => first,
                    ChannelMask.Second => second,
                    _ => third,
                };
                var flat = ToByte(only / (double)MaxCode);
                return (flat, flat, flat);
            }

            return (ToByte(first / (double)MaxCode), ToByte(second / (double)MaxCode), ToByte(third / (double)MaxCode));
        }

        var (a, b2, c, over, under) = StageValues(first, second, third, options, stage);

        if (options.MarkOutOfRange && PipelineStages.SupportsRangeMarking(stage))
            return MarkRange(a, b2, c, over, under, options, stage);

        return (ToByte(a), ToByte(b2), ToByte(c));
    }

    /// <summary>
    /// その段の値と、0-1（色差は ±0.5）の外へ出ているかどうかを返します。
    /// 返す3つは<b>画面へ写したあとの値</b>で、まだ丸めていません。
    /// 丸める前の値を持っておかないと、「潰れた」のか「もともとその値だった」のかが区別できません。
    /// </summary>
    private (double A, double B, double C, bool Over, bool Under) StageValues(
        int first, int second, int third, PreviewRenderOptions options, PipelineStage stage)
    {
        if (!_isYcbcr)
            return (first / (double)MaxCode, second / (double)MaxCode, third / (double)MaxCode, false, false);

        var (kr, kb) = options.Interpretation.Coefficients;
        var kg = 1.0 - kr - kb;
        var shift = 1 << (BitDepth - 8);
        double peak = MaxCode;

        var y = options.Interpretation.IsLimited ? (first - 16.0 * shift) / (219.0 * shift) : first / peak;
        var cb = options.Interpretation.IsLimited ? (second - 128.0 * shift) / (224.0 * shift) : (second - 128.0 * shift) / peak;
        var cr = options.Interpretation.IsLimited ? (third - 128.0 * shift) / (224.0 * shift) : (third - 128.0 * shift) / peak;

        if (stage == PipelineStage.Normalized)
            // Cb・Cr は 0 を中心に ±0.5 で振れる値なので、濃淡にするには +0.5 します。
            // そのままだと負の側が全部黒へ潰れて、振れの向きが読めません。
            return (y, cb + 0.5, cr + 0.5,
                Above(y) || Above(cb + 0.5) || Above(cr + 0.5),
                Below(y) || Below(cb + 0.5) || Below(cr + 0.5));

        var r = y + 2.0 * (1.0 - kr) * cr;
        var b = y + 2.0 * (1.0 - kb) * cb;
        var g = (y - kr * r - kb * b) / kg;
        return (r, g, b,
            Above(r) || Above(g) || Above(b),
            Below(r) || Below(g) || Below(b));
    }

    // 範囲外に数えはじめる幅です。画面の 1 コード値（1/255）ぶんだけ余裕を持たせています。
    //
    // 0 を少しでも外れたら数える、にはできません。**正常なカラーバーが一面に光ります。**
    // 生成側は RGB から Y'CbCr を作るときに整数へ丸めているので、戻すと必ず端数が出ます。
    // 手元の 8bit limited / BT.709 カラーバーで測ると、はみ出しは最大 0.00201 でした
    // （8bit の 1 コード = 0.00392 の半分ほど）。これは丸めれば同じ値になる量です。
    //
    // 一方、range や matrix を取り違えたときのはみ出しは桁が違います。同じカラーバーで
    // full を limited として読むと最大 0.094、bt709 を bt601 として読むと最大 0.174 でした。
    // 1 コード値を境にすると、記録どおりに読んだときの範囲外は 0 画素、
    // 取り違えたときは全画素ないし彩度の高いバー全部、という分かれ方になります。
    private const double RangeEpsilon = 1.0 / 255.0;

    private static bool Above(double value) => value > 1.0 + RangeEpsilon;

    private static bool Below(double value) => value < -RangeEpsilon;

    /// <summary>
    /// 範囲外の画素を色で示します。
    ///
    /// <b>絵のほうは無彩色にします。</b> 元の色を残したまま赤や青を重ねると、
    /// 「もともと赤い画素」と「範囲外だから赤くした画素」が見分けられません。
    /// 無彩色の上なら、飽和した赤・青・マゼンタはこの表示でしか出ません。
    /// </summary>
    private (byte R, byte G, byte B) MarkRange(
        double a, double b, double c, bool over, bool under, PreviewRenderOptions options, PipelineStage stage)
    {
        if (over && under) return (255, 0, 255); // 成分によって上下どちらへも出ている
        if (over) return (255, 0, 0);
        if (under) return (0, 0, 255);

        // 正規化の段では1つ目がそのまま輝度です。RGBの段は matrix の係数で輝度に落とします。
        double luma;
        if (stage == PipelineStage.Normalized)
        {
            luma = a;
        }
        else
        {
            var (kr, kb) = options.Interpretation.Coefficients;
            luma = kr * a + (1.0 - kr - kb) * b + kb * c;
        }

        var gray = ToByte(luma);
        return (gray, gray, gray);
    }

    private int ReadCode(int offset, string? alignment)
    {
        if (BitDepth == 8) return _data[offset];
        var container = _data[offset] | (_data[offset + 1] << 8);
        return ManifestInfo.Same(alignment, "msb") ? container >> 6 : container & 0x03ff;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255.0), 0, 255);

    // --- v210: 6画素を4個の32bitワードへ詰め、行は128バイト境界へ揃えます。 ---

    private static int V210RowStride(int width) => ((width / 6 * 16 + 127) / 128) * 128;

    private (int Y, int Cb, int Cr) ReadV210(int x, int y)
    {
        var group = x / 6;
        var offset = y * V210RowStride(Width) + group * 16;
        var w0 = ReadUInt32LittleEndian(offset);
        var w1 = ReadUInt32LittleEndian(offset + 4);
        var w2 = ReadUInt32LittleEndian(offset + 8);
        var w3 = ReadUInt32LittleEndian(offset + 12);
        return (x % 6) switch
        {
            0 => (Field(w0, 1), Field(w0, 0), Field(w0, 2)),
            1 => (Field(w1, 0), Field(w0, 0), Field(w0, 2)),
            2 => (Field(w1, 2), Field(w1, 1), Field(w2, 0)),
            3 => (Field(w2, 1), Field(w1, 1), Field(w2, 0)),
            4 => (Field(w3, 0), Field(w2, 2), Field(w3, 1)),
            _ => (Field(w3, 2), Field(w2, 2), Field(w3, 1)),
        };
    }

    private uint ReadUInt32LittleEndian(int offset) =>
        (uint)(_data[offset] | _data[offset + 1] << 8 | _data[offset + 2] << 16 | _data[offset + 3] << 24);

    private static int Field(uint word, int position) => (int)((word >> (position * 10)) & 0x03ff);

    // --- MIPI10: 各プレーンを 4サンプル5バイトへ詰めます。 ---

    private static int Mipi10PlaneBytes(int width, int height) => checked(height * (width / 4) * 5);

    private static int Mipi10ExpectedBytes(ManifestInfo manifest)
    {
        var yBytes = Mipi10PlaneBytes(manifest.Width, manifest.Height);
        if (ManifestInfo.Same(manifest.ColorModel, "rgb")) return checked(yBytes * 3);

        var cw = ManifestInfo.Same(manifest.Subsampling, "4:4:4") ? manifest.Width : manifest.Width / 2;
        var ch = ManifestInfo.Same(manifest.Subsampling, "4:2:0") ? manifest.Height / 2 : manifest.Height;
        return checked(yBytes + Mipi10PlaneBytes(cw, ch) * 2);
    }

    private (int First, int Second, int Third) ReadMipi10(int x, int y)
    {
        var yBytes = Mipi10PlaneBytes(Width, Height);

        if (!_isYcbcr)
            return (ReadMipi10Sample(0, Width, x, y),
                ReadMipi10Sample(yBytes, Width, x, y),
                ReadMipi10Sample(yBytes * 2, Width, x, y));

        var cw = ManifestInfo.Same(_manifest.Subsampling, "4:4:4") ? Width : Width / 2;
        var ch = ManifestInfo.Same(_manifest.Subsampling, "4:2:0") ? Height / 2 : Height;
        var cBytes = Mipi10PlaneBytes(cw, ch);
        var cx = cw == Width ? x : x / 2;
        var cy = ch == Height ? y : y / 2;
        return (ReadMipi10Sample(0, Width, x, y),
            ReadMipi10Sample(yBytes, cw, cx, cy),
            ReadMipi10Sample(yBytes + cBytes, cw, cx, cy));
    }

    private int ReadMipi10Sample(int planeOffset, int planeWidth, int x, int y)
    {
        var group = y * (planeWidth / 4) + x / 4;
        var offset = planeOffset + group * 5;
        var index = x % 4;
        return (_data[offset + index] << 2) | ((_data[offset + 4] >> (index * 2)) & 0x03);
    }
}

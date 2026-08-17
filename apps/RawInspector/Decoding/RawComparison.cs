namespace RawInspector.Decoding;

/// <summary>何を突き合わせるかです。</summary>
public enum CompareDomain
{
    /// <summary>
    /// 画面に出る 8bit RGB どうし。<b>見え方</b>の差です。
    /// 変換を通したあとなので、格納形式やビット深度が違っていても比べられます。
    /// </summary>
    Display,

    /// <summary>
    /// RAWから読んだコード値どうし。<b>データ</b>の差です。
    /// 変換を通していないので、8bit へ丸める前の違いがそのまま出ます。
    /// </summary>
    Codes,
}

/// <summary>成分ひとつぶんの差です。</summary>
public readonly record struct DiffChannel(string Label, int Max, double Mean, long Different, int MaxX, int MaxY);

/// <summary>突き合わせた結果です。</summary>
public sealed class ComparisonResult
{
    public required CompareDomain Domain { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required DiffChannel[] Channels { get; init; }

    /// <summary>どれか1成分でも違った画素の数です。</summary>
    public required long DifferentPixels { get; init; }

    /// <summary>比べた値の最大（8bit なら 255、10bit のコード値なら 1023）。差の大きさを読む目安です。</summary>
    public required int Scale { get; init; }

    public long Pixels => (long)Width * Height;

    public int MaxDifference => Channels.Max(c => c.Max);

    public bool IsIdentical => MaxDifference == 0;
}

/// <summary>
/// 2つのRAWを突き合わせます。
///
/// 見比べるだけでは「同じに見える」までしか言えません。
/// <b>1コード値の差は目で見えませんし、見えないからといって無いわけでもありません。</b>
/// このリポジトリの検証（形式を変えても結果が変わらないこと）は、まさにその差を数える作業です。
///
/// <b>大きさの違うものは比べません。</b> 拡大縮小して揃えることもしません。
/// 揃えた時点で、画素は元のどちらとも違う値になります。
/// 「合わせて比べた差」は、どちらのRAWの性質でもありません。
/// </summary>
public sealed class RawComparison
{
    private readonly RawImage _left;
    private readonly RawImage _right;
    private readonly PreviewRenderOptions _leftOptions;
    private readonly PreviewRenderOptions _rightOptions;

    public RawComparison(
        RawImage left, PreviewRenderOptions leftOptions,
        RawImage right, PreviewRenderOptions rightOptions)
    {
        _left = left;
        _right = right;
        _leftOptions = leftOptions;
        _rightOptions = rightOptions;
    }

    public int Width => _left.Width;
    public int Height => _left.Height;

    /// <summary>大きさが同じかどうか。ここが違うと、どの領域でも比べられません。</summary>
    public bool SameSize => _left.Width == _right.Width && _left.Height == _right.Height;

    /// <summary>
    /// コード値で比べられるか。大きさに加えて、<b>成分の意味と目盛りが同じ</b>ことが要ります。
    /// Y' と R を引き算しても意味がありませんし、8bit の 235 と 10bit の 940 の差 705 にも意味がありません。
    /// </summary>
    public bool CanCompareCodes =>
        SameSize && _left.BitDepth == _right.BitDepth
        && _left.ChannelLabels.First == _right.ChannelLabels.First;

    public string? CodesUnavailableReason
    {
        get
        {
            if (!SameSize) return SizeMismatch;
            if (_left.BitDepth != _right.BitDepth)
                return $"ビット深度が違います（{_left.BitDepth}bit と {_right.BitDepth}bit）。"
                    + "目盛りが違う数どうしを引き算しても意味がありません。"
                    + "見え方の差なら「表示RGB」で比べられます（どちらも8bitへ揃えたあとの値です）。";
            if (_left.ChannelLabels.First != _right.ChannelLabels.First)
                return $"成分の意味が違います（{_left.ChannelLabels.First} と {_right.ChannelLabels.First}）。"
                    + "Y' と R を引き算しても意味がありません。"
                    + "見え方の差なら「表示RGB」で比べられます。";
            return null;
        }
    }

    public string SizeMismatch =>
        $"大きさが違います（{_left.Width}×{_left.Height} と {_right.Width}×{_right.Height}）。"
        + "拡大縮小して揃えることはしません。揃えた時点で、画素は元のどちらとも違う値になり、"
        + "その差はどちらのRAWの性質でもなくなるためです。";

    /// <summary>その領域で比べたときの、値の最大です（差の大きさを読む目安になります）。</summary>
    public int ScaleOf(CompareDomain domain) => domain == CompareDomain.Display ? 255 : _left.MaxCode;

    /// <summary>成分の呼び名です。表示RGBで比べるときは、色モデルによらず R・G・B になります。</summary>
    public (string First, string Second, string Third) LabelsOf(CompareDomain domain) =>
        domain == CompareDomain.Display ? ("R", "G", "B") : _left.ChannelLabels;

    /// <summary>全画素を突き合わせて、差を数えます。</summary>
    public ComparisonResult Analyze(CompareDomain domain)
    {
        var (l1, l2, l3) = LabelsOf(domain);
        var labels = new[] { l1, l2, l3 };
        var max = new int[3];
        var maxX = new[] { -1, -1, -1 };
        var maxY = new[] { -1, -1, -1 };
        var sums = new double[3];
        var different = new long[3];
        long differentPixels = 0;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var diff = DiffAt(x, y, domain);
                var any = false;
                for (var i = 0; i < 3; i++)
                {
                    var value = diff[i];
                    sums[i] += value;
                    if (value == 0) continue;
                    any = true;
                    different[i]++;
                    if (value > max[i])
                    {
                        max[i] = value;
                        maxX[i] = x;
                        maxY[i] = y;
                    }
                }
                if (any) differentPixels++;
            }
        }

        var channels = new DiffChannel[3];
        for (var i = 0; i < 3; i++)
            channels[i] = new DiffChannel(labels[i], max[i], sums[i] / (Width * (double)Height),
                different[i], maxX[i], maxY[i]);

        return new ComparisonResult
        {
            Domain = domain,
            Width = Width,
            Height = Height,
            Channels = channels,
            DifferentPixels = differentPixels,
            Scale = ScaleOf(domain),
        };
    }

    /// <summary>
    /// 差そのものを絵にします。
    ///
    /// <b>倍率を掛けないと、たいていの差は真っ黒になります。</b> 1コード値の差は 1/255 なので、
    /// そのまま出せば黒との区別が付きません。「差が無い」のか「差が見えていないだけ」なのかを
    /// 分けるために倍率を掛けますが、掛けたことは画面に出し続ける必要があります。
    /// </summary>
    public byte[] RenderDiffBgra(CompareDomain domain, int amplify)
    {
        var buffer = new byte[checked(Width * Height * 4)];
        var scale = ScaleOf(domain);

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var diff = DiffAt(x, y, domain);
                var target = (y * Width + x) * 4;
                // 成分ごとの差を、そのまま R・G・B の位置へ置きます。
                // どの成分がずれているのかが色で分かります（赤だけずれていれば赤く出ます）。
                buffer[target] = Amplified(diff[2], scale, amplify);
                buffer[target + 1] = Amplified(diff[1], scale, amplify);
                buffer[target + 2] = Amplified(diff[0], scale, amplify);
                buffer[target + 3] = 255;
            }
        }

        return buffer;
    }

    private static byte Amplified(int difference, int scale, int amplify) =>
        (byte)Math.Clamp((int)Math.Round(difference * 255.0 / scale * amplify), 0, 255);

    /// <summary>
    /// 1画素ぶんの差です。<b>どちらの側も、いつもの読み出し経路を通します。</b>
    /// 比較用に別の読み方を用意すると、ここでだけ一致する（あるいはしない）状態が作れてしまいます。
    /// </summary>
    private int[] DiffAt(int x, int y, CompareDomain domain)
    {
        if (domain == CompareDomain.Display)
        {
            var (lr, lg, lb) = _left.RenderPixel(x, y, _leftOptions);
            var (rr, rg, rb) = _right.RenderPixel(x, y, _rightOptions);
            return [Math.Abs(lr - rr), Math.Abs(lg - rg), Math.Abs(lb - rb)];
        }

        var left = _left.ReadCodes(x, y, _leftOptions.Upsample);
        var right = _right.ReadCodes(x, y, _rightOptions.Upsample);
        return
        [
            Math.Abs(left.First - right.First),
            Math.Abs(left.Second - right.Second),
            Math.Abs(left.Third - right.Third),
        ];
    }
}

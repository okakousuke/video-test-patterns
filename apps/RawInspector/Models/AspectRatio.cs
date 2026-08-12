namespace RawInspector.Models;

/// <summary>
/// 画素数の比です。
///
/// **これは「画素がいくつ並んでいるか」の比であって、映したときの形の比ではありません。**
/// 両者が一致するのは画素が正方形のときだけです。
/// たとえば 720 x 480 は画素数では 3:2 ですが、SD の素材は横長の画素を前提にしていて、
/// 映すと 4:3 になります。どちらの前提で作られたものかは RAW にもmanifestにも入っていません。
/// なのでここでは画素数の比だけを出し、映したときの形は名乗りません。
///
/// 名前を添えるのは、比べやすくするためです。1920x1080 と 1280x720 が
/// 同じ 16:9 だと分かれば、拡大縮小で形が崩れないと判断できます。
/// </summary>
public static class AspectRatio
{
    /// <summary>よく使う比の呼び名。約分した比で引きます。</summary>
    private static readonly Dictionary<(int W, int H), string> Names = new()
    {
        [(1, 1)] = "正方形",
        [(4, 3)] = "4:3",
        [(3, 2)] = "3:2",
        [(16, 10)] = "16:10",
        [(8, 5)] = "16:10",     // 1920x1200 は約分すると 8:5
        [(5, 4)] = "5:4",
        [(16, 9)] = "16:9",
        [(64, 27)] = "21:9",    // いわゆるウルトラワイド
        [(43, 18)] = "21.5:9",
        [(11, 9)] = "11:9",     // QCIF / CIF
        [(256, 135)] = "17:9",  // 2K / 4K DCI
    };

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    /// <summary>約分した比を返します。</summary>
    public static (int Width, int Height) Reduce(int width, int height)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        var divisor = Gcd(width, height);
        return (width / divisor, height / divisor);
    }

    /// <summary>
    /// 短い呼び名です。絞り込みの選択肢のように、並べて読むところで使います。
    ///
    /// 約分して 3 桁になると「683:384」のような読めない比になります（1366 x 768 がこれです）。
    /// その場合は近い呼び名に「およそ」を付けます。**まとめてはいません。**
    /// 1366 x 768 は 16:9 ちょうどではないので、同じものとして扱うと
    /// 「16:9 で絞ったのに形が違う」ことになります。
    /// </summary>
    public static string Key(int width, int height)
    {
        if (width <= 0 || height <= 0) return "-";

        var (w, h) = Reduce(width, height);
        if (Names.TryGetValue((w, h), out var name)) return name;
        if (w <= 100 && h <= 100) return $"{w}:{h}";

        var value = width / (double)height;
        var nearest = Names
            .Select(pair => (pair.Value, Difference: Math.Abs(pair.Key.W / (double)pair.Key.H - value)))
            .OrderBy(pair => pair.Difference)
            .First();
        return nearest.Difference < 0.03 ? $"およそ{nearest.Value}" : $"{value:0.###}";
    }

    /// <summary>比べやすいよう、横長のものが大きくなる値を返します（並べ替え用）。</summary>
    public static double Value(int width, int height) =>
        width <= 0 || height <= 0 ? 0 : width / (double)height;

    /// <summary>「16:9（1.778）」の形にします。</summary>
    public static string Describe(int width, int height) =>
        width <= 0 || height <= 0 ? "-" : $"{Key(width, height)}（{Value(width, height):0.###}）";
}

namespace RawInspector.Decoding;

/// <summary>
/// 成分ひとつぶんの数値です。絵を見ただけでは言えないことを数で出します。
/// </summary>
/// <param name="Distinct">
/// 実際に出てくるコード値の種類数です。階調の粗さがここに出ます。
/// 8bit のランプなら 256 に近く、量子化で潰れていれば少なくなります。
/// </param>
/// <param name="BelowNominal">規定の範囲より下にある画素数（limited のとき、輝度なら 16 未満）。</param>
/// <param name="AboveNominal">規定の範囲より上にある画素数（limited のとき、輝度なら 235 超）。</param>
public readonly record struct ChannelStat(
    string Label,
    int Min,
    int Max,
    double Mean,
    int Distinct,
    int NominalLow,
    int NominalHigh,
    int BelowNominal,
    int AboveNominal)
{
    /// <summary>規定の範囲を外れた画素があるか。limited のRAWでは、ここが 0 でないことに意味があります。</summary>
    public bool HasOutside => BelowNominal > 0 || AboveNominal > 0;
}

/// <summary>
/// 面ではなく<b>分布</b>で見るための集計です。
///
/// 絵を眺めても「どこかが 1 コード値だけずれている」「上のほうが少しだけ潰れている」は分かりません。
/// 画素を1つずつ拾って回るのも現実的ではないので、全画素を数えてしまいます。
///
/// <b>集計に効くのは matrix・range・色差の戻し方だけです。</b> 成分の選択と段は効かせません。
/// ここで見たいのはRAWに入っている値そのものであって、いま画面に出している絵ではないためです。
/// （成分を1つだけ表示している状態で分布を見て、それがRAW全体の分布だと読むと間違えます。）
/// </summary>
public sealed class ScopeStatistics
{
    /// <summary>波形の縦の段数です。10bit のRAWでもここへ束ねます（画面の高さがそもそも足りません）。</summary>
    public const int WaveformLevels = 256;

    /// <summary>波形の横の最大列数です。4K をそのまま持つと縦横とも画面に入りません。</summary>
    public const int MaxWaveformColumns = 512;

    /// <summary>ベクトルスコープの一辺です。Cb・Cr を 8bit 相当へ束ねます。</summary>
    public const int VectorSize = 256;

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int BitDepth { get; init; }
    public required int MaxCode { get; init; }
    public required bool IsYcbcr { get; init; }
    public required bool IsLimited { get; init; }

    /// <summary>どの条件で数えたか。画面の条件と違っていれば、それが分かるようにするためです。</summary>
    public required PreviewRenderOptions Options { get; init; }

    /// <summary>成分ごとのヒストグラムです。<b>コード値のまま</b>数えます（表示用の8bitではありません）。</summary>
    public required int[][] Histogram { get; init; }

    public required ChannelStat[] Channels { get; init; }

    /// <summary>横位置ごとの分布です。`列 * WaveformLevels + 段` で引きます。</summary>
    public required int[] Waveform { get; init; }

    public required int WaveformColumns { get; init; }

    /// <summary>1列に何画素ぶんを束ねたか。1 なら束ねていません。</summary>
    public required int PixelsPerColumn { get; init; }

    public required ChannelMask WaveformChannel { get; init; }

    /// <summary>Cb-Cr 平面の分布です（Y'CbCr のときだけ）。`cr * VectorSize + cb` で引きます。</summary>
    public required int[] Vector { get; init; }

    /// <summary>変換後に 0-1 の外へ出た画素数です。プレビューの「範囲外」と同じ数え方をします。</summary>
    public required int ClippedOver { get; init; }

    public required int ClippedUnder { get; init; }

    public required int ClippedBoth { get; init; }

    public long Pixels => (long)Width * Height;

    public int ClippedTotal => ClippedOver + ClippedUnder + ClippedBoth;

    /// <summary>ヒストグラムの山の高さです。縦の目盛りを決めるのに使います。</summary>
    public int PeakCount(int channel)
    {
        var peak = 0;
        foreach (var count in Histogram[channel]) peak = Math.Max(peak, count);
        return peak;
    }

    public int WaveformPeak()
    {
        var peak = 0;
        foreach (var count in Waveform) peak = Math.Max(peak, count);
        return peak;
    }

    public int VectorPeak()
    {
        var peak = 0;
        foreach (var count in Vector) peak = Math.Max(peak, count);
        return peak;
    }
}

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

    /// <summary>指定画素の、コード値と表示RGBを対にして返します。</summary>
    public PixelSample Sample(int x, int y, PreviewRenderOptions options)
    {
        var (first, second, third) = ReadCodes(x, y, options.Upsample);
        var (r, g, b) = ToRgb(first, second, third, options);
        var (l1, l2, l3) = ChannelLabels;
        var interpolated = options.Upsample == ChromaUpsample.Bilinear && HasSubsampledChroma;
        return new PixelSample(x, y, l1, l2, l3, first, second, third, MaxCode, r, g, b, interpolated);
    }

    /// <summary>
    /// WPFのBgra32ビットマップへそのまま渡せるバイト列を作ります（1画素4バイト、B G R A の順）。
    /// </summary>
    public byte[] ToBgra32(PreviewRenderOptions options)
    {
        var buffer = new byte[checked(Width * Height * 4)];
        var stride = Width * 4;

        for (var y = 0; y < Height; y++)
        {
            var rowStart = y * stride;
            for (var x = 0; x < Width; x++)
            {
                var (first, second, third) = ReadCodes(x, y, options.Upsample);
                var (r, g, b) = ToRgb(first, second, third, options);
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

    private (byte R, byte G, byte B) ToRgb(int first, int second, int third, PreviewRenderOptions options)
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
        if (!options.Channels.HasFlag(ChannelMask.First)) first = NeutralCode(0, options);
        if (!options.Channels.HasFlag(ChannelMask.Second)) second = NeutralCode(1, options);
        if (!options.Channels.HasFlag(ChannelMask.Third)) third = NeutralCode(2, options);

        if (!_isYcbcr)
            return (ToByte(first / (double)MaxCode), ToByte(second / (double)MaxCode), ToByte(third / (double)MaxCode));

        var (kr, kb) = options.Interpretation.Coefficients;
        var kg = 1.0 - kr - kb;
        var shift = 1 << (BitDepth - 8);
        double peak = MaxCode;

        var y = options.Interpretation.IsLimited ? (first - 16.0 * shift) / (219.0 * shift) : first / peak;
        var cb = options.Interpretation.IsLimited ? (second - 128.0 * shift) / (224.0 * shift) : (second - 128.0 * shift) / peak;
        var cr = options.Interpretation.IsLimited ? (third - 128.0 * shift) / (224.0 * shift) : (third - 128.0 * shift) / peak;

        var r = y + 2.0 * (1.0 - kr) * cr;
        var b = y + 2.0 * (1.0 - kb) * cb;
        var g = (y - kr * r - kb * b) / kg;
        return (ToByte(r), ToByte(g), ToByte(b));
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

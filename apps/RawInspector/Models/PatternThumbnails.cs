using System.IO;
using System.Windows.Media.Imaging;

namespace RawInspector.Models;

/// <summary>
/// パターンの参考図です。生成画面で「押す前にどんな形の絵か」を出します。
///
/// **実物ではありません。** 実寸で描いたものを長辺480へ縮めた静止画で、
/// 既定のつまみの姿だけを持ちます。画素単位の線・折り返し・ドットバイドットは
/// ここには出ません（出せない大きさです）。それらは生成してから等倍で見るものです。
///
/// つまみを触ったときの絵は画面側がその場で生成して差し替えます。
/// つまり生成器が動くのは、既定から動かしたときだけです。
/// 選んで眺めているあいだは Python は一度も起きません。
///
/// 図は tools/make_pattern_thumbnails.py が作り、csproj が exe へ埋め込みます。
/// </summary>
public static class PatternThumbnails
{
    // 一度読んだものは持っておきます。パターンを行き来するたびに
    // PNG を解き直す必要はありません（42枚すべてでも数MBです）。
    private static readonly Dictionary<string, BitmapImage?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>そのパターンの参考図です。持っていなければ null を返します。</summary>
    public static BitmapImage? For(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(pattern, out var cached)) return cached;
            var image = Load(pattern);
            Cache[pattern] = image;
            return image;
        }
    }

    private static BitmapImage? Load(string pattern)
    {
        // 埋め込み名は csproj の LogicalName で決めています（thumbnails/colorbar.png）。
        using var stream = typeof(PatternThumbnails).Assembly
            .GetManifestResourceStream("thumbnails/" + pattern + ".png");
        if (stream is null) return null;

        // 埋め込みストリームはここで閉じるので、先に全部読ませます
        // （OnLoad にしないと、あとから描くときに読みに戻って落ちます）。
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>ファイルから読み込みます（その場で生成した絵の差し替え用）。</summary>
    public static BitmapImage? FromFile(string path)
    {
        if (!File.Exists(path)) return null;

        var image = new BitmapImage();
        image.BeginInit();
        // 同じ場所へ作り直すので、掴んだままにさせません。
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}

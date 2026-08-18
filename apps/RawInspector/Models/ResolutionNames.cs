namespace RawInspector.Models;

/// <summary>
/// 画素数につく通称です（VGA、XGA、FHD など）。
///
/// **「規格」ではなく「通称」として扱っています。** 理由が2つあります。
///
/// 1つ目は出どころがばらばらなことです。VGA と XGA はもともと IBM の製品名、
/// QCIF と CIF は ITU-T のテレビ会議、NTSC / PAL は放送、HD や 4K は民生の呼び名です。
/// まとめて「VESA規格」と書くと事実と違います。
/// VESA が定めているのは DMT や CVT といった**タイミング**の表のほうです。
///
/// 2つ目は、その規格が決めているのが画素数だけではないことです。
/// DMT は画素クロック・ブランキング・同期の極性まで含みます。
/// ここで扱う RAW にタイミングはありません。あるのは有効画素の数だけです。
/// なので「この RAW は VGA 規格に準拠している」とは言えません。言えるのは
/// 「この画素数は VGA と呼ばれているものと同じ」までです。表示もその言い方に留めます。
///
/// 名前を付けるのは探しやすさのためです。640x480 と 720x480 は数字だけだと似ていますが、
/// 「VGA」と「SD NTSC」なら別物だと分かります。
/// </summary>
public static class ResolutionNames
{
    // 生成側（tools/make_samples.py の STANDARD_SIZES）と重なりますが、あちらは
    // 「どのサイズで作るか」、こちらは「開いたものに何と表示するか」なので用途が違います。
    // こちらは作っていないサイズも引けるように広めに持ちます。
    private static readonly Dictionary<(int Width, int Height), string> Names = new()
    {
        [(128, 96)] = "SQCIF",
        [(176, 144)] = "QCIF",
        [(320, 240)] = "QVGA",
        [(352, 288)] = "CIF",
        [(640, 480)] = "VGA",
        [(704, 576)] = "4CIF",
        [(720, 480)] = "SD NTSC",
        [(720, 576)] = "SD PAL",
        [(800, 600)] = "SVGA",
        [(1024, 768)] = "XGA",
        [(1280, 720)] = "HD 720p",
        [(1280, 800)] = "WXGA 16:10",
        [(1280, 1024)] = "SXGA",
        [(1366, 768)] = "WXGA",
        [(1440, 900)] = "WXGA+",
        [(1600, 900)] = "HD+",
        [(1600, 1200)] = "UXGA",
        [(1680, 1050)] = "WSXGA+",
        [(1920, 1080)] = "FHD 1080p",
        [(1920, 1200)] = "WUXGA",
        [(2048, 1080)] = "2K DCI",
        [(2560, 1440)] = "QHD",
        [(2560, 1600)] = "WQXGA",
        [(3440, 1440)] = "UWQHD",
        [(3840, 2160)] = "4K UHD",
        [(4096, 2160)] = "4K DCI",
        [(7680, 4320)] = "8K UHD",
    };

    /// <summary>通称を返します。知らない大きさなら null です（無理に当てはめません）。</summary>
    public static string? Of(int width, int height) =>
        Names.TryGetValue((width, height), out var name) ? name : null;

    /// <summary>「1920x1080 FHD 1080p」の形にします。通称が無ければ数字だけです。</summary>
    public static string Describe(int width, int height)
    {
        var name = Of(width, height);
        return name is null ? $"{width}x{height}" : $"{width}x{height} {name}";
    }

    /// <summary>
    /// 生成画面で選べる大きさです。小さいほうから並べます。
    ///
    /// 幅と高さを毎回打ち込ませると、桁を1つ間違えても気付けません
    /// （4K のつもりで 384x2160 を作っても、生成器は素直に作ります）。
    /// 通称の付いている大きさは、選べるようにしておきます。
    /// </summary>
    public static IReadOnlyList<(int Width, int Height, string Label)> Presets { get; } =
        Names.OrderBy(e => (long)e.Key.Width * e.Key.Height)
             .Select(e => (e.Key.Width, e.Key.Height, $"{e.Key.Width}x{e.Key.Height}  {e.Value}"))
             .ToList();
}

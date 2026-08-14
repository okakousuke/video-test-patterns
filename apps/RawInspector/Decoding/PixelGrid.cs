using System.Windows;
using System.Windows.Media;

namespace RawInspector.Decoding;

/// <summary>
/// 画素の境目に引く線です。
///
/// 拡大しても、隣り合う画素が同じ値だと境目は見えません。
/// 「1画素の線」なのか「2画素の帯」なのかは、境目が引かれて初めて数えられます。
///
/// 色差ブロックの枠を別に引くのは、そちらのほうが読み取りたいことが多いためです。
/// 4:2:0 なら 2 x 2 の画素が同じ色差を共有しています。その枠を出せば、
/// 「間引かれた」という言葉が画面の上のどの範囲を指すのかが目で分かります。
///
/// 窓の都合とは関係のない図形の話なので、画面のコードから分けてあります
/// （分けておくと、実際に描いた絵を出して確かめられます）。
/// </summary>
public static class PixelGrid
{
    /// <summary>これ未満の拡大では線を引きません。線のほうが画素より太くなって絵が読めなくなります。</summary>
    public const double MinPointsPerPixel = 6.0;

    /// <summary>
    /// 幅 tile のタイルの中へ、step ごとの縦横線を足します。
    /// 白と黒の破線を半周期ずらして重ねます。1色だと、その色に近い画素の上で消えるためです。
    /// </summary>
    private static void AddLines(DrawingGroup group, double tile, double step, double thickness, byte alpha)
    {
        var lines = new GeometryGroup();
        // 0 から tile まで、両端とも引きます。
        // タイルの縁に来る線は、太さの半分がタイルの外へはみ出して切られます。
        // 片側だけにすると細いままなので、隣のタイルの反対側の半分と合わせて 1 本にします。
        for (var at = 0.0; at <= tile + 0.001; at += step)
        {
            lines.Children.Add(new LineGeometry(new Point(at, 0), new Point(at, tile)));
            lines.Children.Add(new LineGeometry(new Point(0, at), new Point(tile, at)));
        }
        lines.Freeze();

        // 破線の刻みは短く、しかも間隔によらず一定にします。
        // 刻みを間隔に比例させると、色差ブロックの線が長い白黒の帯になって
        // 「線」ではなく「模様」に見えてしまいます。
        const double dash = 3.0;
        foreach (var (color, offset) in new[]
                 {
                     (Color.FromArgb(alpha, 0, 0, 0), 0.0),
                     (Color.FromArgb(alpha, 255, 255, 255), dash),
                 })
        {
            // 破線の長さは線の太さを単位に指定します（WPF の決まりです）。
            var pen = new Pen(new SolidColorBrush(color), thickness)
            {
                DashStyle = new DashStyle([dash / thickness, dash / thickness], offset / thickness),
            };
            pen.Freeze();
            group.Children.Add(new GeometryDrawing(null, pen, lines));
        }
    }

    /// <summary>
    /// 1画素が画面で <paramref name="pointsPerPixel"/> 点になるときの線を作ります。
    /// <paramref name="blockWidth"/> / <paramref name="blockHeight"/> は
    /// 色差サンプル1つが受け持つ画素の数です（4:2:0 なら 2 と 2、間引きが無ければ 1 と 1）。
    /// </summary>
    public static Brush Build(double pointsPerPixel, int blockWidth, int blockHeight)
    {
        // タイル1枚を色差ブロック1つ分にします。ブラシ1本を敷き詰めるので、
        // どれだけ拡大しても図形は1組のままです。
        // 画素ごとに線の要素を作ると、拡大したときに数万個になります。
        var tileWidth = pointsPerPixel * blockWidth;
        var tileHeight = pointsPerPixel * blockHeight;
        var tile = Math.Max(tileWidth, tileHeight);

        var group = new DrawingGroup();

        // 画素の境目は控えめに。数えるための目盛りであって、絵の一部ではありません。
        AddLines(group, tile, pointsPerPixel, 1.0, 0x55);

        // 色差ブロックの枠は太く出します。
        if (blockWidth > 1 || blockHeight > 1)
        {
            AddLines(group, tile, tileWidth, 2.0, 0xE0);
            if (Math.Abs(tileHeight - tileWidth) > 0.001) AddLines(group, tile, tileHeight, 2.0, 0xE0);
        }
        group.Freeze();

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, tile, tile),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, tile, tile),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }
}

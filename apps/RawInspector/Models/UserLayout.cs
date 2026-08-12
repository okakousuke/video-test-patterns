using System.IO;
using System.Text.Json;

namespace RawInspector.Models;

/// <summary>
/// 前回終了したときの画面の形です。開くたびに並べ直さなくて済むように覚えておきます。
///
/// 保存に失敗しても操作は続けます。次に開いたときの手間が増えるだけで、
/// RAWの読み書きには関係しないためです。
/// </summary>
public sealed class UserLayout
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }

    /// <summary>manifest一覧の欄の幅。0 のときは記録なしとして既定を使います。</summary>
    public double ListWidth { get; set; }

    /// <summary>生成条件の欄の幅。0 のときは記録なしとして既定を使います。</summary>
    public double DetailWidth { get; set; }

    /// <summary>一覧の並び順。null や知らない値のときは既定（名前順）に戻します。</summary>
    public string? SortOrder { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RawInspector", "layout.json");

    public static UserLayout? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<UserLayout>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 覚えられなくても困るのは次回の並べ直しだけなので、黙って諦めます。
        }
    }

    /// <summary>
    /// 記録した位置が今の画面に収まっているかを見ます。
    /// 前回より画面が狭くなっていたり、外したモニタの上だったりすると、
    /// そのまま復元するとウィンドウが画面の外へ出て掴めなくなります。
    /// </summary>
    public bool FitsInside(double screenLeft, double screenTop, double screenRight, double screenBottom)
    {
        if (Width < 200 || Height < 150) return false;

        // タイトルバーを掴める程度に画面内へ入っていれば良しとします。
        const double margin = 80;
        return Left + margin < screenRight
            && Left + Width - margin > screenLeft
            && Top >= screenTop - 1
            && Top + margin < screenBottom;
    }
}

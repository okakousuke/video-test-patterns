using System.IO;

namespace RawInspector.Models;

/// <summary>
/// リポジトリの位置を探します。
///
/// 実行ファイルは `apps/RawInspector/bin/Release/net8.0-windows/` にあり、
/// 素材は `generated/` や `samples/raw/`、一括生成の道具は `tools/` にあります。
/// 相対の段数を決め打ちすると、出力先を変えただけで見失います。
/// 目印になるファイルを上へ辿って探します。
///
/// 見つからないこともあります（実行ファイルだけ配った場合など）。
/// その場合は黙って動かないのではなく、見つからなかったと言えるように null を返します。
/// </summary>
public static class RepositoryLocator
{
    // このリポジトリだけが持っている組み合わせを目印にします。
    // pyproject.toml だけだと、たまたま上位にある別のPythonプロジェクトを拾います。
    private static readonly string[] Markers = ["pyproject.toml", "src/vtp/patterns.py"];

    private static string? _cached;
    private static bool _searched;

    public static string? Find()
    {
        if (_searched) return _cached;
        _searched = true;

        _cached = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }
            .Select(SearchUpward)
            .FirstOrDefault(found => found is not null);
        return _cached;
    }

    /// <summary>
    /// 指定した場所から上へ辿って探します。
    /// 実行時の場所に依らず確かめられるよう、探し方だけを切り出してあります。
    /// </summary>
    public static string? SearchUpward(string start, int levels = 8)
    {
        var directory = new DirectoryInfo(start);
        for (var i = 0; i <= levels && directory is not null; i++, directory = directory.Parent)
        {
            var found = Markers.All(marker =>
                File.Exists(Path.Combine(directory.FullName, marker.Replace('/', Path.DirectorySeparatorChar))));
            if (found) return directory.FullName;
        }
        return null;
    }

    /// <summary>リポジトリ内のパスを組み立てます。見つかっていなければ null です。</summary>
    public static string? Resolve(params string[] parts) =>
        Find() is { } root ? Path.Combine([root, .. parts]) : null;
}

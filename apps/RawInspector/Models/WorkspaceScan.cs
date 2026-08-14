using System.IO;

namespace RawInspector.Models;

/// <summary>
/// フォルダ1つを数えた結果です。
///
/// 開いてみるまで中身が分からない状態をやめるためのものです。
/// 件数と容量だけでなく<b>読めなかったものの数</b>を持ちます。
/// 「190件あります」より「190件のうち2件が読めません」のほうが、次にやることが決まります。
/// </summary>
public sealed record WorkspaceScan(
    string Path,
    bool Exists,
    int ManifestCount,
    int BrokenCount,
    int MissingRawCount,
    long RawBytes,
    IReadOnlyList<string> Patterns,
    string? Error)
{
    public string SizeText => RawBytes >= 1L << 30
        ? $"{RawBytes / (double)(1L << 30):0.0} GB"
        : RawBytes >= 1L << 20
            ? $"{RawBytes / (double)(1L << 20):0.0} MB"
            : $"{RawBytes / 1024.0:0.0} KB";

    /// <summary>そのまま1行で読める要約です。</summary>
    public string Summary
    {
        get
        {
            if (Error is not null) return $"読めません: {Error}";
            if (!Exists) return "ありません";
            if (ManifestCount == 0) return "manifest がありません";

            var text = $"{ManifestCount} 件 / {SizeText} / パターン {Patterns.Count} 種";
            if (BrokenCount > 0) text += $"  ⚠ 読めない manifest {BrokenCount} 件";
            if (MissingRawCount > 0) text += $"  ⚠ RAW が見つからない {MissingRawCount} 件";
            return text;
        }
    }

    public bool HasProblem => Error is not null || BrokenCount > 0 || MissingRawCount > 0;

    public bool IsUsable => Exists && ManifestCount > 0 && Error is null;

    /// <summary>
    /// フォルダを数えます。
    ///
    /// manifest を読むだけで RAW は開きません。中身の正しさではなく
    /// 「何がどれだけあるか」を出すためのもので、全部開くと 188 件で数十秒かかります。
    /// RAW は存在と大きさだけ見ます。
    /// </summary>
    public static WorkspaceScan Scan(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new WorkspaceScan(path ?? "", false, 0, 0, 0, 0, [], null);

        try
        {
            if (!Directory.Exists(path))
                return new WorkspaceScan(path, false, 0, 0, 0, 0, [], null);

            var manifests = Directory.EnumerateFiles(path, "*.manifest.json", SearchOption.AllDirectories).ToList();

            var broken = 0;
            var missingRaw = 0;
            long bytes = 0;
            var patterns = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var manifestPath in manifests)
            {
                ManifestInfo manifest;
                try
                {
                    manifest = ManifestInfo.Load(manifestPath);
                }
                catch
                {
                    broken++;
                    continue;
                }

                if (manifest.Pattern is { Length: > 0 } name) patterns.Add(name);

                try
                {
                    var raw = new FileInfo(manifest.ResolveRawPath(manifestPath));
                    if (raw.Exists) bytes += raw.Length;
                    else missingRaw++;
                }
                catch
                {
                    missingRaw++;
                }
            }

            return new WorkspaceScan(path, true, manifests.Count, broken, missingRaw, bytes, [.. patterns], null);
        }
        catch (Exception ex)
        {
            return new WorkspaceScan(path, true, 0, 0, 0, 0, [], ex.Message);
        }
    }
}

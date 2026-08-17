using System.IO;

namespace RawInspector.Models;

/// <summary>目次に並べる文書1枚ぶんです。</summary>
/// <param name="Path">`docs/` からの相対パスです。区切りは `/` に統一します。</param>
/// <param name="Section">目次の区分です。使い方と仕様は役割が違うので分けます。</param>
public sealed record HelpDocument(string Path, string Section, string Title, string Summary);

/// <summary>読み込んだ本文です。<paramref name="File"/> が null なら実行ファイルへ埋め込んだものです。</summary>
public sealed record HelpContent(string Text, string? File);

/// <summary>
/// 使い方ドキュメントの置き場所です。
///
/// 実体は1つ、リポジトリの `docs/` です。二重に書くと、片方だけ直したときに黙ってずれます。
/// ただし配布のされ方が2通りあるので、探す場所も2通り要ります。
///
/// - リポジトリごと使う場合  … `docs/` がそのままある
/// - ビルド結果だけ配る場合  … csproj が実行ファイルの隣へ複製する
///
/// リポジトリを先に見るのは、`docs/` が実体で、実行ファイルの隣にあるのはその複製だからです。
/// 文書を直しながら動かしているときに、古い複製のほうが出ると混乱します。
///
/// どちらも無いとき（実行ファイル1つだけを持ち出した場合など）は、埋め込みリソースから読みます。
/// 全部で数十KBなので、持たせておくほうが「ヘルプが開かない」より良い状態です。
/// </summary>
public static class HelpLibrary
{
    public const string Launcher = "usage/launcher.md";
    public const string Viewer = "usage/viewer.md";
    public const string Generator = "usage/generator.md";
    public const string BatchTools = "usage/batch-tools.md";
    public const string Scopes = "usage/scopes.md";

    /// <summary>
    /// 目次です。ここに並べたものだけが左の一覧に出ます。
    /// `docs/` を走査して自動で並べることもできますが、そうすると並び順が
    /// ファイル名まかせになり、読む順番と関係なくなります。
    /// </summary>
    public static IReadOnlyList<HelpDocument> Contents { get; } =
    [
        new(Launcher, "使い方", "ホーム画面",
            "起動して最初に出る画面。素材の件数と生成器の状態を見ます。"),
        new(Viewer, "使い方", "RAWを見る",
            "フォルダを開いて1本ずつ確かめます。表示条件の切り替えと画素の読み方。"),
        new(Scopes, "使い方", "分布で見る",
            "面ではなく分布で見ます。ヒストグラム・波形・ベクトルと、規定範囲の外にある画素の数。"),
        new(Generator, "使い方", "パターンを作る",
            "条件を選んで1本作ります。成立しない組み合わせの読み方。"),
        new(BatchTools, "使い方", "スクリプトを走らせる",
            "一括生成の3つの道具。作る前に件数と容量を見ます。"),
        new("manifest-v1.md", "仕様", "manifest v1 共通仕様",
            "生成器とビューアが共通で守るRAWの契約。"),
        new("formats.md", "仕様", "格納形式",
            "planar / packed / NV12 / P010 / v210 / MIPI10 のバイト配置。"),
    ];

    /// <summary>本文を読みます。見つからなくても、見つからなかったと言える本文を返します。</summary>
    public static HelpContent Read(string relative)
    {
        if (FindFile(relative) is { } path)
        {
            try
            {
                return new HelpContent(File.ReadAllText(path), path);
            }
            catch (Exception ex)
            {
                // 読めない理由は出します。黙って空にすると、文書が無いのか壊れているのか分かりません。
                return new HelpContent($"# 読めませんでした\n\n`{path}`\n\n{ex.Message}\n", null);
            }
        }

        if (ReadEmbedded(relative) is { } embedded) return new HelpContent(embedded, null);

        return new HelpContent(
            $"# 見つかりません\n\n`docs/{relative}` を探しましたが、どこにもありませんでした。\n\n"
            + "実行ファイルの隣の `docs/` が消えているか、ビルドし直しが必要かもしれません。\n", null);
    }

    /// <summary>ファイルとして存在する場所を返します。埋め込みしか無いときは null です。</summary>
    public static string? FindFile(string relative)
    {
        var native = relative.Replace('/', Path.DirectorySeparatorChar);

        if (RepositoryLocator.Resolve("docs", native) is { } inRepository && File.Exists(inRepository))
            return inRepository;

        var besideExecutable = Path.Combine(AppContext.BaseDirectory, "docs", native);
        return File.Exists(besideExecutable) ? besideExecutable : null;
    }

    private static string? ReadEmbedded(string relative)
    {
        // 埋め込み名は csproj の LogicalName で決めています（docs/usage/viewer.md のような形）。
        using var stream = typeof(HelpLibrary).Assembly
            .GetManifestResourceStream("docs/" + relative.Replace('\\', '/'));
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 文書の中のリンクを、`docs/` からの相対パスへ直します。
    /// 文書どうしは `../manifest-v1.md` のように互いを指しているので、
    /// いま開いている文書の場所を基準に畳む必要があります。
    /// </summary>
    public static string ResolveLink(string fromDocument, string target)
    {
        var directory = fromDocument.Contains('/')
            ? fromDocument[..fromDocument.LastIndexOf('/')]
            : "";
        var combined = directory.Length == 0 ? target : $"{directory}/{target}";

        var parts = new List<string>();
        foreach (var part in combined.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }
}

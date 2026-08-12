using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RawInspector.Models;

/// <summary>格納形式ひとつと、その説明です。</summary>
public sealed record StorageInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description);

/// <summary>
/// 成立する条件の組み合わせひとつです。
/// <c>WidthMultiple</c> / <c>HeightMultiple</c> は、その条件で画像の幅・高さが
/// 満たすべき倍数です（v210 なら幅 6、4:2:0 の mipi10 なら幅 8・高さ 2）。
/// </summary>
public sealed record Combination(
    [property: JsonPropertyName("color_model")] string ColorModel,
    [property: JsonPropertyName("subsampling")] string Subsampling,
    [property: JsonPropertyName("bit_depth")] int BitDepth,
    [property: JsonPropertyName("storage")] string Storage,
    [property: JsonPropertyName("alignment")] string Alignment,
    [property: JsonPropertyName("range")] string Range,
    [property: JsonPropertyName("width_multiple")] int WidthMultiple,
    [property: JsonPropertyName("height_multiple")] int HeightMultiple);

/// <summary>
/// 生成器が受け付ける条件の一覧です。
///
/// **この表はここで作りません。** 生成器の `--describe` から読みます。
/// 同じ規則を C# 側にも書くと、片方だけ直したときに黙ってずれます。
/// ずれた結果は「GUI では作れるのに生成器が弾く」「GUI が弾くのに生成器は作れる」
/// のどちらかで、どちらも原因を探しにくい形で出ます。
///
/// 生成の実体も同じ生成器です。ここでやり取りするのは条件だけで、
/// 画素を作る処理は C# 側に持ちません。
/// </summary>
public sealed class GeneratorCatalog
{
    [JsonPropertyName("generator")] public string Generator { get; init; } = "";
    [JsonPropertyName("patterns")] public List<string> Patterns { get; init; } = [];
    [JsonPropertyName("matrices")] public List<string> Matrices { get; init; } = [];
    [JsonPropertyName("outputs")] public List<string> Outputs { get; init; } = [];
    [JsonPropertyName("storages")] public List<StorageInfo> Storages { get; init; } = [];
    [JsonPropertyName("combinations")] public List<Combination> Combinations { get; init; } = [];

    /// <summary>その条件で成立する組み合わせを返します（無ければ null）。</summary>
    public Combination? Find(string colorModel, string subsampling, int bitDepth, string storage,
                             string alignment, string range) =>
        Combinations.FirstOrDefault(c =>
            c.ColorModel == colorModel && c.Subsampling == subsampling && c.BitDepth == bitDepth
            && c.Storage == storage && c.Alignment == alignment && c.Range == range);

    /// <summary>生成器を呼び出して一覧を読み込みます。</summary>
    public static async Task<GeneratorCatalog> LoadAsync(string command, CancellationToken token = default)
    {
        var (exitCode, stdout, stderr) = await RunAsync(command, ["--describe"], null, token);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"生成器を呼び出せませんでした（終了コード {exitCode}）。\n"
                + $"実行したもの: {command} --describe\n"
                + (string.IsNullOrWhiteSpace(stderr) ? "" : stderr.Trim()));

        return JsonSerializer.Deserialize<GeneratorCatalog>(stdout)
               ?? throw new InvalidOperationException("生成器の応答を読み取れませんでした。");
    }

    /// <summary>
    /// 生成器を実行します。
    ///
    /// 標準出力と標準エラーの両方を持ち帰ります。失敗したときに理由を出すためです。
    /// 生成器は成立しない指定を理由付きで断るので、その文面をそのまま見せます。
    /// こちらで言い換えると、生成器が実際に何を嫌がったのかが分からなくなります。
    /// </summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string command, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken token = default)
    {
        // 「python -m vtp」のように、実行ファイルと引数が 1 つの文字列で来ます。
        var parts = SplitCommand(command);
        if (parts.Count == 0) throw new InvalidOperationException("生成器のコマンドが空です。");

        var info = new ProcessStartInfo(parts[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var part in parts.Skip(1)) info.ArgumentList.Add(part);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (workingDirectory is not null && Directory.Exists(workingDirectory))
            info.WorkingDirectory = workingDirectory;

        // 生成器側の print が化けないようにします（Windows の既定は UTF-8 ではありません）。
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"「{parts[0]}」を起動できませんでした: {ex.Message}\n"
                + "生成器は Python 側にあります。`pip install -e \".[dev]\"` を済ませたうえで、"
                + "python に PATH が通っているか確認してください。", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>コマンド文字列を空白で分けます（引用符で囲んだ部分は 1 つとして扱います）。</summary>
    internal static List<string> SplitCommand(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var c in command)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }
}

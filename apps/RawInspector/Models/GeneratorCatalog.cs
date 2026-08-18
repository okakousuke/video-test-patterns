using System.Diagnostics;
using System.IO;
using System.Text;
using System.Globalization;
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
/// <summary>
/// パターン固有のつまみ 1 つ分です。
///
/// 中身は生成器の <c>--describe</c> がそのまま渡してきます。
/// **この画面は「どのパターンに何のつまみがあるか」を一切持ちません。**
/// 持つと生成器側へつまみを足したときに二重に直すことになり、必ず片方が古くなります。
///
/// <c>Default</c> が null のものは、寸法から決まる（幅・高さで変わる）つまみです。
/// 求め方は <c>Auto</c> に文章で、<c>AutoBasis</c> 以下に数として入っています。
/// どちらも生成器が絵を描くときに使っているものと同じ出どころなので、
/// ここで解いた数は実際に描かれる値と一致します。
/// </summary>
public sealed class PatternOption
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";

    /// <summary>int / float / bool / choice / ints / floats / color。入力欄の種類を決めます。</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";

    [JsonPropertyName("help")] public string Help { get; init; } = "";

    /// <summary>型が混ざる（数値・文字列・真偽・配列・null）ので、生の JSON のまま持ちます。</summary>
    [JsonPropertyName("default")] public JsonElement Default { get; init; }

    /// <summary>寸法から決まるときの求め方（文章）。空なら <c>Default</c> が既定値です。</summary>
    [JsonPropertyName("auto")] public string Auto { get; init; } = "";

    // 求め方の中身です。生成器が同じものを使って絵を描いているので、
    // ここで解いた数は実際に描かれる値と一致します。
    // 式を文章から読み取っているのではありません（読み取ると必ずずれます）。

    /// <summary>何を割るか。<c>width</c> なら幅、<c>min</c> なら幅と高さの小さいほうです。</summary>
    [JsonPropertyName("auto_basis")] public string AutoBasis { get; init; } = "";
    [JsonPropertyName("auto_divisor")] public double AutoDivisor { get; init; }
    [JsonPropertyName("auto_floor")] public double AutoFloor { get; init; }
    [JsonPropertyName("auto_integer")] public bool AutoInteger { get; init; }

    /// <summary>既定が寸法から決まるつまみかどうかです。</summary>
    public bool HasAuto => AutoBasis.Length > 0;

    /// <summary>その寸法での既定値です。寸法依存でなければ null を返します。</summary>
    public double? ResolveDefault(int width, int height)
    {
        if (!HasAuto || AutoDivisor == 0) return null;
        var basis = AutoBasis == "width" ? width : Math.Min(width, height);
        return AutoInteger
            ? Math.Max(AutoFloor, Math.Floor(basis / Math.Round(AutoDivisor)))
            : Math.Max(AutoFloor, basis / AutoDivisor);
    }

    [JsonPropertyName("minimum")] public double? Minimum { get; init; }
    [JsonPropertyName("maximum")] public double? Maximum { get; init; }
    [JsonPropertyName("choices")] public List<string> Choices { get; init; } = [];

    /// <summary>並びの個数が決まっているとき（色なら 3）です。</summary>
    [JsonPropertyName("length")] public int? Length { get; init; }

    /// <summary>並びを受け取るつまみ（色を含む）かどうかです。</summary>
    public bool IsList => Kind is "ints" or "floats" or "color";

    /// <summary>整数しか受け取らないかどうかです。</summary>
    public bool IsInteger => Kind is "int" or "ints";

    /// <summary>
    /// 既定値を、入力欄へそのまま置ける文字列にします。
    ///
    /// 寸法依存のつまみは、その寸法で解いた数を返します。寸法を渡さない
    /// （<c>null</c>）ときだけ空になります。
    /// </summary>
    public string DefaultText(int? width = null, int? height = null)
    {
        if (HasAuto)
        {
            if (width is not int w || height is not int h) return "";
            var value = ResolveDefault(w, h);
            if (value is not double v) return "";
            return AutoInteger
                ? ((long)v).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        return Default.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => Default.GetString() ?? "",
            // 並びは CLI へ渡すときも JSON なので、角括弧のまま見せます。
            JsonValueKind.Array => "[" + string.Join(", ", Default.EnumerateArray().Select(v => v.ToString())) + "]",
            _ => Default.ToString(),
        };
    }

    /// <summary>入力欄の下に出す、範囲や既定の一言です。</summary>
    public string Hint(int? width = null, int? height = null)
    {
        var parts = new List<string>();
        var text = DefaultText(width, height);
        if (HasAuto)
            parts.Add(text.Length > 0 ? $"既定 {text}（寸法から: {Auto}）" : $"寸法から決めます（{Auto}）");
        else if (text.Length > 0) parts.Add($"既定 {text}");

        if (Minimum is not null && Maximum is not null) parts.Add($"{Trim(Minimum.Value)}〜{Trim(Maximum.Value)}");
        else if (Minimum is not null) parts.Add($"{Trim(Minimum.Value)} 以上");
        else if (Maximum is not null) parts.Add($"{Trim(Maximum.Value)} 以下");

        if (Length is not null) parts.Add($"{Length} 個");
        return string.Join(" / ", parts);
    }

    private static string Trim(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString()
            : value.ToString("0.###");
}

public sealed class GeneratorCatalog
{
    [JsonPropertyName("generator")] public string Generator { get; init; } = "";
    [JsonPropertyName("patterns")] public List<string> Patterns { get; init; } = [];
    [JsonPropertyName("matrices")] public List<string> Matrices { get; init; } = [];
    [JsonPropertyName("outputs")] public List<string> Outputs { get; init; } = [];
    [JsonPropertyName("storages")] public List<StorageInfo> Storages { get; init; } = [];
    [JsonPropertyName("combinations")] public List<Combination> Combinations { get; init; } = [];

    /// <summary>パターン名 → そのパターンのつまみ。載っていないパターンにはつまみがありません。</summary>
    [JsonPropertyName("pattern_options")]
    public Dictionary<string, List<PatternOption>> PatternOptions { get; init; } = [];

    /// <summary>そのパターンのつまみを返します（無ければ空）。</summary>
    public IReadOnlyList<PatternOption> OptionsFor(string? pattern) =>
        pattern is not null && PatternOptions.TryGetValue(pattern, out var list) ? list : [];

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

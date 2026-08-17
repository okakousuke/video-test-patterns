using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RawInspector.Models;

/// <summary>
/// 表示条件を manifest として書き出します。
///
/// 表示条件を変えても<b>RAWのバイト列は1バイトも変わりません</b>。変わるのは読み方だけです。
/// なので保存した画像やRAWコピーの名前に `_bt601` を付けても、それは嘘に近づくだけでした
/// （「bt601 へ変換したRAW」があるように見えます）。
///
/// 読み方を残したいなら、置き場所は manifest です。<b>同じRAWを指したまま</b>、
/// `matrix` と `range` だけが違う manifest を添えます。
/// これなら、その manifest を開いた人は最初からその条件で見ます。
///
/// 書き換えるのは matrix と range だけです。成分の選択や段は書きません。
/// manifest は<b>データの条件</b>を書くところであって、画面の見せ方を書くところではありません。
/// </summary>
public static class DerivedManifest
{
    /// <summary>この manifest を書いた道具の名前です。生成器と取り違えないための名前です。</summary>
    public const string Tool = "RawInspector";

    /// <summary>
    /// 書き出しの設定です。
    ///
    /// 非ASCIIをエスケープしません。日本語の注記が `\uXXXX` の羅列になると、
    /// テキストエディタで開いたときに読めなくなります。生成側（Python）も
    /// `ensure_ascii=False` で書いているので、そちらへ合わせます。
    ///
    /// 既定の設定を写してから変えているのは、空の <c>JsonSerializerOptions</c> だと
    /// 実行時に型情報の解決先が無いと言われて落ちるためです。
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerOptions.Default)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 元の manifest の JSON から、読み方だけを差し替えた JSON を作ります。
    ///
    /// 元の JSON をそのまま読んで差し替えるので、<b>こちらが知らない項目も落としません</b>。
    /// 型へ読み込んでから書き戻すと、知らない項目が黙って消えます。
    /// </summary>
    /// <param name="originalJson">元の manifest の中身です。</param>
    /// <param name="sourceFileName">元の manifest のファイル名（場所は含めません）。</param>
    /// <param name="matrix">書き込む matrix。Y'CbCr でないときは null を渡します。</param>
    /// <param name="range">書き込む range。</param>
    /// <param name="writtenAt">書き出した時刻です。</param>
    public static string Build(
        string originalJson, string sourceFileName, string? matrix, string range, DateTimeOffset writtenAt)
    {
        var document = JsonNode.Parse(originalJson)?.AsObject()
            ?? throw new InvalidDataException("元の manifest を読み込めませんでした。");

        var parameters = document["parameters"]?.AsObject()
            ?? throw new InvalidDataException("元の manifest に parameters がありません。");

        // 条件のハッシュ（生成器が入れています）は、条件を書き換えた時点で合わなくなります。
        // 合っていないハッシュを残すのは、無いより悪い状態です。
        //
        // 書き換えたあとの値で計算し直しますが、それには生成器と同じ数え方を再現できている
        // 必要があります。**確かめる方法は1つで、書き換える前の値で計算して、
        // 元のファイルに入っている値と一致するかを見ることです。**
        // 一致しないなら再現できていないということなので、そのときは項目ごと外します。
        var recorded = document["parameters_sha256"]?.GetValue<string>();
        var canRehash = recorded is not null && ParametersHash(parameters) == recorded;

        var changed = new JsonObject();
        if (matrix is not null) Replace(parameters, changed, "matrix", matrix);
        Replace(parameters, changed, "range", range);

        var hashNote = "";
        if (recorded is not null)
        {
            if (canRehash && ParametersHash(parameters) is { } rehashed)
            {
                document["parameters_sha256"] = rehashed;
                hashNote = "parameters_sha256 は書き換えたあとの条件で計算し直しました。";
            }
            else
            {
                document.Remove("parameters_sha256");
                hashNote = "parameters_sha256 は外しました。"
                    + "条件を書き換えると元の値は合わなくなりますが、"
                    + "こちらで計算し直した値が生成器の数え方と一致することを確かめられなかったためです。";
            }
        }

        // プレビュー画像などは連れて行きません。**元の条件で作られた絵だからです。**
        // matrix を変えた manifest に、変える前の条件で描かれた preview.png が付いていると、
        // その絵が「この条件で見るとこうなる」ものだと読まれます。
        var dropped = new JsonArray();
        if (document["files"]?.AsArray() is { } files)
        {
            var kept = new JsonArray();
            foreach (var file in files.ToArray())
            {
                var node = file?.AsObject();
                if (node is null) continue;
                files.Remove(file);
                if (ManifestInfo.Same(node["kind"]?.GetValue<string>(), "raw")) kept.Add(node);
                else dropped.Add(node["path"]?.GetValue<string>() ?? "");
            }

            document["files"] = kept;
        }

        document["derived_from"] = new JsonObject
        {
            ["manifest"] = sourceFileName,
            ["tool"] = Tool,
            ["written_at"] = writtenAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["changed"] = changed,
            ["dropped_files"] = dropped,
            ["note"] = "RAWのバイト列は元のものと同じです。読み方（matrix / range）だけを差し替えて記録しました。"
                + "生成器が作ったものではありません。"
                + (dropped.Count > 0
                    ? "元の manifest にあった RAW 以外のファイルは外しています。"
                      + "それらは差し替える前の条件で作られたものなので、この manifest の条件では合いません。"
                    : "")
                + hashNote,
        };

        return document.ToJsonString(WriteOptions) + "\n";
    }

    /// <summary>
    /// 生成器と同じ数え方で、条件のハッシュを求めます。
    ///
    /// 生成側（`src/vtp/manifest.py`）は
    /// `sha256(json.dumps(params, sort_keys=True, ensure_ascii=False).encode("utf-8"))` です。
    /// Python の `json.dumps` は既定で `", "` と `": "` を区切りに使い、キーは
    /// コードポイント順に並べます。ここではその形をそのまま組み立てます。
    ///
    /// <b>小数が入っていたら null を返します。</b> 小数の文字列化は Python と C# で
    /// 揃うと言い切れないので、合わない可能性のあるハッシュを書くくらいなら計算しません。
    /// </summary>
    internal static string? ParametersHash(JsonObject parameters)
    {
        var builder = new System.Text.StringBuilder();
        if (!TryWrite(parameters, builder)) return null;

        var bytes = System.Text.Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool TryWrite(JsonNode? node, System.Text.StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return true;

            case JsonObject obj:
            {
                builder.Append('{');
                var first = true;
                // Python の sort_keys はコードポイント順なので、序数比較で並べます。
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(", ");
                    first = false;
                    WriteString(pair.Key, builder);
                    builder.Append(": ");
                    if (!TryWrite(pair.Value, builder)) return false;
                }
                builder.Append('}');
                return true;
            }

            case JsonArray array:
            {
                builder.Append('[');
                var first = true;
                foreach (var item in array)
                {
                    if (!first) builder.Append(", ");
                    first = false;
                    if (!TryWrite(item, builder)) return false;
                }
                builder.Append(']');
                return true;
            }

            case JsonValue value:
            {
                // 読み込んだままの値（JsonElement が中身）と、こちらで差し替えた値（string が中身）が
                // 混ざります。中身の持ち方で分岐すると、差し替えた側で落ちます。
                // どちらでも通る TryGetValue で取り出します。
                if (value.TryGetValue<string>(out var text))
                {
                    WriteString(text, builder);
                    return true;
                }

                if (value.TryGetValue<bool>(out var flag))
                {
                    builder.Append(flag ? "true" : "false");
                    return true;
                }

                // 整数だけ引き受けます。小数は Python と同じ文字列になると言い切れません。
                if (value.TryGetValue<long>(out var number))
                {
                    builder.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                }

                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Python の `json.dumps(..., ensure_ascii=False)` と同じ文字列化です。
    /// 非ASCIIはそのまま、制御文字は `\uXXXX`（既定の短縮形があるものはそちら）にします。
    /// </summary>
    private static void WriteString(string value, System.Text.StringBuilder builder)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }

    private static void Replace(JsonObject parameters, JsonObject changed, string key, string value)
    {
        var before = parameters[key]?.GetValue<string>();
        if (ManifestInfo.Same(before, value)) return;

        changed[key] = new JsonObject
        {
            ["from"] = before,
            ["to"] = value,
        };
        parameters[key] = value;
    }

    /// <summary>
    /// 書き出す先です。<b>元の manifest と同じフォルダに置きます。</b>
    ///
    /// `files[].path` は manifest のある場所からの相対と決まっているので、
    /// 別のフォルダへ置くと、そのままではRAWを指せなくなります。
    /// 出力先フォルダ（画像の保存先）とは別扱いにしているのはそのためです。
    /// </summary>
    public static string SuggestPath(string sourceManifestPath, string suffix)
    {
        var directory = Path.GetDirectoryName(sourceManifestPath) ?? ".";
        var name = Path.GetFileName(sourceManifestPath);
        if (name.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
            name = name[..^".manifest.json".Length];

        return Path.Combine(directory, $"{name}{suffix}.manifest.json");
    }
}

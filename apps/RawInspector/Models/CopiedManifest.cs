using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RawInspector.Models;

/// <summary>
/// RAWを複製するときに添える manifest を作ります。
///
/// RAWはただのバイト列です。幅も高さもビット深度も格納形式も、<b>どこにも書いてありません</b>。
/// manifest と離した時点で、そのファイルが何なのかは誰にも分かりません
/// （分かるとすれば、大きさを総当たりで当てられる人だけです）。
/// なので複製は RAW と manifest の1組で出します。
///
/// 中身は元の manifest のままで、<b>指し先のファイル名だけ</b>を複製後の名前へ直します。
/// 条件は1つも変えないので、<c>parameters</c> も <c>parameters_sha256</c> もそのまま残します
/// （<see cref="DerivedManifest"/> は読み方を書き換えるので、あちらはハッシュを触ります）。
/// </summary>
public static class CopiedManifest
{
    /// <summary>
    /// 書き出しの設定です。<see cref="DerivedManifest"/> と揃えます。
    /// 非ASCIIをエスケープしないのは、日本語の注記が `\uXXXX` の羅列になると読めないためです。
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerOptions.Default)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 複製に添える manifest のパスです。
    /// <b>RAWと同じ場所・同じ名前</b>にします。組であることが名前から読めるようにするためです。
    /// </summary>
    public static string PathFor(string copiedRawPath)
    {
        var directory = Path.GetDirectoryName(copiedRawPath) ?? ".";
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(copiedRawPath) + ".manifest.json");
    }

    /// <param name="originalJson">元の manifest の中身です。</param>
    /// <param name="sourceManifestFileName">元の manifest のファイル名（場所は含めません）。</param>
    /// <param name="copiedRawFileName">複製したRAWのファイル名（場所は含めません）。</param>
    /// <param name="writtenAt">書き出した時刻です。</param>
    public static string Build(
        string originalJson, string sourceManifestFileName, string copiedRawFileName, DateTimeOffset writtenAt)
    {
        // 元の JSON をそのまま読んで差し替えます。型へ読み込んでから書き戻すと、
        // こちらが知らない項目が黙って消えます。
        var document = JsonNode.Parse(originalJson)?.AsObject()
            ?? throw new InvalidDataException("元の manifest を読み込めませんでした。");

        var dropped = new JsonArray();
        var pointedAtCopy = false;

        if (document["files"]?.AsArray() is { } files)
        {
            var kept = new JsonArray();
            foreach (var file in files.ToArray())
            {
                var node = file?.AsObject();
                if (node is null) continue;
                files.Remove(file);

                // RAW以外（preview.png など）は連れて行きません。複製するのはRAWだけなので、
                // 残すと、そこに無いファイルを指したままの manifest になります。
                if (!ManifestInfo.Same(node["kind"]?.GetValue<string>(), "raw"))
                {
                    dropped.Add(node["path"]?.GetValue<string>() ?? "");
                    continue;
                }

                // 保存のときに名前を変えられることがあるので、指し先を複製後の名前へ直します。
                // 元の名前のままにすると、隣に無いファイルを指します。
                node["path"] = copiedRawFileName;
                pointedAtCopy = true;
                kept.Add(node);
            }

            document["files"] = kept;
        }

        if (!pointedAtCopy)
            throw new InvalidDataException("元の manifest に RAW の項目がありません。");

        document["derived_from"] = new JsonObject
        {
            ["manifest"] = sourceManifestFileName,
            ["tool"] = DerivedManifest.Tool,
            ["written_at"] = writtenAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["dropped_files"] = dropped,
            ["note"] = "RAWをそのまま複製したときに添えた manifest です。"
                + "条件は元のものと同じで、指し先のファイル名だけを複製後の名前へ直しています。"
                + "RAWのバイト列も条件も変えていないので、parameters_sha256 は元の値のままです。"
                + (dropped.Count > 0
                    ? "RAW以外のファイルは複製しないため、files からは外しました。"
                    : ""),
        };

        return document.ToJsonString(WriteOptions) + "\n";
    }
}

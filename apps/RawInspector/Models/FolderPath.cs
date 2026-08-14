using System.IO;

namespace RawInspector.Models;

/// <summary>
/// フォルダのパスを、ファイルダイアログへ渡せる形に整えます。
///
/// **`/` 区切りのパスを渡すとダイアログが落ちます。**
/// `InitialDirectory` はシェルの `SHCreateItemFromParsingName` へ流れ、
/// そこは `\` しか受け付けません。`C:/Users/...` を渡すと E_INVALIDARG になり、
/// `ArgumentException: Value does not fall within the expected range.` で
/// **アプリごと終了します**（例外はダイアログを開く前に出るので、
/// 保存処理を try で囲っていても捕まりません）。
///
/// 区切りが混ざる経路は普通にあります。設定ファイルへ手で書いた場合、
/// スクリプトから書いた場合、他のツールから受け取った場合。
/// なので受け取った時点と使う時点の両方で整えます。
///
/// 消えたフォルダも同じように落ちます。存在しないものは空文字にします
/// （空文字ならダイアログ側が既定の場所を使い、落ちません）。
/// </summary>
public static class FolderPath
{
    /// <summary>区切りを揃えて絶対パスにします。整えられなければ null です。</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            // GetFullPath が区切りを `\` へ揃え、相対パスも解決します。
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            // 使えない文字が入っているなど。呼び手が判断できるよう null を返します。
            return null;
        }
    }

    /// <summary>
    /// ダイアログの <c>InitialDirectory</c> に渡せる値を返します。
    /// 渡せないときは空文字です。ダイアログは空文字なら既定の場所を使います。
    /// </summary>
    public static string ForDialog(string? path)
    {
        var full = Normalize(path);
        return full is not null && Directory.Exists(full) ? full : "";
    }
}

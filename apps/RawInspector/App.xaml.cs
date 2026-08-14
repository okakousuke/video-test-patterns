using System.Windows;
using System.Windows.Threading;

namespace RawInspector;

public partial class App : Application
{
    public App()
    {
        // 拾えなかった例外で黙って終了させません。
        //
        // このアプリは読むだけで、RAWへは書き込みません。
        // なので途中で失敗しても、続けられる状態はそのまま続けられます。
        // 落ちてしまうと、開いていたフォルダも表示条件も倍率も失われ、
        // しかも「何が起きたか」が残りません（実際、保存ダイアログへ `/` 区切りの
        // パスを渡して終了したとき、理由はイベントログを見るまで分かりませんでした）。
        //
        // 握りつぶすのではなく、理由を出して続けます。
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        MessageBox.Show(
            $"{e.Exception.GetType().Name}\n\n{e.Exception.Message}\n\n"
            + "この操作は中断しましたが、開いているRAWと表示条件はそのままです。\n"
            + "RAWファイルには書き込んでいません。",
            "処理を続けられませんでした",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using RawInspector.Models;

namespace RawInspector;

/// <summary>
/// 使い方ドキュメントを出す窓です。
///
/// Markdownを既定のアプリへ投げる方法も考えましたが、Windowsでは `.md` に関連付けが無いと
/// 「このファイルを開く方法を選んでください」が出ます。読む人の環境に何が入っているかは
/// こちらで決められないので、アプリの中で出します。
/// 元のファイルを開きたい人のために、下の帯にボタンを置いています。
///
/// 窓は1つだけにします。ボタンを押すたびに増えると、どれが最新なのか分からなくなります。
/// </summary>
public partial class HelpWindow : Window
{
    private static HelpWindow? _open;

    private string _current = HelpLibrary.Launcher;
    private string? _currentFile;

    /// <summary>目次の選択を自分で書き換えるときに、読み込みが二重に走らないようにします。</summary>
    private bool _syncingSelection;

    public HelpWindow()
    {
        InitializeComponent();

        var view = new ListCollectionView(HelpLibrary.Contents.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HelpDocument.Section)));
        ContentsList.ItemsSource = view;
    }

    /// <summary>使い方を開きます。すでに開いていれば、その窓で行き先を切り替えます。</summary>
    public static void ShowDocument(Window owner, string relativePath)
    {
        if (_open is null)
        {
            // 親は本体の窓にします。生成の窓から開いたときに、生成の窓を閉じると
            // 読んでいる途中の使い方まで一緒に消えてしまうためです。
            _open = new HelpWindow { Owner = Application.Current?.MainWindow ?? owner };
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else if (_open.WindowState == WindowState.Minimized)
        {
            _open.WindowState = WindowState.Normal;
        }

        _open.Navigate(relativePath);
        _open.Activate();
    }

    private void Navigate(string relativePath, string? anchor = null)
    {
        _current = relativePath;

        var content = HelpLibrary.Read(relativePath);
        _currentFile = content.File;

        Viewer.Document = MarkdownRenderer.Render(content.Text, OnLink);

        SourceText.Text = content.File ?? $"実行ファイルへ埋め込んだもの（docs/{relativePath}）";
        OpenSourceButton.IsEnabled = content.File is not null;
        OpenSourceButton.ToolTip = content.File is not null
            ? "この文書の元ファイルをエクスプローラーで選択表示します。"
            : "この文書は実行ファイルへ埋め込んだものです。開ける元ファイルがありません。";

        Title = $"使い方 — {TitleOf(relativePath)}";

        _syncingSelection = true;
        ContentsList.SelectedItem = HelpLibrary.Contents.FirstOrDefault(d => d.Path == relativePath);
        _syncingSelection = false;

        // 同じ文書を開き直したときに、前に読んでいた位置が残っていると読み始めが分かりません。
        if (anchor is { Length: > 0 }) BringAnchorIntoView(anchor);
        else ScrollToTop();
    }

    private void ScrollToTop() =>
        Dispatcher.InvokeAsync(() => Viewer.Document?.Blocks.FirstBlock?.BringIntoView(),
            DispatcherPriority.Loaded);

    private static string TitleOf(string relativePath) =>
        HelpLibrary.Contents.FirstOrDefault(d => d.Path == relativePath)?.Title ?? relativePath;

    /// <summary>
    /// 見出しへ飛びます。組み上がる前に呼ぶと位置が出ないので、レイアウトの後まで待ちます。
    /// </summary>
    private void BringAnchorIntoView(string anchor)
    {
        var target = MarkdownRenderer.Anchor(Uri.UnescapeDataString(anchor));
        Dispatcher.InvokeAsync(() =>
        {
            var heading = Viewer.Document?.Blocks
                .OfType<Paragraph>()
                .FirstOrDefault(p => (p.Tag as string) == target);
            heading?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 文書の中のリンクです。外部URLはブラウザへ、`.md` は同じ窓で開きます。
    /// 画像やそれ以外のファイルは既定のアプリへ渡します。
    /// </summary>
    private void OnLink(string destination)
    {
        if (destination.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Launch(destination);
            return;
        }

        var anchor = "";
        var hash = destination.IndexOf('#');
        if (hash >= 0)
        {
            anchor = destination[(hash + 1)..];
            destination = destination[..hash];
        }

        // 同じ文書の中の見出しを指しているとき（[〜](#見出し)）はファイルを読み直しません。
        if (destination.Length == 0)
        {
            BringAnchorIntoView(anchor);
            return;
        }

        var resolved = HelpLibrary.ResolveLink(_current, destination);

        if (!resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            if (HelpLibrary.FindFile(resolved) is { } file) Launch(file);
            return;
        }

        Navigate(resolved, anchor);
    }

    private void OnContentsSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (ContentsList.SelectedItem is HelpDocument document) Navigate(document.Path);
    }

    /// <summary>元のファイルをエクスプローラーで選択表示します。</summary>
    private void OnOpenSource(object sender, RoutedEventArgs e)
    {
        if (_currentFile is not { } file || !File.Exists(file)) return;
        Launch("explorer.exe", $"/select,\"{file}\"");
    }

    private void OnCloseExecuted(object sender, ExecutedRoutedEventArgs e) => Close();

    private static void Launch(string target, string? arguments = null)
    {
        try
        {
            var info = new ProcessStartInfo(target) { UseShellExecute = true };
            if (arguments is not null)
            {
                info.Arguments = arguments;
                info.UseShellExecute = false;
            }
            Process.Start(info);
        }
        catch (Exception ex)
        {
            // 開けなくても困るのはこの操作だけなので、伝えるだけにします。
            MessageBox.Show(ex.Message, "開けませんでした", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

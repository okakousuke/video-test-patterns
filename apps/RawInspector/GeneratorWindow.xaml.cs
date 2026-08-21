using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RawInspector.Models;
using RawInspector.ViewModels;

namespace RawInspector;

/// <summary>
/// パターンを作る窓です。
///
/// 本体の画面へ差し込まず別の窓にしているのは、一覧とプレビューの並びを崩さないためです。
/// 出したまま作り続けられるよう、手前に固定はしますが操作は止めません。
/// </summary>
public partial class GeneratorWindow : Window
{
    private readonly GeneratorViewModel _viewModel;

    public GeneratorWindow(string outputFolder, Func<string, Task> onGenerated,
                           Action<bool, string> onGenerationStateChanged,
                           int initialWidth = 1920, int initialHeight = 1080,
                           bool? minimizeAfterGenerate = null)
    {
        InitializeComponent();
        _viewModel = new GeneratorViewModel(
            onGenerated, onGenerationStateChanged, initialWidth, initialHeight)
        {
            OutputFolder = outputFolder,
            // 記録が無いときは、ビューモデルが持っている既定のままにします。
            MinimizeAfterGenerate = minimizeAfterGenerate ?? true,
        };
        // 窓を畳むのは画面側の仕事です。ビューモデルからは頼まれるだけにします。
        _viewModel.RequestMinimize = () => WindowState = WindowState.Minimized;
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.LoadCatalogAsync();
    }

    /// <summary>いまのチェックの状態です。閉じたあとも覚えておくために、外から読めるようにします。</summary>
    public bool MinimizeAfterGenerate => _viewModel.MinimizeAfterGenerate;

    private async void OnReconnect(object sender, RoutedEventArgs e) => await _viewModel.LoadCatalogAsync();

    /// <summary>この窓の使い方を出します。使い方の窓は1つだけなので、開いていればそれを使い回します。</summary>
    private void OnHelpExecuted(object sender, ExecutedRoutedEventArgs e) =>
        HelpWindow.ShowDocument(this, HelpLibrary.Generator);

    private void OnResetPatternOptions(object sender, RoutedEventArgs e) => _viewModel.ResetPatternOptions();

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "生成したものを置くフォルダ",
            InitialDirectory = FolderPath.ForDialog(_viewModel.OutputFolder),
        };
        if (dialog.ShowDialog(this) == true) _viewModel.OutputFolder = dialog.FolderName;
    }
}

using System.Windows;
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

    public GeneratorWindow(string outputFolder, Action<string> onGenerated)
    {
        InitializeComponent();
        _viewModel = new GeneratorViewModel(onGenerated) { OutputFolder = outputFolder };
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.LoadCatalogAsync();
    }

    private async void OnReconnect(object sender, RoutedEventArgs e) => await _viewModel.LoadCatalogAsync();

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

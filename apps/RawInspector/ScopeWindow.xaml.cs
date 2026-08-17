using System.Windows;
using System.Windows.Input;
using RawInspector.Models;
using RawInspector.ViewModels;

namespace RawInspector;

/// <summary>
/// 分布を見る窓です。
///
/// 本体へ差し込まず別の窓にしているのは、絵と分布を<b>並べて</b>見たいためです。
/// 同じ枠に入れると、どちらかを畳まないと片方が見えません。
/// モーダルにもしません（<c>Show</c> であって <c>ShowDialog</c> ではありません）。
/// 本体で条件を切り替えながら分布の変わり方を見る、という読み方ができなくなるためです。
/// </summary>
public partial class ScopeWindow : Window
{
    private readonly ScopeViewModel _viewModel;

    public ScopeWindow(Func<InspectionTarget?> provider)
    {
        InitializeComponent();
        _viewModel = new ScopeViewModel(provider);
        DataContext = _viewModel;

        // 開いた時点で1回数えます。開いてから「取り直す」を押させると、
        // 空の窓を見て「壊れている」と読まれます。
        Loaded += (_, _) => _viewModel.Refresh();
    }

    /// <summary>本体で別のRAWが選ばれたり、表示条件が変わったときに呼びます。</summary>
    public void NotifyTargetChanged() => _viewModel.NotifyTargetChanged();

    private void OnHelpExecuted(object sender, ExecutedRoutedEventArgs e) =>
        HelpWindow.ShowDocument(this, HelpLibrary.Scopes);
}

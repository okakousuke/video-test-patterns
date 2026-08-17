using System.Windows;
using System.Windows.Input;
using RawInspector.Models;
using RawInspector.ViewModels;

namespace RawInspector;

/// <summary>
/// 2枚を突き合わせる窓です。
///
/// 分布の窓と同じで、本体とは別の窓にしてモーダルにもしません。
/// 本体で条件を切り替えながら、差がどう変わるかを見られるようにするためです。
/// </summary>
public partial class CompareWindow : Window
{
    private readonly CompareViewModel _viewModel;

    public CompareWindow(
        Func<InspectionTarget?> leftProvider,
        Func<IReadOnlyList<CompareCandidate>> candidateProvider,
        Func<CompareCandidate, InspectionTarget?> loader)
    {
        InitializeComponent();
        _viewModel = new CompareViewModel(leftProvider, candidateProvider, loader);
        DataContext = _viewModel;

        // 候補は開いた時点で集めます。ただし突き合わせは押されるまでやりません。
        // 開いた瞬間に全画素を2枚ぶん読むと、窓が出るまで待たされます。
        Loaded += (_, _) => _viewModel.ReloadCandidates();
    }

    /// <summary>本体で別のRAWが選ばれたときに呼びます。候補は選び直しになります。</summary>
    public void NotifyTargetChanged() => _viewModel.ReloadCandidates();

    private void OnHelpExecuted(object sender, ExecutedRoutedEventArgs e) =>
        HelpWindow.ShowDocument(this, HelpLibrary.Compare);
}

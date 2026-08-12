using System.Windows.Controls;

namespace RawInspector;

/// <summary>
/// 開いたときに最初に出す画面です。
/// 中身は <see cref="ViewModels.DashboardViewModel"/> が持ちます。
/// </summary>
public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
}

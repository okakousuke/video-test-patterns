using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RawInspector;

/// <summary>
/// false のときに <see cref="Visibility.Hidden"/> を返します（Collapsed ではありません）。
///
/// ツールバーの一部は、色モデルやサブサンプリングによって出したり隠したりします。
/// Collapsed にすると幅が詰まって他のボタンが動き、選んだRAWによってツールバーの
/// 行数まで変わってしまいます。位置を固定したいので、場所は確保したまま隠します。
/// </summary>
public sealed class BoolToHiddenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Ch08.Beacon.Wpf;

/// <summary>Collapses an element while a Boolean is true: Send hides while a turn is busy.</summary>
public sealed class CollapsedWhenTrueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

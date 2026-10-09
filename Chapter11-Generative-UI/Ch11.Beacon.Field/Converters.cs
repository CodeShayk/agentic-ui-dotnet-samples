using System.Globalization;

namespace Ch11.Beacon.Field;

/// <summary>A dashed border while a component is a draft, the same cue the Desk gives.</summary>
public sealed class DraftDashesConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new DoubleCollection([4, 2]) : new DoubleCollection();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

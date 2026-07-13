using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PsGameTranslator.App.Converters;

/// <summary>Collapses the element when the bound value is null; shows it otherwise.</summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

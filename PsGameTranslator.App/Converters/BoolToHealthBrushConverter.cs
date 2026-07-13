using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PsGameTranslator.App.Converters;

/// <summary>Green for a healthy (true) status dot, red for a problem (false) one.</summary>
[ValueConversion(typeof(bool), typeof(Brush))]
public sealed class BoolToHealthBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OkBrush = new(Color.FromRgb(0x27, 0xD1, 0x6F));
    private static readonly SolidColorBrush ProblemBrush = new(Color.FromRgb(0xFF, 0x5C, 0x7A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? OkBrush : ProblemBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

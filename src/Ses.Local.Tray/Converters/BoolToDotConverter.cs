using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace Ses.Local.Tray.Converters;

/// <summary>Converts a bool (true = available) to a status dot brush.</summary>
public sealed class BoolToDotConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Brushes.LimeGreen : new SolidColorBrush(Color.Parse("#555"));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

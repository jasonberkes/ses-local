using Avalonia.Data.Converters;
using Avalonia.Media;
using Ses.Local.Core.Models;
using System.Globalization;

namespace Ses.Local.Tray.Converters;

public sealed class DotColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is StatusDot dot ? dot switch
        {
            StatusDot.Green  => Brushes.LimeGreen,
            StatusDot.Yellow => Brushes.Orange,
            StatusDot.Red    => Brushes.Red,
            _                => new SolidColorBrush(Color.Parse("#555"))
        } : Brushes.Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

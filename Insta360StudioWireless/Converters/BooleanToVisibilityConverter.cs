using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Insta360StudioWireless.Converters;

public class BooleanToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool bValue = value is bool b && b;

        bool invert = Invert || (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase));
        if (invert)
        {
            bValue = !bValue;
        }

        return bValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility vis)
        {
            bool result = vis == Visibility.Visible;
            if (Invert || (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)))
            {
                result = !result;
            }
            return result;
        }
        return false;
    }
}

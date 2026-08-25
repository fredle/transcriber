using System;
using System.Globalization;
using System.Windows.Data;

namespace MeetingTranscriber;

/// <summary>
/// Turns a ProgressBar's (Value, Maximum, container width) into the pixel
/// width of its fill, for the flat custom template in Theme.xaml. WPF's own
/// default template does the equivalent internally, but a custom template
/// has to compute it itself.
/// </summary>
public sealed class ProgressBarWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return 0.0;
        if (values[0] is not double value || values[1] is not double maximum || values[2] is not double width)
            return 0.0;
        if (maximum <= 0 || width <= 0 || double.IsNaN(width)) return 0.0;

        var fraction = Math.Clamp(value / maximum, 0.0, 1.0);
        return width * fraction;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

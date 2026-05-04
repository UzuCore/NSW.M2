using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NSW.Avalonia.Converters;

public class LevelToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double level)
        {
            if (level <= 2) return Brushes.White;

            double t = (level - 3) / 15.0;
            t = Math.Clamp(t, 0, 1);

            byte r = (byte)(255 * t);
            byte g = 255;
            byte b = (byte)(255 * (1.0 - t * 0.8));

            return new SolidColorBrush(new Color(255, r, g, b));
        }
        return Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
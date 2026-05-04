using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace NSW.Avalonia.Converters;

public class CompressLevelTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double level)
        {
            int intLevel = (int)level;
            return intLevel == 2 ? "NSP" : $"{intLevel}(NSZ)";
        }
        return "0";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
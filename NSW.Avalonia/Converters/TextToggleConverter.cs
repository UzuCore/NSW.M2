using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NSW.Avalonia.Converters;

public class TextToggleConverter : IMultiValueConverter
{
    public static readonly TextToggleConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 3 && values[0] is bool isRunning)
        {
            return isRunning ? values[2] : values[1];
        }
        return string.Empty;
    }
}
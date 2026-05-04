using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NSW.Avalonia.Converters;

public class IconConverter : IMultiValueConverter
{
    public static readonly IconConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 3 && values[0] is bool isRunning)
        {
            var iconPath = isRunning ? values[2]?.ToString() : values[1]?.ToString();

            if (string.IsNullOrEmpty(iconPath))
                return null;

            try
            {
                var uri = new Uri(iconPath);
                return new Bitmap(AssetLoader.Open(uri));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IconConverter Error] {ex.Message}");
                return null;
            }
        }
        return null;
    }
}
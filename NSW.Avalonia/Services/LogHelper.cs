using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NSW.Core.Enums;
using System;

namespace NSW.Avalonia.Services;

public static class LogHelper
{
    public static void Log(SelectableTextBlock logBox, ScrollViewer svLogBox, Bitmap icon, string msg, LogLevel level = LogLevel.Info)
    {
        var color = GetColor(level);

        Dispatcher.UIThread.Post(() =>
        {
            logBox.Inlines ??= [];
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            var brush = new SolidColorBrush(color);

            var timeRun = new Run($"[{timestamp}] ")
            {
                Foreground = brush,
                BaselineAlignment = BaselineAlignment.Center
            };
            logBox.Inlines.Add(timeRun);

            if (icon != null)
            {
                var img = new Image
                {
                    Source = icon,
                    Width = 40,
                    Height = 40,
                    Margin = new Thickness(4, 0, 8, 0)
                };

                var container = new InlineUIContainer(img)
                {
                    BaselineAlignment = BaselineAlignment.Center
                };
                logBox.Inlines.Add(container);
            }

            var msgRun = new Run($"{msg}{Environment.NewLine}")
            {
                Foreground = brush,
                BaselineAlignment = BaselineAlignment.Center
            };
            logBox.Inlines.Add(msgRun);

            Dispatcher.UIThread.Post(() => svLogBox.ScrollToEnd(), DispatcherPriority.Background);
        });
    }

    private static Color GetColor(LogLevel level)
    {
        return level switch
        {
            LogLevel.Ok => Color.FromRgb(100, 200, 100),
            LogLevel.Highlight => Color.FromRgb(255, 200, 0),
            LogLevel.Error => Color.FromRgb(255, 80, 80),
            _ => Color.FromRgb(180, 180, 180),
        };
    }
}
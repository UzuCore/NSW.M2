using Avalonia;
using Avalonia.Controls;
using System;

namespace NSW.Avalonia.UI;

public partial class IconButton : UserControl
{
    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    public string DefaultText { get => GetValue(DefaultTextProperty); set => SetValue(DefaultTextProperty, value); }

    public string CancelText { get => GetValue(CancelTextProperty); set => SetValue(CancelTextProperty, value); }

    public string DefaultIcon { get => GetValue(DefaultIconProperty); set => SetValue(DefaultIconProperty, value); }

    public string CancelIcon { get => GetValue(CancelIconProperty); set => SetValue(CancelIconProperty, value); }

    public static readonly StyledProperty<bool> IsRunningProperty = AvaloniaProperty.Register<IconButton, bool>(nameof(IsRunning));
    public static readonly StyledProperty<string> DefaultTextProperty = AvaloniaProperty.Register<IconButton, string>(nameof(DefaultText));
    public static readonly StyledProperty<string> CancelTextProperty = AvaloniaProperty.Register<IconButton, string>(nameof(CancelText));
    public static readonly StyledProperty<string> DefaultIconProperty = AvaloniaProperty.Register<IconButton, string>(nameof(DefaultIcon));
    public static readonly StyledProperty<string> CancelIconProperty = AvaloniaProperty.Register<IconButton, string>(nameof(CancelIcon));

    public event EventHandler<global::Avalonia.Interactivity.RoutedEventArgs>? Click;

    public IconButton()
    {
        InitializeComponent();
        InnerButton.Click += (s, e) => Click?.Invoke(this, e);
    }
}
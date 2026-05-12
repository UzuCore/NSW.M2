using Avalonia.Controls;
using NSW.M2.Avalonia.ViewModels;

namespace NSW.M2.Avalonia.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
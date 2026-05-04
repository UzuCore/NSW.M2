using NSW.M2.Avalonia.Services;
using ReactiveUI;
using System;
using System.Diagnostics;
using System.IO;
using System.Reactive;

namespace NSW.M2.Avalonia.ViewModels;

public class MainViewModel: NSW.Avalonia.ViewModels.MainViewModelBase
{
    #region Fields & Properties

    private string _outputPath = string.Empty;
    private bool _isMerging;
    private bool _isSplitting;

    public bool IsMerging
    {
        get => _isMerging;
        set => this.RaiseAndSetIfChanged(ref _isMerging, value);
    }
    
    public bool IsSplitting
    {
        get => _isSplitting;
        set => this.RaiseAndSetIfChanged(ref _isSplitting, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => this.RaiseAndSetIfChanged(ref _outputPath, value);
    }

    public ReactiveCommand<Unit, Unit> OpenWorkSpaceCommand { get; }

    #endregion

    #region Constructor

    public MainViewModel(): base(new AppConfig())
    {
        OutputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
        OpenWorkSpaceCommand = ReactiveCommand.Create(ExecuteOpenWorkSpace);
    }

    #endregion

    #region Private Methods

    private void ExecuteOpenWorkSpace()
    {
        var path = OutputPath?.Trim();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        OpenFolderPlatformSpecific(path);
    }

    private static void OpenFolderPlatformSpecific(string path)
    {
        if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", path);
        else if (OperatingSystem.IsLinux()) Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
        else if (OperatingSystem.IsMacOS()) Process.Start("open", path);
    }

    #endregion
}
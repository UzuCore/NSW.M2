using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NSW.Avalonia.Services;
using NSW.Avalonia.UI;
using NSW.Core.Enums;
using NSW.M2.Avalonia.Services;
using NSW.M2.Avalonia.ViewModels;
using NSW.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Res = NSW.Core.Properties.Resources;

namespace NSW.M2.Avalonia.Views;

public partial class MainView : UserControl
{
    #region Fields & Properties

    private readonly Stopwatch _totalSw = new();
    private CancellationTokenSource? _cts;
    private readonly Progress<ProgressInfo> _progressReporter;

    private MainViewModel ViewModel => DataContext as MainViewModel;

    #endregion

    #region Construtor

    public MainView()
    {
        InitializeComponent();

        _progressReporter = new(info =>
        {
            Dispatcher.UIThread.Post(() => {
                progress.Value = info.Percent;
                progressLabel.Text = info.Label;
                progressPercent.Text = $"{info.Percent}%";
                progressTime.Text = info.TimeInfo;
                progressSpeed.Text = info.Speed;
            });
        });

        this.DetachedFromVisualTree += (s, e) => ViewModel.SaveConfig();
    }

    #endregion

    #region Protected Overrides

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    #endregion

    #region Event Handlers

    private async void BtnSettings_Click(object sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new SettingsWindow(ViewModel);
        await window.ShowDialog(TopLevel.GetTopLevel(this) as Window);
        ViewModel.SaveConfig();
    }

    private async void BtnBrowseOutput_Click(object sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Res.Hint_SelectOutput,
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            txtOutput.Text = folders[0].Path.LocalPath;
        }
    }

    private async void BtnMergeStart_Click(object sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            await SetWorking(false, isSplit: false);
            return;
        }

        if (!fileMgr.GameFiles.Any())
        {
            await MessageBoxHelper.ShowWarning(Res.Main_Err_NoFiles);
            return;
        }

        if (!FileManagerControl.KeyExists())
        {
            await MessageBoxHelper.ShowWarning(Res.Main_Err_NoKeys);
            return;
        }

        if (fileMgr.GameFiles.Any(f => f.IsKeyMissing))
        {
            await SetWorking(true);
            progressLabel.Text = Res.Main_Log_Recalculating;

            bool completed = false;
            fileMgr.RecalcKeyMissingFiles(() => completed = true);

            while (!completed)
                await Task.Delay(100);
        }

        if (!TryGetMergeInputs(out var inputPaths, out var outputDir, out string errorMsg))
        {
            await MessageBoxHelper.ShowWarning(errorMsg);
            await SetWorking(false);
            return;
        }

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        _cts = new CancellationTokenSource();
        await SetWorking(true);
        _totalSw.Restart();

        int compressLevel = (int)ViewModel.CompressLevel;
        if (compressLevel == 2)
            compressLevel = 0;
        bool isValidationEnabled = ViewModel.IsValidationEnabled;
        bool useBlockMode = ViewModel.UseBlockMode;
        bool forceKeyGen0 = ViewModel.ForceKeyGen0;

        try
        {
            await Task.Run(async () =>
            {
                var results = await NspMergeService.Merge(inputPaths, outputDir, compressLevel, isValidationEnabled, useBlockMode, forceKeyGen0, _progressReporter, Log, _cts.Token);

                if (results != null && results.Count > 0)
                {
                    Log(string.Format(Res.Main_Log_AllComplete, _totalSw.Elapsed.ToString(@"mm\:ss")), LogLevel.Ok);
                    Dispatcher.UIThread.Post(async () => await MessageBoxHelper.ShowInfo(Res.Main_Msg_Done));
                }
            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log(Res.Button_Cancel, LogLevel.Error);
        }
        catch (Exception ex)
        {
            Log($"{Res.Log_Error}: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            await SetWorking(false);
        }
    }

    private async void BtnSplitStart_Click(object sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            await SetWorking(false, isSplit: true);
            return;
        }

        if (!fileMgr.GameFiles.Any())
        {
            await MessageBoxHelper.ShowWarning(Res.Main_Err_NoFiles);
            return;
        }

        string outputDir = txtOutput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(outputDir))
        {
            await MessageBoxHelper.ShowWarning(Res.Main_Err_NoOutput);
            return;
        }

        if (!FileManagerControl.KeyExists())
        {
            await MessageBoxHelper.ShowWarning(Res.Main_Err_NoKeys);
            return;
        }

        if (fileMgr.GameFiles.Any(f => f.IsKeyMissing))
        {
            await SetWorking(true, isSplit: true);
            progressLabel.Text = Res.Main_Log_Recalculating;

            bool completed = false;
            fileMgr.RecalcKeyMissingFiles(() => completed = true);

            while (!completed)
                await Task.Delay(100);
        }

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        _cts = new CancellationTokenSource();
        await SetWorking(true, isSplit: true);
        _totalSw.Restart();

        int compressLevel = (int)ViewModel.CompressLevel;
        if (compressLevel == 2)
            compressLevel = 0;
        bool isValidationEnabled = ViewModel.IsValidationEnabled;
        bool useBlockMode = ViewModel.UseBlockMode;
        bool forceKeyGen0 = ViewModel.ForceKeyGen0;

        try
        {
            await Task.Run(async () =>
            {
                int resultCount = 0;

                for (int i = 0; i < fileMgr.GameFiles.Count; i++)
                {
                    var fileVm = fileMgr.GameFiles[i];
                    _cts.Token.ThrowIfCancellationRequested();
                    resultCount += await NspSplitService.Split(fileVm.FilePath, outputDir, compressLevel, useBlockMode, isValidationEnabled, forceKeyGen0, i + 1, fileMgr.GameFiles.Count, _progressReporter, Log, _cts.Token);
                }

                Log(string.Format(Res.Main_Log_AllComplete, _totalSw.Elapsed.ToString(@"mm\:ss")), LogLevel.Ok);

                if (resultCount > 0)
                    Dispatcher.UIThread.Post(async () => await MessageBoxHelper.ShowInfo(Res.Main_Msg_SplitDone));

            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log(Res.Log_Error + ": " + Res.Button_Cancel, LogLevel.Error);
        }
        catch (Exception ex)
        {
            Log($"{Res.Log_Error}: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            await SetWorking(false);
        }
    }

    #endregion

    #region Private Methods

    private bool TryGetMergeInputs(out List<string> inputPaths, out string outputDir, out string errorMsg)
    {
        inputPaths = [];
        outputDir = string.Empty;
        errorMsg = string.Empty;

        if (fileMgr.GameFiles.Any(f => f.IsKeyMissing))
        {
            errorMsg = Res.Main_Err_NoKeys;
            return false;
        }

        outputDir = txtOutput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(outputDir))
        {
            errorMsg = Res.Main_Err_NoOutput;
            return false;
        }

        inputPaths = [.. fileMgr.GameFiles
            .Select(f => f.FilePath)];

        return true;
    }

    private void Log(string msg, LogLevel level = LogLevel.Info, string titleId = "") => LogHelper.Log(logBox, svLogBox, fileMgr.GetCoverImageByTitleId(titleId), msg, level);


    private async Task SetWorking(bool working, bool isSplit = false)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            btnMergeStart.IsRunning = working && !isSplit;
            btnSplitStart.IsRunning = working && isSplit;
            btnMergeStart.IsEnabled = !working || (working && !isSplit);
            btnSplitStart.IsEnabled = !working || (working && isSplit);
            fileMgr.IsEnabled = !working;
            btnWorkSpace.IsEnabled = !working;
            btnBrowseOutput.IsEnabled = !working;
            txtOutput.IsEnabled = !working;
            progressArea.IsVisible = working;
        });
    }

    #endregion
}
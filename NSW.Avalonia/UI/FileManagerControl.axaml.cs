using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using LibHac.Tools.FsSystem;
using NSW.Avalonia.ViewModels;
using NSW.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Path = System.IO.Path;
using Res = NSW.Core.Properties.Resources;

namespace NSW.Avalonia.UI;

public partial class FileManagerControl : UserControl, INotifyPropertyChanged
{
    #region Fields & Properties

    FileManagerViewModel ViewModel { get; } = new();

    public ObservableCollection<GameFile> GameFiles => ViewModel.GameFiles;

    public Button ExtraButton => btnExtra;

    #endregion

    #region Events

    public Action? ExtraButtonClicked;

    #endregion

    #region Constructor

    public FileManagerControl()
    {
        DataContext = ViewModel;
        InitializeComponent();
        
        lvFiles.AddHandler(DragDrop.DragOverEvent, LvFiles_DragOver, RoutingStrategies.Bubble);
        lvFiles.AddHandler(DragDrop.DropEvent, LvFiles_Drop, RoutingStrategies.Bubble);
    }

    #endregion

    #region Event Handlers

    private async void BtnAddFile_Click(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Res.Dialog_SelectGameFile,
            AllowMultiple = true,
            FileTypeFilter =
            [
            new FilePickerFileType(Res.Filter_SwitchFiles)
            {
                Patterns = ["*.nsp", "*.xci", "*.nsz", "*.xcz"]
            },
            new FilePickerFileType(Res.Filter_AllFiles)
            {
                Patterns = ["*.*"]
            }
        ]
        });

        if (files.Count > 0)
        {
            var fileNames = files.Select(f => f.Path.LocalPath).ToArray();
            await ViewModel.AddFilesAsync(fileNames);
        }
    }

    private void BtnRemoveFile_Click(object sender, RoutedEventArgs e)
    {
        var selected = lvFiles.SelectedItems.Cast<GameFile>().ToList();
        if (selected.Count > 0)
            ViewModel.RemoveFiles(selected);
    }

    private void BtnRemoveAllFiles_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearFiles();
    }

    private void BtnExtra_Click(object sender, RoutedEventArgs e) => ExtraButtonClicked?.Invoke();

    private void LvFiles_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) BtnRemoveFile_Click(sender, new RoutedEventArgs());
    }

    private void LvFiles_DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles()?.Length > 0)
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
    }

    private async void LvFiles_Drop(object? sender, DragEventArgs e)
    {
        var storageItems = e.DataTransfer.TryGetFiles();

        if (storageItems != null)
        {
            var paths = storageItems
                .Select(item => {
                    try
                    {
                        return item.Path.LocalPath;
                    }
                    catch (InvalidOperationException)
                    {
                        return Path.GetFullPath(item.Path.OriginalString.Replace("file://", ""));
                    }
                })
                .Where(path => !string.IsNullOrEmpty(path));

            var allFilePaths = ExpandPaths(paths);
            await ViewModel.AddFilesAsync(allFilePaths);
        }
    }

    private async void IconImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel.SelectedGame == null || ViewModel.SelectedGame.CoverBitmap == null) return;

        var pointer = e.GetCurrentPoint(this);

        if (!pointer.Properties.IsLeftButtonPressed) return;

        string safeFileName = Path.GetFileNameWithoutExtension(ViewModel.SelectedGame.FilePath);
        string exportName = $"{safeFileName}.png";
        string tempPath = Path.Combine(Path.GetTempPath(), exportName);

        try
        {
            ViewModel.SelectedGame.CoverBitmap.Save(tempPath);

            var data = new DataTransfer();

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                var storageFile = await topLevel.StorageProvider.TryGetFileFromPathAsync(tempPath);

                if (storageFile != null)
                {
                    var item = new DataTransferItem();
                    item.SetFile(storageFile);
                    data.Add(item);

                    await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DragExport] Error: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch {}
            }
        }
    }

    #endregion

    #region Public Methods

    public static bool KeyExists() => KeySetProvider.Instance.KeySet != null;

    public async void RecalcKeyMissingFiles(Action onCompleted)
    {
        await ViewModel.RecalcKeyMissingFilesAsync();
        onCompleted?.Invoke();
    }

    public Bitmap? GetCoverImageByTitleId(string? titleId)
    {
        return ViewModel.GetCoverImageByTitleId(titleId);
    }

    #endregion

    #region Private Methods

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
        };

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var files = Directory.EnumerateFiles(path, "*.*", enumerationOptions);

                foreach (var file in files)
                    yield return file;
            }
            else if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    #endregion
}
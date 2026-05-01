using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Res = NSW.Core.Properties.Resources;
using Path = System.IO.Path;

namespace NSW.Avalonia.UI;

public partial class FileManagerControl : UserControl, INotifyPropertyChanged
{
    public Button ExtraButton => btnExtra;
    public Action? ExtraButtonClicked;

    public ObservableCollection<GameFile> GameFiles { get; set; } = [];

    private GameFile? _selectedGame;
    public GameFile? SelectedGame
    {
        get => _selectedGame;
        set
        {
            _selectedGame = value;
            OnPropertyChanged();
        }
    }

    public event Action? FileListChanged;

    public new event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public FileManagerControl()
    {
        InitializeComponent();
        this.DataContext = this;

        lvFiles.AddHandler(DragDrop.DragOverEvent, LvFiles_DragOver, RoutingStrategies.Bubble);
        lvFiles.AddHandler(DragDrop.DropEvent, LvFiles_Drop, RoutingStrategies.Bubble);

        GameFiles.CollectionChanged += (s, e) => UpdateDropHint();
        UpdateDropHint();
    }

    public static bool KeyExists() => KeySetProvider.Instance.KeySet != null;

    public void RecalcKeyMissingFiles(Action onCompleted)
    {
        var targets = GameFiles.Where(f => f.IsKeyMissing).ToList();
        if (targets.Count == 0) { onCompleted(); return; }

        var keySet = KeySetProvider.Instance.KeySet;
        if (keySet == null) { onCompleted(); return; }

        int remaining = targets.Count;
        foreach (var vm in targets)
        {
            string capturedPath = vm.FilePath;
            Task.Run(() =>
            {
                string result = LibHacHelper.DetectFileType(keySet, capturedPath);
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    vm.FileType = result;
                    if (Interlocked.Decrement(ref remaining) == 0)
                        onCompleted();
                });
            });
        }
    }

    private void UpdateDropHint()
    {
        dropHint.IsVisible = GameFiles.Count == 0;
        FileListChanged?.Invoke();
    }

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
            AddFiles(fileNames);
        }
    }

    private void BtnRemoveFile_Click(object sender, RoutedEventArgs e)
    {
        var selected = lvFiles.SelectedItems.Cast<GameFile>().ToList();

        if (selected.Count == 0) return;

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in selected)
                GameFiles.Remove(item);
        });
    }

    private void BtnRemoveAllFiles_Click(object sender, RoutedEventArgs e)
    {
        GameFiles.Clear();
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

    private void LvFiles_Drop(object? sender, DragEventArgs e)
    {
        var storageItems = e.DataTransfer.TryGetFiles();

        if (storageItems != null)
        {
            var paths = storageItems
                .Select(item => item.Path.LocalPath)
                .Where(path => !string.IsNullOrEmpty(path));

            var allFilePaths = ExpandPaths(paths);
            AddFiles(allFilePaths);
        }
    }

    private IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                    yield return file;
            }
            else if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private async void AddFiles(IEnumerable<string> paths)
    {
        var keySet = KeySetProvider.Instance.KeySet;
        bool keyMissing = keySet == null;

        if (keyMissing)
        {
            await MessageBoxHelper.ShowWarning(Res.Main_Err_NoKeys);
            foreach (var path in paths)
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".nsp" or ".xci" or ".nsz" or ".xcz")) continue;
                if (GameFiles.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;

                GameFiles.Add(new GameFile(path) { FileType = Res.Status_NoKey });
            }
            return;
        }

        if (keySet == null)
            return;

        var targetFiles = new List<GameFile>();
        foreach (var path in paths)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".nsp" or ".xci" or ".nsz" or ".xcz")) continue;
            if (GameFiles.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;

            var vm = new GameFile(path) { FileType = Res.Status_Analyzing };
            GameFiles.Add(vm);
            targetFiles.Add(vm);
        }

        if (targetFiles.Count == 0) return;

        await Task.Run(() =>
        {
            Parallel.ForEach(targetFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
            {                
                var info = LibHacHelper.GetGameFileInfo(keySet, file.FilePath);
                
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    file.FileType = info.Type;
                    file.TitleName = info.TitleName;
                    file.TitleId = info.TitleId;
                    file.Version = info.DisplayVersion;
                    file.Developer = info.Developer;

                    
                    if (info.IconData != null)
                    {
                        using var ms = new MemoryStream(info.IconData);
                        file.CoverBitmap = new global::Avalonia.Media.Imaging.Bitmap(ms);
                    }
                });
            });
        });

        Dispatcher.UIThread.Post(() =>
        {
            if (GameFiles.Count > 0)
            {
                lvFiles.SelectedIndex = 0;
            }
        }, DispatcherPriority.Background);
    }

    private async void IconImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (SelectedGame == null || SelectedGame.CoverBitmap == null) return;

        var pointer = e.GetCurrentPoint(this);

        if (!pointer.Properties.IsLeftButtonPressed) return;

        string safeFileName = Path.GetFileNameWithoutExtension(SelectedGame.FilePath);
        string exportName = $"{safeFileName}.png";
        string tempPath = Path.Combine(Path.GetTempPath(), exportName);

        try
        {            
            SelectedGame.CoverBitmap.Save(tempPath);

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
    }
}
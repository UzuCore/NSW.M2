using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NSW.Core;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Res = NSW.Core.Properties.Resources;

namespace NSW.Avalonia.ViewModels;

public class FileManagerViewModel : ReactiveObject
{
    #region Fields & Properties

    private GameFile? _selectedGame;

    public ObservableCollection<GameFile> GameFiles { get; } = [];
    
    public GameFile? SelectedGame
    {
        get => _selectedGame;
        set => this.RaiseAndSetIfChanged(ref _selectedGame, value);
    }

    #endregion

    #region Public Methods

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var keySet = KeySetProvider.Instance.KeySet;
        bool keyMissing = keySet == null;

        if (keyMissing)
        {
            foreach (var path in paths)
            {
                if (!IsSupportedExtension(path)) continue;
                if (GameFiles.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;

                GameFiles.Add(new GameFile(path) { FileType = Res.Status_NoKey });
            }
            return;
        }

        var targetFiles = new List<GameFile>();
        foreach (var path in paths)
        {
            if (!IsSupportedExtension(path)) continue;
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
                var info = LibHacHelper.GetGameFileInfo(keySet!, file.FilePath);

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
    }

    public void RemoveFiles(IEnumerable<GameFile> files)
    {
        foreach (var file in files.ToList())
            GameFiles.Remove(file);
    }

    public void ClearFiles() => GameFiles.Clear();

    public async Task RecalcKeyMissingFilesAsync()
    {
        var targets = GameFiles.Where(f => f.IsKeyMissing).ToList();
        if (targets.Count == 0) return;

        var keySet = KeySetProvider.Instance.KeySet;
        if (keySet == null) return;

        var tasks = targets.Select(async vm =>
        {
            string capturedPath = vm.FilePath;

            string result = await Task.Run(() => LibHacHelper.DetectFileType(keySet, capturedPath));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm.FileType = result;
            });
        });

        await Task.WhenAll(tasks);
    }

    public Bitmap? GetCoverImageByTitleId(string? titleId)
    {
        if (string.IsNullOrEmpty(titleId) || titleId.Length < 12)
            return null;

        string baseId = titleId[..12];

        return GameFiles.FirstOrDefault(f =>
            f.TitleId != null &&
            f.TitleId.StartsWith(baseId, StringComparison.OrdinalIgnoreCase))
            ?.CoverBitmap;
    }

    #endregion

    #region Private Methods

    private static bool IsSupportedExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".nsp" or ".xci" or ".nsz" or ".xcz";
    }

    #endregion
}
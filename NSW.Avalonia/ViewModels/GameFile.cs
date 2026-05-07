using Avalonia.Media;
using Avalonia.Media.Imaging;
using NSW.Utils;
using ReactiveUI;
using System;
using System.IO;

namespace NSW.Avalonia.ViewModels;

public class GameFile(string filePath) : ReactiveObject, IDisposable
{
    #region Fields & Properties

    private string _fileType = Core.Properties.Resources.Status_Analyzing;
    private Bitmap? _coverBitmap;

    public string FilePath { get; } = filePath;

    public string FileName => Path.GetFileNameWithoutExtension(FilePath);

    public string Extension => Path.GetExtension(FilePath).TrimStart('.');

    public IBrush ExtensionColor
    {
        get
        {
            string ext = Extension?.ToLower() ?? string.Empty;

            return ext switch
            {
                "nsp" => Brush.Parse("#CC6C5B7B"),
                "nsz" => Brush.Parse("#CCC0B3D5"),
                "xci" => Brush.Parse("#CCE2703A"),
                "xcz" => Brush.Parse("#CCF5D5AE"),
                _ => Brushes.Transparent
            };
        }
    }

    public long RawBytes
    {
        get 
        {
            var info = new FileInfo(FilePath);
            return info.Length;
        }
    }

    public string FileSize
    {
        get
        {
            try
            {
                return Common.FormatFileSize(RawBytes);
            }
            catch { return "-"; }
        }
    }

    public string FileType
    {
        get => _fileType;
        set
        {
            this.RaiseAndSetIfChanged(ref _fileType, value);
            this.RaisePropertyChanged(nameof(TypeBackground));
            this.RaisePropertyChanged(nameof(TypeForeground));
        }
    }

    public bool IsKeyMissing => _fileType == Core.Properties.Resources.Status_NoKey;

    public IBrush TypeBackground
    {
        get
        {
            if (IsKeyMissing)
                return new SolidColorBrush(Color.FromArgb(60, 220, 50, 50));

            if (_fileType == "?")
                return new SolidColorBrush(Color.FromArgb(60, 200, 160, 0));

            bool hasBase = FileType.Contains('B');
            bool hasUpdate = FileType.Contains('U');
            bool hasDlc = FileType.Contains('D');
            int count = (hasBase ? 1 : 0) + (hasUpdate ? 1 : 0) + (hasDlc ? 1 : 0);

            if (count > 1) return new SolidColorBrush(Color.FromArgb(50, 120, 0, 212));
            if (hasBase) return new SolidColorBrush(Color.FromArgb(50, 0, 120, 212));
            if (hasUpdate) return new SolidColorBrush(Color.FromArgb(50, 16, 124, 16));
            if (hasDlc) return new SolidColorBrush(Color.FromArgb(50, 200, 130, 0));

            return new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        }
    }

    public IBrush TypeForeground
    {
        get
        {
            if (IsKeyMissing)
                return new SolidColorBrush(Color.FromRgb(255, 100, 100));

            if (_fileType == "?")
                return new SolidColorBrush(Color.FromRgb(255, 210, 80));

            bool hasBase = FileType.Contains('B');
            bool hasUpdate = FileType.Contains('U');
            bool hasDlc = FileType.Contains('D');
            int count = (hasBase ? 1 : 0) + (hasUpdate ? 1 : 0) + (hasDlc ? 1 : 0);

            if (count > 1) return new SolidColorBrush(Color.FromRgb(180, 100, 255));
            if (hasBase) return new SolidColorBrush(Color.FromRgb(100, 180, 255));
            if (hasUpdate) return new SolidColorBrush(Color.FromRgb(100, 210, 100));
            if (hasDlc) return new SolidColorBrush(Color.FromRgb(255, 190, 80));

            return new SolidColorBrush(Color.FromRgb(160, 160, 160));
        }
    }

    public Bitmap? CoverBitmap
    {
        get => _coverBitmap;
        set
        {
            if (_coverBitmap is IDisposable oldBitmap)
                oldBitmap.Dispose();
            this.RaiseAndSetIfChanged(ref _coverBitmap, value);
        }
    }

    public string TitleName { get; set; } = "Unknown";

    public string TitleId { get; set; } = "0000000000000000";

    public string Version { get; set; } = "0.0.0";

    public string Developer { get; set; } = "Unknown";

    #endregion

    #region Public Methods

    public static GameFile FromPath(string path) => new(path);

    public void Dispose()
    {
        (_coverBitmap as IDisposable)?.Dispose();
        _coverBitmap = null;

        GC.SuppressFinalize(this);
    }

    #endregion
}
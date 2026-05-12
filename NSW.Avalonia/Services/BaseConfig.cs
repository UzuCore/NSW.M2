using System.IO;
using System.Diagnostics;

namespace NSW.Avalonia.Services;

public class BaseConfig
{
    #region Fields

    private int _compressLevel = 18;
    private bool _useBlockMode = true;
    private bool _useBlocklessMode = false;
    private bool _forceKeyGen0 = false;

    #endregion

    #region Properties

    private static string? DefaultFilePath
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            string? exePath = process.MainModule?.FileName;

            return Path.ChangeExtension(exePath, "config.json");
        }
    }

    public int CompressLevel
    {
        get => _compressLevel;
        set => _compressLevel = value;
    }

    public bool IsValidationEnabled { get; set; } = true;

    
    public bool UseBlockMode
    {
        get => _useBlockMode;
        set
        {
            _useBlockMode = value;
            if (value) _useBlocklessMode = false;
        }
    }

    
    public bool UseBlocklessMode
    {
        get => _useBlocklessMode;
        set
        {
            _useBlocklessMode = value;
            if (value) _useBlockMode = false;
        }
    }

    public bool ForceKeyGen0 
    { 
        get => _forceKeyGen0; 
        set => _forceKeyGen0 = value; 
    }

    #endregion

    #region Constructor

    public BaseConfig() { }

    #endregion

    #region Protected Methods

    protected virtual void DefaultSettings()
    {
        CompressLevel = 18;
        UseBlockMode = true;
        UseBlocklessMode = false;
    }

    #endregion

    #region Public Methods

    public BaseConfig Load()
    {
        string path = DefaultFilePath;
        DefaultSettings();

        if (!File.Exists(path))
        {
            Save();
            return this;
        }

        try
        {
            var lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                var trimmed = line.Trim().Replace("\"", "").Replace(",", "");
                if (trimmed.Contains("CompressLevel:"))
                    CompressLevel = int.Parse(trimmed.Split(':')[1].Trim());
                else if (trimmed.Contains("VerifyCompress:"))
                    IsValidationEnabled = bool.Parse(trimmed.Split(':')[1].Trim());
                else if (trimmed.Contains("UseBlockMode:"))
                    UseBlockMode = bool.Parse(trimmed.Split(':')[1].Trim());
                else if (trimmed.Contains("UseBlocklessMode:"))
                    UseBlocklessMode = bool.Parse(trimmed.Split(':')[1].Trim());
                else if (trimmed.Contains("ForceKeyGen0:"))
                    ForceKeyGen0 = bool.Parse(trimmed.Split(':')[1].Trim());
            }
        }
        catch
        {
            Save();
        }
        finally
        {
            if (!_useBlockMode && !_useBlocklessMode)
                _useBlocklessMode = true;
        }
        return this;
    }

    public void Save()
    {
        string path = DefaultFilePath;
        var content = $"{{\n" +
                  $"  \"CompressLevel\": {CompressLevel},\n" +
                  $"  \"VerifyCompress\": {IsValidationEnabled.ToString().ToLower()},\n" +
                  $"  \"UseBlockMode\": {UseBlockMode.ToString().ToLower()},\n" +
                  $"  \"UseBlocklessMode\": {UseBlocklessMode.ToString().ToLower()},\n" +
                  $"  \"ForceKeyGen0\": {ForceKeyGen0.ToString().ToLower()}\n" +
                  $"}}";

        File.WriteAllText(path, content);
    }

    #endregion
}
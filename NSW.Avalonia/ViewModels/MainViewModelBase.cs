using NSW.Avalonia.Services;
using ReactiveUI;

namespace NSW.Avalonia.ViewModels;

public class MainViewModelBase : ReactiveObject
{
    #region Fields & Properties

    readonly BaseConfig _config;

    public bool IsCompressionOptionVisible => CompressLevel > 2;

    public double CompressLevel
    {
        get => _config.CompressLevel;
        set
        {
            _config.CompressLevel = (int)value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsCompressionOptionVisible));
        }
    }

    public bool IsValidationEnabled
    {
        get => _config.IsValidationEnabled;
        set
        {
            _config.IsValidationEnabled = value;
            this.RaisePropertyChanged();
        }
    }

    public bool UseBlockMode
    {
        get => _config.UseBlockMode;
        set
        {
            _config.UseBlockMode = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(UseBlocklessMode));
        }
    }

    public bool UseBlocklessMode
    {
        get => _config.UseBlocklessMode;
        set
        {
            _config.UseBlocklessMode = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(UseBlockMode));
        }
    }

    public BaseConfig Config { get { return _config; } }

    #endregion

    #region Constructor

    public MainViewModelBase(BaseConfig config)
    {
        _config = config;
        _config.Load();
    }

    #endregion

    #region Public Methods

    public void SaveConfig() => _config.Save();

    #endregion
}
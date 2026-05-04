namespace NSW.M2.Avalonia.Services;

public class AppConfig: NSW.Avalonia.Services.BaseConfig
{
    protected override void DefaultSettings()
    {
        base.DefaultSettings();

        this.CompressLevel = 2;
    }
}
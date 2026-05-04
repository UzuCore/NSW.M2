using LibHac.Common.Keys;

namespace NSW.Core;

public sealed class KeySetProvider
{
    const string KeyFileName = "prod.keys";
    private static readonly Lazy<KeySetProvider> _instance = new(() => new KeySetProvider());

    public static KeySetProvider Instance => _instance.Value;    

    public KeySet KeySet { get; }

    private KeySetProvider()
    {
        string KeysPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, KeyFileName);

        if (File.Exists(KeysPath))
            KeySet = ExternalKeyReader.ReadKeyFile(KeysPath);
    }
}
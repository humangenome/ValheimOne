using System.IO;
using BepInEx.Configuration;

namespace ValheimOne.Configuration;

public sealed class ValheimOneConfig
{
    private readonly bool _existedAtStartup;
    private readonly ConfigFile _file;

    public ValheimOneConfig(string path)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _existedAtStartup = File.Exists(path);
        Path = path;
        _file = new ConfigFile(path, saveOnInit: true);
        Features = new FeatureRegistry(_file);
    }

    public string Path { get; }

    public FeatureRegistry Features { get; }

    public void WriteDefaultsIfNeeded()
    {
        if (!_existedAtStartup)
        {
            _file.Save();
        }
    }
}

using System;
using System.IO;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.Configuration;

public sealed class ConfigHotReloadWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ModLogger _log;
    private int _changeObserved;
    private bool _disposed;

    public ConfigHotReloadWatcher(string configPath, ModLogger log)
    {
        string directory = Path.GetDirectoryName(configPath) ?? throw new ArgumentException(
            "The config path must include a directory.",
            nameof(configPath));
        string fileName = Path.GetFileName(configPath);

        _log = log;
        _watcher = new FileSystemWatcher(directory, fileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
        _watcher.Renamed += OnConfigFileRenamed;
    }

    public bool ChangeObserved => Volatile.Read(ref _changeObserved) != 0;

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConfigHotReloadWatcher));
        }

        _watcher.EnableRaisingEvents = true;
        _log.Debug("Config file watcher started; live value application is reserved for a later phase.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnConfigFileChanged;
        _watcher.Created -= OnConfigFileChanged;
        _watcher.Renamed -= OnConfigFileRenamed;
        _watcher.Dispose();
        _disposed = true;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs args)
    {
        Volatile.Write(ref _changeObserved, 1);

        // TODO: Debounce editor write bursts, validate a replacement snapshot, and swap values on the Unity thread.
        // TODO: Reconcile enabled-state changes without an unsafe mid-frame blanket unpatch/repatch cycle.
    }

    private void OnConfigFileRenamed(object sender, RenamedEventArgs args)
    {
        OnConfigFileChanged(sender, args);
    }
}

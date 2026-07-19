using System;
using System.IO;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.Configuration;

public sealed class ConfigHotReloadWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ModLogger _log;
    private long _lastChangeTickPlusOne;
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

    public bool ChangeObserved => Volatile.Read(ref _lastChangeTickPlusOne) != 0;

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConfigHotReloadWatcher));
        }

        _watcher.EnableRaisingEvents = true;
        _log.Debug("Config file watcher started.");
    }

    public bool TryConsumeChange(int debounceMilliseconds)
    {
        if (debounceMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceMilliseconds));
        }

        long observedTickPlusOne = Volatile.Read(ref _lastChangeTickPlusOne);
        if (observedTickPlusOne == 0)
        {
            return false;
        }

        int observedTick = unchecked((int)(uint)(observedTickPlusOne - 1));
        uint elapsedMilliseconds = unchecked((uint)(Environment.TickCount - observedTick));
        if (elapsedMilliseconds < (uint)debounceMilliseconds)
        {
            return false;
        }

        return Interlocked.CompareExchange(
            ref _lastChangeTickPlusOne,
            0,
            observedTickPlusOne) == observedTickPlusOne;
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
        long tickPlusOne = (long)(uint)Environment.TickCount + 1;
        Volatile.Write(ref _lastChangeTickPlusOne, tickPlusOne);
    }

    private void OnConfigFileRenamed(object sender, RenamedEventArgs args)
    {
        OnConfigFileChanged(sender, args);
    }
}

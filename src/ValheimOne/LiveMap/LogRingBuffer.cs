using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ValheimOne.LiveMap;

internal sealed class LogRingBuffer : ILogListener
{
    private const int MaximumMessageLength = 4000;

    private readonly object _sync = new object();
    private readonly LogEntry?[] _entries;
    private int _start;
    private int _count;
    private long _latestSeq;
    private bool _started;

    public LogRingBuffer(int capacity)
    {
        _entries = new LogEntry[Math.Max(1, capacity)];
    }

    public long LatestSeq
    {
        get
        {
            lock (_sync)
            {
                return _latestSeq;
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            BepInEx.Logging.Logger.Listeners.Add(this);
            try
            {
                Application.logMessageReceivedThreaded += OnUnityLog;
                _started = true;
            }
            catch
            {
                BepInEx.Logging.Logger.Listeners.Remove(this);
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            Application.logMessageReceivedThreaded -= OnUnityLog;
            BepInEx.Logging.Logger.Listeners.Remove(this);
        }
    }

    public long CopyAfter(long cursor, int max, List<LogEntry> into)
    {
        lock (_sync)
        {
            long latestCursor = cursor;
            int remaining = Math.Max(0, max);
            for (int offset = 0; offset < _count && remaining > 0; offset++)
            {
                int index = (_start + offset) % _entries.Length;
                LogEntry? entry = _entries[index];
                if (entry == null || entry.Seq <= cursor)
                {
                    continue;
                }

                into.Add(entry);
                latestCursor = entry.Seq;
                remaining--;
            }

            return latestCursor;
        }
    }

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        try
        {
            string source = eventArgs.Source?.SourceName ?? string.Empty;
            if (string.Equals(source, "Unity Log", StringComparison.Ordinal))
            {
                return;
            }

            Add(eventArgs.Level.ToString(), source, eventArgs.Data?.ToString() ?? string.Empty);
        }
        catch
        {
            // Logging must never be disrupted by the web-console listener.
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        try
        {
            Add(type.ToString(), "Unity", condition ?? string.Empty);
        }
        catch
        {
            // Unity may invoke this callback from any thread.
        }
    }

    private void Add(string level, string source, string message)
    {
        if (message.Length > MaximumMessageLength)
        {
            message = message.Substring(0, MaximumMessageLength);
        }

        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            long seq = ++_latestSeq;
            var entry = new LogEntry(
                seq,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                level,
                source,
                message);
            if (_count < _entries.Length)
            {
                int index = (_start + _count) % _entries.Length;
                _entries[index] = entry;
                _count++;
                return;
            }

            _entries[_start] = entry;
            _start = (_start + 1) % _entries.Length;
        }
    }
}

internal sealed class LogEntry
{
    public LogEntry(long seq, long unixMs, string level, string source, string message)
    {
        Seq = seq;
        UnixMs = unixMs;
        Level = level;
        Source = source;
        Message = message;
    }

    public long Seq { get; }

    public long UnixMs { get; }

    public string Level { get; }

    public string Source { get; }

    public string Message { get; }
}

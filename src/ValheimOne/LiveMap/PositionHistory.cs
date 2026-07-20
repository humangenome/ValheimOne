using System;
using System.Collections.Generic;
using System.Globalization;

namespace ValheimOne.LiveMap;

internal sealed class PositionHistory
{
    private const int MaximumSamplesPerKey = 900;
    private const long MinimumSampleIntervalMilliseconds = 2000L;
    private const long KeyEvictionMilliseconds = 10L * 60L * 1000L;
    private const long EvictionCheckIntervalMilliseconds = 60L * 1000L;

    private readonly object _lock = new object();
    private readonly Dictionary<string, HistoryBuffer> _buffers =
        new Dictionary<string, HistoryBuffer>(StringComparer.Ordinal);
    private long _nextEvictionUnixMs;

    public static string PlayerKey(long id)
    {
        return "player:" + id.ToString(CultureInfo.InvariantCulture);
    }

    public static string EntityKey(string id)
    {
        return "entity:" + id;
    }

    public void Record(string key, float x, float z, long unixMs)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        lock (_lock)
        {
            if (!_buffers.TryGetValue(key, out HistoryBuffer? buffer))
            {
                buffer = new HistoryBuffer();
                _buffers.Add(key, buffer);
            }

            buffer.LastSeenUnixMs = unixMs;
            if (buffer.Count == 0 ||
                unixMs - buffer.LastRecordedUnixMs >= MinimumSampleIntervalMilliseconds)
            {
                buffer.Add(new PositionSample(x, z, unixMs));
                buffer.LastRecordedUnixMs = unixMs;
            }

            if (unixMs >= _nextEvictionUnixMs)
            {
                EvictStaleBuffers(unixMs);
                _nextEvictionUnixMs = unixMs + EvictionCheckIntervalMilliseconds;
            }
        }
    }

    public PositionSample[] Snapshot(string key, long windowMs)
    {
        long oldestUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() -
                            Math.Max(0L, windowMs);
        lock (_lock)
        {
            if (!_buffers.TryGetValue(key, out HistoryBuffer? buffer) || buffer.Count == 0)
            {
                return Array.Empty<PositionSample>();
            }

            int included = 0;
            for (int index = 0; index < buffer.Count; index++)
            {
                if (buffer.Get(index).UnixMs >= oldestUnixMs)
                {
                    included++;
                }
            }

            if (included == 0)
            {
                return Array.Empty<PositionSample>();
            }

            var samples = new PositionSample[included];
            int outputIndex = 0;
            for (int index = 0; index < buffer.Count; index++)
            {
                PositionSample sample = buffer.Get(index);
                if (sample.UnixMs >= oldestUnixMs)
                {
                    samples[outputIndex] = sample;
                    outputIndex++;
                }
            }

            return samples;
        }
    }

    private void EvictStaleBuffers(long unixMs)
    {
        List<string>? staleKeys = null;
        foreach (KeyValuePair<string, HistoryBuffer> entry in _buffers)
        {
            long elapsed = unixMs - entry.Value.LastSeenUnixMs;
            if (elapsed < 0L || elapsed < KeyEvictionMilliseconds)
            {
                continue;
            }

            staleKeys ??= new List<string>();
            staleKeys.Add(entry.Key);
        }

        if (staleKeys == null)
        {
            return;
        }

        for (int index = 0; index < staleKeys.Count; index++)
        {
            _buffers.Remove(staleKeys[index]);
        }
    }

    private sealed class HistoryBuffer
    {
        private readonly PositionSample[] _samples =
            new PositionSample[MaximumSamplesPerKey];
        private int _start;

        public int Count { get; private set; }

        public long LastRecordedUnixMs { get; set; }

        public long LastSeenUnixMs { get; set; }

        public void Add(PositionSample sample)
        {
            if (Count < _samples.Length)
            {
                _samples[(_start + Count) % _samples.Length] = sample;
                Count++;
                return;
            }

            _samples[_start] = sample;
            _start = (_start + 1) % _samples.Length;
        }

        public PositionSample Get(int index)
        {
            return _samples[(_start + index) % _samples.Length];
        }
    }
}

internal readonly struct PositionSample
{
    public PositionSample(float x, float z, long unixMs)
    {
        X = x;
        Z = z;
        UnixMs = unixMs;
    }

    public float X { get; }

    public float Z { get; }

    public long UnixMs { get; }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

// The server's numbers once a minute for the last 24 hours (GET /api/stats/history): frame time,
// objects, memory and players. Every minute is also written to stats-yyyyMMdd.jsonl in the world's
// data folder, with a line when the server starts and one when it quits cleanly, so the minutes
// before a crash survive it: a run that ended without its clean line reads stop "none".
internal sealed class StatsHistory
{
    public const int IntervalSeconds = 60;
    public const int RetentionMinutes = 1440;

    private const int KeepDays = 2;
    private const string FilePrefix = "stats-";
    private const string FileSuffix = ".jsonl";
    private const float SlowFrameSeconds = 0.1f;

    private readonly string _directory;
    private readonly ModLogger _log;
    private readonly object _lock = new object();
    private readonly object _fileLock = new object();
    private readonly List<StatsSample> _samples = new List<StatsSample>();
    private readonly List<StatsRun> _runs = new List<StatsRun>();
    private readonly ConcurrentQueue<KeyValuePair<long, string>> _pending =
        new ConcurrentQueue<KeyValuePair<long, string>>();
    private int _writing;
    private bool _writeWarningLogged;
    private long _bootUnixMs;
    private StatsRun? _current;
    private long _minute = -1L;
    private double _frameSeconds;
    private float _worstFrameSeconds;
    private int _frames;
    private int _slowFrames;
    private bool _stopped;

    public StatsHistory(string directory, ModLogger log)
    {
        _directory = directory;
        _log = log;
    }

    public long BootUnixMs => _bootUnixMs;

    // Once, at start: the last two days from disk, old files deleted, this run's start line written.
    public void Begin(long bootUnixMs, long nowUnixMs)
    {
        _bootUnixMs = bootUnixMs;
        try
        {
            Directory.CreateDirectory(_directory);
            DeleteOldFiles(nowUnixMs);
            LoadFiles(nowUnixMs);
        }
        catch (Exception exception)
        {
            _log.Warning($"[Stats] history could not be read back ({exception.GetType().Name}); starting with this run only.");
        }

        lock (_lock)
        {
            if (_runs.Count > 0 && _runs[_runs.Count - 1].Stop == "running")
            {
                _runs[_runs.Count - 1].Stop = "none";
            }

            _current = new StatsRun(bootUnixMs) { Stop = "running" };
            _runs.Add(_current);
            Trim(nowUnixMs);
        }

        Write(nowUnixMs, "{\"boot\":" + bootUnixMs.ToString(CultureInfo.InvariantCulture) + "}");
    }

    // Main thread, every frame. At each new UTC minute the minute just ended becomes a sample.
    public void Tick(float deltaSeconds, long nowUnixMs, Func<StatsGauges> readGauges)
    {
        if (_stopped || _current == null)
        {
            return;
        }

        if (deltaSeconds >= 0f && !float.IsNaN(deltaSeconds) && !float.IsInfinity(deltaSeconds))
        {
            _frameSeconds += deltaSeconds;
            _frames++;
            if (deltaSeconds > _worstFrameSeconds)
            {
                _worstFrameSeconds = deltaSeconds;
            }

            if (deltaSeconds > SlowFrameSeconds)
            {
                _slowFrames++;
            }
        }

        long minute = nowUnixMs / 60000L;
        if (_minute < 0L)
        {
            _minute = minute;
            return;
        }

        if (minute == _minute)
        {
            return;
        }

        StatsGauges gauges = readGauges();
        var sample = new StatsSample(
            minute * 60000L,
            _frames > 0 ? (float)(_frameSeconds * 1000d / _frames) : 0f,
            _worstFrameSeconds * 1000f,
            _frames,
            _slowFrames,
            gauges.ZdoCount,
            gauges.MonoHeapBytes,
            gauges.WorkingSetBytes,
            gauges.Players,
            gauges.Peers);
        _minute = minute;
        _frameSeconds = 0d;
        _worstFrameSeconds = 0f;
        _frames = 0;
        _slowFrames = 0;
        Add(sample, nowUnixMs);
    }

    internal void Add(StatsSample sample, long nowUnixMs)
    {
        lock (_lock)
        {
            _samples.Add(sample);
            if (_current != null)
            {
                _current.LastSampleUnixMs = sample.UnixMs;
            }

            Trim(nowUnixMs);
        }

        Write(sample.UnixMs, sample.ToJson());
    }

    // OnApplicationQuit: the clean stop line, written at once with anything still queued.
    public void WriteCleanStop(long nowUnixMs)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        lock (_lock)
        {
            if (_current != null)
            {
                _current.Stop = "clean";
            }
        }

        _pending.Enqueue(new KeyValuePair<long, string>(
            nowUnixMs,
            "{\"stop\":\"clean\",\"unixMs\":" + nowUnixMs.ToString(CultureInfo.InvariantCulture) + "}"));
        Drain();
    }

    public string BuildJson(long sinceUnixMs, long nowUnixMs)
    {
        long windowStart = nowUnixMs - (RetentionMinutes * 60000L);
        long from = Math.Max(sinceUnixMs, windowStart);
        var json = new StringBuilder(4096);
        lock (_lock)
        {
            json.Append("{\"intervalSeconds\":").Append(IntervalSeconds.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"retentionMinutes\":").Append(RetentionMinutes.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"bootUnixMs\":").Append(_bootUnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"runs\":[");
            bool first = true;
            foreach (StatsRun run in _runs)
            {
                if (!first)
                {
                    json.Append(',');
                }

                first = false;
                json.Append("{\"bootUnixMs\":").Append(run.BootUnixMs.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"lastSampleUnixMs\":").Append(run.LastSampleUnixMs.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"stop\":").Append(JsonWriter.Quote(run.Stop)).Append('}');
            }

            json.Append("],\"samples\":[");
            first = true;
            foreach (StatsSample sample in _samples)
            {
                if (sample.UnixMs <= from)
                {
                    continue;
                }

                if (!first)
                {
                    json.Append(',');
                }

                first = false;
                json.Append(sample.ToJson());
            }
        }

        json.Append("]}");
        return json.ToString();
    }

    // Under _lock: the 24-hour window.
    private void Trim(long nowUnixMs)
    {
        long windowStart = nowUnixMs - (RetentionMinutes * 60000L);
        int drop = 0;
        while (drop < _samples.Count && _samples[drop].UnixMs <= windowStart)
        {
            drop++;
        }

        if (drop > 0)
        {
            _samples.RemoveRange(0, drop);
        }

        _runs.RemoveAll(run =>
            !ReferenceEquals(run, _current) &&
            Math.Max(run.BootUnixMs, run.LastSampleUnixMs) <= windowStart);
    }

    private void LoadFiles(long nowUnixMs)
    {
        DateTime today = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs).UtcDateTime.Date;
        StatsRun? run = null;
        for (int daysAgo = KeepDays - 1; daysAgo >= 0; daysAgo--)
        {
            string path = Path.Combine(_directory, FileName(today.AddDays(-daysAgo)));
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                var fields = FlatJson.Parse(line);
                if (fields == null)
                {
                    continue;
                }

                if (fields.TryGetValue("boot", out string? boot) && long.TryParse(boot, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bootUnixMs))
                {
                    if (run != null && run.Stop == "running")
                    {
                        run.Stop = "none";
                    }

                    run = new StatsRun(bootUnixMs) { Stop = "running" };
                    _runs.Add(run);
                }
                else if (fields.TryGetValue("stop", out string? stop))
                {
                    if (run != null)
                    {
                        run.Stop = stop == "clean" ? "clean" : "none";
                    }
                }
                else if (StatsSample.TryRead(fields, out StatsSample? sample) && sample != null)
                {
                    if (run == null)
                    {
                        // The file starts in the middle of a run whose start line is in a deleted file.
                        run = new StatsRun(sample.UnixMs) { Stop = "running" };
                        _runs.Add(run);
                    }

                    run.LastSampleUnixMs = sample.UnixMs;
                    _samples.Add(sample);
                }
            }
        }
    }

    private void DeleteOldFiles(long nowUnixMs)
    {
        DateTime oldestKept = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs).UtcDateTime.Date.AddDays(-(KeepDays - 1));
        foreach (string path in Directory.GetFiles(_directory, FilePrefix + "*" + FileSuffix))
        {
            string name = Path.GetFileName(path);
            string stamp = name.Substring(FilePrefix.Length, name.Length - FilePrefix.Length - FileSuffix.Length);
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime day) &&
                day.Date < oldestKept)
            {
                File.Delete(path);
            }
        }
    }

    private void Write(long unixMs, string line)
    {
        _pending.Enqueue(new KeyValuePair<long, string>(unixMs, line));
        if (Interlocked.CompareExchange(ref _writing, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Drain();
                }
                finally
                {
                    Interlocked.Exchange(ref _writing, 0);
                    if (!_pending.IsEmpty && Interlocked.CompareExchange(ref _writing, 1, 0) == 0)
                    {
                        try
                        {
                            Drain();
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _writing, 0);
                        }
                    }
                }
            });
        }
    }

    private void Drain()
    {
        lock (_fileLock)
        {
            while (_pending.TryDequeue(out KeyValuePair<long, string> item))
            {
                try
                {
                    DateTime day = DateTimeOffset.FromUnixTimeMilliseconds(item.Key).UtcDateTime.Date;
                    File.AppendAllText(Path.Combine(_directory, FileName(day)), item.Value + "\n", new UTF8Encoding(false));
                }
                catch (Exception exception)
                {
                    if (!_writeWarningLogged)
                    {
                        _writeWarningLogged = true;
                        _log.Warning($"[Stats] history could not be written ({exception.GetType().Name}); the API keeps it in memory.");
                    }
                }
            }
        }
    }

    // Tests: write whatever is queued now.
    internal void Flush()
    {
        Drain();
    }

    internal static string FileName(DateTime dayUtc)
    {
        return FilePrefix + dayUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + FileSuffix;
    }
}

internal sealed class StatsRun
{
    public StatsRun(long bootUnixMs)
    {
        BootUnixMs = bootUnixMs;
        LastSampleUnixMs = bootUnixMs;
    }

    public long BootUnixMs { get; }

    public long LastSampleUnixMs { get; set; }

    // "running", "clean" (the mod saw its own quit) or "none" (ended without it: crash, kill, power).
    public string Stop { get; set; } = "running";
}

internal readonly struct StatsGauges
{
    public StatsGauges(long zdoCount, long monoHeapBytes, long workingSetBytes, int players, int peers)
    {
        ZdoCount = zdoCount;
        MonoHeapBytes = monoHeapBytes;
        WorkingSetBytes = workingSetBytes;
        Players = players;
        Peers = peers;
    }

    public long ZdoCount { get; }

    public long MonoHeapBytes { get; }

    public long WorkingSetBytes { get; }

    public int Players { get; }

    public int Peers { get; }
}

internal sealed class StatsSample
{
    public StatsSample(
        long unixMs,
        float frameAvgMs,
        float frameMaxMs,
        int frames,
        int framesOver100Ms,
        long zdoCount,
        long monoHeapBytes,
        long workingSetBytes,
        int players,
        int peers)
    {
        UnixMs = unixMs;
        FrameAvgMs = frameAvgMs;
        FrameMaxMs = frameMaxMs;
        Frames = frames;
        FramesOver100Ms = framesOver100Ms;
        ZdoCount = zdoCount;
        MonoHeapBytes = monoHeapBytes;
        WorkingSetBytes = workingSetBytes;
        Players = players;
        Peers = peers;
    }

    // The end of the minute this sample covers.
    public long UnixMs { get; }

    public float FrameAvgMs { get; }

    public float FrameMaxMs { get; }

    public int Frames { get; }

    public int FramesOver100Ms { get; }

    public long ZdoCount { get; }

    public long MonoHeapBytes { get; }

    public long WorkingSetBytes { get; }

    public int Players { get; }

    public int Peers { get; }

    public string ToJson()
    {
        var json = new StringBuilder(220);
        json.Append("{\"unixMs\":").Append(UnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"frameAvgMs\":").Append(JsonWriter.Number(Math.Round(FrameAvgMs, 2)));
        json.Append(",\"frameMaxMs\":").Append(JsonWriter.Number(Math.Round(FrameMaxMs, 2)));
        json.Append(",\"frames\":").Append(Frames.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"framesOver100Ms\":").Append(FramesOver100Ms.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"zdoCount\":").Append(ZdoCount.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"monoHeapBytes\":").Append(MonoHeapBytes.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"workingSetBytes\":").Append(WorkingSetBytes.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"players\":").Append(Players.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"peers\":").Append(Peers.ToString(CultureInfo.InvariantCulture));
        json.Append('}');
        return json.ToString();
    }

    public static bool TryRead(Dictionary<string, string> fields, out StatsSample? sample)
    {
        sample = null;
        if (!Long(fields, "unixMs", out long unixMs) ||
            !Float(fields, "frameAvgMs", out float frameAvgMs) ||
            !Float(fields, "frameMaxMs", out float frameMaxMs))
        {
            return false;
        }

        Long(fields, "frames", out long frames);
        Long(fields, "framesOver100Ms", out long slow);
        Long(fields, "zdoCount", out long zdoCount);
        Long(fields, "monoHeapBytes", out long heap);
        Long(fields, "workingSetBytes", out long workingSet);
        Long(fields, "players", out long players);
        Long(fields, "peers", out long peers);
        sample = new StatsSample(unixMs, frameAvgMs, frameMaxMs, (int)frames, (int)slow, zdoCount, heap, workingSet, (int)players, (int)peers);
        return true;
    }

    private static bool Long(Dictionary<string, string> fields, string key, out long value)
    {
        value = 0L;
        return fields.TryGetValue(key, out string? text) &&
               long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool Float(Dictionary<string, string> fields, string key, out float value)
    {
        value = 0f;
        return fields.TryGetValue(key, out string? text) &&
               float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

// The one-level objects this history writes: {"key":number,"key":"text"}. Anything else is null.
internal static class FlatJson
{
    public static Dictionary<string, string>? Parse(string line)
    {
        string text = (line ?? string.Empty).Trim();
        if (text.Length < 2 || text[0] != '{' || text[text.Length - 1] != '}')
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        int index = 1;
        while (index < text.Length - 1)
        {
            SkipSpace(text, ref index);
            if (text[index] == '}')
            {
                break;
            }

            if (!ReadString(text, ref index, out string key))
            {
                return null;
            }

            SkipSpace(text, ref index);
            if (index >= text.Length || text[index] != ':')
            {
                return null;
            }

            index++;
            SkipSpace(text, ref index);
            string value;
            if (index < text.Length && text[index] == '"')
            {
                if (!ReadString(text, ref index, out value))
                {
                    return null;
                }
            }
            else
            {
                int start = index;
                while (index < text.Length && text[index] != ',' && text[index] != '}')
                {
                    index++;
                }

                value = text.Substring(start, index - start).Trim();
            }

            fields[key] = value;
            SkipSpace(text, ref index);
            if (index < text.Length && text[index] == ',')
            {
                index++;
            }
        }

        return fields;
    }

    private static void SkipSpace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }

    private static bool ReadString(string text, ref int index, out string value)
    {
        value = string.Empty;
        if (index >= text.Length || text[index] != '"')
        {
            return false;
        }

        var builder = new StringBuilder();
        index++;
        while (index < text.Length && text[index] != '"')
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                index++;
            }

            builder.Append(text[index]);
            index++;
        }

        if (index >= text.Length)
        {
            return false;
        }

        index++;
        value = builder.ToString();
        return true;
    }
}

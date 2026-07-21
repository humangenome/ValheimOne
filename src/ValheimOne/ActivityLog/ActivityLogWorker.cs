using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;
using ValheimOne.LiveMap;

namespace ValheimOne.ActivityLog;

internal sealed class ActivityLogWorker
{
    private const int QueueCapacity = 512;
    private const int HistoryCapacity = 200;
    private const int ActivityFeedCapacity = 200;
    private const int ActivitySeedMaximumBytes = 1024 * 1024;
    private const int BatchDelayMilliseconds = 250;
    private const int DateCheckMilliseconds = 60000;

    private readonly object _queueLock = new object();
    private readonly object _historyLock = new object();
    private readonly object _activityFeedLock = new object();
    private readonly Queue<ActivityEventRecord> _queue =
        new Queue<ActivityEventRecord>(QueueCapacity);
    private readonly List<ConsoleHistoryEntry> _history =
        new List<ConsoleHistoryEntry>(HistoryCapacity);
    private readonly List<ActivityFeedEntry> _activityFeed =
        new List<ActivityFeedEntry>(ActivityFeedCapacity);
    private readonly AutoResetEvent _queueSignal = new AutoResetEvent(false);
    private readonly ManualResetEvent _stopSignal = new ManualResetEvent(false);
    private readonly Func<int> _getRetentionDays;
    private readonly ModLogger _log;
    private readonly string _historyPath;
    private Thread? _thread;
    private StreamWriter? _activityWriter;
    private DateTime _activityDateUtc;
    private string _currentFileName = string.Empty;
    private long _eventsWrittenToday;
    private long _lastWriteUnixMs;
    private long _nextHistoryId;
    private long _nextActivityFeedId;
    private int _historyDirty;
    private int _overflowWarningLogged;
    private int _stopping;

    public ActivityLogWorker(
        string dataDirectory,
        Func<int> getRetentionDays,
        ModLogger log)
    {
        DataDirectory = dataDirectory;
        _getRetentionDays = getRetentionDays;
        _log = log;
        _historyPath = Path.Combine(dataDirectory, "console-history.json");
        LoadConsoleHistory();
        LoadRecentActivity();
    }

    public string DataDirectory { get; }

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ValheimOne.ActivityLog",
        };
        _thread = thread;
        thread.Start();
    }

    public void EnqueueActivity(ActivityEventRecord record)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        lock (_queueLock)
        {
            if (_queue.Count >= QueueCapacity)
            {
                if (Interlocked.Exchange(ref _overflowWarningLogged, 1) == 0)
                {
                    _log.Warning(
                        "[ActivityLog] writer queue is full; excess events will be dropped.");
                }

                return;
            }

            _queue.Enqueue(record);
            AppendActivityFeed(record);
        }

        _queueSignal.Set();
    }

    public long LatestActivityCursor
    {
        get
        {
            lock (_activityFeedLock)
            {
                return _nextActivityFeedId;
            }
        }
    }

    public long CopyActivityAfter(
        long cursor,
        int maximum,
        List<ActivityFeedEntry> into)
    {
        into.Clear();
        lock (_activityFeedLock)
        {
            int firstEligible = 0;
            while (firstEligible < _activityFeed.Count &&
                   _activityFeed[firstEligible].Id <= cursor)
            {
                firstEligible++;
            }

            int remaining = Math.Max(0, maximum);
            int available = _activityFeed.Count - firstEligible;
            int first = firstEligible + Math.Max(0, available - remaining);
            long latestCursor = cursor;
            for (int index = first; index < _activityFeed.Count && remaining > 0; index++)
            {
                ActivityFeedEntry entry = _activityFeed[index];
                into.Add(entry);
                latestCursor = entry.Id;
                remaining--;
            }

            return latestCursor;
        }
    }

    public void AppendConsoleHistory(
        string operatorName,
        string command,
        string output,
        string status)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        lock (_historyLock)
        {
            long id = ++_nextHistoryId;
            _history.Add(new ConsoleHistoryEntry(
                id,
                operatorName,
                command,
                output,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                status));
            if (_history.Count > HistoryCapacity)
            {
                _history.RemoveAt(0);
            }

            Volatile.Write(ref _historyDirty, 1);
        }

        _queueSignal.Set();
    }

    public long CopyConsoleHistoryAfter(
        long cursor,
        int maximum,
        List<ConsoleHistoryEntry> into)
    {
        lock (_historyLock)
        {
            long latestCursor = cursor;
            int remaining = Math.Max(0, maximum);
            for (int index = 0; index < _history.Count && remaining > 0; index++)
            {
                ConsoleHistoryEntry entry = _history[index];
                if (entry.Id <= cursor)
                {
                    continue;
                }

                into.Add(entry);
                latestCursor = entry.Id;
                remaining--;
            }

            return latestCursor;
        }
    }

    public ActivityLogHealthSnapshot GetHealth(bool enabled)
    {
        string todayFileName = ActivityFileName(DateTime.UtcNow.Date);
        string currentFileName = Volatile.Read(ref _currentFileName);
        bool currentFileIsToday = string.Equals(
            currentFileName,
            todayFileName,
            StringComparison.Ordinal);
        currentFileName = currentFileIsToday ? currentFileName : todayFileName;

        long lastWriteUnixMs = Interlocked.Read(ref _lastWriteUnixMs);
        double? lastWriteAgeSeconds = lastWriteUnixMs <= 0L
            ? (double?)null
            : Math.Max(
                0d,
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastWriteUnixMs) / 1000d);
        return new ActivityLogHealthSnapshot(
            enabled,
            currentFileName,
            currentFileIsToday ? Interlocked.Read(ref _eventsWrittenToday) : 0L,
            lastWriteAgeSeconds);
    }

    public void StopAndFlush(int timeoutMilliseconds)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        _stopSignal.Set();
        _queueSignal.Set();
        int boundedTimeout = Math.Max(0, Math.Min(2000, timeoutMilliseconds));
        Thread? thread = _thread;
        if (thread != null && thread.IsAlive && !thread.Join(boundedTimeout))
        {
            _log.Warning(
                "[ActivityLog] writer did not exit within the shutdown flush window.");
        }
    }

    private void Run()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            WaitHandle[] signals = { _queueSignal, _stopSignal };
            while (true)
            {
                int signal = WaitHandle.WaitAny(signals, DateCheckMilliseconds);
                if (signal == WaitHandle.WaitTimeout)
                {
                    RollAtUtcDateChange();
                    continue;
                }

                if (signal == 0 && Volatile.Read(ref _stopping) == 0)
                {
                    _stopSignal.WaitOne(BatchDelayMilliseconds);
                }

                ProcessPending();
                if (Volatile.Read(ref _stopping) != 0)
                {
                    ProcessPending();
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _stopping, 1);
            _log.Warning(
                $"[ActivityLog] writer stopped after a disk failure " +
                $"({exception.GetType().Name}: {SingleLineMessage(exception)}).");
        }
        finally
        {
            CloseActivityWriter();
        }
    }

    private void ProcessPending()
    {
        List<ActivityEventRecord> records = DrainPending();
        bool wroteActivity = false;
        for (int index = 0; index < records.Count; index++)
        {
            WriteActivity(records[index]);
            wroteActivity = true;
        }

        if (wroteActivity && _activityWriter != null)
        {
            _activityWriter.Flush();
            Interlocked.Exchange(
                ref _lastWriteUnixMs,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        if (Interlocked.Exchange(ref _historyDirty, 0) != 0)
        {
            try
            {
                PersistConsoleHistory();
            }
            catch
            {
                Volatile.Write(ref _historyDirty, 1);
                throw;
            }
        }
    }

    private List<ActivityEventRecord> DrainPending()
    {
        lock (_queueLock)
        {
            var records = new List<ActivityEventRecord>(_queue.Count);
            while (_queue.Count != 0)
            {
                records.Add(_queue.Dequeue());
            }

            return records;
        }
    }

    private void WriteActivity(ActivityEventRecord record)
    {
        DateTime eventDateUtc = DateTimeOffset
            .FromUnixTimeMilliseconds(record.UnixMs)
            .UtcDateTime
            .Date;
        EnsureActivityWriter(eventDateUtc);
        var json = new StringBuilder(64 + record.Type.Length + record.DataJson.Length);
        json.Append("{\"t\":");
        json.Append(record.UnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"type\":").Append(JsonWriter.Quote(record.Type));
        json.Append(",\"data\":").Append(record.DataJson);
        json.Append('}');
        _activityWriter!.WriteLine(json.ToString());
        Interlocked.Increment(ref _eventsWrittenToday);
    }

    private void EnsureActivityWriter(DateTime eventDateUtc)
    {
        if (_activityWriter != null && _activityDateUtc == eventDateUtc)
        {
            return;
        }

        CloseActivityWriter();
        _activityDateUtc = eventDateUtc;
        string fileName = ActivityFileName(eventDateUtc);
        string path = Path.Combine(DataDirectory, fileName);
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        _activityWriter = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Volatile.Write(ref _currentFileName, fileName);
        Interlocked.Exchange(ref _eventsWrittenToday, 0L);
        PruneOldActivityFiles(eventDateUtc);
    }

    private void RollAtUtcDateChange()
    {
        DateTime todayUtc = DateTime.UtcNow.Date;
        if (_activityWriter == null || _activityDateUtc == todayUtc)
        {
            return;
        }

        CloseActivityWriter();
        _activityDateUtc = todayUtc;
        Volatile.Write(ref _currentFileName, ActivityFileName(todayUtc));
        Interlocked.Exchange(ref _eventsWrittenToday, 0L);
        PruneOldActivityFiles(todayUtc);
    }

    private void CloseActivityWriter()
    {
        try
        {
            _activityWriter?.Flush();
            _activityWriter?.Dispose();
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[ActivityLog] could not close the current activity file " +
                $"({exception.GetType().Name}).");
        }
        finally
        {
            _activityWriter = null;
        }
    }

    private void PruneOldActivityFiles(DateTime currentDateUtc)
    {
        int retentionDays = Math.Max(1, Math.Min(3650, _getRetentionDays()));
        DateTime oldestRetainedDate = currentDateUtc.AddDays(1 - retentionDays);
        string[] paths = Directory.GetFiles(DataDirectory, "activity-*.jsonl");
        for (int index = 0; index < paths.Length; index++)
        {
            string fileName = Path.GetFileName(paths[index]);
            if (!TryParseActivityDate(fileName, out DateTime fileDateUtc) ||
                fileDateUtc >= oldestRetainedDate)
            {
                continue;
            }

            try
            {
                File.Delete(paths[index]);
            }
            catch (Exception exception)
            {
                _log.Warning(
                    $"[ActivityLog] could not prune {fileName} " +
                    $"({exception.GetType().Name}).");
            }
        }
    }

    private void PersistConsoleHistory()
    {
        List<ConsoleHistoryEntry> snapshot;
        lock (_historyLock)
        {
            snapshot = new List<ConsoleHistoryEntry>(_history);
        }

        var json = new StringBuilder(16 + (snapshot.Count * 160));
        json.Append('[');
        for (int index = 0; index < snapshot.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            ConsoleHistoryEntry entry = snapshot[index];
            json.Append('{');
            json.Append("\"id\":").Append(entry.Id.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"operator\":").Append(JsonWriter.Quote(entry.Operator));
            json.Append(",\"command\":").Append(JsonWriter.Quote(entry.Command));
            json.Append(",\"output\":").Append(JsonWriter.Quote(entry.Output));
            json.Append(",\"t\":").Append(entry.UnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"status\":").Append(JsonWriter.Quote(entry.Status));
            json.Append('}');
        }

        json.Append(']');
        string temporaryPath = _historyPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            json.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(_historyPath))
        {
            File.Replace(temporaryPath, _historyPath, null);
        }
        else
        {
            File.Move(temporaryPath, _historyPath);
        }
    }

    private void LoadConsoleHistory()
    {
        if (!File.Exists(_historyPath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(_historyPath, Encoding.UTF8);
            List<ConsoleHistoryEntry> loaded = ConsoleHistoryJsonParser.Parse(json);
            int first = Math.Max(0, loaded.Count - HistoryCapacity);
            for (int index = first; index < loaded.Count; index++)
            {
                ConsoleHistoryEntry entry = loaded[index];
                _history.Add(entry);
                _nextHistoryId = Math.Max(_nextHistoryId, entry.Id);
            }
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[ActivityLog] console history could not be loaded " +
                $"({exception.GetType().Name}); starting with an empty journal.");
            _history.Clear();
            _nextHistoryId = 0L;
        }
    }

    private void AppendActivityFeed(ActivityEventRecord record)
    {
        lock (_activityFeedLock)
        {
            long id = ++_nextActivityFeedId;
            _activityFeed.Add(new ActivityFeedEntry(
                id,
                record.UnixMs,
                record.Type,
                record.DataJson));
            if (_activityFeed.Count > ActivityFeedCapacity)
            {
                _activityFeed.RemoveAt(0);
            }
        }
    }

    private void LoadRecentActivity()
    {
        string path = Path.Combine(DataDirectory, ActivityFileName(DateTime.UtcNow.Date));
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            List<string> lines = ReadActivityTail(path);
            for (int index = 0; index < lines.Count; index++)
            {
                if (ActivityEventJsonParser.TryParse(lines[index], out ActivityEventRecord record))
                {
                    AppendActivityFeed(record);
                }
            }
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[ActivityLog] recent activity could not be loaded " +
                $"({exception.GetType().Name}); starting the web feed empty.");
            lock (_activityFeedLock)
            {
                _activityFeed.Clear();
                _nextActivityFeedId = 0L;
            }
        }
    }

    private static List<string> ReadActivityTail(string path)
    {
        var retained = new Queue<string>(ActivityFeedCapacity);
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite))
        {
            long start = Math.Max(0L, stream.Length - ActivitySeedMaximumBytes);
            int byteCount = (int)(stream.Length - start);
            var buffer = new byte[byteCount];
            stream.Position = start;
            int read = 0;
            while (read < byteCount)
            {
                int current = stream.Read(buffer, read, byteCount - read);
                if (current <= 0)
                {
                    break;
                }

                read += current;
            }

            int lineStart = 0;
            if (start > 0L)
            {
                while (lineStart < read && buffer[lineStart] != (byte)'\n')
                {
                    lineStart++;
                }

                if (lineStart < read)
                {
                    lineStart++;
                }
            }

            for (int index = lineStart; index < read; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                RetainActivityLine(retained, buffer, lineStart, index - lineStart);
                lineStart = index + 1;
            }

            if (lineStart < read)
            {
                RetainActivityLine(retained, buffer, lineStart, read - lineStart);
            }
        }

        return new List<string>(retained);
    }

    private static void RetainActivityLine(
        Queue<string> retained,
        byte[] buffer,
        int start,
        int count)
    {
        if (count > 0 && buffer[start + count - 1] == (byte)'\r')
        {
            count--;
        }

        if (count <= 0)
        {
            return;
        }

        string line = Encoding.UTF8.GetString(buffer, start, count);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        retained.Enqueue(line);
        if (retained.Count > ActivityFeedCapacity)
        {
            retained.Dequeue();
        }
    }

    private static string ActivityFileName(DateTime dateUtc)
    {
        return "activity-" + dateUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl";
    }

    private static bool TryParseActivityDate(string fileName, out DateTime dateUtc)
    {
        dateUtc = default;
        const string prefix = "activity-";
        const string suffix = ".jsonl";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(suffix, StringComparison.Ordinal) ||
            fileName.Length != prefix.Length + 8 + suffix.Length)
        {
            return false;
        }

        return DateTime.TryParseExact(
            fileName.Substring(prefix.Length, 8),
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out dateUtc);
    }

    private static string SingleLineMessage(Exception exception)
    {
        return (exception.Message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }
}

internal sealed class ActivityEventRecord
{
    public ActivityEventRecord(long unixMs, string type, string dataJson)
    {
        UnixMs = unixMs;
        Type = type;
        DataJson = dataJson;
    }

    public long UnixMs { get; }

    public string Type { get; }

    public string DataJson { get; }
}

internal sealed class ActivityFeedEntry
{
    public ActivityFeedEntry(long id, long unixMs, string type, string dataJson)
    {
        Id = id;
        UnixMs = unixMs;
        Type = type;
        DataJson = dataJson;
    }

    public long Id { get; }

    public long UnixMs { get; }

    public string Type { get; }

    public string DataJson { get; }
}

internal sealed class ConsoleHistoryEntry
{
    public ConsoleHistoryEntry(
        long id,
        string operatorName,
        string command,
        string output,
        long unixMs,
        string status)
    {
        Id = id;
        Operator = operatorName;
        Command = command;
        Output = output;
        UnixMs = unixMs;
        Status = status;
    }

    public long Id { get; }

    public string Operator { get; }

    public string Command { get; }

    public string Output { get; }

    public long UnixMs { get; }

    public string Status { get; }
}

internal sealed class ActivityLogHealthSnapshot
{
    public ActivityLogHealthSnapshot(
        bool enabled,
        string currentFileName,
        long eventsWrittenToday,
        double? lastWriteAgeSeconds)
    {
        Enabled = enabled;
        CurrentFileName = currentFileName;
        EventsWrittenToday = eventsWrittenToday;
        LastWriteAgeSeconds = lastWriteAgeSeconds;
    }

    public bool Enabled { get; }

    public string CurrentFileName { get; }

    public long EventsWrittenToday { get; }

    public double? LastWriteAgeSeconds { get; }
}

internal static class ActivityEventJsonParser
{
    public static bool TryParse(string json, out ActivityEventRecord record)
    {
        try
        {
            record = new Parser(json).Parse();
            return true;
        }
        catch (FormatException)
        {
            record = new ActivityEventRecord(0L, string.Empty, "{}");
            return false;
        }
    }

    private sealed class Parser
    {
        private readonly string _json;
        private int _index;

        public Parser(string json)
        {
            _json = json;
        }

        public ActivityEventRecord Parse()
        {
            Expect('{');
            string timestampProperty = ReadString();
            if (!string.Equals(timestampProperty, "t", StringComparison.Ordinal) &&
                !string.Equals(timestampProperty, "unixMs", StringComparison.Ordinal))
            {
                throw new FormatException("Activity event has an unexpected timestamp property.");
            }

            Expect(':');
            long unixMs = ReadInt64();
            Expect(',');
            ExpectProperty("type");
            string type = ReadString();
            Expect(',');
            ExpectProperty("data");
            string dataJson = ReadObjectJson();
            Expect('}');
            EnsureEnd();
            if (unixMs <= 0L || string.IsNullOrWhiteSpace(type))
            {
                throw new FormatException("Activity event is missing required values.");
            }

            return new ActivityEventRecord(unixMs, type, dataJson);
        }

        private void ExpectProperty(string name)
        {
            string actual = ReadString();
            if (!string.Equals(actual, name, StringComparison.Ordinal))
            {
                throw new FormatException("Activity event has an unexpected property.");
            }

            Expect(':');
        }

        private long ReadInt64()
        {
            SkipWhitespace();
            int start = _index;
            if (_index < _json.Length && _json[_index] == '-')
            {
                _index++;
            }

            while (_index < _json.Length && char.IsDigit(_json[_index]))
            {
                _index++;
            }

            if (_index == start ||
                !long.TryParse(
                    _json.Substring(start, _index - start),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long value))
            {
                throw new FormatException("Activity event contains an invalid timestamp.");
            }

            return value;
        }

        private string ReadString()
        {
            SkipWhitespace();
            if (_index >= _json.Length || _json[_index++] != '"')
            {
                throw new FormatException("Activity event contains an invalid string.");
            }

            var value = new StringBuilder();
            while (_index < _json.Length)
            {
                char character = _json[_index++];
                if (character == '"')
                {
                    return value.ToString();
                }

                if (character < 0x20)
                {
                    throw new FormatException("Activity event contains a control character.");
                }

                if (character != '\\')
                {
                    value.Append(character);
                    continue;
                }

                if (_index >= _json.Length)
                {
                    break;
                }

                char escaped = _json[_index++];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                    case '/':
                        value.Append(escaped);
                        break;
                    case 'b':
                        value.Append('\b');
                        break;
                    case 'f':
                        value.Append('\f');
                        break;
                    case 'n':
                        value.Append('\n');
                        break;
                    case 'r':
                        value.Append('\r');
                        break;
                    case 't':
                        value.Append('\t');
                        break;
                    case 'u':
                        value.Append(ReadUnicodeEscape());
                        break;
                    default:
                        throw new FormatException("Activity event contains an invalid escape.");
                }
            }

            throw new FormatException("Activity event contains an unterminated string.");
        }

        private char ReadUnicodeEscape()
        {
            if (_index + 4 > _json.Length ||
                !ushort.TryParse(
                    _json.Substring(_index, 4),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out ushort value))
            {
                throw new FormatException("Activity event contains an invalid Unicode escape.");
            }

            _index += 4;
            return (char)value;
        }

        private string ReadObjectJson()
        {
            SkipWhitespace();
            int start = _index;
            if (_index >= _json.Length || _json[_index] != '{')
            {
                throw new FormatException("Activity event data is not an object.");
            }

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            while (_index < _json.Length)
            {
                char character = _json[_index++];
                if (inString)
                {
                    if (character < 0x20)
                    {
                        throw new FormatException("Activity event data contains a control character.");
                    }

                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                }
                else if (character == '{')
                {
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return _json.Substring(start, _index - start);
                    }
                }
            }

            throw new FormatException("Activity event data is unterminated.");
        }

        private bool TryConsume(char expected)
        {
            SkipWhitespace();
            if (_index >= _json.Length || _json[_index] != expected)
            {
                return false;
            }

            _index++;
            return true;
        }

        private void Expect(char expected)
        {
            if (!TryConsume(expected))
            {
                throw new FormatException("Activity event JSON is malformed.");
            }
        }

        private void SkipWhitespace()
        {
            while (_index < _json.Length && char.IsWhiteSpace(_json[_index]))
            {
                _index++;
            }
        }

        private void EnsureEnd()
        {
            SkipWhitespace();
            if (_index != _json.Length)
            {
                throw new FormatException("Activity event contains trailing data.");
            }
        }
    }
}

internal static class ConsoleHistoryJsonParser
{
    public static List<ConsoleHistoryEntry> Parse(string json)
    {
        var parser = new Parser(json);
        return parser.ParseEntries();
    }

    private sealed class Parser
    {
        private readonly string _json;
        private int _index;

        public Parser(string json)
        {
            _json = json;
        }

        public List<ConsoleHistoryEntry> ParseEntries()
        {
            var entries = new List<ConsoleHistoryEntry>();
            Expect('[');
            SkipWhitespace();
            if (TryConsume(']'))
            {
                EnsureEnd();
                return entries;
            }

            while (true)
            {
                entries.Add(ParseEntry());
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    EnsureEnd();
                    return entries;
                }

                Expect(',');
            }
        }

        private ConsoleHistoryEntry ParseEntry()
        {
            Expect('{');
            ExpectProperty("id");
            long id = ReadInt64();
            Expect(',');
            ExpectProperty("operator");
            string operatorName = ReadString();
            Expect(',');
            ExpectProperty("command");
            string command = ReadString();
            Expect(',');
            ExpectProperty("output");
            string output = ReadString();
            Expect(',');
            ExpectProperty("t");
            long unixMs = ReadInt64();
            Expect(',');
            ExpectProperty("status");
            string status = ReadString();
            Expect('}');
            if (id <= 0L)
            {
                throw new FormatException("Console history contains a non-positive id.");
            }

            return new ConsoleHistoryEntry(
                id,
                operatorName,
                command,
                output,
                unixMs,
                status);
        }

        private void ExpectProperty(string name)
        {
            string actual = ReadString();
            if (!string.Equals(actual, name, StringComparison.Ordinal))
            {
                throw new FormatException("Console history has an unexpected property.");
            }

            Expect(':');
        }

        private long ReadInt64()
        {
            SkipWhitespace();
            int start = _index;
            if (_index < _json.Length && _json[_index] == '-')
            {
                _index++;
            }

            while (_index < _json.Length && char.IsDigit(_json[_index]))
            {
                _index++;
            }

            if (_index == start ||
                !long.TryParse(
                    _json.Substring(start, _index - start),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long value))
            {
                throw new FormatException("Console history contains an invalid number.");
            }

            return value;
        }

        private string ReadString()
        {
            SkipWhitespace();
            if (_index >= _json.Length || _json[_index++] != '"')
            {
                throw new FormatException("Console history contains an invalid string.");
            }

            var value = new StringBuilder();
            while (_index < _json.Length)
            {
                char character = _json[_index++];
                if (character == '"')
                {
                    return value.ToString();
                }

                if (character < 0x20)
                {
                    throw new FormatException("Console history contains a control character.");
                }

                if (character != '\\')
                {
                    value.Append(character);
                    continue;
                }

                if (_index >= _json.Length)
                {
                    break;
                }

                char escaped = _json[_index++];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                    case '/':
                        value.Append(escaped);
                        break;
                    case 'b':
                        value.Append('\b');
                        break;
                    case 'f':
                        value.Append('\f');
                        break;
                    case 'n':
                        value.Append('\n');
                        break;
                    case 'r':
                        value.Append('\r');
                        break;
                    case 't':
                        value.Append('\t');
                        break;
                    case 'u':
                        value.Append(ReadUnicodeEscape());
                        break;
                    default:
                        throw new FormatException("Console history contains an invalid escape.");
                }
            }

            throw new FormatException("Console history contains an unterminated string.");
        }

        private char ReadUnicodeEscape()
        {
            if (_index + 4 > _json.Length ||
                !ushort.TryParse(
                    _json.Substring(_index, 4),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out ushort value))
            {
                throw new FormatException("Console history contains an invalid Unicode escape.");
            }

            _index += 4;
            return (char)value;
        }

        private bool TryConsume(char expected)
        {
            SkipWhitespace();
            if (_index >= _json.Length || _json[_index] != expected)
            {
                return false;
            }

            _index++;
            return true;
        }

        private void Expect(char expected)
        {
            if (!TryConsume(expected))
            {
                throw new FormatException("Console history JSON is malformed.");
            }
        }

        private void SkipWhitespace()
        {
            while (_index < _json.Length && char.IsWhiteSpace(_json[_index]))
            {
                _index++;
            }
        }

        private void EnsureEnd()
        {
            SkipWhitespace();
            if (_index != _json.Length)
            {
                throw new FormatException("Console history contains trailing data.");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class ActivityHeatmap : IDisposable
{
    public const int GridSize = 128;

    private const int HourSliceCount = 168;
    private const int MaximumSparseCells = 32768;
    private const int PersistedFileMaximumBytes = 2 * 1024 * 1024;
    private const int PersistIntervalMilliseconds = 5 * 60 * 1000;
    private const int ShutdownFlushMilliseconds = 2000;
    private const long HourMilliseconds = 60L * 60L * 1000L;
    private const int PersistedVersion = 1;

    private readonly object _lock = new object();
    private readonly HourSlice[] _slices = new HourSlice[HourSliceCount];
    private readonly ManualResetEvent _stopSignal = new ManualResetEvent(false);
    private readonly string _path;
    private readonly ModLogger _log;
    private readonly Thread _writerThread;
    private long _latestHourStartUnixMs;
    private int _totalCellEntries;
    private int _dirty;
    private int _writeFailureWarningLogged;
    private int _fileTrimWarningLogged;
    private bool _disposed;

    public ActivityHeatmap(string dataDirectory, ModLogger log)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }

        _log = log ?? throw new ArgumentNullException(nameof(log));
        _path = Path.Combine(dataDirectory, "heatmap.json");
        for (int index = 0; index < _slices.Length; index++)
        {
            _slices[index] = new HourSlice();
        }

        _latestHourStartUnixMs = FloorToHour(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Load();

        _writerThread = new Thread(RunWriter)
        {
            IsBackground = true,
            Name = "ValheimOne.ActivityHeatmap",
        };
        _writerThread.Start();
    }

    public void Record(float x, float z, long unixMs)
    {
        if (!TryGetCellIndex(x, z, out int cellIndex))
        {
            return;
        }

        long hourStartUnixMs = FloorToHour(unixMs);
        lock (_lock)
        {
            if (_disposed ||
                hourStartUnixMs < _latestHourStartUnixMs -
                ((HourSliceCount - 1L) * HourMilliseconds))
            {
                return;
            }

            if (hourStartUnixMs > _latestHourStartUnixMs)
            {
                _latestHourStartUnixMs = hourStartUnixMs;
                PruneExpiredSlicesLocked(hourStartUnixMs);
            }

            HourSlice slice = _slices[SliceIndex(hourStartUnixMs)];
            if (slice.HourStartUnixMs != hourStartUnixMs)
            {
                ClearSliceLocked(slice);
                slice.HourStartUnixMs = hourStartUnixMs;
            }

            if (slice.Cells.TryGetValue(cellIndex, out int count))
            {
                if (count < int.MaxValue)
                {
                    slice.Cells[cellIndex] = count + 1;
                }
            }
            else
            {
                while (_totalCellEntries >= MaximumSparseCells &&
                       DropOldestSliceLocked(hourStartUnixMs))
                {
                }

                if (_totalCellEntries >= MaximumSparseCells)
                {
                    return;
                }

                slice.Cells.Add(cellIndex, 1);
                _totalCellEntries++;
            }

            Volatile.Write(ref _dirty, 1);
        }
    }

    public ActivityHeatmapSnapshot Snapshot(string window, long generatedUnixMs)
    {
        int hours = string.Equals(window, "7d", StringComparison.Ordinal) ? 168 : 24;
        long currentHourStartUnixMs = FloorToHour(generatedUnixMs);
        long oldestHourStartUnixMs = currentHourStartUnixMs -
                                     ((hours - 1L) * HourMilliseconds);
        var totals = new int[GridSize * GridSize];

        lock (_lock)
        {
            for (int sliceIndex = 0; sliceIndex < _slices.Length; sliceIndex++)
            {
                HourSlice slice = _slices[sliceIndex];
                if (slice.HourStartUnixMs < oldestHourStartUnixMs ||
                    slice.HourStartUnixMs > currentHourStartUnixMs)
                {
                    continue;
                }

                foreach (KeyValuePair<int, int> entry in slice.Cells)
                {
                    int total = totals[entry.Key];
                    totals[entry.Key] = entry.Value > int.MaxValue - total
                        ? int.MaxValue
                        : total + entry.Value;
                }
            }
        }

        int populated = 0;
        int maximumCount = 0;
        for (int cellIndex = 0; cellIndex < totals.Length; cellIndex++)
        {
            if (totals[cellIndex] <= 0)
            {
                continue;
            }

            populated++;
            maximumCount = Math.Max(maximumCount, totals[cellIndex]);
        }

        var cells = new ActivityHeatmapCell[populated];
        int outputIndex = 0;
        for (int cellIndex = 0; cellIndex < totals.Length; cellIndex++)
        {
            int count = totals[cellIndex];
            if (count <= 0)
            {
                continue;
            }

            cells[outputIndex] = new ActivityHeatmapCell(
                cellIndex % GridSize,
                cellIndex / GridSize,
                count);
            outputIndex++;
        }

        return new ActivityHeatmapSnapshot(
            hours == 168 ? "7d" : "24h",
            generatedUnixMs,
            maximumCount,
            cells);
    }

    public ActivityHeatmapHourSlice[] HarvestSlices(
        long fromHourStartUnixMsExclusive,
        long toHourStartUnixMsInclusive)
    {
        var slices = new List<ActivityHeatmapHourSlice>(HourSliceCount);
        lock (_lock)
        {
            for (int index = 0; index < _slices.Length; index++)
            {
                HourSlice slice = _slices[index];
                if (slice.HourStartUnixMs <= fromHourStartUnixMsExclusive ||
                    slice.HourStartUnixMs > toHourStartUnixMsInclusive ||
                    slice.Cells.Count == 0)
                {
                    continue;
                }

                var cells = new ActivityHeatmapCell[slice.Cells.Count];
                int cellIndex = 0;
                foreach (KeyValuePair<int, int> entry in slice.Cells)
                {
                    cells[cellIndex] = new ActivityHeatmapCell(
                        entry.Key % GridSize,
                        entry.Key / GridSize,
                        entry.Value);
                    cellIndex++;
                }

                Array.Sort(cells, CompareActivityCells);
                slices.Add(new ActivityHeatmapHourSlice(
                    slice.HourStartUnixMs,
                    cells));
            }
        }

        ActivityHeatmapHourSlice[] result = slices.ToArray();
        Array.Sort(result, CompareActivitySlices);
        return result;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopSignal.Set();
        }

        if (!ReferenceEquals(Thread.CurrentThread, _writerThread) &&
            _writerThread.IsAlive &&
            !_writerThread.Join(ShutdownFlushMilliseconds))
        {
            _log.Warning(
                "[LiveMap] activity-heatmap writer did not exit within the shutdown flush window.");
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _log.Warning(
                "[LiveMap] heatmap data could not be loaded because heatmap.json is missing; " +
                "starting with an empty store.");
            return;
        }

        try
        {
            if (new FileInfo(_path).Length > PersistedFileMaximumBytes)
            {
                throw new FormatException("Activity-heatmap JSON is too large.");
            }

            string json = File.ReadAllText(_path, Encoding.UTF8);
            List<PersistedSlice> loaded = HeatmapJsonParser.Parse(json);
            long currentHourStartUnixMs = FloorToHour(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            long oldestHourStartUnixMs = currentHourStartUnixMs -
                                         ((HourSliceCount - 1L) * HourMilliseconds);
            var seenHours = new HashSet<long>();
            int loadedCellEntries = 0;
            for (int index = 0; index < loaded.Count; index++)
            {
                PersistedSlice persisted = loaded[index];
                if (persisted.HourStartUnixMs % HourMilliseconds != 0L ||
                    !seenHours.Add(persisted.HourStartUnixMs))
                {
                    throw new FormatException(
                        "Activity-heatmap JSON contains an invalid hourly slice.");
                }

                if (persisted.HourStartUnixMs < oldestHourStartUnixMs ||
                    persisted.HourStartUnixMs > currentHourStartUnixMs)
                {
                    continue;
                }

                HourSlice slice = _slices[SliceIndex(persisted.HourStartUnixMs)];
                if (slice.HourStartUnixMs >= 0L)
                {
                    throw new FormatException(
                        "Activity-heatmap JSON contains colliding hourly slices.");
                }

                slice.HourStartUnixMs = persisted.HourStartUnixMs;
                for (int cellIndex = 0; cellIndex < persisted.Cells.Length; cellIndex++)
                {
                    PersistedCell cell = persisted.Cells[cellIndex];
                    if (cell.Index < 0 || cell.Index >= GridSize * GridSize ||
                        cell.Count <= 0 || slice.Cells.ContainsKey(cell.Index))
                    {
                        throw new FormatException(
                            "Activity-heatmap JSON contains invalid cell data.");
                    }

                    loadedCellEntries++;
                    if (loadedCellEntries > MaximumSparseCells)
                    {
                        throw new FormatException(
                            "Activity-heatmap JSON exceeds the sparse-cell cap.");
                    }

                    slice.Cells.Add(cell.Index, cell.Count);
                }
            }

            _totalCellEntries = loadedCellEntries;
            _latestHourStartUnixMs = currentHourStartUnixMs;
        }
        catch (Exception exception)
        {
            ResetLocked();
            _log.Warning(
                $"[LiveMap] heatmap data could not be loaded ({exception.GetType().Name}); " +
                "starting with an empty store.");
        }
    }

    private void RunWriter()
    {
        while (!_stopSignal.WaitOne(PersistIntervalMilliseconds))
        {
            FlushPendingWrite();
        }

        FlushPendingWrite();
    }

    private void FlushPendingWrite()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        try
        {
            Persist();
            Interlocked.Exchange(ref _writeFailureWarningLogged, 0);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _dirty, 1);
            if (Interlocked.Exchange(ref _writeFailureWarningLogged, 1) == 0)
            {
                _log.Warning(
                    $"[LiveMap] heatmap data could not be persisted " +
                    $"({exception.GetType().Name}: {SingleLineMessage(exception)}). " +
                    "The writer will retry.");
            }
        }
    }

    private void Persist()
    {
        PersistedSlice[] snapshot = CapturePersistenceSnapshot();
        int firstSlice = 0;
        string json = BuildPersistenceJson(snapshot, firstSlice);
        while (Encoding.UTF8.GetByteCount(json) > PersistedFileMaximumBytes &&
               firstSlice < snapshot.Length)
        {
            firstSlice++;
            json = BuildPersistenceJson(snapshot, firstSlice);
        }

        if (Encoding.UTF8.GetByteCount(json) > PersistedFileMaximumBytes)
        {
            throw new InvalidOperationException(
                "Activity-heatmap JSON could not be reduced below its file cap.");
        }

        if (firstSlice > 0 && Interlocked.Exchange(ref _fileTrimWarningLogged, 1) == 0)
        {
            _log.Warning(
                $"[LiveMap] heatmap persistence dropped {firstSlice} oldest hourly " +
                "slice(s) to stay below 2 MB.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(_path))
        {
            File.Replace(temporaryPath, _path, null);
        }
        else
        {
            File.Move(temporaryPath, _path);
        }
    }

    private PersistedSlice[] CapturePersistenceSnapshot()
    {
        var slices = new List<PersistedSlice>(HourSliceCount);
        long currentHourStartUnixMs = FloorToHour(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        long oldestHourStartUnixMs = currentHourStartUnixMs -
                                     ((HourSliceCount - 1L) * HourMilliseconds);
        lock (_lock)
        {
            for (int index = 0; index < _slices.Length; index++)
            {
                HourSlice slice = _slices[index];
                if (slice.HourStartUnixMs < oldestHourStartUnixMs ||
                    slice.HourStartUnixMs > currentHourStartUnixMs ||
                    slice.Cells.Count == 0)
                {
                    continue;
                }

                var cells = new PersistedCell[slice.Cells.Count];
                int cellIndex = 0;
                foreach (KeyValuePair<int, int> entry in slice.Cells)
                {
                    cells[cellIndex] = new PersistedCell(entry.Key, entry.Value);
                    cellIndex++;
                }

                Array.Sort(cells, ComparePersistedCells);
                slices.Add(new PersistedSlice(slice.HourStartUnixMs, cells));
            }
        }

        PersistedSlice[] result = slices.ToArray();
        Array.Sort(result, ComparePersistedSlices);
        return result;
    }

    private static string BuildPersistenceJson(PersistedSlice[] slices, int firstSlice)
    {
        var json = new StringBuilder(96 + ((slices.Length - firstSlice) * 64));
        json.Append("{\"version\":").Append(
            PersistedVersion.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"size\":").Append(GridSize.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"worldRadius\":").Append(
            WorldMapRenderer.WorldRadius.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"slices\":[");
        for (int sliceIndex = firstSlice; sliceIndex < slices.Length; sliceIndex++)
        {
            if (sliceIndex > firstSlice)
            {
                json.Append(',');
            }

            PersistedSlice slice = slices[sliceIndex];
            json.Append("{\"hourUnixMs\":").Append(
                slice.HourStartUnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"cells\":[");
            for (int cellIndex = 0; cellIndex < slice.Cells.Length; cellIndex++)
            {
                if (cellIndex > 0)
                {
                    json.Append(',');
                }

                PersistedCell cell = slice.Cells[cellIndex];
                json.Append('[').Append(cell.Index.ToString(CultureInfo.InvariantCulture));
                json.Append(',').Append(cell.Count.ToString(CultureInfo.InvariantCulture));
                json.Append(']');
            }

            json.Append("]}");
        }

        json.Append("]}");
        return json.ToString();
    }

    private void PruneExpiredSlicesLocked(long currentHourStartUnixMs)
    {
        long oldestHourStartUnixMs = currentHourStartUnixMs -
                                     ((HourSliceCount - 1L) * HourMilliseconds);
        for (int index = 0; index < _slices.Length; index++)
        {
            HourSlice slice = _slices[index];
            if (slice.HourStartUnixMs >= 0L &&
                (slice.HourStartUnixMs < oldestHourStartUnixMs ||
                 slice.HourStartUnixMs > currentHourStartUnixMs))
            {
                ClearSliceLocked(slice);
            }
        }
    }

    private bool DropOldestSliceLocked(long protectedHourStartUnixMs)
    {
        HourSlice? oldest = null;
        for (int index = 0; index < _slices.Length; index++)
        {
            HourSlice candidate = _slices[index];
            if (candidate.Cells.Count == 0 ||
                candidate.HourStartUnixMs == protectedHourStartUnixMs)
            {
                continue;
            }

            if (oldest == null ||
                candidate.HourStartUnixMs < oldest.HourStartUnixMs)
            {
                oldest = candidate;
            }
        }

        if (oldest == null)
        {
            return false;
        }

        ClearSliceLocked(oldest);
        return true;
    }

    private void ClearSliceLocked(HourSlice slice)
    {
        _totalCellEntries -= slice.Cells.Count;
        slice.Cells = new Dictionary<int, int>();
        slice.HourStartUnixMs = -1L;
    }

    private void ResetLocked()
    {
        for (int index = 0; index < _slices.Length; index++)
        {
            _slices[index].Cells = new Dictionary<int, int>();
            _slices[index].HourStartUnixMs = -1L;
        }

        _totalCellEntries = 0;
        _latestHourStartUnixMs = FloorToHour(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static bool TryGetCellIndex(float x, float z, out int cellIndex)
    {
        cellIndex = 0;
        if (float.IsNaN(x) || float.IsInfinity(x) ||
            float.IsNaN(z) || float.IsInfinity(z) ||
            x < -WorldMapRenderer.WorldRadius || x > WorldMapRenderer.WorldRadius ||
            z < -WorldMapRenderer.WorldRadius || z > WorldMapRenderer.WorldRadius)
        {
            return false;
        }

        double span = WorldMapRenderer.WorldRadius * 2.0;
        int ix = Math.Min(
            GridSize - 1,
            (int)(((x + WorldMapRenderer.WorldRadius) / span) * GridSize));
        int iz = Math.Min(
            GridSize - 1,
            (int)(((z + WorldMapRenderer.WorldRadius) / span) * GridSize));
        cellIndex = (iz * GridSize) + ix;
        return true;
    }

    private static long FloorToHour(long unixMs)
    {
        long remainder = unixMs % HourMilliseconds;
        return unixMs - (remainder < 0L ? remainder + HourMilliseconds : remainder);
    }

    private static int SliceIndex(long hourStartUnixMs)
    {
        long index = (hourStartUnixMs / HourMilliseconds) % HourSliceCount;
        return (int)(index < 0L ? index + HourSliceCount : index);
    }

    private static int ComparePersistedCells(PersistedCell left, PersistedCell right)
    {
        return left.Index.CompareTo(right.Index);
    }

    private static int ComparePersistedSlices(PersistedSlice left, PersistedSlice right)
    {
        return left.HourStartUnixMs.CompareTo(right.HourStartUnixMs);
    }

    private static int CompareActivityCells(
        ActivityHeatmapCell left,
        ActivityHeatmapCell right)
    {
        int z = left.Z.CompareTo(right.Z);
        return z != 0 ? z : left.X.CompareTo(right.X);
    }

    private static int CompareActivitySlices(
        ActivityHeatmapHourSlice left,
        ActivityHeatmapHourSlice right)
    {
        return left.HourStartUnixMs.CompareTo(right.HourStartUnixMs);
    }

    private static string SingleLineMessage(Exception exception)
    {
        return (exception.Message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private sealed class HourSlice
    {
        public long HourStartUnixMs { get; set; } = -1L;

        public Dictionary<int, int> Cells { get; set; } = new Dictionary<int, int>();
    }

    private readonly struct PersistedCell
    {
        public PersistedCell(int index, int count)
        {
            Index = index;
            Count = count;
        }

        public int Index { get; }

        public int Count { get; }
    }

    private sealed class PersistedSlice
    {
        public PersistedSlice(long hourStartUnixMs, PersistedCell[] cells)
        {
            HourStartUnixMs = hourStartUnixMs;
            Cells = cells;
        }

        public long HourStartUnixMs { get; }

        public PersistedCell[] Cells { get; }
    }

    private static class HeatmapJsonParser
    {
        public static List<PersistedSlice> Parse(string json)
        {
            return new Parser(json).ParseDocument();
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _index;
            private int _cellCount;

            public Parser(string json)
            {
                _json = json;
            }

            public List<PersistedSlice> ParseDocument()
            {
                Expect('{');
                ExpectProperty("version");
                int version = ReadInt32();
                Expect(',');
                ExpectProperty("size");
                int size = ReadInt32();
                Expect(',');
                ExpectProperty("worldRadius");
                int worldRadius = ReadInt32();
                Expect(',');
                ExpectProperty("slices");
                List<PersistedSlice> slices = ParseSlices();
                Expect('}');
                EnsureEnd();
                if (version != PersistedVersion || size != GridSize ||
                    worldRadius != WorldMapRenderer.WorldRadius)
                {
                    throw new FormatException(
                        "Activity-heatmap JSON has incompatible grid metadata.");
                }

                return slices;
            }

            private List<PersistedSlice> ParseSlices()
            {
                var slices = new List<PersistedSlice>();
                Expect('[');
                if (TryConsume(']'))
                {
                    return slices;
                }

                while (true)
                {
                    slices.Add(ParseSlice());
                    if (slices.Count > HourSliceCount)
                    {
                        throw new FormatException(
                            "Activity-heatmap JSON contains too many hourly slices.");
                    }

                    if (TryConsume(']'))
                    {
                        return slices;
                    }

                    Expect(',');
                }
            }

            private PersistedSlice ParseSlice()
            {
                Expect('{');
                ExpectProperty("hourUnixMs");
                long hourStartUnixMs = ReadInt64();
                Expect(',');
                ExpectProperty("cells");
                PersistedCell[] cells = ParseCells();
                Expect('}');
                return new PersistedSlice(hourStartUnixMs, cells);
            }

            private PersistedCell[] ParseCells()
            {
                var cells = new List<PersistedCell>();
                Expect('[');
                if (TryConsume(']'))
                {
                    return cells.ToArray();
                }

                while (true)
                {
                    Expect('[');
                    int cellIndex = ReadInt32();
                    Expect(',');
                    int count = ReadInt32();
                    Expect(']');
                    cells.Add(new PersistedCell(cellIndex, count));
                    _cellCount++;
                    if (_cellCount > MaximumSparseCells)
                    {
                        throw new FormatException(
                            "Activity-heatmap JSON exceeds the sparse-cell cap.");
                    }

                    if (TryConsume(']'))
                    {
                        return cells.ToArray();
                    }

                    Expect(',');
                }
            }

            private void ExpectProperty(string expected)
            {
                string actual = ReadString();
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    throw new FormatException(
                        "Activity-heatmap JSON has an unexpected property.");
                }

                Expect(':');
            }

            private int ReadInt32()
            {
                long value = ReadInt64();
                if (value < int.MinValue || value > int.MaxValue)
                {
                    throw new FormatException(
                        "Activity-heatmap JSON contains an invalid integer.");
                }

                return (int)value;
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
                    (_index == start + 1 && _json[start] == '-') ||
                    !long.TryParse(
                        _json.Substring(start, _index - start),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long value))
                {
                    throw new FormatException(
                        "Activity-heatmap JSON contains an invalid integer.");
                }

                return value;
            }

            private string ReadString()
            {
                SkipWhitespace();
                if (_index >= _json.Length || _json[_index++] != '"')
                {
                    throw new FormatException(
                        "Activity-heatmap JSON contains an invalid string.");
                }

                int start = _index;
                while (_index < _json.Length && _json[_index] != '"')
                {
                    char character = _json[_index];
                    if (character == '\\' || character < 0x20)
                    {
                        throw new FormatException(
                            "Activity-heatmap JSON contains an invalid property name.");
                    }

                    _index++;
                }

                if (_index >= _json.Length)
                {
                    throw new FormatException(
                        "Activity-heatmap JSON contains an unterminated string.");
                }

                string value = _json.Substring(start, _index - start);
                _index++;
                return value;
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
                    throw new FormatException("Activity-heatmap JSON is malformed.");
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
                    throw new FormatException(
                        "Activity-heatmap JSON contains trailing data.");
                }
            }
        }
    }
}

internal sealed class ActivityHeatmapSnapshot
{
    public ActivityHeatmapSnapshot(
        string window,
        long generatedUnixMs,
        int maximumCount,
        ActivityHeatmapCell[] cells)
    {
        Window = window;
        GeneratedUnixMs = generatedUnixMs;
        MaximumCount = maximumCount;
        Cells = cells;
    }

    public string Window { get; }

    public long GeneratedUnixMs { get; }

    public int MaximumCount { get; }

    public ActivityHeatmapCell[] Cells { get; }
}

internal readonly struct ActivityHeatmapCell
{
    public ActivityHeatmapCell(int x, int z, int count)
    {
        X = x;
        Z = z;
        Count = count;
    }

    public int X { get; }

    public int Z { get; }

    public int Count { get; }
}

internal readonly struct ActivityHeatmapHourSlice
{
    public ActivityHeatmapHourSlice(
        long hourStartUnixMs,
        ActivityHeatmapCell[] cells)
    {
        HourStartUnixMs = hourStartUnixMs;
        Cells = cells;
    }

    public long HourStartUnixMs { get; }

    public ActivityHeatmapCell[] Cells { get; }
}

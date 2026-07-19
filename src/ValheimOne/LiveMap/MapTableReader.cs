using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class MapTableReader
{
    private const string PrefabName = "piece_cartographytable";
    private const float RefreshIntervalSeconds = 60f;
    private const int OldestSharedMapVersion = 1;
    private const int SharedMapPinsVersion = 2;
    private const int SharedMapAuthorVersion = 3;
    private const int SourceSize = 2048;
    private const int SourceCellCount = SourceSize * SourceSize;
    private const int DownsampleScale = SourceSize / FogTracker.Size;
    private const int MaximumPinCount = 100000;

    private readonly FogTracker _fogTracker;
    private readonly ModLogger _log;
    private readonly List<ZDO> _scanResults = new List<ZDO>();
    private readonly ManualResetEventSlim _workerDone = new ManualResetEventSlim(true);
    private readonly object _warningLock = new object();
    private readonly HashSet<int> _warnedVersions = new HashSet<int>();
    private readonly HashSet<int> _warnedCorruptPayloads = new HashSet<int>();
    private MapTableSnapshot _snapshot = MapTableSnapshot.Empty;
    private MapTableSnapshot _fogSnapshot = MapTableSnapshot.Empty;
    private float _nextRefresh;
    private int _scanIndex;
    private int _workerRunning;
    private bool _scanning;
    private bool _started;
    private volatile bool _stopped;

    public MapTableReader(FogTracker fogTracker, ModLogger log)
    {
        _fogTracker = fogTracker;
        _log = log;
    }

    public MapTableSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Start()
    {
        if (_started || _stopped)
        {
            return;
        }

        _started = true;
        _nextRefresh = 0f;
    }

    public void Tick(float now, bool includeExplored)
    {
        if (!_started || _stopped)
        {
            return;
        }

        MapTableSnapshot snapshot = Snapshot;
        if (includeExplored && !ReferenceEquals(snapshot, _fogSnapshot))
        {
            _fogTracker.OrExternalMask(snapshot.ExploredMask);
            _fogSnapshot = snapshot;
        }

        if (_scanning)
        {
            ContinueScan(now);
            return;
        }

        if (now < _nextRefresh || Volatile.Read(ref _workerRunning) != 0)
        {
            return;
        }

        _scanResults.Clear();
        _scanIndex = 0;
        _scanning = true;
        ContinueScan(now);
    }

    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _scanning = false;
        _scanResults.Clear();
        _workerDone.Wait();
    }

    private void ContinueScan(float now)
    {
        ZDOMan? manager = ZDOMan.instance;
        if (manager == null)
        {
            FinishScanWithoutWorker(now);
            return;
        }

        bool complete;
        try
        {
            complete = manager.GetAllZDOsWithPrefabIterative(
                PrefabName,
                _scanResults,
                ref _scanIndex);
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] cartography ZDO scan failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            FinishScanWithoutWorker(now);
            return;
        }

        if (!complete)
        {
            return;
        }

        int tableCount = _scanResults.Count;
        var payloads = new List<byte[]>(tableCount);
        for (int index = 0; index < _scanResults.Count; index++)
        {
            try
            {
                byte[]? payload = _scanResults[index].GetByteArray(ZDOVars.s_data);
                if (payload != null && payload.Length > 0)
                {
                    payloads.Add(payload);
                }
            }
            catch (Exception exception)
            {
                _log.Warning(
                    $"[LiveMap] could not read cartography table ZDO data: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        _scanResults.Clear();
        _scanning = false;
        _nextRefresh = now + RefreshIntervalSeconds;
        QueueParse(new ReadBatch(tableCount, payloads.ToArray()));
    }

    private void FinishScanWithoutWorker(float now)
    {
        _scanResults.Clear();
        _scanning = false;
        _nextRefresh = now + RefreshIntervalSeconds;
    }

    private void QueueParse(ReadBatch batch)
    {
        if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) != 0)
        {
            return;
        }

        _workerDone.Reset();
        try
        {
            if (!ThreadPool.QueueUserWorkItem(ProcessBatch, batch))
            {
                Interlocked.Exchange(ref _workerRunning, 0);
                _workerDone.Set();
                _log.Warning("[LiveMap] could not queue cartography table parsing.");
            }
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _workerRunning, 0);
            _workerDone.Set();
            _log.Warning(
                $"[LiveMap] could not queue cartography table parsing: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void ProcessBatch(object? state)
    {
        try
        {
            var batch = state as ReadBatch;
            if (batch == null || _stopped)
            {
                return;
            }

            var explored = new byte[FogTracker.Size * FogTracker.Size];
            var pinsByKey = new Dictionary<PinKey, MapTablePin>();
            for (int index = 0; index < batch.Payloads.Length && !_stopped; index++)
            {
                byte[] payload = batch.Payloads[index];
                try
                {
                    ParsedTable? table = ParseTable(payload);
                    if (table == null)
                    {
                        continue;
                    }

                    OrMask(explored, table.ExploredMask);
                    for (int pinIndex = 0; pinIndex < table.Pins.Length; pinIndex++)
                    {
                        MapTablePin pin = table.Pins[pinIndex];
                        var key = new PinKey(pin.Name, pin.X, pin.Z);
                        if (!pinsByKey.ContainsKey(key))
                        {
                            pinsByKey.Add(key, pin);
                        }
                    }
                }
                catch (Exception exception)
                {
                    WarnCorruptPayloadOnce(payload, exception);
                }
            }

            if (_stopped)
            {
                return;
            }

            var pins = new MapTablePin[pinsByKey.Count];
            pinsByKey.Values.CopyTo(pins, 0);
            Array.Sort(pins, MapTablePinComparer.Instance);
            var next = new MapTableSnapshot(batch.TableCount, explored, pins);
            MapTableSnapshot current = Snapshot;
            if (MapTableSnapshot.DataEquals(current, next))
            {
                return;
            }

            Volatile.Write(ref _snapshot, next);
            double coverage = next.ExploredCellCount * 100d / explored.Length;
            _log.Info(
                $"[LiveMap] cartography: {next.TableCount} tables, {next.Pins.Length} pins, " +
                $"explored coverage {coverage.ToString("0.0", CultureInfo.InvariantCulture)}%");
        }
        catch (Exception exception)
        {
            if (!_stopped)
            {
                _log.Warning(
                    $"[LiveMap] cartography refresh failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _workerRunning, 0);
            _workerDone.Set();
        }
    }

    private ParsedTable? ParseTable(byte[] compressed)
    {
        byte[] data = Utils.Decompress(compressed);
        var package = new ZPackage(data);
        int version = package.ReadInt();
        if (version < OldestSharedMapVersion || version > SharedMapAuthorVersion)
        {
            WarnVersionOnce(version);
            return null;
        }

        int exploredLength = package.ReadInt();
        if (exploredLength != SourceCellCount)
        {
            throw new InvalidDataException(
                $"explored bitmap has {exploredLength} cells; expected {SourceCellCount}");
        }

        byte[] sourceExplored = package.ReadByteArray(exploredLength);
        if (sourceExplored.Length != exploredLength)
        {
            throw new EndOfStreamException(
                $"explored bitmap ended after {sourceExplored.Length} of {exploredLength} cells");
        }

        byte[] explored = DownsampleExplored(sourceExplored);
        if (version < SharedMapPinsVersion)
        {
            return new ParsedTable(explored, Array.Empty<MapTablePin>());
        }

        int pinCount = package.ReadInt();
        if (pinCount < 0 || pinCount > MaximumPinCount)
        {
            throw new InvalidDataException($"pin count {pinCount} is invalid");
        }

        var pins = new MapTablePin[pinCount];
        for (int index = 0; index < pinCount; index++)
        {
            package.ReadLong();
            string name = package.ReadString();
            Vector3 position = package.ReadVector3();
            int type = package.ReadInt();
            bool isChecked = package.ReadBool();
            string author = version >= SharedMapAuthorVersion
                ? package.ReadString()
                : string.Empty;
            if (!IsFinite(position.x) || !IsFinite(position.z))
            {
                throw new InvalidDataException("pin position is not finite");
            }

            pins[index] = new MapTablePin(
                name,
                position.x,
                position.z,
                type,
                PinIconNames.FromType(type),
                author,
                isChecked);
        }

        return new ParsedTable(explored, pins);
    }

    private static byte[] DownsampleExplored(byte[] source)
    {
        var destination = new byte[FogTracker.Size * FogTracker.Size];
        for (int sourceY = 0; sourceY < SourceSize; sourceY++)
        {
            int destinationY = (FogTracker.Size - 1) - (sourceY / DownsampleScale);
            int sourceRow = sourceY * SourceSize;
            int destinationRow = destinationY * FogTracker.Size;
            for (int sourceX = 0; sourceX < SourceSize; sourceX++)
            {
                if (source[sourceRow + sourceX] != 0)
                {
                    destination[destinationRow + (sourceX / DownsampleScale)] = byte.MaxValue;
                }
            }
        }

        return destination;
    }

    private static void OrMask(byte[] destination, byte[] source)
    {
        for (int index = 0; index < destination.Length; index++)
        {
            if (source[index] != 0)
            {
                destination[index] = byte.MaxValue;
            }
        }
    }

    private void WarnVersionOnce(int version)
    {
        bool shouldLog;
        lock (_warningLock)
        {
            shouldLog = _warnedVersions.Add(version);
        }

        if (shouldLog)
        {
            _log.Warning(
                $"[LiveMap] cartography table data version {version} is not verified; skipping it.");
        }
    }

    private void WarnCorruptPayloadOnce(byte[] payload, Exception exception)
    {
        int fingerprint = Fingerprint(payload);
        bool shouldLog;
        lock (_warningLock)
        {
            shouldLog = _warnedCorruptPayloads.Add(fingerprint);
        }

        if (shouldLog)
        {
            _log.Warning(
                $"[LiveMap] skipped corrupt cartography table data: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static int Fingerprint(byte[] payload)
    {
        unchecked
        {
            int hash = (int)2166136261;
            for (int index = 0; index < payload.Length; index++)
            {
                hash = (hash ^ payload[index]) * 16777619;
            }

            return hash;
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private sealed class ReadBatch
    {
        public ReadBatch(int tableCount, byte[][] payloads)
        {
            TableCount = tableCount;
            Payloads = payloads;
        }

        public int TableCount { get; }

        public byte[][] Payloads { get; }
    }

    private sealed class ParsedTable
    {
        public ParsedTable(byte[] exploredMask, MapTablePin[] pins)
        {
            ExploredMask = exploredMask;
            Pins = pins;
        }

        public byte[] ExploredMask { get; }

        public MapTablePin[] Pins { get; }
    }

    private readonly struct PinKey : IEquatable<PinKey>
    {
        private readonly string _name;
        private readonly int _x;
        private readonly int _z;

        public PinKey(string name, float x, float z)
        {
            _name = name;
            _x = RoundPosition(x);
            _z = RoundPosition(z);
        }

        public bool Equals(PinKey other)
        {
            return _x == other._x &&
                   _z == other._z &&
                   string.Equals(_name, other._name, StringComparison.Ordinal);
        }

        public override bool Equals(object? value)
        {
            return value is PinKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(_name);
                hash = (hash * 31) + _x;
                hash = (hash * 31) + _z;
                return hash;
            }
        }

        private static int RoundPosition(float value)
        {
            return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
        }
    }
}

internal sealed class MapTableSnapshot
{
    public static readonly MapTableSnapshot Empty = new MapTableSnapshot(
        0,
        new byte[FogTracker.Size * FogTracker.Size],
        Array.Empty<MapTablePin>());

    public MapTableSnapshot(int tableCount, byte[] exploredMask, MapTablePin[] pins)
    {
        TableCount = tableCount;
        ExploredMask = exploredMask;
        Pins = pins;

        int exploredCellCount = 0;
        for (int index = 0; index < exploredMask.Length; index++)
        {
            if (exploredMask[index] != 0)
            {
                exploredCellCount++;
            }
        }

        ExploredCellCount = exploredCellCount;
    }

    public int TableCount { get; }

    public byte[] ExploredMask { get; }

    public int ExploredCellCount { get; }

    public MapTablePin[] Pins { get; }

    public static bool DataEquals(MapTableSnapshot left, MapTableSnapshot right)
    {
        if (left.TableCount != right.TableCount ||
            left.ExploredCellCount != right.ExploredCellCount ||
            left.Pins.Length != right.Pins.Length ||
            left.ExploredMask.Length != right.ExploredMask.Length)
        {
            return false;
        }

        for (int index = 0; index < left.ExploredMask.Length; index++)
        {
            if (left.ExploredMask[index] != right.ExploredMask[index])
            {
                return false;
            }
        }

        for (int index = 0; index < left.Pins.Length; index++)
        {
            if (!MapTablePin.DataEquals(left.Pins[index], right.Pins[index]))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class MapTablePin
{
    public MapTablePin(
        string name,
        float x,
        float z,
        int type,
        string icon,
        string author,
        bool isChecked)
    {
        Name = name;
        X = x;
        Z = z;
        Type = type;
        Icon = icon;
        Author = author;
        IsChecked = isChecked;
    }

    public string Name { get; }

    public float X { get; }

    public float Z { get; }

    public int Type { get; }

    public string Icon { get; }

    public string Author { get; }

    public bool IsChecked { get; }

    public static bool DataEquals(MapTablePin left, MapTablePin right)
    {
        return string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
               left.X.Equals(right.X) &&
               left.Z.Equals(right.Z) &&
               left.Type == right.Type &&
               string.Equals(left.Icon, right.Icon, StringComparison.Ordinal) &&
               string.Equals(left.Author, right.Author, StringComparison.Ordinal) &&
               left.IsChecked == right.IsChecked;
    }
}

internal sealed class MapTablePinComparer : IComparer<MapTablePin>
{
    public static readonly MapTablePinComparer Instance = new MapTablePinComparer();

    public int Compare(MapTablePin? left, MapTablePin? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left == null)
        {
            return -1;
        }

        if (right == null)
        {
            return 1;
        }

        int result = string.CompareOrdinal(left.Name, right.Name);
        if (result != 0)
        {
            return result;
        }

        result = left.X.CompareTo(right.X);
        if (result != 0)
        {
            return result;
        }

        result = left.Z.CompareTo(right.Z);
        if (result != 0)
        {
            return result;
        }

        result = left.Type.CompareTo(right.Type);
        if (result != 0)
        {
            return result;
        }

        result = string.CompareOrdinal(left.Author, right.Author);
        return result != 0 ? result : left.IsChecked.CompareTo(right.IsChecked);
    }
}

internal static class PinIconNames
{
    public static string FromType(int type)
    {
        switch (type)
        {
            case 0:
                return "icon0";
            case 1:
                return "icon1";
            case 2:
                return "icon2";
            case 3:
                return "icon3";
            case 4:
                return "death";
            case 5:
                return "bed";
            case 6:
                return "icon4";
            case 7:
                return "shout";
            case 8:
                return "none";
            case 9:
                return "boss";
            case 10:
                return "player";
            case 11:
                return "randomevent";
            case 12:
                return "ping";
            case 13:
                return "eventarea";
            case 14:
                return "hildir1";
            case 15:
                return "hildir2";
            case 16:
                return "hildir3";
            default:
                return "unknown";
        }
    }
}

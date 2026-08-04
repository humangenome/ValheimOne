using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class TimelapseRecorder : IDisposable
{
    private const ushort FormatVersion = 1;
    private const int FixedHeaderSize = 50;
    private const int MaximumPayloadBytes = 4 * 1024 * 1024;
    private const int MaximumDecodedPayloadBytes = 512 * 1024;
    private const int MaximumBases = 512;
    private const int MaximumPoints = 4096;
    private const int MaximumMovementCells = ActivityHeatmap.GridSize * ActivityHeatmap.GridSize;
    private const int MaximumBossKeys = 64;
    private const int MaximumStringBytes = 256;
    private const int MinimumChangeScore = 24;
    private const int WriterPollMilliseconds = 100;
    private const int ShutdownFlushMilliseconds = 2000;
    private const long HourMilliseconds = 60L * 60L * 1000L;

    private static readonly byte[] Magic = { (byte)'V', (byte)'O', (byte)'T', (byte)'L' };
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly string[] KnownBossKeys =
    {
        "defeated_eikthyr",
        "defeated_gdking",
        "defeated_bonemass",
        "defeated_dragon",
        "defeated_goblinking",
        "defeated_queen",
        "defeated_fader",
    };

    private readonly object _lock = new object();
    private readonly SortedDictionary<long, TimelapseIndexEntry> _index =
        new SortedDictionary<long, TimelapseIndexEntry>();
    private readonly ManualResetEvent _stopSignal = new ManualResetEvent(false);
    private readonly string _directory;
    private readonly ModLogger _log;
    private readonly Thread _writerThread;
    private PendingCapture? _pendingCapture;
    private byte[]? _previousFogBits;
    private long _previousFogRevision;
    private long _previousTotalPieces;
    private long _lastFrameUnixMs;
    private long _lastHarvestedHourUnixMs;
    private long _totalBytes;
    private int _previousExploredCells;
    private int _previousBaseCount;
    private int _previousPortalCount;
    private int _previousBossMask;
    private int _dirty;
    private int _writeFailureWarningLogged;
    private int _pendingDropLogged;
    private bool _previousFogRevisionKnown;
    private bool _disposed;

    public TimelapseRecorder(string dataDirectory, ModLogger log)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }

        _log = log ?? throw new ArgumentNullException(nameof(log));
        _directory = Path.Combine(dataDirectory, "timelapse");
        LoadIndex();
        EvictFrames(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        SeedPreviousFrame();

        _writerThread = new Thread(RunWriter)
        {
            IsBackground = true,
            Name = "ValheimOne.Timelapse",
        };
        _writerThread.Start();
    }

    public long TotalBytes
    {
        get
        {
            lock (_lock)
            {
                return _totalBytes;
            }
        }
    }

    public long LastHarvestedHourUnixMs
    {
        get
        {
            lock (_lock)
            {
                return _lastHarvestedHourUnixMs;
            }
        }
    }

    public void Capture(TimelapseCaptureInput input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        PreviousState previous;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_pendingCapture != null)
            {
                LogPendingDropOnce();
                return;
            }

            previous = new PreviousState(
                _previousFogBits,
                _previousFogRevision,
                _previousFogRevisionKnown,
                _previousExploredCells,
                _previousBaseCount,
                _previousTotalPieces,
                _previousPortalCount,
                _previousBossMask,
                _lastFrameUnixMs);
        }

        PendingCapture prepared;
        try
        {
            prepared = PrepareCapture(input, previous);
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] timelapse capture was rejected " +
                $"({exception.GetType().Name}: {SingleLineMessage(exception)}).");
            return;
        }

        if (!TimelapseRetention.ShouldCapture(
                previous.LastFrameUnixMs,
                prepared.UnixMs,
                input.CaptureIntervalMinutes,
                prepared.ChangeScore,
                MinimumChangeScore))
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_pendingCapture != null || _lastFrameUnixMs != previous.LastFrameUnixMs)
            {
                LogPendingDropOnce();
                return;
            }

            _pendingCapture = prepared;
            Volatile.Write(ref _dirty, 1);
        }
    }

    public TimelapseIndexEntry[] ListFrames()
    {
        lock (_lock)
        {
            TimelapseIndexEntry[] result = new TimelapseIndexEntry[_index.Count];
            int index = 0;
            foreach (TimelapseIndexEntry entry in _index.Values)
            {
                result[index] = entry;
                index++;
            }

            return result;
        }
    }

    public TimelapseFrame? ReadFrame(long unixMs)
    {
        TimelapseIndexEntry entry;
        lock (_lock)
        {
            if (!_index.TryGetValue(unixMs, out TimelapseIndexEntry? indexed))
            {
                return null;
            }

            entry = indexed;
        }

        string path = FramePath(unixMs);
        try
        {
            return ReadFrameFile(path, unixMs);
        }
        catch (Exception exception)
        {
            RemoveCorruptFrame(path, entry, exception);
            return null;
        }
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
                "[LiveMap] timelapse writer did not exit within the shutdown flush window.");
        }
    }

    private void LoadIndex()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            string[] paths = Directory.GetFiles(_directory, "*.vof");
            Array.Sort(paths, StringComparer.Ordinal);
            for (int index = 0; index < paths.Length; index++)
            {
                LoadIndexEntry(paths[index]);
            }

            lock (_lock)
            {
                // The hourly harvest cursor is not a separate manifest. After a restart,
                // the newest frame's hour is a conservative approximation of its cursor.
                RecoverLastHarvestedHourLocked();
            }
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] timelapse index could not be scanned " +
                $"({exception.GetType().Name}: {SingleLineMessage(exception)}).");
        }
    }

    private void LoadIndexEntry(string path)
    {
        try
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            if (!long.TryParse(
                    fileName,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long fileUnixMs))
            {
                throw new FormatException("The frame filename is not a Unix timestamp.");
            }

            using (FileStream stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (BinaryReader reader = new BinaryReader(stream, Utf8, leaveOpen: true))
            {
                FrameHeader header = ReadHeader(reader);
                ValidateHeader(header, stream.Length, fileUnixMs);
                TimelapseIndexEntry entry = CreateIndexEntry(
                    header,
                    checked((int)stream.Length));
                lock (_lock)
                {
                    _index[entry.UnixMs] = entry;
                    _totalBytes += entry.SizeBytes;
                }
            }
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] corrupt timelapse frame {JsonWriter.Quote(Path.GetFileName(path))} " +
                $"was removed ({exception.GetType().Name}: {SingleLineMessage(exception)}).");
            DeleteCorruptFile(path);
        }
    }

    private void SeedPreviousFrame()
    {
        while (true)
        {
            long newestUnixMs;
            lock (_lock)
            {
                if (!TryGetNewestUnixMsLocked(out newestUnixMs))
                {
                    return;
                }
            }

            TimelapseFrame? frame = ReadFrame(newestUnixMs);
            if (frame == null)
            {
                continue;
            }

            long totalPieces = TotalPieces(frame.Bases);
            lock (_lock)
            {
                _previousFogBits = frame.FogBits;
                _previousExploredCells = frame.ExploredCells;
                _previousBaseCount = frame.BaseCount;
                _previousTotalPieces = totalPieces;
                _previousPortalCount = frame.PortalCount;
                _previousBossMask = frame.BossMask;
                _lastFrameUnixMs = frame.UnixMs;
                _previousFogRevisionKnown = false;
            }

            return;
        }
    }

    private void RunWriter()
    {
        while (!_stopSignal.WaitOne(WriterPollMilliseconds))
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

        PendingCapture? capture;
        lock (_lock)
        {
            capture = _pendingCapture;
        }

        if (capture == null)
        {
            return;
        }

        try
        {
            TimelapseIndexEntry entry = WriteFrame(capture);
            lock (_lock)
            {
                if (!ReferenceEquals(_pendingCapture, capture))
                {
                    return;
                }

                if (_index.TryGetValue(entry.UnixMs, out TimelapseIndexEntry? replaced))
                {
                    _totalBytes -= replaced.SizeBytes;
                }

                _index[entry.UnixMs] = entry;
                _totalBytes += entry.SizeBytes;
                _previousFogBits = capture.FogBits;
                _previousFogRevision = capture.FogRevision;
                _previousFogRevisionKnown = true;
                _previousExploredCells = capture.ExploredCells;
                _previousBaseCount = capture.Bases.Length;
                _previousTotalPieces = capture.TotalPieces;
                _previousPortalCount = capture.Portals.Length;
                _previousBossMask = capture.BossMask;
                _lastFrameUnixMs = capture.UnixMs;
                if (capture.LastHarvestedHourUnixMs > _lastHarvestedHourUnixMs)
                {
                    _lastHarvestedHourUnixMs = capture.LastHarvestedHourUnixMs;
                }

                _pendingCapture = null;
            }

            Interlocked.Exchange(ref _writeFailureWarningLogged, 0);
            EvictFrames(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _dirty, 1);
            if (Interlocked.Exchange(ref _writeFailureWarningLogged, 1) == 0)
            {
                _log.Warning(
                    $"[LiveMap] timelapse frame could not be persisted " +
                    $"({exception.GetType().Name}: {SingleLineMessage(exception)}). " +
                    "The writer will retry.");
            }
        }
    }

    private TimelapseIndexEntry WriteFrame(PendingCapture capture)
    {
        byte[] payload = BuildCompressedPayload(capture);
        if (payload.Length <= 0 || payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidOperationException(
                "The compressed timelapse payload exceeds the frame cap.");
        }

        FrameHeader header = new FrameHeader(
            capture.UnixMs,
            capture.WorldDay,
            capture.ExploredCells,
            capture.Bases.Length,
            capture.Portals.Length,
            capture.Beds.Length,
            capture.Wards.Length,
            capture.BossMask,
            capture.MovementCells.Length,
            payload.Length);
        Directory.CreateDirectory(_directory);
        string path = FramePath(capture.UnixMs);
        string temporaryPath = path + ".tmp";
        using (FileStream stream = new FileStream(
                   temporaryPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        using (BinaryWriter writer = new BinaryWriter(stream, Utf8, leaveOpen: true))
        {
            WriteHeader(writer, header);
            writer.Write(payload);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(temporaryPath, path, null);
        }
        else
        {
            File.Move(temporaryPath, path);
        }

        return CreateIndexEntry(header, checked(FixedHeaderSize + payload.Length));
    }

    private static byte[] BuildCompressedPayload(PendingCapture capture)
    {
        using (MemoryStream uncompressed = new MemoryStream())
        {
            using (BinaryWriter writer = new BinaryWriter(uncompressed, Utf8, leaveOpen: true))
            {
                writer.Write(capture.FogBits);
                writer.Write(capture.Bases.Length);
                for (int index = 0; index < capture.Bases.Length; index++)
                {
                    PlayerBaseEntry entry = capture.Bases[index];
                    writer.Write(entry.X);
                    writer.Write(entry.Z);
                    writer.Write(entry.Radius);
                    writer.Write(entry.Pieces);
                }

                WritePoints(writer, capture.Portals);
                WritePoints(writer, capture.Beds);
                WritePoints(writer, capture.Wards);

                writer.Write(capture.MovementCells.Length);
                for (int index = 0; index < capture.MovementCells.Length; index++)
                {
                    TimelapseMovementCell cell = capture.MovementCells[index];
                    writer.Write(checked((ushort)cell.Index));
                    writer.Write(cell.Count);
                }

                writer.Write(capture.BossKeys.Length);
                for (int index = 0; index < capture.BossKeys.Length; index++)
                {
                    WriteString(writer, capture.BossKeys[index]);
                }

                WriteString(writer, capture.Season);
                writer.Flush();
            }

            uncompressed.Position = 0L;
            using (MemoryStream compressed = new MemoryStream())
            {
                using (DeflateStream deflate = new DeflateStream(
                           compressed,
                           CompressionLevel.Optimal,
                           leaveOpen: true))
                {
                    uncompressed.CopyTo(deflate);
                }

                return compressed.ToArray();
            }
        }
    }

    private TimelapseFrame ReadFrameFile(string path, long expectedUnixMs)
    {
        using (FileStream stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (BinaryReader reader = new BinaryReader(stream, Utf8, leaveOpen: true))
        {
            FrameHeader header = ReadHeader(reader);
            ValidateHeader(header, stream.Length, expectedUnixMs);
            byte[] compressed = ReadBytes(reader, header.PayloadLength);
            byte[] payload = DecompressPayload(compressed);
            return DecodePayload(
                header,
                checked((int)stream.Length),
                payload);
        }
    }

    private static byte[] DecompressPayload(byte[] compressed)
    {
        using (MemoryStream source = new MemoryStream(compressed, writable: false))
        using (DeflateStream deflate = new DeflateStream(
                   source,
                   CompressionMode.Decompress,
                   leaveOpen: false))
        using (MemoryStream output = new MemoryStream())
        {
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = deflate.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                if (output.Length + read > MaximumDecodedPayloadBytes)
                {
                    throw new FormatException("The decoded timelapse payload is too large.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
    }

    private static TimelapseFrame DecodePayload(
        FrameHeader header,
        int sizeBytes,
        byte[] payload)
    {
        using (MemoryStream stream = new MemoryStream(payload, writable: false))
        using (BinaryReader reader = new BinaryReader(stream, Utf8, leaveOpen: true))
        {
            int fogByteCount = (FogTracker.Size * FogTracker.Size) / 8;
            byte[] fogBits = ReadBytes(reader, fogByteCount);
            if (CountExploredCells(fogBits) != header.ExploredCells)
            {
                throw new FormatException("The frame fog count does not match its header.");
            }

            int baseCount = ReadCount(reader, MaximumBases, "base");
            if (baseCount != header.BaseCount)
            {
                throw new FormatException("The frame base count does not match its header.");
            }

            PlayerBaseEntry[] bases = new PlayerBaseEntry[baseCount];
            for (int index = 0; index < bases.Length; index++)
            {
                float x = ReadFiniteSingle(reader, "base X");
                float z = ReadFiniteSingle(reader, "base Z");
                float radius = ReadFiniteSingle(reader, "base radius");
                int pieces = reader.ReadInt32();
                if (radius < 0f || pieces < 0)
                {
                    throw new FormatException("The frame contains invalid base data.");
                }

                bases[index] = new PlayerBaseEntry(
                    $"base-{index + 1}",
                    x,
                    z,
                    radius,
                    pieces);
            }

            TimelapsePoint[] portals = ReadPoints(reader, header.PortalCount, "portal");
            TimelapsePoint[] beds = ReadPoints(reader, header.BedCount, "bed");
            TimelapsePoint[] wards = ReadPoints(reader, header.WardCount, "ward");

            int movementCount = ReadCount(reader, MaximumMovementCells, "movement-cell");
            if (movementCount != header.MovementCellCount)
            {
                throw new FormatException(
                    "The frame movement-cell count does not match its header.");
            }

            TimelapseMovementCell[] movementCells =
                new TimelapseMovementCell[movementCount];
            HashSet<int> seenMovementCells = new HashSet<int>();
            for (int index = 0; index < movementCells.Length; index++)
            {
                int cellIndex = reader.ReadUInt16();
                int count = reader.ReadInt32();
                if (cellIndex < 0 || cellIndex >= MaximumMovementCells ||
                    count <= 0 || !seenMovementCells.Add(cellIndex))
                {
                    throw new FormatException("The frame contains invalid movement-cell data.");
                }

                movementCells[index] = new TimelapseMovementCell(cellIndex, count);
            }

            int bossKeyCount = ReadCount(reader, MaximumBossKeys, "boss-key");
            string[] bossKeys = new string[bossKeyCount];
            for (int index = 0; index < bossKeys.Length; index++)
            {
                bossKeys[index] = ReadString(reader);
            }

            if (CalculateBossMask(bossKeys) != header.BossMask)
            {
                throw new FormatException("The frame boss mask does not match its payload.");
            }

            string season = ReadString(reader);
            if (stream.Position != stream.Length)
            {
                throw new FormatException("The frame contains trailing payload data.");
            }

            TimelapseIndexEntry entry = CreateIndexEntry(header, sizeBytes);
            return new TimelapseFrame(
                entry,
                fogBits,
                bases,
                portals,
                beds,
                wards,
                movementCells,
                bossKeys,
                season);
        }
    }

    private static PendingCapture PrepareCapture(
        TimelapseCaptureInput input,
        PreviousState previous)
    {
        if (input.FogMask == null ||
            input.FogMask.Length != FogTracker.Size * FogTracker.Size)
        {
            throw new ArgumentException("The fog mask has an invalid size.");
        }

        if (input.WorldDay < 0)
        {
            throw new ArgumentException("The world day cannot be negative.");
        }

        PlayerBaseEntry[] bases = CopyBases(input.Bases);
        TimelapsePoint[] portals = CopyPoints(input.Portals, MaximumPoints, "portal");
        TimelapsePoint[] beds = CopyPoints(input.Beds, MaximumPoints, "bed");
        TimelapsePoint[] wards = CopyPoints(input.Wards, MaximumPoints, "ward");
        string[] bossKeys = CopyStrings(input.BossKeys, MaximumBossKeys, "boss-key");
        string season = CopyString(input.Season);
        TimelapseMovementCell[] movementCells = AggregateMovement(
            input.MovementSlices,
            out long lastHarvestedHourUnixMs);

        byte[] fogBits;
        int exploredCells;
        int newlyExploredCells;
        if (previous.FogRevisionKnown &&
            previous.FogBits != null &&
            previous.FogRevision == input.FogRevision)
        {
            fogBits = (byte[])previous.FogBits.Clone();
            exploredCells = previous.ExploredCells;
            newlyExploredCells = 0;
        }
        else
        {
            fogBits = PackFog(
                input.FogMask,
                previous.FogBits,
                out exploredCells,
                out newlyExploredCells);
        }

        long totalPieces = TotalPieces(bases);
        int bossMask = CalculateBossMask(bossKeys);
        long score = newlyExploredCells;
        score = SaturatingAdd(
            score,
            Math.Abs((long)bases.Length - previous.BaseCount) * 50L);
        score = SaturatingAdd(score, AbsoluteDifference(totalPieces, previous.TotalPieces));
        score = SaturatingAdd(
            score,
            Math.Abs((long)portals.Length - previous.PortalCount) * 25L);
        if (bossMask != previous.BossMask)
        {
            score = SaturatingAdd(score, 500L);
        }

        score = SaturatingAdd(score, movementCells.Length);
        return new PendingCapture(
            input.UnixMs,
            input.FogRevision,
            fogBits,
            exploredCells,
            bases,
            totalPieces,
            portals,
            beds,
            wards,
            input.WorldDay,
            season,
            bossKeys,
            bossMask,
            movementCells,
            lastHarvestedHourUnixMs,
            score >= int.MaxValue ? int.MaxValue : (int)score);
    }

    private static PlayerBaseEntry[] CopyBases(PlayerBaseEntry[] bases)
    {
        if (bases == null)
        {
            throw new ArgumentNullException(nameof(bases));
        }

        if (bases.Length > MaximumBases)
        {
            throw new ArgumentException("The capture contains too many bases.");
        }

        PlayerBaseEntry[] result = new PlayerBaseEntry[bases.Length];
        for (int index = 0; index < bases.Length; index++)
        {
            PlayerBaseEntry? entry = bases[index];
            if (entry == null ||
                !IsFinite(entry.X) || !IsFinite(entry.Z) ||
                !IsFinite(entry.Radius) || entry.Radius < 0f || entry.Pieces < 0)
            {
                throw new ArgumentException("The capture contains invalid base data.");
            }

            result[index] = entry;
        }

        return result;
    }

    private static TimelapsePoint[] CopyPoints(
        TimelapsePoint[] points,
        int maximumCount,
        string name)
    {
        if (points == null)
        {
            throw new ArgumentNullException(nameof(points));
        }

        if (points.Length > maximumCount)
        {
            throw new ArgumentException($"The capture contains too many {name} entries.");
        }

        TimelapsePoint[] result = new TimelapsePoint[points.Length];
        for (int index = 0; index < points.Length; index++)
        {
            TimelapsePoint point = points[index];
            if (!IsFinite(point.X) || !IsFinite(point.Z))
            {
                throw new ArgumentException($"The capture contains invalid {name} data.");
            }

            result[index] = point;
        }

        return result;
    }

    private static string[] CopyStrings(string[] values, int maximumCount, string name)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        if (values.Length > maximumCount)
        {
            throw new ArgumentException($"The capture contains too many {name} entries.");
        }

        string[] result = new string[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            result[index] = CopyString(values[index]);
        }

        return result;
    }

    private static string CopyString(string? value)
    {
        string result = value ?? string.Empty;
        if (Utf8.GetByteCount(result) > MaximumStringBytes)
        {
            throw new ArgumentException("The capture contains an oversized string.");
        }

        return result;
    }

    private static TimelapseMovementCell[] AggregateMovement(
        ActivityHeatmapHourSlice[] slices,
        out long lastHarvestedHourUnixMs)
    {
        if (slices == null)
        {
            throw new ArgumentNullException(nameof(slices));
        }

        int[] totals = new int[MaximumMovementCells];
        int populated = 0;
        bool harvested = false;
        lastHarvestedHourUnixMs = 0L;
        for (int sliceIndex = 0; sliceIndex < slices.Length; sliceIndex++)
        {
            ActivityHeatmapHourSlice slice = slices[sliceIndex];
            if (slice.Cells == null)
            {
                throw new ArgumentException("The capture contains an invalid movement slice.");
            }

            bool sliceUsed = false;
            for (int cellIndex = 0; cellIndex < slice.Cells.Length; cellIndex++)
            {
                ActivityHeatmapCell cell = slice.Cells[cellIndex];
                if (cell.X < 0 || cell.X >= ActivityHeatmap.GridSize ||
                    cell.Z < 0 || cell.Z >= ActivityHeatmap.GridSize ||
                    cell.Count <= 0)
                {
                    throw new ArgumentException(
                        "The capture contains invalid movement-cell data.");
                }

                int index = (cell.Z * ActivityHeatmap.GridSize) + cell.X;
                if (totals[index] == 0)
                {
                    populated++;
                }

                totals[index] = cell.Count > int.MaxValue - totals[index]
                    ? int.MaxValue
                    : totals[index] + cell.Count;
                sliceUsed = true;
            }

            if (sliceUsed && (!harvested || slice.HourStartUnixMs > lastHarvestedHourUnixMs))
            {
                lastHarvestedHourUnixMs = slice.HourStartUnixMs;
                harvested = true;
            }
        }

        TimelapseMovementCell[] result = new TimelapseMovementCell[populated];
        int outputIndex = 0;
        for (int index = 0; index < totals.Length; index++)
        {
            if (totals[index] <= 0)
            {
                continue;
            }

            result[outputIndex] = new TimelapseMovementCell(index, totals[index]);
            outputIndex++;
        }

        return result;
    }

    private static byte[] PackFog(
        byte[] fogMask,
        byte[]? previousFogBits,
        out int exploredCells,
        out int newlyExploredCells)
    {
        byte[] result = new byte[fogMask.Length / 8];
        exploredCells = 0;
        newlyExploredCells = 0;
        for (int index = 0; index < fogMask.Length; index++)
        {
            if (fogMask[index] == 0)
            {
                continue;
            }

            int byteIndex = index >> 3;
            byte bit = (byte)(1 << (index & 7));
            result[byteIndex] |= bit;
            exploredCells++;
            if (previousFogBits == null || (previousFogBits[byteIndex] & bit) == 0)
            {
                newlyExploredCells++;
            }
        }

        return result;
    }

    private void EvictFrames(long nowUnixMs)
    {
        TimelapseFrameInfo[] frames;
        lock (_lock)
        {
            frames = new TimelapseFrameInfo[_index.Count];
            int outputIndex = 0;
            foreach (TimelapseIndexEntry entry in _index.Values)
            {
                frames[outputIndex] = new TimelapseFrameInfo(entry.UnixMs, entry.SizeBytes);
                outputIndex++;
            }
        }

        List<long> evictions = TimelapseRetention.SelectEvictions(frames, nowUnixMs);
        int evictedCount = 0;
        long reclaimedBytes = 0L;
        for (int index = 0; index < evictions.Count; index++)
        {
            long unixMs = evictions[index];
            TimelapseIndexEntry? entry;
            lock (_lock)
            {
                _index.TryGetValue(unixMs, out entry);
            }

            if (entry == null)
            {
                continue;
            }

            try
            {
                File.Delete(FramePath(unixMs));
                lock (_lock)
                {
                    if (_index.TryGetValue(unixMs, out TimelapseIndexEntry? current) &&
                        ReferenceEquals(current, entry))
                    {
                        _index.Remove(unixMs);
                        _totalBytes -= entry.SizeBytes;
                        evictedCount++;
                        reclaimedBytes += entry.SizeBytes;
                    }
                }
            }
            catch (Exception exception)
            {
                _log.Warning(
                    $"[LiveMap] timelapse frame {unixMs.ToString(CultureInfo.InvariantCulture)} " +
                    $"could not be evicted ({exception.GetType().Name}: " +
                    $"{SingleLineMessage(exception)}).");
            }
        }

        if (evictedCount > 0)
        {
            _log.Info(
                $"[LiveMap] timelapse retention evicted " +
                $"{evictedCount.ToString(CultureInfo.InvariantCulture)} frame(s), reclaiming " +
                $"{reclaimedBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }
    }

    private void RemoveCorruptFrame(
        string path,
        TimelapseIndexEntry entry,
        Exception exception)
    {
        _log.Warning(
            $"[LiveMap] corrupt timelapse frame {JsonWriter.Quote(Path.GetFileName(path))} " +
            $"was removed ({exception.GetType().Name}: {SingleLineMessage(exception)}).");
        DeleteCorruptFile(path);
        lock (_lock)
        {
            if (_index.TryGetValue(entry.UnixMs, out TimelapseIndexEntry? current) &&
                ReferenceEquals(current, entry))
            {
                _index.Remove(entry.UnixMs);
                _totalBytes -= entry.SizeBytes;
                if (_lastFrameUnixMs == entry.UnixMs)
                {
                    ClearPreviousStateLocked();
                }

                RecoverLastHarvestedHourLocked();
            }
        }
    }

    private void DeleteCorruptFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] corrupt timelapse frame {JsonWriter.Quote(Path.GetFileName(path))} " +
                $"could not be deleted ({exception.GetType().Name}: " +
                $"{SingleLineMessage(exception)}).");
        }
    }

    private void RecoverLastHarvestedHourLocked()
    {
        _lastHarvestedHourUnixMs = TryGetNewestUnixMsLocked(out long newestUnixMs)
            ? FloorToHour(newestUnixMs)
            : 0L;
    }

    private bool TryGetNewestUnixMsLocked(out long unixMs)
    {
        unixMs = 0L;
        bool found = false;
        foreach (long candidate in _index.Keys)
        {
            unixMs = candidate;
            found = true;
        }

        return found;
    }

    private void ClearPreviousStateLocked()
    {
        _previousFogBits = null;
        _previousFogRevision = 0L;
        _previousFogRevisionKnown = false;
        _previousExploredCells = 0;
        _previousBaseCount = 0;
        _previousTotalPieces = 0L;
        _previousPortalCount = 0;
        _previousBossMask = 0;
        _lastFrameUnixMs = 0L;
    }

    private void LogPendingDropOnce()
    {
        if (Interlocked.Exchange(ref _pendingDropLogged, 1) == 0)
        {
            _log.Debug(
                "[LiveMap] timelapse capture dropped because a frame is already pending.");
        }
    }

    private string FramePath(long unixMs)
    {
        return Path.Combine(
            _directory,
            unixMs.ToString(CultureInfo.InvariantCulture) + ".vof");
    }

    private static void WriteHeader(BinaryWriter writer, FrameHeader header)
    {
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(header.UnixMs);
        writer.Write(header.WorldDay);
        writer.Write(header.ExploredCells);
        writer.Write(header.BaseCount);
        writer.Write(header.PortalCount);
        writer.Write(header.BedCount);
        writer.Write(header.WardCount);
        writer.Write(header.BossMask);
        writer.Write(header.MovementCellCount);
        writer.Write(header.PayloadLength);
    }

    private static FrameHeader ReadHeader(BinaryReader reader)
    {
        byte[] magic = ReadBytes(reader, Magic.Length);
        for (int index = 0; index < Magic.Length; index++)
        {
            if (magic[index] != Magic[index])
            {
                throw new FormatException("The frame magic is invalid.");
            }
        }

        ushort version = reader.ReadUInt16();
        if (version != FormatVersion)
        {
            throw new FormatException("The frame version is unsupported.");
        }

        return new FrameHeader(
            reader.ReadInt64(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32());
    }

    private static void ValidateHeader(
        FrameHeader header,
        long fileLength,
        long expectedUnixMs)
    {
        if (header.UnixMs != expectedUnixMs)
        {
            throw new FormatException("The frame timestamp does not match its filename.");
        }

        if (header.WorldDay < 0 ||
            header.ExploredCells < 0 ||
            header.ExploredCells > FogTracker.Size * FogTracker.Size ||
            header.BaseCount < 0 || header.BaseCount > MaximumBases ||
            header.PortalCount < 0 || header.PortalCount > MaximumPoints ||
            header.BedCount < 0 || header.BedCount > MaximumPoints ||
            header.WardCount < 0 || header.WardCount > MaximumPoints ||
            header.MovementCellCount < 0 ||
            header.MovementCellCount > MaximumMovementCells ||
            header.PayloadLength <= 0 || header.PayloadLength > MaximumPayloadBytes)
        {
            throw new FormatException("The frame header contains invalid values.");
        }

        if (fileLength != FixedHeaderSize + (long)header.PayloadLength)
        {
            throw new FormatException("The frame length does not match its header.");
        }
    }

    private static TimelapseIndexEntry CreateIndexEntry(FrameHeader header, int sizeBytes)
    {
        return new TimelapseIndexEntry(
            header.UnixMs,
            header.WorldDay,
            header.ExploredCells,
            header.BaseCount,
            header.PortalCount,
            header.BedCount,
            header.WardCount,
            header.BossMask,
            header.MovementCellCount,
            header.PayloadLength,
            sizeBytes);
    }

    private static void WritePoints(BinaryWriter writer, TimelapsePoint[] points)
    {
        writer.Write(points.Length);
        for (int index = 0; index < points.Length; index++)
        {
            writer.Write(points[index].X);
            writer.Write(points[index].Z);
        }
    }

    private static TimelapsePoint[] ReadPoints(
        BinaryReader reader,
        int expectedCount,
        string name)
    {
        int count = ReadCount(reader, MaximumPoints, name);
        if (count != expectedCount)
        {
            throw new FormatException(
                $"The frame {name} count does not match its header.");
        }

        TimelapsePoint[] result = new TimelapsePoint[count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = new TimelapsePoint(
                ReadFiniteSingle(reader, name + " X"),
                ReadFiniteSingle(reader, name + " Z"));
        }

        return result;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length > MaximumStringBytes)
        {
            throw new InvalidOperationException("A timelapse string exceeds its size cap.");
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumStringBytes)
        {
            throw new FormatException("The frame contains an invalid string length.");
        }

        return Utf8.GetString(ReadBytes(reader, length));
    }

    private static byte[] ReadBytes(BinaryReader reader, int count)
    {
        byte[] result = reader.ReadBytes(count);
        if (result.Length != count)
        {
            throw new EndOfStreamException("The timelapse frame ended unexpectedly.");
        }

        return result;
    }

    private static int ReadCount(BinaryReader reader, int maximum, string name)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new FormatException($"The frame contains an invalid {name} count.");
        }

        return count;
    }

    private static float ReadFiniteSingle(BinaryReader reader, string name)
    {
        float value = reader.ReadSingle();
        if (!IsFinite(value))
        {
            throw new FormatException($"The frame contains an invalid {name} value.");
        }

        return value;
    }

    private static int CountExploredCells(byte[] fogBits)
    {
        int count = 0;
        for (int index = 0; index < fogBits.Length; index++)
        {
            int value = fogBits[index];
            while (value != 0)
            {
                value &= value - 1;
                count++;
            }
        }

        return count;
    }

    private static int CalculateBossMask(string[] bossKeys)
    {
        int mask = 0;
        SortedSet<string> unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int keyIndex = 0; keyIndex < bossKeys.Length; keyIndex++)
        {
            string key = bossKeys[keyIndex].Trim();
            bool known = false;
            for (int bossIndex = 0; bossIndex < KnownBossKeys.Length; bossIndex++)
            {
                if (!string.Equals(
                        key,
                        KnownBossKeys[bossIndex],
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                mask |= 1 << bossIndex;
                known = true;
                break;
            }

            if (!known && key.Length > 0)
            {
                unknown.Add(key);
            }
        }

        if (unknown.Count == 0)
        {
            return mask;
        }

        uint hash = 2166136261u;
        foreach (string key in unknown)
        {
            byte[] bytes = Utf8.GetBytes(key.ToUpperInvariant());
            for (int index = 0; index < bytes.Length; index++)
            {
                hash ^= bytes[index];
                hash *= 16777619u;
            }

            hash ^= 0xffu;
            hash *= 16777619u;
        }

        return mask | (int)((hash & 0x01ffffffu) << KnownBossKeys.Length);
    }

    private static long TotalPieces(PlayerBaseEntry[] bases)
    {
        long total = 0L;
        for (int index = 0; index < bases.Length; index++)
        {
            total = SaturatingAdd(total, bases[index].Pieces);
        }

        return total;
    }

    private static long AbsoluteDifference(long left, long right)
    {
        if (left >= right)
        {
            return left - right;
        }

        return right - left;
    }

    private static long SaturatingAdd(long left, long right)
    {
        return right > 0L && left > long.MaxValue - right
            ? long.MaxValue
            : left + right;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static long FloorToHour(long unixMs)
    {
        long remainder = unixMs % HourMilliseconds;
        return unixMs - (remainder < 0L ? remainder + HourMilliseconds : remainder);
    }

    private static string SingleLineMessage(Exception exception)
    {
        return (exception.Message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private readonly struct FrameHeader
    {
        public FrameHeader(
            long unixMs,
            int worldDay,
            int exploredCells,
            int baseCount,
            int portalCount,
            int bedCount,
            int wardCount,
            int bossMask,
            int movementCellCount,
            int payloadLength)
        {
            UnixMs = unixMs;
            WorldDay = worldDay;
            ExploredCells = exploredCells;
            BaseCount = baseCount;
            PortalCount = portalCount;
            BedCount = bedCount;
            WardCount = wardCount;
            BossMask = bossMask;
            MovementCellCount = movementCellCount;
            PayloadLength = payloadLength;
        }

        public long UnixMs { get; }

        public int WorldDay { get; }

        public int ExploredCells { get; }

        public int BaseCount { get; }

        public int PortalCount { get; }

        public int BedCount { get; }

        public int WardCount { get; }

        public int BossMask { get; }

        public int MovementCellCount { get; }

        public int PayloadLength { get; }
    }

    private readonly struct PreviousState
    {
        public PreviousState(
            byte[]? fogBits,
            long fogRevision,
            bool fogRevisionKnown,
            int exploredCells,
            int baseCount,
            long totalPieces,
            int portalCount,
            int bossMask,
            long lastFrameUnixMs)
        {
            FogBits = fogBits;
            FogRevision = fogRevision;
            FogRevisionKnown = fogRevisionKnown;
            ExploredCells = exploredCells;
            BaseCount = baseCount;
            TotalPieces = totalPieces;
            PortalCount = portalCount;
            BossMask = bossMask;
            LastFrameUnixMs = lastFrameUnixMs;
        }

        public byte[]? FogBits { get; }

        public long FogRevision { get; }

        public bool FogRevisionKnown { get; }

        public int ExploredCells { get; }

        public int BaseCount { get; }

        public long TotalPieces { get; }

        public int PortalCount { get; }

        public int BossMask { get; }

        public long LastFrameUnixMs { get; }
    }

    private sealed class PendingCapture
    {
        public PendingCapture(
            long unixMs,
            long fogRevision,
            byte[] fogBits,
            int exploredCells,
            PlayerBaseEntry[] bases,
            long totalPieces,
            TimelapsePoint[] portals,
            TimelapsePoint[] beds,
            TimelapsePoint[] wards,
            int worldDay,
            string season,
            string[] bossKeys,
            int bossMask,
            TimelapseMovementCell[] movementCells,
            long lastHarvestedHourUnixMs,
            int changeScore)
        {
            UnixMs = unixMs;
            FogRevision = fogRevision;
            FogBits = fogBits;
            ExploredCells = exploredCells;
            Bases = bases;
            TotalPieces = totalPieces;
            Portals = portals;
            Beds = beds;
            Wards = wards;
            WorldDay = worldDay;
            Season = season;
            BossKeys = bossKeys;
            BossMask = bossMask;
            MovementCells = movementCells;
            LastHarvestedHourUnixMs = lastHarvestedHourUnixMs;
            ChangeScore = changeScore;
        }

        public long UnixMs { get; }

        public long FogRevision { get; }

        public byte[] FogBits { get; }

        public int ExploredCells { get; }

        public PlayerBaseEntry[] Bases { get; }

        public long TotalPieces { get; }

        public TimelapsePoint[] Portals { get; }

        public TimelapsePoint[] Beds { get; }

        public TimelapsePoint[] Wards { get; }

        public int WorldDay { get; }

        public string Season { get; }

        public string[] BossKeys { get; }

        public int BossMask { get; }

        public TimelapseMovementCell[] MovementCells { get; }

        public long LastHarvestedHourUnixMs { get; }

        public int ChangeScore { get; }
    }
}

internal sealed class TimelapseCaptureInput
{
    public TimelapseCaptureInput(
        long unixMs,
        byte[] fogMask,
        long fogRevision,
        PlayerBaseEntry[] bases,
        TimelapsePoint[] portals,
        TimelapsePoint[] beds,
        TimelapsePoint[] wards,
        int worldDay,
        string season,
        string[] bossKeys,
        ActivityHeatmapHourSlice[] movementSlices,
        int captureIntervalMinutes)
    {
        UnixMs = unixMs;
        FogMask = fogMask;
        FogRevision = fogRevision;
        Bases = bases;
        Portals = portals;
        Beds = beds;
        Wards = wards;
        WorldDay = worldDay;
        Season = season;
        BossKeys = bossKeys;
        MovementSlices = movementSlices;
        CaptureIntervalMinutes = captureIntervalMinutes;
    }

    public long UnixMs { get; }

    public byte[] FogMask { get; }

    public long FogRevision { get; }

    public PlayerBaseEntry[] Bases { get; }

    public TimelapsePoint[] Portals { get; }

    public TimelapsePoint[] Beds { get; }

    public TimelapsePoint[] Wards { get; }

    public int WorldDay { get; }

    public string Season { get; }

    public string[] BossKeys { get; }

    public ActivityHeatmapHourSlice[] MovementSlices { get; }

    public int CaptureIntervalMinutes { get; }
}

internal readonly struct TimelapsePoint
{
    public TimelapsePoint(float x, float z)
    {
        X = x;
        Z = z;
    }

    public float X { get; }

    public float Z { get; }
}

internal readonly struct TimelapseMovementCell
{
    public TimelapseMovementCell(int index, int count)
    {
        Index = index;
        Count = count;
    }

    public int Index { get; }

    public int Count { get; }
}

internal sealed class TimelapseIndexEntry
{
    public TimelapseIndexEntry(
        long unixMs,
        int worldDay,
        int exploredCells,
        int baseCount,
        int portalCount,
        int bedCount,
        int wardCount,
        int bossMask,
        int movementCellCount,
        int payloadLength,
        int sizeBytes)
    {
        UnixMs = unixMs;
        WorldDay = worldDay;
        ExploredCells = exploredCells;
        BaseCount = baseCount;
        PortalCount = portalCount;
        BedCount = bedCount;
        WardCount = wardCount;
        BossMask = bossMask;
        MovementCellCount = movementCellCount;
        PayloadLength = payloadLength;
        SizeBytes = sizeBytes;
    }

    public long UnixMs { get; }

    public int WorldDay { get; }

    public int ExploredCells { get; }

    public int BaseCount { get; }

    public int PortalCount { get; }

    public int BedCount { get; }

    public int WardCount { get; }

    public int BossMask { get; }

    public int MovementCellCount { get; }

    public int PayloadLength { get; }

    public int SizeBytes { get; }

    public double ExploredPercent =>
        (ExploredCells * 100.0) / (FogTracker.Size * FogTracker.Size);
}

internal sealed class TimelapseFrame
{
    public TimelapseFrame(
        TimelapseIndexEntry entry,
        byte[] fogBits,
        PlayerBaseEntry[] bases,
        TimelapsePoint[] portals,
        TimelapsePoint[] beds,
        TimelapsePoint[] wards,
        TimelapseMovementCell[] movementCells,
        string[] bossKeys,
        string season)
    {
        Entry = entry;
        FogBits = fogBits;
        Bases = bases;
        Portals = portals;
        Beds = beds;
        Wards = wards;
        MovementCells = movementCells;
        BossKeys = bossKeys;
        Season = season;
    }

    public TimelapseIndexEntry Entry { get; }

    public long UnixMs => Entry.UnixMs;

    public int WorldDay => Entry.WorldDay;

    public int ExploredCells => Entry.ExploredCells;

    public int BaseCount => Entry.BaseCount;

    public int PortalCount => Entry.PortalCount;

    public int BedCount => Entry.BedCount;

    public int WardCount => Entry.WardCount;

    public int BossMask => Entry.BossMask;

    public int MovementCellCount => Entry.MovementCellCount;

    public int PayloadLength => Entry.PayloadLength;

    public int SizeBytes => Entry.SizeBytes;

    public double ExploredPercent => Entry.ExploredPercent;

    public byte[] FogBits { get; }

    public PlayerBaseEntry[] Bases { get; }

    public TimelapsePoint[] Portals { get; }

    public TimelapsePoint[] Beds { get; }

    public TimelapsePoint[] Wards { get; }

    public TimelapseMovementCell[] MovementCells { get; }

    public string[] BossKeys { get; }

    public string Season { get; }
}

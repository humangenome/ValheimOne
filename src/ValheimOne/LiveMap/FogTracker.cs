using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class FogTracker
{
    public const int Size = 512;
    public const float WorldSpan = 24576f;
    public const float MetersPerPixel = WorldSpan / Size;
    public const float RevealRadius = 100f;

    private const int Version = 1;
    private const int SaveIntervalSeconds = 30;
    private const int PublicationIntervalSeconds = 10;
    private const int ShutdownSaveWaitMilliseconds = 2000;
    private const int ShutdownSavePollMilliseconds = 10;
    private const int CellCount = Size * Size;

    private readonly byte[] _mask = new byte[CellCount];
    private readonly string _bitsPath;
    private readonly string _metadataPath;
    private readonly ModLogger _log;
    private readonly object _saveLock = new object();
    private FogMaskSnapshot _snapshot = FogMaskSnapshot.Empty;
    private DateTime _nextSaveUtc;
    private long _lastPublicationTimestamp;
    private long _revision;
    private int _dirty;
    private int _finalSaveStarted;
    private int _saveInFlight;
    private bool _publicationPending;
    private bool _stopped;

    public FogTracker(string cacheDirectory, ModLogger log)
    {
        _log = log;
        string fogDirectory = Path.Combine(cacheDirectory, "fog");
        _bitsPath = Path.Combine(fogDirectory, "trails.bits");
        _metadataPath = Path.Combine(fogDirectory, "trails.meta.json");

        Directory.CreateDirectory(fogDirectory);
        Load();
        _nextSaveUtc = DateTime.UtcNow.AddSeconds(SaveIntervalSeconds);
    }

    public FogMaskSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Tick(IReadOnlyList<LiveMapPlayerSnapshot> players)
    {
        if (_stopped)
        {
            return;
        }

        bool changed = false;
        for (int index = 0; index < players.Count; index++)
        {
            LiveMapPlayerSnapshot player = players[index];
            changed |= Stamp(player.X, player.Z);
        }

        DateTime now = DateTime.UtcNow;
        if (changed)
        {
            MarkChanged();
        }

        PublishPending(false);
        if (now >= _nextSaveUtc)
        {
            if (Volatile.Read(ref _dirty) != 0)
            {
                QueueSave();
            }

            _nextSaveUtc = now.AddSeconds(SaveIntervalSeconds);
        }
    }

    public void OrExternalMask(byte[] externalMask)
    {
        if (_stopped)
        {
            return;
        }

        if (externalMask.Length != CellCount)
        {
            _log.Warning(
                $"[LiveMap] ignored external fog mask with invalid size {externalMask.Length}; " +
                $"expected {CellCount} bytes.");
            return;
        }

        bool changed = false;
        for (int index = 0; index < externalMask.Length; index++)
        {
            if (externalMask[index] != 0 && _mask[index] == 0)
            {
                _mask[index] = byte.MaxValue;
                changed = true;
            }
        }

        if (changed)
        {
            MarkChanged();
            PublishPending(false);
        }
    }

    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        PublishPending(true);
        int remainingWaitMilliseconds = ShutdownSaveWaitMilliseconds;
        while (Volatile.Read(ref _saveInFlight) != 0 && remainingWaitMilliseconds > 0)
        {
            Thread.Sleep(ShutdownSavePollMilliseconds);
            remainingWaitMilliseconds -= ShutdownSavePollMilliseconds;
        }

        Interlocked.Exchange(ref _finalSaveStarted, 1);
        Save();
    }

    private void MarkChanged()
    {
        Interlocked.Exchange(ref _dirty, 1);
        _publicationPending = true;
    }

    private void PublishPending(bool force)
    {
        long now = Stopwatch.GetTimestamp();
        if (!_publicationPending ||
            (!force && _lastPublicationTimestamp != 0 &&
             now - _lastPublicationTimestamp < Stopwatch.Frequency * PublicationIntervalSeconds))
        {
            return;
        }

        _revision++;
        _publicationPending = false;
        _lastPublicationTimestamp = now;
        Volatile.Write(ref _snapshot, new FogMaskSnapshot((byte[])_mask.Clone(), _revision));
    }

    private bool Stamp(float worldX, float worldZ)
    {
        if (float.IsNaN(worldX) || float.IsInfinity(worldX) ||
            float.IsNaN(worldZ) || float.IsInfinity(worldZ))
        {
            return false;
        }

        const float halfWorld = WorldSpan / 2f;
        const float radiusSquared = RevealRadius * RevealRadius;
        int minimumX = ClampCell((int)Math.Floor((worldX - RevealRadius + halfWorld) / MetersPerPixel));
        int maximumX = ClampCell((int)Math.Floor((worldX + RevealRadius + halfWorld) / MetersPerPixel));
        int minimumY = ClampCell((int)Math.Floor((halfWorld - worldZ - RevealRadius) / MetersPerPixel));
        int maximumY = ClampCell((int)Math.Floor((halfWorld - worldZ + RevealRadius) / MetersPerPixel));
        if (worldX + RevealRadius < -halfWorld || worldX - RevealRadius >= halfWorld ||
            worldZ + RevealRadius < -halfWorld || worldZ - RevealRadius >= halfWorld)
        {
            return false;
        }

        bool changed = false;
        for (int y = minimumY; y <= maximumY; y++)
        {
            float cellZ = halfWorld - ((y + 0.5f) * MetersPerPixel);
            float deltaZ = cellZ - worldZ;
            for (int x = minimumX; x <= maximumX; x++)
            {
                float cellX = -halfWorld + ((x + 0.5f) * MetersPerPixel);
                float deltaX = cellX - worldX;
                if ((deltaX * deltaX) + (deltaZ * deltaZ) > radiusSquared)
                {
                    continue;
                }

                int cell = (y * Size) + x;
                if (_mask[cell] == 0)
                {
                    _mask[cell] = byte.MaxValue;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private void Load()
    {
        if (!File.Exists(_bitsPath))
        {
            Volatile.Write(ref _snapshot, new FogMaskSnapshot((byte[])_mask.Clone(), 0));
            return;
        }

        try
        {
            byte[] storedMask = File.ReadAllBytes(_bitsPath);
            if (storedMask.Length != CellCount)
            {
                _log.Warning(
                    $"[LiveMap] discarded trails fog cache with invalid size {storedMask.Length}; " +
                    $"expected {CellCount} bytes.");
                TryDelete(_bitsPath);
                TryDelete(_metadataPath);
                Volatile.Write(ref _snapshot, new FogMaskSnapshot((byte[])_mask.Clone(), 0));
                return;
            }

            for (int index = 0; index < storedMask.Length; index++)
            {
                _mask[index] = storedMask[index] == 0 ? (byte)0 : byte.MaxValue;
            }

            _revision = ReadStoredRevision();
            Volatile.Write(
                ref _snapshot,
                new FogMaskSnapshot((byte[])_mask.Clone(), _revision));
            _log.Info($"[LiveMap] loaded trails fog mask at revision {_revision}.");
        }
        catch (Exception exception)
        {
            Array.Clear(_mask, 0, _mask.Length);
            _revision = 0;
            Volatile.Write(ref _snapshot, new FogMaskSnapshot((byte[])_mask.Clone(), 0));
            _log.Warning(
                $"[LiveMap] could not load trails fog cache; starting empty: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private long ReadStoredRevision()
    {
        if (!File.Exists(_metadataPath))
        {
            return 0;
        }

        string metadata = File.ReadAllText(_metadataPath);
        return TryReadNonNegativeInteger(metadata, "revision", out long revision)
            ? revision
            : 0;
    }

    private void QueueSave()
    {
        if (Interlocked.CompareExchange(ref _saveInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            byte[] mask = (byte[])_mask.Clone();
            long revision = CaptureSaveRevision();
            Interlocked.Exchange(ref _dirty, 0);
            var save = new PendingSave(mask, revision);
            if (!ThreadPool.QueueUserWorkItem(SaveInBackground, save))
            {
                Interlocked.Exchange(ref _dirty, 1);
                Interlocked.Exchange(ref _saveInFlight, 0);
                _log.Warning("[LiveMap] could not queue trails fog cache save.");
            }
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _dirty, 1);
            Interlocked.Exchange(ref _saveInFlight, 0);
            _log.Warning(
                $"[LiveMap] could not queue trails fog cache save: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void SaveInBackground(object? state)
    {
        try
        {
            var save = state as PendingSave;
            if ((save == null || !WriteSave(save.Mask, save.Revision, true)) &&
                Volatile.Read(ref _finalSaveStarted) == 0)
            {
                Interlocked.Exchange(ref _dirty, 1);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _saveInFlight, 0);
        }
    }

    private void Save()
    {
        if (WriteSave(_mask, _revision, false))
        {
            Interlocked.Exchange(ref _dirty, 0);
        }
        else
        {
            Interlocked.Exchange(ref _dirty, 1);
        }
    }

    private long CaptureSaveRevision()
    {
        // A live mask with unpublished changes must not reuse the revision of
        // the older snapshot already visible to HTTP clients.
        if (_publicationPending)
        {
            _revision++;
        }

        return _revision;
    }

    private bool WriteSave(byte[] mask, long revision, bool background)
    {
        string temporarySuffix = background ? ".background.tmp" : ".final.tmp";
        string bitsTemporaryPath = _bitsPath + temporarySuffix;
        string metadataTemporaryPath = _metadataPath + temporarySuffix;
        try
        {
            File.WriteAllBytes(bitsTemporaryPath, mask);

            var metadata = new StringBuilder(64);
            metadata.Append('{');
            metadata.Append("\"version\":").Append(Version);
            metadata.Append(",\"size\":").Append(Size);
            metadata.Append(",\"revision\":").Append(revision.ToString(CultureInfo.InvariantCulture));
            metadata.Append('}');
            File.WriteAllText(
                metadataTemporaryPath,
                metadata.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            lock (_saveLock)
            {
                if (background && Volatile.Read(ref _finalSaveStarted) != 0)
                {
                    return true;
                }

                ReplaceAtomically(bitsTemporaryPath, _bitsPath);
                ReplaceAtomically(metadataTemporaryPath, _metadataPath);
            }

            return true;
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] could not save trails fog cache: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            TryDelete(bitsTemporaryPath);
            TryDelete(metadataTemporaryPath);
        }
    }

    private static bool TryReadNonNegativeInteger(string json, string name, out long value)
    {
        value = 0;
        string property = "\"" + name + "\"";
        int propertyIndex = json.IndexOf(property, StringComparison.Ordinal);
        if (propertyIndex < 0)
        {
            return false;
        }

        int colonIndex = json.IndexOf(':', propertyIndex + property.Length);
        if (colonIndex < 0)
        {
            return false;
        }

        int start = colonIndex + 1;
        while (start < json.Length && char.IsWhiteSpace(json[start]))
        {
            start++;
        }

        int end = start;
        while (end < json.Length && json[end] >= '0' && json[end] <= '9')
        {
            end++;
        }

        return end > start &&
               long.TryParse(
                   json.Substring(start, end - start),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static void ReplaceAtomically(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, null);
        }
        else
        {
            File.Move(temporaryPath, destinationPath);
        }
    }

    private static int ClampCell(int value)
    {
        return Math.Max(0, Math.Min(Size - 1, value));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cache cleanup is best effort.
        }
    }

    private sealed class PendingSave
    {
        public PendingSave(byte[] mask, long revision)
        {
            Mask = mask;
            Revision = revision;
        }

        public byte[] Mask { get; }

        public long Revision { get; }
    }
}

internal sealed class FogMaskSnapshot
{
    public static readonly FogMaskSnapshot Empty = new FogMaskSnapshot(new byte[FogTracker.Size * FogTracker.Size], 0);

    public FogMaskSnapshot(byte[] mask, long revision)
    {
        Mask = mask;
        Revision = revision;
    }

    public byte[] Mask { get; }

    public long Revision { get; }
}

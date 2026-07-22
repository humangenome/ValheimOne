using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class PlayerBaseTracker
{
    // Base surveys remain valid for ten minutes and only stale layer requests re-arm them.
    public const long CacheMilliseconds = 10L * 60L * 1000L;
    public const int MaximumPiecePositions = 200000;
    public const int MaximumBases = 200;

    // Bound the main-thread sector walk independently of world size.
    private const int MaximumZdosPerFrame = 2048;
    private const int MaximumSectorsPerFrame = 256;
    // Pieces no farther apart than this become part of the same connected component.
    private const float LinkDistance = 32f;
    // A cluster smaller than this is more likely scattered construction than a base.
    private const int MinimumPiecesPerBase = 25;
    // Rendering bounds keep tiny camps visible and pathological settlements manageable.
    private const float MinimumRenderRadius = 15f;
    private const float MaximumRenderRadius = 150f;
    // Half-link cells are internally connected, reducing dense-cell neighbor checks.
    private const float ClusterCellSize = LinkDistance / 2f;

    private readonly ModLogger _log;
    private readonly List<ZDO> _scanBatch = new List<ZDO>(MaximumZdosPerFrame);
    private readonly List<BasePiecePosition> _piecePositions =
        new List<BasePiecePosition>();
    private readonly ManualResetEventSlim _workerDone = new ManualResetEventSlim(true);
    private volatile PlayerBaseMapSnapshot _snapshot = PlayerBaseMapSnapshot.Empty;
    private long _lastRequestUnixMs;
    private long _lastServicedRequestUnixMs;
    private long _scanStartedUnixMs;
    private int _sectorIndex;
    private int _objectIndex;
    private int _workerRunning;
    private bool _scanning;
    private bool _piecesTruncated;
    private bool _scanWarningLogged;
    private volatile bool _stopped;

    public PlayerBaseTracker(ModLogger log)
    {
        _log = log;
    }

    public PlayerBaseMapSnapshot Snapshot => _snapshot;

    public void NoteBasesRequested()
    {
        Interlocked.Exchange(
            ref _lastRequestUnixMs,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Tick()
    {
        if (_stopped)
        {
            return;
        }

        if (_scanning)
        {
            ContinueScan();
            return;
        }

        if (Volatile.Read(ref _workerRunning) != 0)
        {
            return;
        }

        long requestedUnixMs = Interlocked.Read(ref _lastRequestUnixMs);
        if (requestedUnixMs == 0L || requestedUnixMs <= _lastServicedRequestUnixMs)
        {
            return;
        }

        PlayerBaseMapSnapshot snapshot = Snapshot;
        long refreshAfterUnixMs = snapshot.LastScanUnixMs == 0L
            ? 0L
            : snapshot.LastScanUnixMs + CacheMilliseconds;
        if (requestedUnixMs < refreshAfterUnixMs)
        {
            return;
        }

        StartScan(requestedUnixMs);
        ContinueScan();
    }

    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _scanning = false;
        _scanBatch.Clear();
        _piecePositions.Clear();
        _workerDone.Wait();
    }

    private void StartScan(long requestedUnixMs)
    {
        _scanBatch.Clear();
        _piecePositions.Clear();
        _sectorIndex = 0;
        _objectIndex = 0;
        _lastServicedRequestUnixMs = requestedUnixMs;
        _scanStartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _piecesTruncated = false;
        _scanWarningLogged = false;
        _scanning = true;
        _snapshot = _snapshot.WithScanState(true, 0, -1);
    }

    private void ContinueScan()
    {
        ZDOMan? manager = ZDOMan.instance;
        if (manager == null)
        {
            FinishFailedScan("ZDO manager unavailable");
            return;
        }

        _scanBatch.Clear();
        bool copied = GameAccess.TryCopyZdoBatch(
            manager,
            _scanBatch,
            ref _sectorIndex,
            ref _objectIndex,
            MaximumZdosPerFrame,
            MaximumSectorsPerFrame,
            out int sectorCount,
            out bool complete);
        if (!copied)
        {
            FinishFailedScan("could not read the ZDO sector table");
            return;
        }

        try
        {
            for (int index = 0; index < _scanBatch.Count; index++)
            {
                ZDO zdo = _scanBatch[index];
                if (zdo.GetLong(ZDOVars.s_creator, 0L) == 0L)
                {
                    continue;
                }

                if (_piecePositions.Count >= MaximumPiecePositions)
                {
                    _piecesTruncated = true;
                    complete = true;
                    break;
                }

                Vector3 position = zdo.GetPosition();
                _piecePositions.Add(new BasePiecePosition(position.x, position.z));
            }
        }
        catch (Exception exception)
        {
            FinishFailedScan(
                $"piece data read failed: {exception.GetType().Name}: {exception.Message}");
            return;
        }

        if (!complete)
        {
            PublishScanProgress(sectorCount);
            return;
        }

        var positions = _piecePositions.ToArray();
        bool piecesTruncated = _piecesTruncated;
        _scanBatch.Clear();
        _piecePositions.Clear();
        _scanning = false;
        _snapshot = _snapshot.WithScanState(true, 99, 0);
        QueueClustering(new BaseScanBatch(positions, piecesTruncated));
    }

    private void PublishScanProgress(int sectorCount)
    {
        if (!_scanning || sectorCount <= 0)
        {
            _snapshot = _snapshot.WithScanState(true, -1, -1);
            return;
        }

        int completedSectors = Math.Min(sectorCount, Math.Max(0, _sectorIndex));
        int progress = Math.Min(98, completedSectors * 100 / sectorCount);
        int etaSeconds = -1;
        long elapsedMilliseconds =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _scanStartedUnixMs;
        if (completedSectors > 0 && elapsedMilliseconds > 0L)
        {
            double remainingSeconds = elapsedMilliseconds / 1000d *
                                      (sectorCount - completedSectors) /
                                      completedSectors;
            etaSeconds = (int)Math.Min(
                int.MaxValue,
                Math.Ceiling(Math.Max(0d, remainingSeconds) / 30d) * 30d);
        }

        _snapshot = _snapshot.WithScanState(true, progress, etaSeconds);
    }

    private void QueueClustering(BaseScanBatch batch)
    {
        if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) != 0)
        {
            FinishFailedScan("clustering worker was already active");
            return;
        }

        _workerDone.Reset();
        try
        {
            if (!ThreadPool.QueueUserWorkItem(ProcessBatch, batch))
            {
                Interlocked.Exchange(ref _workerRunning, 0);
                _workerDone.Set();
                FinishFailedScan("could not queue clustering");
            }
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _workerRunning, 0);
            _workerDone.Set();
            FinishFailedScan(
                $"could not queue clustering: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void ProcessBatch(object? state)
    {
        try
        {
            var batch = state as BaseScanBatch;
            if (batch == null || _stopped)
            {
                return;
            }

            PlayerBaseMapSnapshot snapshot = ClusterPieces(batch);
            if (!_stopped)
            {
                _snapshot = snapshot;
                _log.Info(
                    $"[LiveMap] base survey: {batch.Positions.Length} player pieces, " +
                    $"{snapshot.Count} bases" +
                    (snapshot.PiecesTruncated || snapshot.OutputTruncated
                        ? " (truncated)"
                        : string.Empty));
            }
        }
        catch (Exception exception)
        {
            if (!_stopped)
            {
                _snapshot = _snapshot.WithScanning(false);
                _log.Warning(
                    $"[LiveMap] base clustering failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _workerRunning, 0);
            _workerDone.Set();
        }
    }

    private PlayerBaseMapSnapshot ClusterPieces(BaseScanBatch batch)
    {
        BasePiecePosition[] positions = batch.Positions;
        int count = positions.Length;
        var parent = new int[count];
        var rank = new byte[count];
        var nextInCell = new int[count];
        var cellHeads = new Dictionary<long, int>();
        float linkDistanceSquared = LinkDistance * LinkDistance;
        int neighborCells = (int)Math.Ceiling(LinkDistance / ClusterCellSize);

        for (int index = 0; index < count; index++)
        {
            if (_stopped)
            {
                return PlayerBaseMapSnapshot.Empty;
            }

            parent[index] = index;
            nextInCell[index] = -1;
            BasePiecePosition position = positions[index];
            int cellX = (int)Math.Floor(position.X / ClusterCellSize);
            int cellZ = (int)Math.Floor(position.Z / ClusterCellSize);
            long ownKey = CellKey(cellX, cellZ);
            for (int offsetX = -neighborCells; offsetX <= neighborCells; offsetX++)
            {
                for (int offsetZ = -neighborCells; offsetZ <= neighborCells; offsetZ++)
                {
                    long neighborKey = CellKey(cellX + offsetX, cellZ + offsetZ);
                    if (!cellHeads.TryGetValue(neighborKey, out int candidate))
                    {
                        continue;
                    }

                    if (neighborKey == ownKey)
                    {
                        Union(parent, rank, index, candidate);
                        continue;
                    }

                    while (candidate >= 0)
                    {
                        BasePiecePosition other = positions[candidate];
                        float deltaX = position.X - other.X;
                        float deltaZ = position.Z - other.Z;
                        if ((deltaX * deltaX) + (deltaZ * deltaZ) <=
                            linkDistanceSquared)
                        {
                            Union(parent, rank, index, candidate);
                            break;
                        }

                        candidate = nextInCell[candidate];
                    }
                }
            }

            if (cellHeads.TryGetValue(ownKey, out int head))
            {
                nextInCell[index] = head;
                cellHeads[ownKey] = index;
            }
            else
            {
                cellHeads.Add(ownKey, index);
            }
        }

        var pieceCounts = new int[count];
        var sumX = new double[count];
        var sumZ = new double[count];
        for (int index = 0; index < count; index++)
        {
            int root = Find(parent, index);
            parent[index] = root;
            pieceCounts[root]++;
            sumX[root] += positions[index].X;
            sumZ[root] += positions[index].Z;
        }

        var candidates = new List<BaseCandidate>();
        var candidateByRoot = new Dictionary<int, int>();
        for (int index = 0; index < count; index++)
        {
            if (pieceCounts[index] < MinimumPiecesPerBase)
            {
                continue;
            }

            var candidate = new BaseCandidate(
                index,
                pieceCounts[index],
                (float)(sumX[index] / pieceCounts[index]),
                (float)(sumZ[index] / pieceCounts[index]));
            candidateByRoot.Add(index, candidates.Count);
            candidates.Add(candidate);
        }

        for (int index = 0; index < count; index++)
        {
            if (!candidateByRoot.TryGetValue(parent[index], out int candidateIndex))
            {
                continue;
            }

            BaseCandidate candidate = candidates[candidateIndex];
            float deltaX = positions[index].X - candidate.X;
            float deltaZ = positions[index].Z - candidate.Z;
            candidate.RadiusSquared = Math.Max(
                candidate.RadiusSquared,
                (deltaX * deltaX) + (deltaZ * deltaZ));
        }

        candidates.Sort(BaseCandidateComparer.Instance);
        int outputCount = Math.Min(MaximumBases, candidates.Count);
        var bases = new PlayerBaseEntry[outputCount];
        for (int index = 0; index < outputCount; index++)
        {
            BaseCandidate candidate = candidates[index];
            float radius = Math.Min(
                MaximumRenderRadius,
                Math.Max(MinimumRenderRadius, (float)Math.Sqrt(candidate.RadiusSquared)));
            bases[index] = new PlayerBaseEntry(
                $"base-{index + 1}",
                candidate.X,
                candidate.Z,
                radius,
                candidate.Pieces);
        }

        return new PlayerBaseMapSnapshot(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            false,
            100,
            0,
            bases,
            candidates.Count,
            batch.PiecesTruncated,
            candidates.Count > MaximumBases);
    }

    private void FinishFailedScan(string message)
    {
        _scanBatch.Clear();
        _piecePositions.Clear();
        _scanning = false;
        _snapshot = _snapshot.WithScanning(false);
        if (_scanWarningLogged || _stopped)
        {
            return;
        }

        _scanWarningLogged = true;
        _log.Warning($"[LiveMap] base ZDO scan failed: {message}");
    }

    private static long CellKey(int x, int z)
    {
        return ((long)x << 32) ^ (uint)z;
    }

    private static int Find(int[] parent, int index)
    {
        int root = index;
        while (parent[root] != root)
        {
            root = parent[root];
        }

        while (parent[index] != index)
        {
            int next = parent[index];
            parent[index] = root;
            index = next;
        }

        return root;
    }

    private static void Union(int[] parent, byte[] rank, int left, int right)
    {
        int leftRoot = Find(parent, left);
        int rightRoot = Find(parent, right);
        if (leftRoot == rightRoot)
        {
            return;
        }

        if (rank[leftRoot] < rank[rightRoot])
        {
            parent[leftRoot] = rightRoot;
        }
        else if (rank[leftRoot] > rank[rightRoot])
        {
            parent[rightRoot] = leftRoot;
        }
        else
        {
            parent[rightRoot] = leftRoot;
            rank[leftRoot]++;
        }
    }

    private readonly struct BasePiecePosition
    {
        public BasePiecePosition(float x, float z)
        {
            X = x;
            Z = z;
        }

        public float X { get; }

        public float Z { get; }
    }

    private sealed class BaseScanBatch
    {
        public BaseScanBatch(BasePiecePosition[] positions, bool piecesTruncated)
        {
            Positions = positions;
            PiecesTruncated = piecesTruncated;
        }

        public BasePiecePosition[] Positions { get; }

        public bool PiecesTruncated { get; }
    }

    private sealed class BaseCandidate
    {
        public BaseCandidate(int root, int pieces, float x, float z)
        {
            Root = root;
            Pieces = pieces;
            X = x;
            Z = z;
        }

        public int Root { get; }

        public int Pieces { get; }

        public float X { get; }

        public float Z { get; }

        public float RadiusSquared { get; set; }
    }

    private sealed class BaseCandidateComparer : IComparer<BaseCandidate>
    {
        public static readonly BaseCandidateComparer Instance = new BaseCandidateComparer();

        public int Compare(BaseCandidate? left, BaseCandidate? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left == null)
            {
                return 1;
            }
            if (right == null)
            {
                return -1;
            }

            int pieces = right.Pieces.CompareTo(left.Pieces);
            if (pieces != 0)
            {
                return pieces;
            }

            int x = left.X.CompareTo(right.X);
            return x != 0 ? x : left.Z.CompareTo(right.Z);
        }
    }
}

internal sealed class PlayerBaseMapSnapshot
{
    public static readonly PlayerBaseMapSnapshot Empty = new PlayerBaseMapSnapshot(
        0L,
        false,
        0,
        -1,
        Array.Empty<PlayerBaseEntry>(),
        0,
        false,
        false);

    public PlayerBaseMapSnapshot(
        long lastScanUnixMs,
        bool scanning,
        int scanProgress,
        int scanEtaSeconds,
        PlayerBaseEntry[] bases,
        int count,
        bool piecesTruncated,
        bool outputTruncated)
    {
        LastScanUnixMs = lastScanUnixMs;
        Scanning = scanning;
        ScanProgress = scanProgress;
        ScanEtaSeconds = scanEtaSeconds;
        Bases = bases;
        Count = count;
        PiecesTruncated = piecesTruncated;
        OutputTruncated = outputTruncated;
    }

    public long LastScanUnixMs { get; }

    public bool Scanning { get; }

    public int ScanProgress { get; }

    public int ScanEtaSeconds { get; }

    public PlayerBaseEntry[] Bases { get; }

    public int Count { get; }

    public bool PiecesTruncated { get; }

    public bool OutputTruncated { get; }

    public PlayerBaseMapSnapshot WithScanning(bool scanning)
    {
        return WithScanState(scanning, ScanProgress, ScanEtaSeconds);
    }

    public PlayerBaseMapSnapshot WithScanState(
        bool scanning,
        int scanProgress,
        int scanEtaSeconds)
    {
        return Scanning == scanning &&
               ScanProgress == scanProgress &&
               ScanEtaSeconds == scanEtaSeconds
            ? this
            : new PlayerBaseMapSnapshot(
                LastScanUnixMs,
                scanning,
                scanProgress,
                scanEtaSeconds,
                Bases,
                Count,
                PiecesTruncated,
                OutputTruncated);
    }
}

internal sealed class PlayerBaseEntry
{
    public PlayerBaseEntry(string id, float x, float z, float radius, int pieces)
    {
        Id = id;
        X = x;
        Z = z;
        Radius = radius;
        Pieces = pieces;
    }

    public string Id { get; }

    public float X { get; }

    public float Z { get; }

    public float Radius { get; }

    public int Pieces { get; }
}

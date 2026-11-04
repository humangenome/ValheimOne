using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal enum ObjectCensusRequestResult
{
    Started,
    Running,
    Cached,
    Busy,
}

// On demand, for the admin view: which object kinds fill the world and which players built the
// most (GET and POST /api/stats/objects). One pass over every object through ZdoWorldWalker, so a
// world of millions of objects takes more frames, never a longer frame. The counts are a census
// over a few seconds: an object that moves or appears during it may be missed or counted twice.
internal sealed class ObjectCensus
{
    public const int TopCount = 20;
    public const long CacheMilliseconds = 5L * 60L * 1000L;

    internal static double BudgetMilliseconds = ZdoWorldWalker.DefaultBudgetMilliseconds;

    // Objects that carry a player's name next to that player's id: beds and tombstones as owner
    // (owner + ownerName), wards as builder (creator + creatorName).
    private static readonly HashSet<int> OwnerNamePrefabs = new HashSet<int>
    {
        "bed".GetStableHashCode(),
        "piece_bed02".GetStableHashCode(),
        "ashwood_bed".GetStableHashCode(),
        "Player_tombstone".GetStableHashCode(),
    };

    private static readonly int WardPrefab = "guard_stone".GetStableHashCode();

    private readonly ModLogger _log;
    private readonly Func<bool> _otherWalkRunning;
    private readonly ZdoWorldWalker _walker = new ZdoWorldWalker();
    private readonly Dictionary<int, int> _kinds = new Dictionary<int, int>();
    private readonly Dictionary<long, int> _owners = new Dictionary<long, int>();
    private readonly Dictionary<long, string> _names = new Dictionary<long, string>();
    private readonly Action<ZDO> _visit;
    private long _owned;
    private long _unowned;
    private int _requested;
    private long _requestedUnixMs;
    private int _lastPublishedProgress = -1;
    private volatile ObjectCensusSnapshot _snapshot = ObjectCensusSnapshot.Idle;

    public ObjectCensus(ModLogger log, Func<bool> otherWalkRunning)
    {
        _log = log;
        _otherWalkRunning = otherWalkRunning;
        _visit = Visit;
    }

    public ObjectCensusSnapshot Snapshot => _snapshot;

    // Request threads. A census already running, or a result under five minutes old, is returned
    // instead of starting another; the bases survey (also a pass over every object) makes it wait.
    public ObjectCensusRequestResult Request()
    {
        ObjectCensusSnapshot current = _snapshot;
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (current.Scanning || Volatile.Read(ref _requested) != 0)
        {
            return ObjectCensusRequestResult.Running;
        }

        if (current.State == "ready" && nowUnixMs - current.FinishedUnixMs < CacheMilliseconds)
        {
            return ObjectCensusRequestResult.Cached;
        }

        if (_otherWalkRunning())
        {
            return ObjectCensusRequestResult.Busy;
        }

        // Published before the flag: the main thread may start and even finish a small world's
        // census before this method returns, and its snapshot must be the one that stays.
        Interlocked.Exchange(ref _requestedUnixMs, nowUnixMs);
        _snapshot = ObjectCensusSnapshot.Requested(nowUnixMs);
        return Interlocked.Exchange(ref _requested, 1) != 0
            ? ObjectCensusRequestResult.Running
            : ObjectCensusRequestResult.Started;
    }

    // Main thread, once a frame.
    public void Tick()
    {
        if (!_walker.Active)
        {
            if (Interlocked.Exchange(ref _requested, 0) == 0)
            {
                return;
            }

            Begin();
        }

        ZDOMan? manager = ZDOMan.instance;
        if (manager == null)
        {
            Fail("the world is not loaded");
            return;
        }

        bool complete;
        try
        {
            complete = _walker.Step(manager, _visit, BudgetMilliseconds);
        }
        catch (Exception exception)
        {
            Fail($"{exception.GetType().Name}: {exception.Message}");
            return;
        }

        if (!complete)
        {
            int progress = _walker.Progress;
            if (progress != _lastPublishedProgress)
            {
                _lastPublishedProgress = progress;
                _snapshot = ObjectCensusSnapshot.Running(
                    Interlocked.Read(ref _requestedUnixMs),
                    _walker,
                    BudgetMilliseconds);
            }

            return;
        }

        try
        {
            Finish(manager);
        }
        catch (Exception exception)
        {
            Fail($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public void Stop()
    {
        _walker.Abandon();
        Interlocked.Exchange(ref _requested, 0);
    }

    // Tests: the full count for one prefab from the last census.
    internal int CountOf(string prefabName)
    {
        return _kinds.TryGetValue(prefabName.GetStableHashCode(), out int count) ? count : 0;
    }

    private void Begin()
    {
        _kinds.Clear();
        _owners.Clear();
        _names.Clear();
        _owned = 0L;
        _unowned = 0L;
        _lastPublishedProgress = -1;
        _walker.Begin();
        _snapshot = ObjectCensusSnapshot.Running(
            Interlocked.Read(ref _requestedUnixMs),
            _walker,
            BudgetMilliseconds);
    }

    private void Visit(ZDO zdo)
    {
        int prefab = zdo.GetPrefab();
        _kinds.TryGetValue(prefab, out int count);
        _kinds[prefab] = count + 1;
        long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
        if (creator != 0L)
        {
            _owned++;
            _owners.TryGetValue(creator, out int built);
            _owners[creator] = built + 1;
            if (prefab == WardPrefab && !_names.ContainsKey(creator))
            {
                RememberName(creator, zdo.GetString(ZDOVars.s_creatorName, string.Empty));
            }
        }
        else
        {
            _unowned++;
        }

        if (OwnerNamePrefabs.Contains(prefab))
        {
            long owner = zdo.GetLong(ZDOVars.s_owner, 0L);
            if (owner != 0L && !_names.ContainsKey(owner))
            {
                RememberName(owner, zdo.GetString(ZDOVars.s_ownerName, string.Empty));
            }
        }
    }

    private void RememberName(long id, string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > 0)
        {
            _names[id] = trimmed.Length > 64 ? trimmed.Substring(0, 64) : trimmed;
        }
    }

    private void Finish(ZDOMan manager)
    {
        // Online players' characters carry the same id as the pieces they build.
        ZNet? network = ZNet.instance;
        if (network != null)
        {
            foreach (ZNetPeer peer in GameAccess.GetPeers(network))
            {
                ZDO? character = peer == null ? null : manager.GetZDO(peer.m_characterID);
                if (character == null)
                {
                    continue;
                }

                long playerId = character.GetLong(ZDOVars.s_playerID, 0L);
                if (playerId != 0L)
                {
                    RememberName(playerId, character.GetString(ZDOVars.s_playerName, string.Empty));
                }
            }
        }

        var kinds = new List<KeyValuePair<int, int>>(_kinds);
        var kindNames = new Dictionary<int, string>(kinds.Count);
        for (int index = 0; index < kinds.Count; index++)
        {
            kindNames[kinds[index].Key] = PrefabName(kinds[index].Key);
        }

        kinds.Sort((left, right) =>
        {
            int byCount = right.Value.CompareTo(left.Value);
            return byCount != 0
                ? byCount
                : string.CompareOrdinal(kindNames[left.Key], kindNames[right.Key]);
        });
        var topKinds = new List<ObjectCensusKind>(Math.Min(TopCount, kinds.Count));
        for (int index = 0; index < kinds.Count && index < TopCount; index++)
        {
            topKinds.Add(new ObjectCensusKind(kindNames[kinds[index].Key], kinds[index].Value));
        }

        var owners = new List<KeyValuePair<long, int>>(_owners);
        owners.Sort((left, right) =>
        {
            int byCount = right.Value.CompareTo(left.Value);
            return byCount != 0 ? byCount : left.Key.CompareTo(right.Key);
        });
        var topOwners = new List<ObjectCensusOwner>(Math.Min(TopCount, owners.Count));
        for (int index = 0; index < owners.Count && index < TopCount; index++)
        {
            long id = owners[index].Key;
            topOwners.Add(new ObjectCensusOwner(
                id.ToString(CultureInfo.InvariantCulture),
                _names.TryGetValue(id, out string? name) ? name : string.Empty,
                owners[index].Value));
        }

        long finishedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new ObjectCensusSnapshot(
            "ready",
            Interlocked.Read(ref _requestedUnixMs),
            _walker.StartedUnixMs,
            finishedUnixMs,
            _walker.Slices,
            _walker.BusyMilliseconds,
            _walker.WorstSliceMilliseconds,
            BudgetMilliseconds,
            100,
            0,
            _owned + _unowned,
            manager.NrOfObjects(),
            kinds.Count,
            topKinds.ToArray(),
            _owned,
            _unowned,
            topOwners.ToArray(),
            string.Empty);
        _snapshot = result;
        _log.Info(
            $"[LiveMap] object census: {result.Objects.ToString(CultureInfo.InvariantCulture)} objects, " +
            $"{result.KindsTotal.ToString(CultureInfo.InvariantCulture)} kinds, " +
            $"{owners.Count.ToString(CultureInfo.InvariantCulture)} builders, in " +
            $"{(result.DurationMilliseconds / 1000d).ToString("0.0", CultureInfo.InvariantCulture)} s over " +
            $"{result.Frames.ToString(CultureInfo.InvariantCulture)} frames" +
            (_walker.SkippedFrames > 0
                ? $" ({_walker.SkippedFrames.ToString(CultureInfo.InvariantCulture)} waited)"
                : string.Empty) +
            $", busy {result.BusyMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms, " +
            $"worst frame {result.WorstFrameMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)} ms");
    }

    private void Fail(string error)
    {
        _walker.Abandon();
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _snapshot = ObjectCensusSnapshot.Failed(Interlocked.Read(ref _requestedUnixMs), nowUnixMs, error);
        _log.Warning("[LiveMap] object census failed: " + error);
    }

    private static string PrefabName(int hash)
    {
        try
        {
            GameObject? prefab = ZNetScene.instance?.GetPrefab(hash);
            if (prefab != null && !string.IsNullOrEmpty(prefab.name))
            {
                return prefab.name;
            }
        }
        catch (Exception)
        {
            // A prefab the scene no longer knows (a removed mod's piece) keeps its hash.
        }

        return "#" + hash.ToString(CultureInfo.InvariantCulture);
    }
}

internal sealed class ObjectCensusKind
{
    public ObjectCensusKind(string prefab, int count)
    {
        Prefab = prefab;
        Count = count;
    }

    public string Prefab { get; }

    public int Count { get; }
}

internal sealed class ObjectCensusOwner
{
    public ObjectCensusOwner(string creatorId, string name, int objects)
    {
        CreatorId = creatorId;
        Name = name;
        Objects = objects;
    }

    public string CreatorId { get; }

    public string Name { get; }

    public int Objects { get; }
}

internal sealed class ObjectCensusSnapshot
{
    public static readonly ObjectCensusSnapshot Idle = new ObjectCensusSnapshot(
        "idle", 0L, 0L, 0L, 0, 0d, 0d, ObjectCensus.BudgetMilliseconds, 0, -1, 0L, 0L, 0,
        Array.Empty<ObjectCensusKind>(), 0L, 0L, Array.Empty<ObjectCensusOwner>(), string.Empty);

    public ObjectCensusSnapshot(
        string state,
        long requestedUnixMs,
        long startedUnixMs,
        long finishedUnixMs,
        int frames,
        double busyMilliseconds,
        double worstFrameMilliseconds,
        double budgetMilliseconds,
        int scanProgress,
        int scanEtaSeconds,
        long objects,
        long zdoCount,
        int kindsTotal,
        ObjectCensusKind[] kinds,
        long ownedObjects,
        long unownedObjects,
        ObjectCensusOwner[] owners,
        string error)
    {
        State = state;
        RequestedUnixMs = requestedUnixMs;
        StartedUnixMs = startedUnixMs;
        FinishedUnixMs = finishedUnixMs;
        Frames = frames;
        BusyMilliseconds = busyMilliseconds;
        WorstFrameMilliseconds = worstFrameMilliseconds;
        BudgetMilliseconds = budgetMilliseconds;
        ScanProgress = scanProgress;
        ScanEtaSeconds = scanEtaSeconds;
        Objects = objects;
        ZdoCount = zdoCount;
        KindsTotal = kindsTotal;
        Kinds = kinds;
        OwnedObjects = ownedObjects;
        UnownedObjects = unownedObjects;
        Owners = owners;
        Error = error;
    }

    public string State { get; }

    public bool Scanning => State == "scanning";

    public long RequestedUnixMs { get; }

    public long StartedUnixMs { get; }

    public long FinishedUnixMs { get; }

    public long DurationMilliseconds =>
        FinishedUnixMs > 0L && StartedUnixMs > 0L ? Math.Max(0L, FinishedUnixMs - StartedUnixMs) : 0L;

    public int Frames { get; }

    public double BusyMilliseconds { get; }

    public double WorstFrameMilliseconds { get; }

    public double BudgetMilliseconds { get; }

    public int ScanProgress { get; }

    public int ScanEtaSeconds { get; }

    public long Objects { get; }

    public long ZdoCount { get; }

    public int KindsTotal { get; }

    public ObjectCensusKind[] Kinds { get; }

    public long OwnedObjects { get; }

    public long UnownedObjects { get; }

    public ObjectCensusOwner[] Owners { get; }

    public string Error { get; }

    public static ObjectCensusSnapshot Requested(long requestedUnixMs)
    {
        return new ObjectCensusSnapshot(
            "scanning", requestedUnixMs, 0L, 0L, 0, 0d, 0d, ObjectCensus.BudgetMilliseconds, 0, -1, 0L, 0L, 0,
            Array.Empty<ObjectCensusKind>(), 0L, 0L, Array.Empty<ObjectCensusOwner>(), string.Empty);
    }

    public static ObjectCensusSnapshot Running(long requestedUnixMs, ZdoWorldWalker walker, double budgetMilliseconds)
    {
        int progress = walker.Progress;
        int eta = -1;
        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - walker.StartedUnixMs;
        if (progress > 0 && elapsed > 0L)
        {
            eta = (int)Math.Min(int.MaxValue, Math.Ceiling(elapsed / 1000d * (100 - progress) / progress));
        }

        return new ObjectCensusSnapshot(
            "scanning", requestedUnixMs, walker.StartedUnixMs, 0L, walker.Slices, walker.BusyMilliseconds,
            walker.WorstSliceMilliseconds, budgetMilliseconds, progress, eta, walker.ObjectsRead, 0L, 0,
            Array.Empty<ObjectCensusKind>(), 0L, 0L, Array.Empty<ObjectCensusOwner>(), string.Empty);
    }

    public static ObjectCensusSnapshot Failed(long requestedUnixMs, long finishedUnixMs, string error)
    {
        return new ObjectCensusSnapshot(
            "failed", requestedUnixMs, 0L, finishedUnixMs, 0, 0d, 0d, ObjectCensus.BudgetMilliseconds, 0, -1, 0L, 0L, 0,
            Array.Empty<ObjectCensusKind>(), 0L, 0L, Array.Empty<ObjectCensusOwner>(), error);
    }

    public string ToJson(bool cached)
    {
        var json = new StringBuilder(512 + (Kinds.Length * 48) + (Owners.Length * 80));
        json.Append("{\"state\":").Append(JsonWriter.Quote(State));
        if (cached)
        {
            json.Append(",\"cached\":true");
        }

        json.Append(",\"scanning\":").Append(Scanning ? "true" : "false");
        json.Append(",\"scanProgress\":").Append(ScanProgress.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"scanEtaSeconds\":").Append(ScanEtaSeconds.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"requestedUnixMs\":").Append(RequestedUnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"startedUnixMs\":").Append(StartedUnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"finishedUnixMs\":").Append(FinishedUnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"durationMs\":").Append(DurationMilliseconds.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"frames\":").Append(Frames.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"busyMs\":").Append(JsonWriter.Number(Math.Round(BusyMilliseconds, 1)));
        json.Append(",\"worstFrameMs\":").Append(JsonWriter.Number(Math.Round(WorstFrameMilliseconds, 2)));
        json.Append(",\"budgetMs\":").Append(JsonWriter.Number(Math.Round(BudgetMilliseconds, 2)));
        json.Append(",\"objects\":").Append(Objects.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"zdoCount\":").Append(ZdoCount.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"kindsTotal\":").Append(KindsTotal.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"kinds\":[");
        for (int index = 0; index < Kinds.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append("{\"prefab\":").Append(JsonWriter.Quote(Kinds[index].Prefab));
            json.Append(",\"count\":").Append(Kinds[index].Count.ToString(CultureInfo.InvariantCulture)).Append('}');
        }

        json.Append("],\"ownedObjects\":").Append(OwnedObjects.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"unownedObjects\":").Append(UnownedObjects.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"owners\":[");
        for (int index = 0; index < Owners.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append("{\"creatorId\":").Append(JsonWriter.Quote(Owners[index].CreatorId));
            json.Append(",\"name\":").Append(JsonWriter.Quote(Owners[index].Name));
            json.Append(",\"objects\":").Append(Owners[index].Objects.ToString(CultureInfo.InvariantCulture)).Append('}');
        }

        json.Append(']');
        if (Error.Length > 0)
        {
            json.Append(",\"error\":").Append(JsonWriter.Quote(Error));
        }

        json.Append('}');
        return json.ToString();
    }
}

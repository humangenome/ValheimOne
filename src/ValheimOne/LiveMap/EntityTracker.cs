using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class EntityTracker
{
    private const float EventRefreshIntervalSeconds = 5f;
    private const float EntityRefreshIntervalSeconds = 30f;
    private const float FocusRefreshIntervalSeconds = 2f;
    private const long RequestActiveMilliseconds = 2L * 60L * 1000L;
    private const long FocusRequestActiveMilliseconds = 30L * 1000L;
    private const int MaximumEntities = 500;

    private static readonly Lazy<FieldInfo> RandomEventField = new(
        () => AccessTools.Field(typeof(RandEventSystem), "m_randomEvent") ??
              throw new MissingFieldException(typeof(RandEventSystem).FullName, "m_randomEvent"));

    private static readonly PrefabDefinition[] Prefabs =
    {
        new PrefabDefinition("ship", "Raft"),
        new PrefabDefinition("ship", "Karve"),
        new PrefabDefinition("ship", "VikingShip"),
        new PrefabDefinition("ship", "VikingShip_Ashlands"),
        new PrefabDefinition("cart", "Cart"),
        new PrefabDefinition("portal", "portal_wood"),
        new PrefabDefinition("portal", "portal_stone"),
        new PrefabDefinition("portal", "portal"),
        new PrefabDefinition("tombstone", "Player_tombstone"),
    };

    private readonly LiveMapConfig _config;
    private readonly PositionHistory _positionHistory;
    private readonly ModLogger _log;
    private readonly List<ZDO> _scanResults = new List<ZDO>();
    private readonly List<TrackedEntitySnapshot> _pendingEntities =
        new List<TrackedEntitySnapshot>(MaximumEntities);
    private volatile EntityMapSnapshot _snapshot = EntityMapSnapshot.Empty;
    private EntityFocusRequest _focusRequest = EntityFocusRequest.Empty;
    private volatile EntityFocusSnapshot _focusSnapshot = EntityFocusSnapshot.Empty;
    private TrackedEntitySnapshot[] _entities = Array.Empty<TrackedEntitySnapshot>();
    private RaidEventSnapshot? _activeEvent;
    private float _nextEntityRefresh;
    private float _nextEventRefresh;
    private float _nextFocusRefresh;
    private long _lastEntitiesRequestUnixMs;
    private long _lastEntityScanUnixMs;
    private int _prefabIndex;
    private int _scanIndex;
    private int _revision;
    private string _focusedId = string.Empty;
    private bool _scanning;
    private bool _scanWarningLogged;
    private bool _eventWarningLogged;
    private bool _focusWarningLogged;

    public EntityTracker(
        LiveMapConfig config,
        PositionHistory positionHistory,
        ModLogger log)
    {
        _config = config;
        _positionHistory = positionHistory;
        _log = log;
    }

    public EntityMapSnapshot Snapshot => _snapshot;

    public EntityFocusSnapshot FocusSnapshot => _focusSnapshot;

    public void NoteEntitiesRequested()
    {
        Interlocked.Exchange(
            ref _lastEntitiesRequestUnixMs,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void NoteFocusRequested(string id)
    {
        Volatile.Write(
            ref _focusRequest,
            new EntityFocusRequest(
                id,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public void Tick(float now)
    {
        ServiceFocus(now);
        bool publish = false;
        if (now >= _nextEventRefresh)
        {
            _nextEventRefresh = now + EventRefreshIntervalSeconds;
            RaidEventSnapshot? activeEvent = ReadActiveEvent();
            if (!RaidEventsEqual(_activeEvent, activeEvent))
            {
                _activeEvent = activeEvent;
                publish = true;
            }
        }

        if (!_config.EntityLayer)
        {
            ResetScan();
            if (_entities.Length != 0)
            {
                _entities = Array.Empty<TrackedEntitySnapshot>();
                publish = true;
            }
        }
        else if (_scanning)
        {
            publish |= ContinueScan(now);
        }
        else if (now >= _nextEntityRefresh && EntitiesWereRecentlyRequested())
        {
            StartScan(now);
            publish |= ContinueScan(now);
        }

        if (!publish && _snapshot.Revision != 0)
        {
            return;
        }

        PublishSnapshot();
    }

    private bool EntitiesWereRecentlyRequested()
    {
        long requestedUnixMs = Interlocked.Read(ref _lastEntitiesRequestUnixMs);
        if (requestedUnixMs == 0L)
        {
            return false;
        }

        long elapsedMilliseconds =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - requestedUnixMs;
        return elapsedMilliseconds >= 0L && elapsedMilliseconds <= RequestActiveMilliseconds;
    }

    private void StartScan(float now)
    {
        _scanResults.Clear();
        _pendingEntities.Clear();
        _prefabIndex = 0;
        _scanIndex = 0;
        _scanWarningLogged = false;
        _scanning = true;
        _nextEntityRefresh = now + EntityRefreshIntervalSeconds;
    }

    private bool ContinueScan(float now)
    {
        ZDOMan? manager = ZDOMan.instance;
        if (manager == null)
        {
            ResetScan();
            _nextEntityRefresh = now + EntityRefreshIntervalSeconds;
            return false;
        }

        PrefabDefinition prefab = Prefabs[_prefabIndex];
        bool complete;
        int pendingCount = _pendingEntities.Count;
        try
        {
            // Valheim appends results across iterative calls, so retain this list until completion.
            complete = manager.GetAllZDOsWithPrefabIterative(
                prefab.Name,
                _scanResults,
                ref _scanIndex);
            if (!complete)
            {
                return false;
            }

            for (int entityIndex = 0;
                 entityIndex < _scanResults.Count &&
                 _pendingEntities.Count < MaximumEntities;
                 entityIndex++)
            {
                ZDO? zdo = _scanResults[entityIndex];
                if (zdo == null)
                {
                    continue;
                }

                Vector3 position = zdo.GetPosition();
                ZDOID uid = zdo.m_uid;
                string id = uid.UserID.ToString(CultureInfo.InvariantCulture) + ":" +
                            uid.ID.ToString(CultureInfo.InvariantCulture);
                float rotationY = zdo.GetRotation().eulerAngles.y;
                string tag = string.Equals(prefab.Group, "portal", StringComparison.Ordinal)
                    ? zdo.GetString(ZDOVars.s_tag, string.Empty)
                    : string.Empty;
                bool isTombstone = string.Equals(
                    prefab.Group,
                    "tombstone",
                    StringComparison.Ordinal);
                string owner = isTombstone
                    ? zdo.GetString(ZDOVars.s_ownerName, string.Empty)
                    : string.Empty;
                double? deathAgeSec = null;
                if (isTombstone)
                {
                    long timeOfDeath = zdo.GetLong(ZDOVars.s_timeOfDeath, 0L);
                    ZNet? network = ZNet.instance;
                    if (timeOfDeath != 0L && network != null)
                    {
                        long ageTicks = network.GetTime().Ticks - timeOfDeath;
                        deathAgeSec = Math.Max(
                            0d,
                            ageTicks / (double)TimeSpan.TicksPerSecond);
                    }
                }

                _pendingEntities.Add(new TrackedEntitySnapshot(
                    prefab.Group,
                    prefab.Name,
                    position.x,
                    position.y,
                    position.z,
                    id,
                    rotationY,
                    tag,
                    owner,
                    deathAgeSec));
            }
        }
        catch (Exception exception)
        {
            if (_pendingEntities.Count > pendingCount)
            {
                _pendingEntities.RemoveRange(
                    pendingCount,
                    _pendingEntities.Count - pendingCount);
            }

            if (!_scanWarningLogged)
            {
                _scanWarningLogged = true;
                _log.Warning(
                    $"[LiveMap] entity ZDO scan for {prefab.Name} failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        _scanResults.Clear();
        _scanIndex = 0;
        _prefabIndex++;
        if (_prefabIndex < Prefabs.Length && _pendingEntities.Count < MaximumEntities)
        {
            return false;
        }

        _entities = _pendingEntities.ToArray();
        _lastEntityScanUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int index = 0; index < _entities.Length; index++)
        {
            TrackedEntitySnapshot entity = _entities[index];
            if (IsTrailEntity(entity))
            {
                _positionHistory.Record(
                    PositionHistory.EntityKey(entity.Id),
                    entity.X,
                    entity.Z,
                    _lastEntityScanUnixMs);
            }
        }

        ResetScan();
        return true;
    }

    private void ServiceFocus(float now)
    {
        if (!_config.EntityLayer || !FocusWasRecentlyRequested(out string requestedId))
        {
            _focusedId = string.Empty;
            _focusSnapshot = EntityFocusSnapshot.Empty;
            return;
        }

        bool focusChanged = !string.Equals(_focusedId, requestedId, StringComparison.Ordinal);
        if (!focusChanged &&
            now < _nextFocusRefresh)
        {
            return;
        }

        _focusedId = requestedId;
        _nextFocusRefresh = now + FocusRefreshIntervalSeconds;
        if (focusChanged)
        {
            _focusWarningLogged = false;
        }
        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        TrackedEntitySnapshot? tracked = FindEntity(requestedId);
        ZDOMan? manager = ZDOMan.instance;
        if (tracked == null || manager == null ||
            !TryParseEntityId(requestedId, out long userId, out uint objectId))
        {
            _focusSnapshot = EntityFocusSnapshot.Missing(requestedId, unixMs);
            return;
        }

        try
        {
            ZDO? zdo = manager.GetZDO(new ZDOID(userId, objectId));
            if (zdo == null)
            {
                _focusSnapshot = EntityFocusSnapshot.Missing(requestedId, unixMs);
                return;
            }

            Vector3 position = zdo.GetPosition();
            var focus = new EntityFocusSnapshot(
                true,
                tracked.Group,
                tracked.Prefab,
                position.x,
                position.y,
                position.z,
                tracked.Id,
                zdo.GetRotation().eulerAngles.y,
                tracked.Tag,
                unixMs);
            _focusSnapshot = focus;
            if (IsTrailEntity(tracked))
            {
                _positionHistory.Record(
                    PositionHistory.EntityKey(tracked.Id),
                    position.x,
                    position.z,
                    unixMs);
            }
        }
        catch (Exception exception)
        {
            _focusSnapshot = EntityFocusSnapshot.Missing(requestedId, unixMs);
            if (!_focusWarningLogged)
            {
                _focusWarningLogged = true;
                _log.Warning(
                    $"[LiveMap] focused entity read failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private bool FocusWasRecentlyRequested(out string requestedId)
    {
        EntityFocusRequest request = Volatile.Read(ref _focusRequest);
        requestedId = request.Id;
        if (request.UnixMs == 0L || string.IsNullOrEmpty(requestedId))
        {
            return false;
        }

        long elapsedMilliseconds =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - request.UnixMs;
        if (elapsedMilliseconds >= 0L &&
            elapsedMilliseconds <= FocusRequestActiveMilliseconds)
        {
            return true;
        }

        Interlocked.CompareExchange(
            ref _focusRequest,
            EntityFocusRequest.Empty,
            request);
        return false;
    }

    private TrackedEntitySnapshot? FindEntity(string id)
    {
        for (int index = 0; index < _entities.Length; index++)
        {
            if (string.Equals(_entities[index].Id, id, StringComparison.Ordinal))
            {
                return _entities[index];
            }
        }

        return null;
    }

    private static bool IsTrailEntity(TrackedEntitySnapshot entity)
    {
        return string.Equals(entity.Group, "ship", StringComparison.Ordinal) ||
               string.Equals(entity.Group, "cart", StringComparison.Ordinal);
    }

    internal static bool TryParseEntityId(
        string value,
        out long userId,
        out uint objectId)
    {
        userId = 0L;
        objectId = 0U;
        int separator = value.IndexOf(':');
        return separator > 0 && separator == value.LastIndexOf(':') &&
               long.TryParse(
                   value.Substring(0, separator),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out userId) &&
               uint.TryParse(
                   value.Substring(separator + 1),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out objectId);
    }

    private void ResetScan()
    {
        _scanResults.Clear();
        _pendingEntities.Clear();
        _prefabIndex = 0;
        _scanIndex = 0;
        _scanning = false;
    }

    private void PublishSnapshot()
    {
        _revision = unchecked(_revision + 1);
        _snapshot = new EntityMapSnapshot(
            _revision,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            _lastEntityScanUnixMs,
            _entities,
            _activeEvent);
    }

    private static bool RaidEventsEqual(
        RaidEventSnapshot? first,
        RaidEventSnapshot? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first == null || second == null)
        {
            return false;
        }

        return string.Equals(first.Name, second.Name, StringComparison.Ordinal) &&
               first.X.Equals(second.X) &&
               first.Z.Equals(second.Z) &&
               first.Radius.Equals(second.Radius) &&
               first.Elapsed.Equals(second.Elapsed) &&
               first.Duration.Equals(second.Duration);
    }

    private RaidEventSnapshot? ReadActiveEvent()
    {
        RandEventSystem? eventSystem = RandEventSystem.instance;
        if (eventSystem == null)
        {
            return null;
        }

        try
        {
            var activeEvent = RandomEventField.Value.GetValue(eventSystem) as RandomEvent;
            if (activeEvent == null)
            {
                return null;
            }

            Vector3 position = activeEvent.m_pos;
            return new RaidEventSnapshot(
                activeEvent.m_name ?? string.Empty,
                position.x,
                position.z,
                activeEvent.m_eventRange,
                activeEvent.m_time,
                activeEvent.m_duration);
        }
        catch (Exception exception)
        {
            if (!_eventWarningLogged)
            {
                _eventWarningLogged = true;
                _log.Warning(
                    $"[LiveMap] active raid event read failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }

            return null;
        }
    }

    private sealed class PrefabDefinition
    {
        public PrefabDefinition(string group, string name)
        {
            Group = group;
            Name = name;
        }

        public string Group { get; }

        public string Name { get; }
    }

    private sealed class EntityFocusRequest
    {
        public static readonly EntityFocusRequest Empty =
            new EntityFocusRequest(string.Empty, 0L);

        public EntityFocusRequest(string id, long unixMs)
        {
            Id = id;
            UnixMs = unixMs;
        }

        public string Id { get; }

        public long UnixMs { get; }
    }
}

internal sealed class EntityMapSnapshot
{
    public static readonly EntityMapSnapshot Empty = new EntityMapSnapshot(
        0,
        0L,
        0L,
        Array.Empty<TrackedEntitySnapshot>(),
        null);

    public EntityMapSnapshot(
        int revision,
        long unixMs,
        long entitiesUnixMs,
        TrackedEntitySnapshot[] entities,
        RaidEventSnapshot? activeEvent)
    {
        Revision = revision;
        UnixMs = unixMs;
        EntitiesUnixMs = entitiesUnixMs;
        Entities = entities;
        Event = activeEvent;
    }

    public int Revision { get; }

    public long UnixMs { get; }

    public long EntitiesUnixMs { get; }

    public TrackedEntitySnapshot[] Entities { get; }

    public RaidEventSnapshot? Event { get; }
}

internal sealed class TrackedEntitySnapshot
{
    public TrackedEntitySnapshot(
        string group,
        string prefab,
        float x,
        float y,
        float z,
        string id,
        float rotYDeg,
        string tag,
        string owner,
        double? deathAgeSec)
    {
        Group = group;
        Prefab = prefab;
        X = x;
        Y = y;
        Z = z;
        Id = id;
        RotYDeg = rotYDeg;
        Tag = tag;
        Owner = owner;
        DeathAgeSec = deathAgeSec;
    }

    public string Group { get; }

    public string Prefab { get; }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public string Id { get; }

    public float RotYDeg { get; }

    public string Tag { get; }

    public string Owner { get; }

    public double? DeathAgeSec { get; }
}

internal sealed class EntityFocusSnapshot
{
    public static readonly EntityFocusSnapshot Empty = Missing(string.Empty, 0L);

    public EntityFocusSnapshot(
        bool found,
        string group,
        string prefab,
        float x,
        float y,
        float z,
        string id,
        float rotYDeg,
        string tag,
        long unixMs)
    {
        Found = found;
        Group = group;
        Prefab = prefab;
        X = x;
        Y = y;
        Z = z;
        Id = id;
        RotYDeg = rotYDeg;
        Tag = tag;
        UnixMs = unixMs;
    }

    public bool Found { get; }

    public string Group { get; }

    public string Prefab { get; }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public string Id { get; }

    public float RotYDeg { get; }

    public string Tag { get; }

    public long UnixMs { get; }

    public static EntityFocusSnapshot Missing(string id, long unixMs)
    {
        return new EntityFocusSnapshot(
            false,
            string.Empty,
            string.Empty,
            0f,
            0f,
            0f,
            id,
            0f,
            string.Empty,
            unixMs);
    }
}

internal sealed class RaidEventSnapshot
{
    public RaidEventSnapshot(
        string name,
        float x,
        float z,
        float radius,
        float elapsed,
        float duration)
    {
        Name = name;
        X = x;
        Z = z;
        Radius = radius;
        Elapsed = elapsed;
        Duration = duration;
    }

    public string Name { get; }

    public float X { get; }

    public float Z { get; }

    public float Radius { get; }

    public float Elapsed { get; }

    public float Duration { get; }
}

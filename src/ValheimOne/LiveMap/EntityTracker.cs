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
    private const float CreatureRefreshIntervalSeconds = 10f;
    private const float FocusRefreshIntervalSeconds = 2f;
    private const long RequestActiveMilliseconds = 2L * 60L * 1000L;
    private const long FocusRequestActiveMilliseconds = 30L * 1000L;
    private const int MaximumShips = 300;
    private const int MaximumCarts = 150;
    private const int MaximumPortals = 500;
    private const int MaximumTombstones = 200;
    private const int MaximumWards = 300;
    private const int MaximumBeds = 500;
    private const int MaximumCreatures = 150;
    private const int MaximumRaidCreaturePrefabs = 24;
    private const float WardRadiusFallback = 32f;

    private static readonly Lazy<FieldInfo> RandomEventField = new(
        () => AccessTools.Field(typeof(RandEventSystem), "m_randomEvent") ??
              throw new MissingFieldException(typeof(RandEventSystem).FullName, "m_randomEvent"));

    private static readonly EntityGroupDefinition[] Groups =
    {
        new EntityGroupDefinition("ship", MaximumShips),
        new EntityGroupDefinition("cart", MaximumCarts),
        new EntityGroupDefinition("portal", MaximumPortals),
        new EntityGroupDefinition("tombstone", MaximumTombstones),
        new EntityGroupDefinition("ward", MaximumWards),
        new EntityGroupDefinition("bed", MaximumBeds),
        new EntityGroupDefinition("creatures", MaximumCreatures),
    };

    private static readonly PrefabDefinition[] CorePrefabs =
    {
        new PrefabDefinition(EntityGroup.Ship, "Raft"),
        new PrefabDefinition(EntityGroup.Ship, "Karve"),
        new PrefabDefinition(EntityGroup.Ship, "VikingShip"),
        new PrefabDefinition(EntityGroup.Ship, "VikingShip_Ashlands"),
        new PrefabDefinition(EntityGroup.Cart, "Cart"),
        new PrefabDefinition(EntityGroup.Portal, "portal_wood"),
        new PrefabDefinition(EntityGroup.Portal, "portal_stone"),
        new PrefabDefinition(EntityGroup.Portal, "portal"),
        new PrefabDefinition(EntityGroup.Tombstone, "Player_tombstone"),
        new PrefabDefinition(EntityGroup.Ward, "guard_stone"),
        new PrefabDefinition(EntityGroup.Bed, "bed"),
        new PrefabDefinition(EntityGroup.Bed, "piece_bed02"),
        new PrefabDefinition(EntityGroup.Bed, "ashwood_bed"),
    };

    private static readonly PrefabDefinition[] CreaturePrefabs =
    {
        new PrefabDefinition(EntityGroup.Creature, "Eikthyr", "Eikthyr"),
        new PrefabDefinition(EntityGroup.Creature, "gd_king", "The Elder"),
        new PrefabDefinition(EntityGroup.Creature, "Bonemass", "Bonemass"),
        new PrefabDefinition(EntityGroup.Creature, "Dragon", "Moder"),
        new PrefabDefinition(EntityGroup.Creature, "GoblinKing", "Yagluth"),
        new PrefabDefinition(EntityGroup.Creature, "SeekerQueen", "The Queen"),
        new PrefabDefinition(EntityGroup.Creature, "Fader", "Fader"),
        new PrefabDefinition(EntityGroup.Creature, "Serpent", "Serpent"),
    };

    private readonly LiveMapConfig _config;
    private readonly PositionHistory _positionHistory;
    private readonly ModLogger _log;
    private readonly List<ZDO> _scanResults = new List<ZDO>();
    private readonly List<TrackedEntitySnapshot> _pendingEntities =
        new List<TrackedEntitySnapshot>();
    private readonly List<PrefabDefinition> _scanPrefabs = new List<PrefabDefinition>();
    private readonly int[] _pendingGroupCounts = new int[Groups.Length];
    private readonly bool[] _pendingGroupTruncated = new bool[Groups.Length];
    private readonly bool[] _scanGroups = new bool[Groups.Length];
    private volatile EntityMapSnapshot _snapshot = EntityMapSnapshot.Empty;
    private EntityFocusRequest _focusRequest = EntityFocusRequest.Empty;
    private volatile EntityFocusSnapshot _focusSnapshot = EntityFocusSnapshot.Empty;
    private TrackedEntitySnapshot[] _entities = Array.Empty<TrackedEntitySnapshot>();
    private EntityGroupSnapshot[] _entityGroups = CreateGroupSnapshots(
        new int[Groups.Length],
        new bool[Groups.Length]);
    private RaidEventSnapshot? _activeEvent;
    private string[] _activeRaidCreaturePrefabs = Array.Empty<string>();
    private RaidEventSnapshot? _scanEvent;
    private float _nextEntityRefresh;
    private float _nextCreatureRefresh;
    private float _nextEventRefresh;
    private float _nextFocusRefresh;
    private long _lastEntitiesRequestUnixMs;
    private long _lastCreaturesRequestUnixMs;
    private long _lastEntityScanUnixMs;
    private int _prefabIndex;
    private int _scanIndex;
    private int _revision;
    private string _focusedId = string.Empty;
    private float _wardRadius = WardRadiusFallback;
    private bool _scanning;
    private bool _scanWarningLogged;
    private bool _eventWarningLogged;
    private bool _focusWarningLogged;
    private bool _wardRadiusResolved;

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
        NoteEntitiesRequested(true, true);
    }

    public void NoteEntitiesRequested(bool entitiesRequested, bool creaturesRequested)
    {
        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (entitiesRequested)
        {
            Interlocked.Exchange(ref _lastEntitiesRequestUnixMs, unixMs);
        }
        if (creaturesRequested)
        {
            Interlocked.Exchange(ref _lastCreaturesRequestUnixMs, unixMs);
        }
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
                _entityGroups = CreateGroupSnapshots(
                    new int[Groups.Length],
                    new bool[Groups.Length]);
                publish = true;
            }
        }
        else if (_scanning)
        {
            publish |= ContinueScan(now);
        }
        else
        {
            bool scanEntities = now >= _nextEntityRefresh &&
                                EntitiesWereRecentlyRequested(Interlocked.Read(
                                    ref _lastEntitiesRequestUnixMs));
            bool scanCreatures = now >= _nextCreatureRefresh &&
                                 EntitiesWereRecentlyRequested(Interlocked.Read(
                                     ref _lastCreaturesRequestUnixMs));
            if (scanEntities || scanCreatures)
            {
                StartScan(now, scanEntities, scanCreatures);
                publish |= ContinueScan(now);
            }
        }

        if (!publish && _snapshot.Revision != 0)
        {
            return;
        }

        PublishSnapshot();
    }

    private static bool EntitiesWereRecentlyRequested(long requestedUnixMs)
    {
        if (requestedUnixMs == 0L)
        {
            return false;
        }

        long elapsedMilliseconds =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - requestedUnixMs;
        return elapsedMilliseconds >= 0L && elapsedMilliseconds <= RequestActiveMilliseconds;
    }

    private void StartScan(float now, bool scanEntities, bool scanCreatures)
    {
        ResolveWardRadius();
        _scanResults.Clear();
        _pendingEntities.Clear();
        _scanPrefabs.Clear();
        Array.Clear(_pendingGroupCounts, 0, _pendingGroupCounts.Length);
        Array.Clear(_pendingGroupTruncated, 0, _pendingGroupTruncated.Length);
        Array.Clear(_scanGroups, 0, _scanGroups.Length);
        for (int index = 0; index < Groups.Length; index++)
        {
            _scanGroups[index] = scanEntities && index != (int)EntityGroup.Creature ||
                                 scanCreatures && index == (int)EntityGroup.Creature;
            if (!_scanGroups[index])
            {
                _pendingGroupCounts[index] = _entityGroups[index].Count;
                _pendingGroupTruncated[index] = _entityGroups[index].Truncated;
            }
        }

        for (int index = 0; index < _entities.Length; index++)
        {
            TrackedEntitySnapshot entity = _entities[index];
            if (!TryGetGroupIndex(entity.Group, out int groupIndex) ||
                !_scanGroups[groupIndex])
            {
                _pendingEntities.Add(entity);
            }
        }

        if (scanEntities)
        {
            _scanPrefabs.AddRange(CorePrefabs);
            _nextEntityRefresh = now + EntityRefreshIntervalSeconds;
        }
        if (scanCreatures)
        {
            _scanPrefabs.AddRange(CreaturePrefabs);
            _scanEvent = _activeEvent;
            var seenPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < CreaturePrefabs.Length; index++)
            {
                seenPrefabs.Add(CreaturePrefabs[index].Name);
            }
            for (int index = 0; index < _activeRaidCreaturePrefabs.Length; index++)
            {
                string prefabName = _activeRaidCreaturePrefabs[index];
                if (seenPrefabs.Add(prefabName))
                {
                    _scanPrefabs.Add(new PrefabDefinition(
                        EntityGroup.Creature,
                        prefabName,
                        ResolveCreatureDisplayName(prefabName),
                        true));
                }
            }
            _nextCreatureRefresh = now + CreatureRefreshIntervalSeconds;
        }
        else
        {
            _scanEvent = null;
        }

        _prefabIndex = 0;
        _scanIndex = 0;
        _scanWarningLogged = false;
        _scanning = true;
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

        PrefabDefinition prefab = _scanPrefabs[_prefabIndex];
        int groupIndex = (int)prefab.Group;
        EntityGroupDefinition group = Groups[groupIndex];
        bool complete;
        int pendingCount = _pendingEntities.Count;
        int pendingGroupCount = _pendingGroupCounts[groupIndex];
        bool pendingGroupTruncated = _pendingGroupTruncated[groupIndex];
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
                 _pendingGroupCounts[groupIndex] < group.MaximumEntities;
                 entityIndex++)
            {
                ZDO? zdo = _scanResults[entityIndex];
                if (zdo == null)
                {
                    continue;
                }

                Vector3 position = zdo.GetPosition();
                if (prefab.RaidOnly && !IsInsideEvent(position, _scanEvent))
                {
                    continue;
                }
                if (prefab.Group == EntityGroup.Creature &&
                    zdo.GetBool(ZDOVars.s_dead, false))
                {
                    continue;
                }
                ZDOID uid = zdo.m_uid;
                string id = uid.UserID.ToString(CultureInfo.InvariantCulture) + ":" +
                            uid.ID.ToString(CultureInfo.InvariantCulture);
                float rotationY = zdo.GetRotation().eulerAngles.y;
                string tag = prefab.Group == EntityGroup.Portal
                    ? zdo.GetString(ZDOVars.s_tag, string.Empty)
                    : string.Empty;
                bool isTombstone = prefab.Group == EntityGroup.Tombstone;
                bool isWard = prefab.Group == EntityGroup.Ward;
                bool isBed = prefab.Group == EntityGroup.Bed;
                string owner = isWard
                    ? zdo.GetString(ZDOVars.s_creatorName, string.Empty)
                    : isTombstone || isBed
                        ? zdo.GetString(ZDOVars.s_ownerName, string.Empty)
                        : string.Empty;
                bool? wardEnabled = isWard
                    ? zdo.GetBool(ZDOVars.s_enabled)
                    : null;
                float? wardRadius = isWard ? _wardRadius : null;
                double? deathAgeSec = null;
                int? level = null;
                if (prefab.Group == EntityGroup.Creature &&
                    zdo.GetInt(ZDOVars.s_level, out int creatureLevel))
                {
                    level = Math.Max(1, creatureLevel);
                }
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
                    group.Key,
                    prefab.Name,
                    position.x,
                    position.y,
                    position.z,
                    id,
                    rotationY,
                    tag,
                    owner,
                    deathAgeSec,
                    wardEnabled,
                    wardRadius,
                    prefab.DisplayName,
                    level));
                _pendingGroupCounts[groupIndex]++;
            }

            if (_pendingGroupCounts[groupIndex] >= group.MaximumEntities)
            {
                _pendingGroupTruncated[groupIndex] = true;
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
            _pendingGroupCounts[groupIndex] = pendingGroupCount;
            _pendingGroupTruncated[groupIndex] = pendingGroupTruncated;

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
        while (_prefabIndex < _scanPrefabs.Count &&
               _pendingGroupTruncated[(int)_scanPrefabs[_prefabIndex].Group])
        {
            _prefabIndex++;
        }

        if (_prefabIndex < _scanPrefabs.Count)
        {
            return false;
        }

        _entities = _pendingEntities.ToArray();
        _entityGroups = CreateGroupSnapshots(
            _pendingGroupCounts,
            _pendingGroupTruncated);
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

    private void ResolveWardRadius()
    {
        if (_wardRadiusResolved)
        {
            return;
        }

        _wardRadiusResolved = true;
        try
        {
            GameObject? prefab = ZNetScene.instance?.GetPrefab("guard_stone");
            PrivateArea? privateArea = prefab?.GetComponent<PrivateArea>();
            float radius = privateArea?.m_radius ?? 0f;
            if (radius > 0f && !float.IsNaN(radius) && !float.IsInfinity(radius))
            {
                _wardRadius = radius;
                return;
            }

            _log.Warning(
                $"[LiveMap] guard_stone PrivateArea radius unavailable; " +
                $"using {WardRadiusFallback.ToString(CultureInfo.InvariantCulture)}m fallback.");
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] guard_stone PrivateArea radius lookup failed; " +
                $"using {WardRadiusFallback.ToString(CultureInfo.InvariantCulture)}m fallback " +
                $"({exception.GetType().Name}: {exception.Message}).");
        }
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
               string.Equals(entity.Group, "cart", StringComparison.Ordinal) ||
               string.Equals(entity.Group, "creatures", StringComparison.Ordinal);
    }

    private static bool TryGetGroupIndex(string key, out int groupIndex)
    {
        for (int index = 0; index < Groups.Length; index++)
        {
            if (string.Equals(Groups[index].Key, key, StringComparison.Ordinal))
            {
                groupIndex = index;
                return true;
            }
        }

        groupIndex = -1;
        return false;
    }

    private static bool IsInsideEvent(Vector3 position, RaidEventSnapshot? activeEvent)
    {
        if (activeEvent == null || activeEvent.Radius <= 0f)
        {
            return false;
        }

        float deltaX = position.x - activeEvent.X;
        float deltaZ = position.z - activeEvent.Z;
        return (deltaX * deltaX) + (deltaZ * deltaZ) <=
               activeEvent.Radius * activeEvent.Radius;
    }

    private static string ResolveCreatureDisplayName(string prefabName)
    {
        try
        {
            GameObject? prefab = ZNetScene.instance?.GetPrefab(prefabName);
            Character? character = prefab?.GetComponent<Character>();
            string localized = character == null
                ? string.Empty
                : Localization.instance.Localize(character.m_name ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(localized) && localized[0] != '$')
            {
                return localized;
            }
        }
        catch (Exception)
        {
            // Prefab names remain a safe display fallback if localization is unavailable.
        }

        return prefabName;
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

    internal static bool IsTrackedShipPrefab(int prefabHash)
    {
        for (int index = 0; index < CorePrefabs.Length; index++)
        {
            PrefabDefinition prefab = CorePrefabs[index];
            if (prefab.Group == EntityGroup.Ship &&
                prefab.Name.GetStableHashCode() == prefabHash)
            {
                return true;
            }
        }

        return false;
    }

    private void ResetScan()
    {
        _scanResults.Clear();
        _pendingEntities.Clear();
        _scanPrefabs.Clear();
        Array.Clear(_pendingGroupCounts, 0, _pendingGroupCounts.Length);
        Array.Clear(_pendingGroupTruncated, 0, _pendingGroupTruncated.Length);
        Array.Clear(_scanGroups, 0, _scanGroups.Length);
        _scanEvent = null;
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
            _entityGroups,
            _entities,
            _activeEvent);
    }

    private static EntityGroupSnapshot[] CreateGroupSnapshots(
        int[] counts,
        bool[] truncated)
    {
        var snapshots = new EntityGroupSnapshot[Groups.Length];
        for (int index = 0; index < Groups.Length; index++)
        {
            EntityGroupDefinition group = Groups[index];
            snapshots[index] = new EntityGroupSnapshot(
                group.Key,
                counts[index],
                group.MaximumEntities,
                truncated[index]);
        }

        return snapshots;
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
            _activeRaidCreaturePrefabs = Array.Empty<string>();
            return null;
        }

        try
        {
            var activeEvent = RandomEventField.Value.GetValue(eventSystem) as RandomEvent;
            if (activeEvent == null)
            {
                _activeRaidCreaturePrefabs = Array.Empty<string>();
                return null;
            }

            _activeRaidCreaturePrefabs = ReadRaidCreaturePrefabs(activeEvent);
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
            _activeRaidCreaturePrefabs = Array.Empty<string>();
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

    private static string[] ReadRaidCreaturePrefabs(RandomEvent activeEvent)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Event spawn lists are normally single-digit. Cap unique prefabs so a modded event
        // cannot turn one requested creature refresh into an unbounded series of ZDO queries.
        for (int index = 0;
             index < activeEvent.m_spawn.Count && names.Count < MaximumRaidCreaturePrefabs;
             index++)
        {
            SpawnSystem.SpawnData? spawner = activeEvent.m_spawn[index];
            GameObject? prefab = spawner?.m_prefab;
            if (spawner == null || !spawner.m_enabled || prefab == null)
            {
                continue;
            }

            string name = prefab.name?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names.ToArray();
    }

    private sealed class PrefabDefinition
    {
        public PrefabDefinition(
            EntityGroup group,
            string name,
            string displayName = "",
            bool raidOnly = false)
        {
            Group = group;
            Name = name;
            DisplayName = displayName;
            RaidOnly = raidOnly;
        }

        public EntityGroup Group { get; }

        public string Name { get; }

        public string DisplayName { get; }

        public bool RaidOnly { get; }
    }

    private sealed class EntityGroupDefinition
    {
        public EntityGroupDefinition(string key, int maximumEntities)
        {
            Key = key;
            MaximumEntities = maximumEntities;
        }

        public string Key { get; }

        public int MaximumEntities { get; }
    }

    private enum EntityGroup
    {
        Ship,
        Cart,
        Portal,
        Tombstone,
        Ward,
        Bed,
        Creature,
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
        Array.Empty<EntityGroupSnapshot>(),
        Array.Empty<TrackedEntitySnapshot>(),
        null);

    public EntityMapSnapshot(
        int revision,
        long unixMs,
        long entitiesUnixMs,
        EntityGroupSnapshot[] groups,
        TrackedEntitySnapshot[] entities,
        RaidEventSnapshot? activeEvent)
    {
        Revision = revision;
        UnixMs = unixMs;
        EntitiesUnixMs = entitiesUnixMs;
        Groups = groups;
        Entities = entities;
        Event = activeEvent;
    }

    public int Revision { get; }

    public long UnixMs { get; }

    public long EntitiesUnixMs { get; }

    public EntityGroupSnapshot[] Groups { get; }

    public TrackedEntitySnapshot[] Entities { get; }

    public RaidEventSnapshot? Event { get; }
}

internal sealed class EntityGroupSnapshot
{
    public EntityGroupSnapshot(string key, int count, int cap, bool truncated)
    {
        Key = key;
        Count = count;
        Cap = cap;
        Truncated = truncated;
    }

    public string Key { get; }

    public int Count { get; }

    public int Cap { get; }

    public bool Truncated { get; }
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
        double? deathAgeSec,
        bool? wardEnabled,
        float? wardRadius,
        string name,
        int? level)
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
        WardEnabled = wardEnabled;
        WardRadius = wardRadius;
        Name = name;
        Level = level;
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

    public bool? WardEnabled { get; }

    public float? WardRadius { get; }

    public string Name { get; }

    public int? Level { get; }
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

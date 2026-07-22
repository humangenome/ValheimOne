using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class ResourcePoiTracker
{
    private const float RefreshIntervalSeconds = 180f;
    private const long RequestActiveMilliseconds = 5L * 60L * 1000L;
    private const int MaximumEntriesPerGroup = 5000;
    private const float ForageClusterSize = 64f;

    private static readonly ResourcePrefabDefinition[] Prefabs =
    {
        new ResourcePrefabDefinition(
            "spawner_greydwarf",
            "Spawner_GreydwarfNest",
            ResourceNodeKind.Plain),
        new ResourcePrefabDefinition(
            "spawner_bonepile",
            "BonePileSpawner",
            ResourceNodeKind.Plain),
        new ResourcePrefabDefinition(
            "spawner_draugrpile",
            "Spawner_DraugrPile",
            ResourceNodeKind.Plain),
        new ResourcePrefabDefinition("ore_copper", "rock4_copper", ResourceNodeKind.MineRock5),
        new ResourcePrefabDefinition("ore_tin", "MineRock_Tin", ResourceNodeKind.SingleHealth),
        new ResourcePrefabDefinition("ore_iron", "mudpile2", ResourceNodeKind.SingleHealth),
        new ResourcePrefabDefinition("ore_iron", "mudpile_beacon", ResourceNodeKind.SingleHealth),
        new ResourcePrefabDefinition("ore_silver", "silvervein", ResourceNodeKind.MineRock5),
        new ResourcePrefabDefinition("ore_silver", "MineRock_Silver", ResourceNodeKind.MineRock5),
        new ResourcePrefabDefinition("ore_obsidian", "MineRock_Obsidian", ResourceNodeKind.SingleHealth),
        new ResourcePrefabDefinition("ore_meteorite", "MineRock_Meteorite", ResourceNodeKind.SingleHealth),
        new ResourcePrefabDefinition("ore_leviathan", "Leviathan", ResourceNodeKind.Leviathan),
        new ResourcePrefabDefinition("forage_berries", "RaspberryBush", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_berries", "BlueberryBush", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_berries", "CloudberryBush", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_thistle", "Pickable_Thistle", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_mushroom", "Pickable_Mushroom", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_mushroom", "Pickable_Mushroom_yellow", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_mushroom", "Pickable_Mushroom_blue", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_mushroom", "Pickable_Mushroom_JotunPuffs", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_mushroom", "Pickable_Mushroom_Magecap", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_seeds", "Pickable_SeedCarrot", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_seeds", "Pickable_SeedTurnip", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_seeds", "Pickable_SeedOnion", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_crops", "Pickable_Barley", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_crops", "Pickable_Flax", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_dragonegg", "Pickable_DragonEgg", ResourceNodeKind.Pickable),
        new ResourcePrefabDefinition("forage_blackcore", "Pickable_BlackCoreStand", ResourceNodeKind.Pickable),
    };

    private static readonly int MineRockHealthHash =
        StringExtensionMethods.GetStableHashCode("Health0");

    private readonly LiveMapConfig _config;
    private readonly ModLogger _log;
    private readonly List<ZDO> _scanResults = new List<ZDO>();
    private readonly Dictionary<string, List<ResourcePoiEntry>> _pendingGroups =
        CreatePendingGroups();
    private readonly HashSet<string> _pendingTruncatedGroups =
        new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceHealthDefinition> _healthDefinitions =
        new Dictionary<string, ResourceHealthDefinition>(StringComparer.Ordinal);
    private volatile ResourcePoiMapSnapshot _snapshot = ResourcePoiMapSnapshot.Empty;
    private float _nextRefresh;
    private long _lastRequestUnixMs;
    private int _prefabIndex;
    private int _scanIndex;
    private long _scanStartedUnixMs;
    private bool _scanning;
    private bool _scanWarningLogged;

    public ResourcePoiTracker(LiveMapConfig config, ModLogger log)
    {
        _config = config;
        _log = log;
    }

    public ResourcePoiMapSnapshot Snapshot => _snapshot;

    public void NoteResourcesRequested()
    {
        Interlocked.Exchange(
            ref _lastRequestUnixMs,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Tick(float now)
    {
        if (!_config.ResourceLayers)
        {
            ResetScan();
            _nextRefresh = 0f;
            if (!ReferenceEquals(_snapshot, ResourcePoiMapSnapshot.Empty))
            {
                _snapshot = ResourcePoiMapSnapshot.Empty;
            }

            return;
        }

        if (_scanning)
        {
            ContinueScan(now);
        }
        else if (now >= _nextRefresh && ResourcesWereRecentlyRequested())
        {
            StartScan(now);
            ContinueScan(now);
        }
    }

    private bool ResourcesWereRecentlyRequested()
    {
        long requestedUnixMs = Interlocked.Read(ref _lastRequestUnixMs);
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
        foreach (List<ResourcePoiEntry> entries in _pendingGroups.Values)
        {
            entries.Clear();
        }
        _pendingTruncatedGroups.Clear();

        _prefabIndex = 0;
        _scanIndex = 0;
        _scanStartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _scanWarningLogged = false;
        _scanning = true;
        _nextRefresh = now + RefreshIntervalSeconds;
        _snapshot = _snapshot.WithScanState(true, 0, -1);
    }

    private void ContinueScan(float now)
    {
        ZDOMan? manager = ZDOMan.instance;
        if (manager == null)
        {
            ResetScan();
            _nextRefresh = now + RefreshIntervalSeconds;
            _snapshot = _snapshot.WithScanning(false);
            return;
        }

        ResourcePrefabDefinition prefab = Prefabs[_prefabIndex];
        List<ResourcePoiEntry> pending = _pendingGroups[prefab.Group];
        int pendingCount = pending.Count;
        try
        {
            bool complete = manager.GetAllZDOsWithPrefabIterative(
                prefab.Name,
                _scanResults,
                ref _scanIndex);
            if (!complete)
            {
                PublishScanProgress(manager);
                return;
            }

            ResourceHealthDefinition healthDefinition = GetHealthDefinition(prefab);
            for (int index = 0; index < _scanResults.Count; index++)
            {
                if (!prefab.Cluster && pending.Count >= MaximumEntriesPerGroup)
                {
                    break;
                }

                ZDO? zdo = _scanResults[index];
                if (zdo != null)
                {
                    pending.Add(ReadResourcePoi(prefab, zdo, healthDefinition));
                }
            }

            if (!prefab.Cluster && pending.Count >= MaximumEntriesPerGroup)
            {
                _pendingTruncatedGroups.Add(prefab.Group);
            }
        }
        catch (Exception exception)
        {
            if (pending.Count > pendingCount)
            {
                pending.RemoveRange(pendingCount, pending.Count - pendingCount);
            }

            if (!_scanWarningLogged)
            {
                _scanWarningLogged = true;
                _log.Warning(
                    $"[LiveMap] resource ZDO scan for {prefab.Name} failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        _scanResults.Clear();
        _scanIndex = 0;
        _prefabIndex++;
        if (_prefabIndex < Prefabs.Length)
        {
            PublishScanProgress(manager);
            return;
        }

        long scanUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _snapshot = BuildSnapshot(scanUnixMs);
        ResetScan();
    }

    private void PublishScanProgress(ZDOMan manager)
    {
        int sectorCount = manager.m_objectsBySector?.Length ?? 0;
        if (!_scanning || sectorCount <= 0)
        {
            return;
        }

        long totalSectors = (long)Prefabs.Length * sectorCount;
        long completedSectors = ((long)_prefabIndex * sectorCount) +
                                Math.Min(sectorCount, Math.Max(0, _scanIndex));
        int progress = (int)Math.Min(
            99L,
            completedSectors * 100L / totalSectors);
        int etaSeconds = -1;
        long elapsedMilliseconds =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _scanStartedUnixMs;
        if (completedSectors > 0L && elapsedMilliseconds > 0L)
        {
            double remainingSeconds = elapsedMilliseconds / 1000d *
                                      (totalSectors - completedSectors) /
                                      completedSectors;
            etaSeconds = (int)Math.Min(
                int.MaxValue,
                Math.Ceiling(Math.Max(0d, remainingSeconds) / 30d) * 30d);
        }

        _snapshot = _snapshot.WithScanState(true, progress, etaSeconds);
    }

    private ResourceHealthDefinition GetHealthDefinition(ResourcePrefabDefinition prefab)
    {
        if (prefab.Kind != ResourceNodeKind.MineRock5 &&
            prefab.Kind != ResourceNodeKind.SingleHealth)
        {
            return ResourceHealthDefinition.Unknown;
        }

        if (_healthDefinitions.TryGetValue(
                prefab.Name,
                out ResourceHealthDefinition? definition))
        {
            return definition;
        }

        HealthStorageKind storage = HealthStorageKind.Unknown;
        float maximumHealth = 0f;
        ZNetScene? scene = ZNetScene.instance;
        GameObject? gamePrefab = scene?.GetPrefab(prefab.Name);
        if (gamePrefab != null)
        {
            if (prefab.Kind == ResourceNodeKind.MineRock5)
            {
                MineRock5? mineRock5 = gamePrefab.GetComponent<MineRock5>();
                if (mineRock5 != null)
                {
                    storage = HealthStorageKind.MineRock5;
                    maximumHealth = GetEffectiveMineHealth(mineRock5.m_health);
                }
            }
            else
            {
                Destructible? destructible = gamePrefab.GetComponent<Destructible>();
                if (destructible != null)
                {
                    storage = HealthStorageKind.Destructible;
                    maximumHealth = GetEffectiveMineHealth(destructible.m_health);
                }
                else
                {
                    MineRock? mineRock = gamePrefab.GetComponent<MineRock>();
                    if (mineRock != null)
                    {
                        storage = HealthStorageKind.MineRock;
                        maximumHealth = GetEffectiveMineHealth(mineRock.m_health);
                    }
                }
            }
        }

        definition = new ResourceHealthDefinition(storage, maximumHealth);
        _healthDefinitions.Add(prefab.Name, definition);
        return definition;
    }

    private static float GetEffectiveMineHealth(float baseHealth)
    {
        Game? game = Game.instance;
        return game == null
            ? baseHealth
            : baseHealth +
              (Game.m_worldLevel * baseHealth * game.m_worldLevelMineHPMultiplier);
    }

    private static ResourcePoiEntry ReadResourcePoi(
        ResourcePrefabDefinition prefab,
        ZDO zdo,
        ResourceHealthDefinition healthDefinition)
    {
        Vector3 position = zdo.GetPosition();
        string state = string.Empty;
        int minedPct = 0;
        int available = -1;
        switch (prefab.Kind)
        {
            case ResourceNodeKind.MineRock5:
                ReadMineRock5State(zdo, healthDefinition.MaximumHealth, out state, out minedPct);
                break;
            case ResourceNodeKind.SingleHealth:
                ReadSingleHealthState(zdo, healthDefinition, out state, out minedPct);
                break;
            case ResourceNodeKind.Pickable:
                available = zdo.GetBool(ZDOVars.s_picked) ? 0 : 1;
                break;
            case ResourceNodeKind.Leviathan:
                if (zdo.GetBool(ZDOVars.s_dead))
                {
                    state = "submerged";
                }

                break;
        }

        return new ResourcePoiEntry(
            prefab.Name,
            prefab.Group,
            position.x,
            position.z,
            1,
            state,
            minedPct,
            available);
    }

    private static void ReadMineRock5State(
        ZDO zdo,
        float maximumAreaHealth,
        out string state,
        out int minedPct)
    {
        state = string.Empty;
        minedPct = 0;
        if (!zdo.GetString(ZDOVars.s_health, out string healthData) ||
            string.IsNullOrEmpty(healthData))
        {
            return;
        }

        // MineRock5 only writes the package after a successful damaging hit.
        state = "partial";
        if (maximumAreaHealth <= 0f)
        {
            return;
        }

        try
        {
            var package = new ZPackage(Convert.FromBase64String(healthData));
            int areaCount = package.ReadInt();
            if (areaCount <= 0 || areaCount > 10000)
            {
                return;
            }

            double missingHealth = 0d;
            for (int index = 0; index < areaCount; index++)
            {
                float areaHealth = package.ReadSingle();
                if (float.IsNaN(areaHealth) || areaHealth >= maximumAreaHealth)
                {
                    continue;
                }

                missingHealth += Math.Min(
                    maximumAreaHealth,
                    Math.Max(0d, maximumAreaHealth - areaHealth));
            }

            if (missingHealth > 0d)
            {
                minedPct = Math.Max(
                    1,
                    Math.Min(
                        100,
                        (int)Math.Round(
                            missingHealth / (maximumAreaHealth * areaCount) * 100d,
                            MidpointRounding.AwayFromZero)));
            }
        }
        catch (Exception)
        {
            // The persisted entry still proves the node was hit; omit an unsafe percentage.
        }
    }

    private static void ReadSingleHealthState(
        ZDO zdo,
        ResourceHealthDefinition definition,
        out string state,
        out int minedPct)
    {
        state = string.Empty;
        minedPct = 0;
        float currentHealth = 0f;
        bool hasHealth = definition.Storage switch
        {
            HealthStorageKind.Destructible =>
                zdo.GetFloat(ZDOVars.s_health, out currentHealth),
            HealthStorageKind.MineRock =>
                zdo.GetFloat(MineRockHealthHash, out currentHealth),
            _ => false,
        };
        if (!hasHealth)
        {
            return;
        }

        // Both components create their health entry on the first successful damaging hit.
        state = "partial";
        if (definition.MaximumHealth <= 0f || float.IsNaN(currentHealth) ||
            currentHealth >= definition.MaximumHealth)
        {
            return;
        }

        minedPct = Math.Max(
            1,
            Math.Min(
                100,
                (int)Math.Round(
                    (definition.MaximumHealth - Math.Max(0f, currentHealth)) /
                    definition.MaximumHealth * 100f,
                    MidpointRounding.AwayFromZero)));
    }

    private ResourcePoiMapSnapshot BuildSnapshot(long scanUnixMs)
    {
        var groups = new List<ResourcePoiGroupSnapshot>();
        IReadOnlyList<PoiGroupDefinition> definitions = PoiGroups.All;
        for (int index = 0; index < definitions.Count; index++)
        {
            PoiGroupDefinition definition = definitions[index];
            if (!definition.Resource)
            {
                continue;
            }

            List<ResourcePoiEntry> rawEntries = _pendingGroups[definition.Key];
            ResourcePoiEntry[] entries = definition.Category == "forage"
                ? ClusterForage(rawEntries)
                : rawEntries.ToArray();
            groups.Add(new ResourcePoiGroupSnapshot(
                definition.Key,
                entries,
                rawEntries.Count,
                _pendingTruncatedGroups.Contains(definition.Key)
                    ? MaximumEntriesPerGroup
                    : 0));
        }

        return new ResourcePoiMapSnapshot(
            scanUnixMs,
            false,
            100,
            0,
            groups.ToArray());
    }

    private static ResourcePoiEntry[] ClusterForage(List<ResourcePoiEntry> entries)
    {
        var cells = new Dictionary<long, ForageCell>();
        for (int index = 0; index < entries.Count; index++)
        {
            ResourcePoiEntry entry = entries[index];
            int cellX = (int)Math.Floor(entry.X / ForageClusterSize);
            int cellZ = (int)Math.Floor(entry.Z / ForageClusterSize);
            long key = ((long)cellX << 32) ^ (uint)cellZ;
            if (!cells.TryGetValue(key, out ForageCell? cell))
            {
                cell = new ForageCell(entry.Name, entry.Group);
                cells.Add(key, cell);
            }

            cell.X += entry.X;
            cell.Z += entry.Z;
            cell.Count++;
            cell.Available += entry.Available >= 0 ? entry.Available : 1;
        }

        var clustered = new ResourcePoiEntry[cells.Count];
        int outputIndex = 0;
        foreach (ForageCell cell in cells.Values)
        {
            clustered[outputIndex++] = new ResourcePoiEntry(
                cell.Name,
                cell.Group,
                cell.X / cell.Count,
                cell.Z / cell.Count,
                cell.Count,
                cell.Available == 0 ? "respawning" : string.Empty,
                0,
                cell.Available);
        }

        return clustered;
    }

    private void ResetScan()
    {
        _scanResults.Clear();
        foreach (List<ResourcePoiEntry> entries in _pendingGroups.Values)
        {
            entries.Clear();
        }
        _pendingTruncatedGroups.Clear();

        _prefabIndex = 0;
        _scanIndex = 0;
        _scanStartedUnixMs = 0L;
        _scanning = false;
    }

    private static Dictionary<string, List<ResourcePoiEntry>> CreatePendingGroups()
    {
        var groups = new Dictionary<string, List<ResourcePoiEntry>>(StringComparer.Ordinal);
        IReadOnlyList<PoiGroupDefinition> definitions = PoiGroups.All;
        for (int index = 0; index < definitions.Count; index++)
        {
            PoiGroupDefinition definition = definitions[index];
            if (definition.Resource)
            {
                groups.Add(definition.Key, new List<ResourcePoiEntry>());
            }
        }

        return groups;
    }

    private sealed class ResourcePrefabDefinition
    {
        public ResourcePrefabDefinition(string group, string name, ResourceNodeKind kind)
        {
            Group = group;
            Name = name;
            Kind = kind;
        }

        public string Group { get; }

        public string Name { get; }

        public ResourceNodeKind Kind { get; }

        public bool Cluster => Kind == ResourceNodeKind.Pickable;
    }

    private sealed class ForageCell
    {
        public ForageCell(string name, string group)
        {
            Name = name;
            Group = group;
        }

        public string Name { get; }

        public string Group { get; }

        public float X { get; set; }

        public float Z { get; set; }

        public int Count { get; set; }

        public int Available { get; set; }
    }

    private sealed class ResourceHealthDefinition
    {
        public static readonly ResourceHealthDefinition Unknown =
            new ResourceHealthDefinition(HealthStorageKind.Unknown, 0f);

        public ResourceHealthDefinition(HealthStorageKind storage, float maximumHealth)
        {
            Storage = storage;
            MaximumHealth = maximumHealth;
        }

        public HealthStorageKind Storage { get; }

        public float MaximumHealth { get; }
    }

    private enum ResourceNodeKind
    {
        Plain,
        MineRock5,
        SingleHealth,
        Leviathan,
        Pickable,
    }

    private enum HealthStorageKind
    {
        Unknown,
        MineRock5,
        Destructible,
        MineRock,
    }
}

internal sealed class ResourcePoiMapSnapshot
{
    public static readonly ResourcePoiMapSnapshot Empty = CreateEmpty();

    public ResourcePoiMapSnapshot(
        long lastScanUnixMs,
        bool scanning,
        int scanProgress,
        int scanEtaSeconds,
        ResourcePoiGroupSnapshot[] groups)
    {
        LastScanUnixMs = lastScanUnixMs;
        Scanning = scanning;
        ScanProgress = scanProgress;
        ScanEtaSeconds = scanEtaSeconds;
        Groups = groups;
    }

    public long LastScanUnixMs { get; }

    public bool Scanning { get; }

    public int ScanProgress { get; }

    public int ScanEtaSeconds { get; }

    public ResourcePoiGroupSnapshot[] Groups { get; }

    public bool TryGetGroup(string key, out ResourcePoiGroupSnapshot? group)
    {
        for (int index = 0; index < Groups.Length; index++)
        {
            if (string.Equals(Groups[index].Key, key, StringComparison.Ordinal))
            {
                group = Groups[index];
                return true;
            }
        }

        group = null;
        return false;
    }

    public ResourcePoiMapSnapshot WithScanning(bool scanning)
    {
        return Scanning == scanning
            ? this
            : new ResourcePoiMapSnapshot(
                LastScanUnixMs,
                scanning,
                ScanProgress,
                ScanEtaSeconds,
                Groups);
    }

    public ResourcePoiMapSnapshot WithScanState(
        bool scanning,
        int scanProgress,
        int scanEtaSeconds)
    {
        return Scanning == scanning &&
               ScanProgress == scanProgress &&
               ScanEtaSeconds == scanEtaSeconds
            ? this
            : new ResourcePoiMapSnapshot(
                LastScanUnixMs,
                scanning,
                scanProgress,
                scanEtaSeconds,
                Groups);
    }

    private static ResourcePoiMapSnapshot CreateEmpty()
    {
        var groups = new List<ResourcePoiGroupSnapshot>();
        IReadOnlyList<PoiGroupDefinition> definitions = PoiGroups.All;
        for (int index = 0; index < definitions.Count; index++)
        {
            PoiGroupDefinition definition = definitions[index];
            if (definition.Resource)
            {
                groups.Add(new ResourcePoiGroupSnapshot(
                    definition.Key,
                    Array.Empty<ResourcePoiEntry>(),
                    0,
                    0));
            }
        }

        return new ResourcePoiMapSnapshot(0L, false, 0, -1, groups.ToArray());
    }
}

internal sealed class ResourcePoiGroupSnapshot
{
    public ResourcePoiGroupSnapshot(
        string key,
        ResourcePoiEntry[] entries,
        int count,
        int cap)
    {
        Key = key;
        Entries = entries;
        Count = count;
        Cap = cap;
    }

    public string Key { get; }

    public ResourcePoiEntry[] Entries { get; }

    public int Count { get; }

    public int Cap { get; }

    public bool Truncated => Cap > 0;
}

internal sealed class ResourcePoiEntry
{
    public ResourcePoiEntry(
        string name,
        string group,
        float x,
        float z,
        int count,
        string state,
        int minedPct,
        int available)
    {
        Name = name;
        Group = group;
        X = x;
        Z = z;
        Count = count;
        State = state;
        MinedPct = minedPct;
        Available = available;
    }

    public string Name { get; }

    public string Group { get; }

    public float X { get; }

    public float Z { get; }

    public int Count { get; }

    public string State { get; }

    public int MinedPct { get; }

    public int Available { get; }
}

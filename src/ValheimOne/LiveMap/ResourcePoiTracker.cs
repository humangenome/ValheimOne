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
    private const int MaximumOreEntriesPerGroup = 5000;
    private const float ForageClusterSize = 64f;

    private static readonly ResourcePrefabDefinition[] Prefabs =
    {
        new ResourcePrefabDefinition("ore_copper", "rock4_copper", false),
        new ResourcePrefabDefinition("ore_tin", "MineRock_Tin", false),
        new ResourcePrefabDefinition("ore_iron", "mudpile2", false),
        new ResourcePrefabDefinition("ore_iron", "mudpile_beacon", false),
        new ResourcePrefabDefinition("ore_silver", "silvervein", false),
        new ResourcePrefabDefinition("ore_silver", "MineRock_Silver", false),
        new ResourcePrefabDefinition("ore_obsidian", "MineRock_Obsidian", false),
        new ResourcePrefabDefinition("ore_meteorite", "MineRock_Meteorite", false),
        new ResourcePrefabDefinition("ore_leviathan", "Leviathan", false),
        new ResourcePrefabDefinition("forage_berries", "RaspberryBush", true),
        new ResourcePrefabDefinition("forage_berries", "BlueberryBush", true),
        new ResourcePrefabDefinition("forage_berries", "CloudberryBush", true),
        new ResourcePrefabDefinition("forage_thistle", "Pickable_Thistle", true),
        new ResourcePrefabDefinition("forage_mushroom", "Pickable_Mushroom", true),
        new ResourcePrefabDefinition("forage_mushroom", "Pickable_Mushroom_yellow", true),
        new ResourcePrefabDefinition("forage_mushroom", "Pickable_Mushroom_blue", true),
        new ResourcePrefabDefinition("forage_mushroom", "Pickable_Mushroom_JotunPuffs", true),
        new ResourcePrefabDefinition("forage_mushroom", "Pickable_Mushroom_Magecap", true),
        new ResourcePrefabDefinition("forage_seeds", "Pickable_SeedCarrot", true),
        new ResourcePrefabDefinition("forage_seeds", "Pickable_SeedTurnip", true),
        new ResourcePrefabDefinition("forage_seeds", "Pickable_SeedOnion", true),
        new ResourcePrefabDefinition("forage_crops", "Pickable_Barley", true),
        new ResourcePrefabDefinition("forage_crops", "Pickable_Flax", true),
        new ResourcePrefabDefinition("forage_dragonegg", "Pickable_DragonEgg", true),
        new ResourcePrefabDefinition("forage_blackcore", "Pickable_BlackCoreStand", true),
    };

    private readonly LiveMapConfig _config;
    private readonly ModLogger _log;
    private readonly List<ZDO> _scanResults = new List<ZDO>();
    private readonly Dictionary<string, List<ResourcePoiEntry>> _pendingGroups =
        CreatePendingGroups();
    private volatile ResourcePoiMapSnapshot _snapshot = ResourcePoiMapSnapshot.Empty;
    private float _nextRefresh;
    private long _lastRequestUnixMs;
    private int _prefabIndex;
    private int _scanIndex;
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

        _prefabIndex = 0;
        _scanIndex = 0;
        _scanWarningLogged = false;
        _scanning = true;
        _nextRefresh = now + RefreshIntervalSeconds;
        _snapshot = _snapshot.WithScanning(true);
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
                return;
            }

            for (int index = 0; index < _scanResults.Count; index++)
            {
                if (!prefab.Cluster && pending.Count >= MaximumOreEntriesPerGroup)
                {
                    break;
                }

                ZDO? zdo = _scanResults[index];
                if (zdo != null)
                {
                    pending.Add(ReadResourcePoi(prefab, zdo));
                }
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
            return;
        }

        long scanUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _snapshot = BuildSnapshot(scanUnixMs);
        ResetScan();
    }

    private static ResourcePoiEntry ReadResourcePoi(
        ResourcePrefabDefinition prefab,
        ZDO zdo)
    {
        Vector3 position = zdo.GetPosition();
        return new ResourcePoiEntry(
            prefab.Name,
            prefab.Group,
            position.x,
            position.z,
            1);
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
                rawEntries.Count));
        }

        return new ResourcePoiMapSnapshot(
            scanUnixMs,
            false,
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
                cell.Count);
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

        _prefabIndex = 0;
        _scanIndex = 0;
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
        public ResourcePrefabDefinition(string group, string name, bool cluster)
        {
            Group = group;
            Name = name;
            Cluster = cluster;
        }

        public string Group { get; }

        public string Name { get; }

        public bool Cluster { get; }
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
    }
}

internal sealed class ResourcePoiMapSnapshot
{
    public static readonly ResourcePoiMapSnapshot Empty = CreateEmpty();

    public ResourcePoiMapSnapshot(
        long lastScanUnixMs,
        bool scanning,
        ResourcePoiGroupSnapshot[] groups)
    {
        LastScanUnixMs = lastScanUnixMs;
        Scanning = scanning;
        Groups = groups;
    }

    public long LastScanUnixMs { get; }

    public bool Scanning { get; }

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
            : new ResourcePoiMapSnapshot(LastScanUnixMs, scanning, Groups);
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
                    0));
            }
        }

        return new ResourcePoiMapSnapshot(0L, false, groups.ToArray());
    }
}

internal sealed class ResourcePoiGroupSnapshot
{
    public ResourcePoiGroupSnapshot(string key, ResourcePoiEntry[] entries, int count)
    {
        Key = key;
        Entries = entries;
        Count = count;
    }

    public string Key { get; }

    public ResourcePoiEntry[] Entries { get; }

    public int Count { get; }
}

internal sealed class ResourcePoiEntry
{
    public ResourcePoiEntry(string name, string group, float x, float z, int count)
    {
        Name = name;
        Group = group;
        X = x;
        Z = z;
        Count = count;
    }

    public string Name { get; }

    public string Group { get; }

    public float X { get; }

    public float Z { get; }

    public int Count { get; }
}

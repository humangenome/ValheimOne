using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class DungeonRegistry
{
    private const float RefreshIntervalSeconds = 60f;
    private const float InteriorHeightOffset = 5000f;
    private const float FixedInteriorHalfHeight = 250f;
    private const float BoundsPadding = 1f;
    private const int MaximumRoomCount = 4096;
    private const int PackedRoomRecordSize = 28;

    private readonly ZoneSystem _zoneSystem;
    private readonly ModLogger _log;
    private readonly Dictionary<int, DungeonGeneratorPrefab> _generatorPrefabs =
        new Dictionary<int, DungeonGeneratorPrefab>();
    private readonly Dictionary<int, DungeonRoomDefinition> _roomDefinitions =
        new Dictionary<int, DungeonRoomDefinition>();
    private readonly Dictionary<string, DungeonLayout> _layouts =
        new Dictionary<string, DungeonLayout>(StringComparer.Ordinal);
    private readonly HashSet<string> _scannedDungeonIds =
        new HashSet<string>(StringComparer.Ordinal);
    private readonly List<DungeonLocation> _locations = new List<DungeonLocation>();
    private readonly List<DungeonLocation> _pendingLocations = new List<DungeonLocation>();
    private readonly List<ZDO> _sectorObjects = new List<ZDO>();
    private volatile DungeonRegistrySnapshot _snapshot = DungeonRegistrySnapshot.Empty;
    private volatile DungeonBoundsIndexEntry[] _bounds = Array.Empty<DungeonBoundsIndexEntry>();
    private float _nextRefresh;
    private int _armed;
    private int _pendingIndex;
    private bool _initialScanComplete;
    private bool _scanning;
    private bool _scanWarningLogged;

    public DungeonRegistry(ZoneSystem zoneSystem, ModLogger log)
    {
        _zoneSystem = zoneSystem;
        _log = log;
        DiscoverGeneratorPrefabs();
        RefreshLocations();
        PublishSnapshot(false);
    }

    public DungeonRegistrySnapshot Snapshot => _snapshot;

    public void Arm()
    {
        Interlocked.Exchange(ref _armed, 1);
    }

    public DungeonRegistrySnapshot EnsureScanned()
    {
        Arm();
        return _snapshot;
    }

    public bool TryGetDungeonId(Vector3 playerPosition, out string dungeonId)
    {
        DungeonBoundsIndexEntry[] bounds = _bounds;
        for (int index = 0; index < bounds.Length; index++)
        {
            DungeonBoundsIndexEntry entry = bounds[index];
            if (entry.Contains(playerPosition))
            {
                dungeonId = entry.Id;
                return true;
            }
        }

        dungeonId = string.Empty;
        return false;
    }

    public void Tick(float now)
    {
        if (Interlocked.CompareExchange(ref _armed, 0, 0) == 0)
        {
            return;
        }

        if (_scanning)
        {
            ContinueScan(now);
        }
        else if (now >= _nextRefresh)
        {
            StartScan(now);
            ContinueScan(now);
        }
    }

    private void DiscoverGeneratorPrefabs()
    {
        ZNetScene? scene = ZNetScene.instance;
        if (scene == null)
        {
            return;
        }

        for (int index = 0; index < scene.m_prefabs.Count; index++)
        {
            GameObject? prefab = scene.m_prefabs[index];
            if (prefab == null)
            {
                continue;
            }

            DungeonGenerator? generator = prefab.GetComponent<DungeonGenerator>();
            if (generator == null)
            {
                continue;
            }

            int hash = scene.GetPrefabHash(prefab);
            _generatorPrefabs[hash] =
                new DungeonGeneratorPrefab(prefab.name, generator.m_zoneSize);
        }
    }

    private void StartScan(float now)
    {
        DiscoverGeneratorPrefabs();

        RefreshLocations();
        _pendingLocations.Clear();
        for (int index = 0; index < _locations.Count; index++)
        {
            DungeonLocation location = _locations[index];
            if (location.Placed &&
                location.HasInterior &&
                !_scannedDungeonIds.Contains(location.Id))
            {
                _pendingLocations.Add(location);
            }
        }

        _pendingIndex = 0;
        _scanWarningLogged = false;
        _scanning = _pendingLocations.Count > 0;
        _nextRefresh = now + RefreshIntervalSeconds;
        if (!_scanning)
        {
            _initialScanComplete = true;
        }

        PublishSnapshot(_scanning);
    }

    private void ContinueScan(float now)
    {
        if (!_scanning)
        {
            return;
        }

        ZDOMan? manager = ZDOMan.instance;
        ZNetScene? scene = ZNetScene.instance;
        DungeonDB? dungeonDb = DungeonDB.instance;
        if (manager == null || scene == null || dungeonDb == null)
        {
            _scanning = false;
            _nextRefresh = now + RefreshIntervalSeconds;
            PublishSnapshot(false);
            return;
        }

        DungeonLocation location = _pendingLocations[_pendingIndex];
        try
        {
            ScanLocation(manager, scene, dungeonDb, location);
        }
        catch (Exception exception)
        {
            if (!_scanWarningLogged)
            {
                _scanWarningLogged = true;
                _log.Warning(
                    $"[LiveMap] dungeon scan failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        _pendingIndex++;
        if (_pendingIndex < _pendingLocations.Count)
        {
            return;
        }

        _pendingLocations.Clear();
        _pendingIndex = 0;
        _scanning = false;
        _initialScanComplete = true;
        PublishSnapshot(false);
    }

    private void ScanLocation(
        ZDOMan manager,
        ZNetScene scene,
        DungeonDB dungeonDb,
        DungeonLocation location)
    {
        _sectorObjects.Clear();
        manager.FindSectorObjects(location.Zone, 0, 0, _sectorObjects);

        ZDO? generatorZdo = null;
        DungeonGeneratorPrefab? generatorPrefab = null;
        float nearestDistanceSquared = float.MaxValue;
        for (int index = 0; index < _sectorObjects.Count; index++)
        {
            ZDO? zdo = _sectorObjects[index];
            if (zdo == null || !zdo.IsValid())
            {
                continue;
            }

            Vector3 position = zdo.GetPosition();
            if (!Character.InInterior(position) ||
                !_generatorPrefabs.TryGetValue(
                    zdo.GetPrefab(),
                    out DungeonGeneratorPrefab? candidatePrefab))
            {
                continue;
            }

            GameObject? livePrefab = scene.GetPrefab(zdo.GetPrefab());
            if (livePrefab == null ||
                livePrefab.GetComponent<DungeonGenerator>() == null)
            {
                continue;
            }

            float deltaX = position.x - location.Entrance.x;
            float deltaZ = position.z - location.Entrance.z;
            float distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestDistanceSquared = distanceSquared;
                generatorZdo = zdo;
                generatorPrefab = candidatePrefab;
            }
        }

        if (generatorZdo == null || generatorPrefab == null)
        {
            // Troll Cave, PlaceofMystery3, and the Putrid Holes are fixed
            // interiors in 0.221.12. Their generated location is still
            // represented by fallback bounds. Do not cache a miss, because a
            // newly placed procedural dungeon may publish its generator ZDO
            // after the location's placed flag becomes visible.
            return;
        }

        if (TryReadLayout(
                dungeonDb,
                generatorZdo,
                generatorPrefab,
                out DungeonLayout? layout) &&
            layout != null)
        {
            _layouts[location.Id] = layout;
            _scannedDungeonIds.Add(location.Id);
        }
    }

    private bool TryReadLayout(
        DungeonDB dungeonDb,
        ZDO generatorZdo,
        DungeonGeneratorPrefab generatorPrefab,
        out DungeonLayout? layout)
    {
        if (generatorZdo.GetByteArray(ZDOVars.s_roomData, out byte[] roomData))
        {
            return TryReadPackedLayout(
                dungeonDb,
                generatorZdo,
                generatorPrefab,
                roomData,
                out layout);
        }

        int legacyRoomCount = generatorZdo.GetInt(ZDOVars.s_rooms);
        if (legacyRoomCount <= 0 || legacyRoomCount > MaximumRoomCount)
        {
            layout = null;
            return false;
        }

        var rooms = new List<DungeonRoomSnapshot>(legacyRoomCount);
        BoundsAccumulator bounds = BoundsAccumulator.Empty;
        for (int index = 0; index < legacyRoomCount; index++)
        {
            string key = "room" + index.ToString(CultureInfo.InvariantCulture);
            int roomHash = generatorZdo.GetInt(key);
            Vector3 position = generatorZdo.GetVec3(key + "_pos", Vector3.zero);
            Quaternion rotation = generatorZdo.GetQuaternion(
                key + "_rot",
                Quaternion.identity);
            AddRoom(
                dungeonDb,
                rooms,
                ref bounds,
                roomHash,
                position,
                rotation,
                NormalizeDegrees(rotation.eulerAngles.y));
        }

        layout = CreateLayout(generatorZdo, generatorPrefab, rooms, bounds);
        return true;
    }

    private bool TryReadPackedLayout(
        DungeonDB dungeonDb,
        ZDO generatorZdo,
        DungeonGeneratorPrefab generatorPrefab,
        byte[] roomData,
        out DungeonLayout? layout)
    {
        if (roomData.Length < sizeof(int))
        {
            layout = null;
            return false;
        }

        using var stream = new MemoryStream(roomData, false);
        using var reader = new BinaryReader(stream);
        int roomCount = reader.ReadInt32();
        long expectedLength =
            sizeof(int) + ((long)roomCount * PackedRoomRecordSize);
        if (roomCount <= 0 ||
            roomCount > MaximumRoomCount ||
            expectedLength != roomData.Length)
        {
            layout = null;
            return false;
        }

        var rooms = new List<DungeonRoomSnapshot>(roomCount);
        BoundsAccumulator bounds = BoundsAccumulator.Empty;
        for (int index = 0; index < roomCount; index++)
        {
            int roomHash = reader.ReadInt32();
            var position = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
            var euler = new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
            Quaternion rotation = Quaternion.Euler(euler);
            AddRoom(
                dungeonDb,
                rooms,
                ref bounds,
                roomHash,
                position,
                rotation,
                NormalizeDegrees(euler.y));
        }

        layout = CreateLayout(generatorZdo, generatorPrefab, rooms, bounds);
        return true;
    }

    private void AddRoom(
        DungeonDB dungeonDb,
        List<DungeonRoomSnapshot> rooms,
        ref BoundsAccumulator bounds,
        int roomHash,
        Vector3 position,
        Quaternion rotation,
        float rotationYDegrees)
    {
        DungeonRoomDefinition definition =
            GetRoomDefinition(dungeonDb, roomHash);
        Vector3Int size = definition.Size;
        rooms.Add(
            new DungeonRoomSnapshot(
                roomHash,
                definition.Name,
                position.x,
                position.y,
                position.z,
                rotationYDegrees,
                size.x,
                size.y,
                size.z));
        bounds.IncludeRoom(position, rotation, size);
    }

    private static float NormalizeDegrees(float degrees)
    {
        if (float.IsNaN(degrees) || float.IsInfinity(degrees))
        {
            return 0f;
        }

        float normalized = degrees % 360f;
        return normalized < 0f ? normalized + 360f : normalized;
    }

    private DungeonRoomDefinition GetRoomDefinition(
        DungeonDB dungeonDb,
        int roomHash)
    {
        if (_roomDefinitions.TryGetValue(
                roomHash,
                out DungeonRoomDefinition? definition))
        {
            return definition;
        }

        DungeonDB.RoomData? roomData = dungeonDb.GetRoom(roomHash);
        if (roomData == null)
        {
            definition = new DungeonRoomDefinition(
                "room#" + roomHash.ToString(CultureInfo.InvariantCulture),
                Vector3Int.zero);
            _roomDefinitions.Add(roomHash, definition);
            return definition;
        }

        bool release = false;
        try
        {
            if (!roomData.m_prefab.IsLoaded)
            {
                roomData.m_prefab.Load();
                release = true;
            }

            GameObject? roomPrefab = roomData.m_prefab.Asset;
            Room? room = roomPrefab?.GetComponent<Room>();
            definition = new DungeonRoomDefinition(
                roomData.m_prefab.Name,
                room == null ? Vector3Int.zero : room.m_size);
            _roomDefinitions.Add(roomHash, definition);
            return definition;
        }
        finally
        {
            if (release)
            {
                roomData.m_prefab.Release();
            }
        }
    }

    private static DungeonLayout CreateLayout(
        ZDO generatorZdo,
        DungeonGeneratorPrefab generatorPrefab,
        List<DungeonRoomSnapshot> rooms,
        BoundsAccumulator bounds)
    {
        Vector3 origin = generatorZdo.GetPosition();
        if (!bounds.HasValue)
        {
            Vector3 halfSize = generatorPrefab.ZoneSize * 0.5f;
            bounds.IncludePoint(origin - halfSize);
            bounds.IncludePoint(origin + halfSize);
        }

        return new DungeonLayout(
            generatorPrefab.Name,
            origin,
            bounds.ToSnapshot(),
            rooms.ToArray());
    }

    private void RefreshLocations()
    {
        _locations.Clear();
        foreach (KeyValuePair<Vector2i, ZoneSystem.LocationInstance> pair in
                 _zoneSystem.m_locationInstances)
        {
            ZoneSystem.LocationInstance instance = pair.Value;
            ZoneSystem.ZoneLocation? location = instance.m_location;
            string name = GetLocationName(location);
            string type = GetDungeonType(
                name,
                location != null && location.m_iconAlways,
                location != null && location.m_iconPlaced);
            if (!type.StartsWith("dungeon_", StringComparison.Ordinal))
            {
                continue;
            }

            string id = string.Concat(
                name,
                "@",
                pair.Key.x.ToString(CultureInfo.InvariantCulture),
                ",",
                pair.Key.y.ToString(CultureInfo.InvariantCulture));
            _locations.Add(
                new DungeonLocation(
                    id,
                    name,
                    type,
                    GetTypeLabel(type, name),
                    pair.Key,
                    instance.m_position,
                    instance.m_placed,
                    location?.m_interiorRadius ?? 0f));
        }

        _locations.Sort(DungeonLocation.Compare);
    }

    private void PublishSnapshot(bool scanning)
    {
        var dungeons = new DungeonSnapshot[_locations.Count];
        var bounds = new List<DungeonBoundsIndexEntry>();
        int generatedCount = 0;
        for (int index = 0; index < _locations.Count; index++)
        {
            DungeonLocation location = _locations[index];
            _layouts.TryGetValue(location.Id, out DungeonLayout? layout);
            bool generated = location.Placed || layout != null;
            if (generated)
            {
                generatedCount++;
            }

            DungeonInteriorSnapshot? interior = null;
            if (generated && location.HasInterior)
            {
                interior = layout == null
                    ? CreateFixedInteriorSnapshot(location)
                    : layout.ToSnapshot();
                bounds.Add(new DungeonBoundsIndexEntry(location.Id, interior.Bounds));
            }

            dungeons[index] = new DungeonSnapshot(
                location.Id,
                location.Name,
                location.Type,
                location.Label,
                location.Zone.x,
                location.Zone.y,
                location.Entrance.x,
                location.Entrance.y,
                location.Entrance.z,
                location.HasInterior,
                generated,
                interior);
        }

        _bounds = bounds.ToArray();
        _snapshot = new DungeonRegistrySnapshot(
            dungeons,
            scanning,
            _initialScanComplete,
            generatedCount,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static DungeonInteriorSnapshot CreateFixedInteriorSnapshot(
        DungeonLocation location)
    {
        float radius = Math.Max(1f, location.InteriorRadius);
        var origin = new Vector3(
            location.Entrance.x,
            location.Entrance.y + InteriorHeightOffset,
            location.Entrance.z);
        var bounds = new DungeonBoundsSnapshot(
            origin.x - radius,
            origin.x + radius,
            origin.z - radius,
            origin.z + radius,
            origin.y - FixedInteriorHalfHeight,
            origin.y + FixedInteriorHalfHeight);
        return new DungeonInteriorSnapshot(
            string.Empty,
            false,
            origin.x,
            origin.y,
            origin.z,
            bounds,
            Array.Empty<DungeonRoomSnapshot>());
    }

    private static string GetLocationName(ZoneSystem.ZoneLocation? location)
    {
        if (location == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(location.m_prefabName))
        {
            return location.m_prefabName.Trim();
        }

        return (location.m_name ?? string.Empty).Trim();
    }

    private static string GetDungeonType(
        string name,
        bool iconAlways,
        bool iconPlaced)
    {
        // The shared POI classifier intentionally treats Hildir-prefixed
        // locations as trader content. Her three quest locations are dungeons,
        // though, and two use interior DungeonGenerators.
        if (string.Equals(
                name,
                "Hildir_crypt",
                StringComparison.OrdinalIgnoreCase))
        {
            return "dungeon_crypt";
        }

        if (string.Equals(
                name,
                "Hildir_cave",
                StringComparison.OrdinalIgnoreCase))
        {
            return "dungeon_frostcave";
        }

        if (string.Equals(
                name,
                "Hildir_plainsfortress",
                StringComparison.OrdinalIgnoreCase))
        {
            return "dungeon_ashlands";
        }

        if (name.StartsWith(
                "MorgenHole",
                StringComparison.OrdinalIgnoreCase))
        {
            return "dungeon_ashlands";
        }

        if (string.Equals(
                name,
                "Mistlands_DvergrBossEntrance1",
                StringComparison.OrdinalIgnoreCase))
        {
            return "dungeon_mine";
        }

        return PoiClassifier.Classify(name, iconAlways, iconPlaced);
    }

    private static string GetTypeLabel(string type, string locationName)
    {
        if (string.Equals(
                locationName,
                "Hildir_crypt",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Smouldering Tomb";
        }

        if (string.Equals(
                locationName,
                "Hildir_cave",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Howling Cavern";
        }

        if (string.Equals(
                locationName,
                "Hildir_plainsfortress",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Sealed Tower";
        }

        if (locationName.StartsWith(
                "MorgenHole",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Putrid Hole";
        }

        if (string.Equals(
                locationName,
                "Mistlands_DvergrBossEntrance1",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Infested Citadel";
        }

        switch (type)
        {
            case "dungeon_sunkencrypt":
                return "Sunken Crypt";
            case "dungeon_trollcave":
                return "Troll Cave";
            case "dungeon_frostcave":
                return "Frost Cave";
            case "dungeon_mine":
                return "Infested Mine";
            case "dungeon_ashlands":
                return "Ashlands Dungeon";
            case "dungeon_crypt":
            default:
                return "Burial Chamber";
        }
    }

    private sealed class DungeonGeneratorPrefab
    {
        public DungeonGeneratorPrefab(string name, Vector3 zoneSize)
        {
            Name = name;
            ZoneSize = zoneSize;
        }

        public string Name { get; }

        public Vector3 ZoneSize { get; }
    }

    private sealed class DungeonRoomDefinition
    {
        public DungeonRoomDefinition(string name, Vector3Int size)
        {
            Name = name;
            Size = size;
        }

        public string Name { get; }

        public Vector3Int Size { get; }
    }

    private sealed class DungeonLayout
    {
        private readonly DungeonRoomSnapshot[] _rooms;

        public DungeonLayout(
            string generatorPrefab,
            Vector3 origin,
            DungeonBoundsSnapshot bounds,
            DungeonRoomSnapshot[] rooms)
        {
            GeneratorPrefab = generatorPrefab;
            Origin = origin;
            Bounds = bounds;
            _rooms = rooms;
        }

        public string GeneratorPrefab { get; }

        public Vector3 Origin { get; }

        public DungeonBoundsSnapshot Bounds { get; }

        public DungeonInteriorSnapshot ToSnapshot()
        {
            return new DungeonInteriorSnapshot(
                GeneratorPrefab,
                true,
                Origin.x,
                Origin.y,
                Origin.z,
                Bounds,
                _rooms);
        }
    }

    private readonly struct DungeonLocation
    {
        public DungeonLocation(
            string id,
            string name,
            string type,
            string label,
            Vector2i zone,
            Vector3 entrance,
            bool placed,
            float interiorRadius)
        {
            Id = id;
            Name = name;
            Type = type;
            Label = label;
            Zone = zone;
            Entrance = entrance;
            Placed = placed;
            InteriorRadius = interiorRadius;
        }

        public string Id { get; }

        public string Name { get; }

        public string Type { get; }

        public string Label { get; }

        public Vector2i Zone { get; }

        public Vector3 Entrance { get; }

        public bool Placed { get; }

        public float InteriorRadius { get; }

        public bool HasInterior => InteriorRadius > 0f;

        public static int Compare(DungeonLocation left, DungeonLocation right)
        {
            int typeComparison = string.CompareOrdinal(left.Type, right.Type);
            return typeComparison != 0
                ? typeComparison
                : string.CompareOrdinal(left.Id, right.Id);
        }
    }

    private readonly struct DungeonBoundsIndexEntry
    {
        public DungeonBoundsIndexEntry(string id, DungeonBoundsSnapshot bounds)
        {
            Id = id;
            Bounds = bounds;
        }

        public string Id { get; }

        private DungeonBoundsSnapshot Bounds { get; }

        public bool Contains(Vector3 position)
        {
            return position.x >= Bounds.MinX &&
                   position.x <= Bounds.MaxX &&
                   position.z >= Bounds.MinZ &&
                   position.z <= Bounds.MaxZ &&
                   position.y >= Bounds.MinY &&
                   position.y <= Bounds.MaxY;
        }
    }

    private struct BoundsAccumulator
    {
        public static BoundsAccumulator Empty => new BoundsAccumulator
        {
            MinX = float.MaxValue,
            MaxX = float.MinValue,
            MinZ = float.MaxValue,
            MaxZ = float.MinValue,
            MinY = float.MaxValue,
            MaxY = float.MinValue,
        };

        public float MinX;
        public float MaxX;
        public float MinZ;
        public float MaxZ;
        public float MinY;
        public float MaxY;
        public bool HasValue;

        public void IncludeRoom(
            Vector3 position,
            Quaternion rotation,
            Vector3Int size)
        {
            Vector3 halfSize = (Vector3)size * 0.5f;
            if (halfSize == Vector3.zero)
            {
                IncludePoint(position);
                return;
            }

            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        var corner = new Vector3(
                            halfSize.x * x,
                            halfSize.y * y,
                            halfSize.z * z);
                        IncludePoint(position + (rotation * corner));
                    }
                }
            }
        }

        public void IncludePoint(Vector3 point)
        {
            MinX = Math.Min(MinX, point.x);
            MaxX = Math.Max(MaxX, point.x);
            MinZ = Math.Min(MinZ, point.z);
            MaxZ = Math.Max(MaxZ, point.z);
            MinY = Math.Min(MinY, point.y);
            MaxY = Math.Max(MaxY, point.y);
            HasValue = true;
        }

        public DungeonBoundsSnapshot ToSnapshot()
        {
            return new DungeonBoundsSnapshot(
                MinX - BoundsPadding,
                MaxX + BoundsPadding,
                MinZ - BoundsPadding,
                MaxZ + BoundsPadding,
                MinY - BoundsPadding,
                MaxY + BoundsPadding);
        }
    }
}

internal sealed class DungeonRegistrySnapshot
{
    public static DungeonRegistrySnapshot Empty { get; } =
        new DungeonRegistrySnapshot(
            Array.Empty<DungeonSnapshot>(),
            false,
            false,
            0,
            0L);

    private readonly ReadOnlyCollection<DungeonSnapshot> _dungeons;

    public DungeonRegistrySnapshot(
        DungeonSnapshot[] dungeons,
        bool scanning,
        bool initialScanComplete,
        int generatedCount,
        long refreshedUnixMs)
    {
        _dungeons = Array.AsReadOnly(dungeons);
        Scanning = scanning;
        InitialScanComplete = initialScanComplete;
        GeneratedCount = generatedCount;
        RefreshedUnixMs = refreshedUnixMs;
    }

    public IReadOnlyList<DungeonSnapshot> Dungeons => _dungeons;

    public bool Scanning { get; }

    public bool InitialScanComplete { get; }

    public int GeneratedCount { get; }

    public long RefreshedUnixMs { get; }
}

internal sealed class DungeonSnapshot
{
    public DungeonSnapshot(
        string id,
        string locationName,
        string type,
        string label,
        int zoneX,
        int zoneY,
        float entranceX,
        float entranceY,
        float entranceZ,
        bool hasInterior,
        bool generated,
        DungeonInteriorSnapshot? interior)
    {
        Id = id;
        LocationName = locationName;
        Type = type;
        Label = label;
        ZoneX = zoneX;
        ZoneY = zoneY;
        EntranceX = entranceX;
        EntranceY = entranceY;
        EntranceZ = entranceZ;
        HasInterior = hasInterior;
        Generated = generated;
        Interior = interior;
    }

    public string Id { get; }

    public string LocationName { get; }

    public string Type { get; }

    public string Label { get; }

    public int ZoneX { get; }

    public int ZoneY { get; }

    public float EntranceX { get; }

    public float EntranceY { get; }

    public float EntranceZ { get; }

    public bool HasInterior { get; }

    public bool Generated { get; }

    public DungeonInteriorSnapshot? Interior { get; }
}

internal sealed class DungeonInteriorSnapshot
{
    private readonly ReadOnlyCollection<DungeonRoomSnapshot> _rooms;

    public DungeonInteriorSnapshot(
        string generatorPrefab,
        bool procedural,
        float originX,
        float originY,
        float originZ,
        DungeonBoundsSnapshot bounds,
        DungeonRoomSnapshot[] rooms)
    {
        GeneratorPrefab = generatorPrefab;
        Procedural = procedural;
        OriginX = originX;
        OriginY = originY;
        OriginZ = originZ;
        Bounds = bounds;
        _rooms = Array.AsReadOnly(rooms);
    }

    public string GeneratorPrefab { get; }

    public bool Procedural { get; }

    public float OriginX { get; }

    public float OriginY { get; }

    public float OriginZ { get; }

    public DungeonBoundsSnapshot Bounds { get; }

    public IReadOnlyList<DungeonRoomSnapshot> Rooms => _rooms;
}

internal sealed class DungeonBoundsSnapshot
{
    public DungeonBoundsSnapshot(
        float minX,
        float maxX,
        float minZ,
        float maxZ,
        float minY,
        float maxY)
    {
        MinX = minX;
        MaxX = maxX;
        MinZ = minZ;
        MaxZ = maxZ;
        MinY = minY;
        MaxY = maxY;
    }

    public float MinX { get; }

    public float MaxX { get; }

    public float MinZ { get; }

    public float MaxZ { get; }

    public float MinY { get; }

    public float MaxY { get; }
}

internal sealed class DungeonRoomSnapshot
{
    public DungeonRoomSnapshot(
        int hash,
        string name,
        float x,
        float y,
        float z,
        float rotationYDegrees,
        int sizeX,
        int sizeY,
        int sizeZ)
    {
        Hash = hash;
        Name = name;
        X = x;
        Y = y;
        Z = z;
        RotationYDegrees = rotationYDegrees;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
    }

    public int Hash { get; }

    public string Name { get; }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public float RotationYDegrees { get; }

    public int SizeX { get; }

    public int SizeY { get; }

    public int SizeZ { get; }
}

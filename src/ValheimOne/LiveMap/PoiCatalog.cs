using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimOne.LiveMap;

internal sealed class PoiCatalog
{
    private readonly PoiSnapshot[] _locations;
    private readonly PoiSnapshot[] _servedPois;

    private PoiCatalog(PoiSnapshot[] locations, PoiSnapshot[] servedPois)
    {
        _locations = locations;
        _servedPois = servedPois;
    }

    public static PoiCatalog Empty { get; } = new PoiCatalog(
        Array.Empty<PoiSnapshot>(),
        Array.Empty<PoiSnapshot>());

    public int TotalLocations => _locations.Length;

    public IReadOnlyList<PoiSnapshot> ServedPois => _servedPois;

    public static PoiCatalog Build(ZoneSystem zoneSystem)
    {
        Dictionary<Vector2i, ZoneSystem.LocationInstance> instances = zoneSystem.m_locationInstances;
        var locations = new List<PoiSnapshot>(instances.Count);
        var servedPois = new List<PoiSnapshot>(Math.Min(instances.Count, 2048));

        foreach (KeyValuePair<Vector2i, ZoneSystem.LocationInstance> pair in instances)
        {
            ZoneSystem.LocationInstance instance = pair.Value;
            ZoneSystem.ZoneLocation? location = instance.m_location;
            string name = GetLocationName(location);
            bool iconAlways = location != null && location.m_iconAlways;
            bool iconPlaced = location != null && location.m_iconPlaced;
            Vector3 position = instance.m_position;
            string group = PoiClassifier.Classify(name, iconAlways, iconPlaced);
            var poi = new PoiSnapshot(
                name,
                group,
                position.x,
                position.z,
                instance.m_placed,
                iconAlways,
                iconPlaced);

            locations.Add(poi);
            if (ShouldServe(poi))
            {
                servedPois.Add(poi);
            }
        }

        return new PoiCatalog(locations.ToArray(), servedPois.ToArray());
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

    private static bool ShouldServe(PoiSnapshot poi)
    {
        return !string.Equals(poi.Group, PoiClassifier.OtherGroup, StringComparison.Ordinal) ||
               poi.IconAlways ||
               poi.IconPlaced;
    }
}

internal sealed class PoiSnapshot
{
    public PoiSnapshot(
        string name,
        string group,
        float x,
        float z,
        bool placed,
        bool iconAlways,
        bool iconPlaced)
    {
        Name = name;
        Group = group;
        X = x;
        Z = z;
        Placed = placed;
        IconAlways = iconAlways;
        IconPlaced = iconPlaced;
    }

    public string Name { get; }

    public string Group { get; }

    public float X { get; }

    public float Z { get; }

    public bool Placed { get; }

    public bool IconAlways { get; }

    public bool IconPlaced { get; }
}

internal static class PoiClassifier
{
    public const string OtherGroup = "other";

    // Keep all location-name rules together so additions for future game versions are obvious.
    private static readonly string[] BossExactNames =
    {
        "Eikthyrnir",
        "GDKing",
        "Bonemass",
        "Dragonqueen",
        "GoblinKing",
        "Mistlands_DvergrBossEntrance1",
    };

    private static readonly string[] BossNameFragments =
    {
        "boss",
        "FaderLocation",
        "PlaceofMystery",
    };

    private static readonly string[] SpawnExactNames =
    {
        "StartTemple",
    };

    private static readonly string[] TraderExactNames =
    {
        "Vendor_BlackForest",
        "Hildir_camp",
        "BogWitch_Camp",
    };

    private static readonly string[] TraderPrefixes =
    {
        "Vendor",
        "Hildir",
        "BogWitch",
    };

    private static readonly string[] DungeonNameFragments =
    {
        "Crypt",
        "TrollCave",
        "MountainCave",
        "DvergrTown",
        "Mistlands_DvergrTownEntrance",
        "SunkenCrypt",
        "Grave",
        "FireHole",
    };

    private static readonly string[] SpawnerNameFragments =
    {
        "Spawner",
    };

    public static string Classify(string name, bool iconAlways, bool iconPlaced)
    {
        if (MatchesExact(name, BossExactNames) || ContainsAny(name, BossNameFragments))
        {
            return "boss";
        }

        if (MatchesExact(name, SpawnExactNames))
        {
            return "spawn";
        }

        if (MatchesExact(name, TraderExactNames) || StartsWithAny(name, TraderPrefixes))
        {
            return "trader";
        }

        if (ContainsAny(name, DungeonNameFragments))
        {
            return "dungeon";
        }

        if (ContainsAny(name, SpawnerNameFragments))
        {
            return "spawner";
        }

        return iconAlways || iconPlaced ? "misc" : OtherGroup;
    }

    private static bool MatchesExact(string name, string[] candidates)
    {
        for (int index = 0; index < candidates.Length; index++)
        {
            if (string.Equals(name, candidates[index], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string name, string[] fragments)
    {
        for (int index = 0; index < fragments.Length; index++)
        {
            if (name.IndexOf(fragments[index], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithAny(string name, string[] prefixes)
    {
        for (int index = 0; index < prefixes.Length; index++)
        {
            if (name.StartsWith(prefixes[index], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

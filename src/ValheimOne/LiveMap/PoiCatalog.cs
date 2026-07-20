using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimOne.LiveMap;

internal sealed class PoiCatalog
{
    private readonly PoiSnapshot[] _locations;
    private readonly PoiSnapshot[] _servedPois;
    private readonly Dictionary<string, int> _counts;

    private PoiCatalog(
        PoiSnapshot[] locations,
        PoiSnapshot[] servedPois,
        Dictionary<string, int> counts)
    {
        _locations = locations;
        _servedPois = servedPois;
        _counts = counts;
    }

    public static PoiCatalog Empty { get; } = new PoiCatalog(
        Array.Empty<PoiSnapshot>(),
        Array.Empty<PoiSnapshot>(),
        CreateEmptyCounts());

    public int TotalLocations => _locations.Length;

    public IReadOnlyList<PoiSnapshot> ServedPois => _servedPois;

    public int GetCount(string group)
    {
        return _counts.TryGetValue(group, out int count) ? count : 0;
    }

    public static PoiCatalog Build(ZoneSystem zoneSystem)
    {
        Dictionary<Vector2i, ZoneSystem.LocationInstance> instances = zoneSystem.m_locationInstances;
        var locations = new List<PoiSnapshot>(instances.Count);
        var servedPois = new List<PoiSnapshot>(Math.Min(instances.Count, 2048));
        Dictionary<string, int> counts = CreateEmptyCounts();

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
                if (counts.TryGetValue(group, out int count))
                {
                    counts[group] = count + 1;
                }
            }
        }

        return new PoiCatalog(locations.ToArray(), servedPois.ToArray(), counts);
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

    private static Dictionary<string, int> CreateEmptyCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        IReadOnlyList<PoiGroupDefinition> definitions = PoiGroups.All;
        for (int index = 0; index < definitions.Count; index++)
        {
            PoiGroupDefinition definition = definitions[index];
            if (!definition.Resource)
            {
                counts.Add(definition.Key, 0);
            }
        }

        return counts;
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

    private static readonly PoiRule[] Rules =
    {
        new PoiRule("spawn", MatchKind.Exact, "StartTemple"),
        new PoiRule(
            "boss",
            MatchKind.Exact,
            "Eikthyrnir",
            "GDKing",
            "Bonemass",
            "Dragonqueen",
            "GoblinKing",
            "Mistlands_DvergrBossEntrance1",
            "FaderLocation"),
        new PoiRule("boss", MatchKind.Contains, "boss"),
        new PoiRule(
            "trader",
            MatchKind.Exact,
            "Vendor_BlackForest",
            "Hildir_camp",
            "BogWitch_Camp"),
        new PoiRule("trader", MatchKind.StartsWith, "Vendor", "Hildir", "BogWitch"),
        new PoiRule("dungeon_sunkencrypt", MatchKind.StartsWith, "SunkenCrypt"),
        new PoiRule("dungeon_trollcave", MatchKind.StartsWith, "TrollCave"),
        new PoiRule("dungeon_frostcave", MatchKind.StartsWith, "MountainCave"),
        new PoiRule(
            "dungeon_mine",
            MatchKind.Contains,
            "DvergrTownEntrance",
            "DvergrTown"),
        new PoiRule(
            "dungeon_ashlands",
            MatchKind.StartsWith,
            "CharredFortress",
            "PlaceofMystery",
            "FortressRuins"),
        new PoiRule(
            "dungeon_crypt",
            MatchKind.Contains,
            new[] { "Crypt", "Grave" },
            new[] { "SunkenCrypt" }),
        new PoiRule("spawner_greydwarf", MatchKind.Contains, "GreydwarfNest"),
        new PoiRule("spawner_bonepile", MatchKind.Contains, "BonePile"),
        new PoiRule("spawner_draugrpile", MatchKind.Contains, "DraugrPile"),
        new PoiRule("spawner_firehole", MatchKind.Exact, "FireHole"),
        new PoiRule("spawner_other", MatchKind.Contains, "Spawner"),
        new PoiRule("structure_camp", MatchKind.StartsWith, "GoblinCamp"),
        new PoiRule("structure_camp", MatchKind.Contains, "DraugrVillage"),
        new PoiRule("structure_tarpit", MatchKind.StartsWith, "TarPit"),
        new PoiRule("structure_shipwreck", MatchKind.StartsWith, "ShipWreck"),
        new PoiRule(
            "structure_ruins",
            MatchKind.StartsWith,
            "WoodFarm",
            "WoodVillage",
            "SwampHut",
            "SwampRuin",
            "StoneTowerRuins",
            "StoneHouse",
            "StoneTower",
            "AbandonedLogCabin"),
        new PoiRule("structure_ruins", MatchKind.Exact, "Ruin1", "Ruin2", "Ruin3"),
        new PoiRule(
            "structure_mistlands",
            MatchKind.StartsWith,
            "Mistlands_GuardTower",
            "Mistlands_Excavation",
            "Mistlands_Harbour",
            "Mistlands_Viaduct",
            "Mistlands_Giant",
            "Mistlands_Statue",
            "Mistlands_Swords"),
        new PoiRule("structure_runestone", MatchKind.StartsWith, "Runestone"),
        new PoiRule("structure_runestone", MatchKind.Contains, "StoneCircle", "Dolmen"),
    };

    public static string Classify(string name, bool iconAlways, bool iconPlaced)
    {
        for (int index = 0; index < Rules.Length; index++)
        {
            if (Rules[index].Matches(name))
            {
                return Rules[index].Group;
            }
        }

        return iconAlways || iconPlaced ? "misc" : OtherGroup;
    }

    private enum MatchKind
    {
        Exact,
        Contains,
        StartsWith,
    }

    private sealed class PoiRule
    {
        private readonly MatchKind _kind;
        private readonly string[] _patterns;
        private readonly string[] _excludedFragments;

        public PoiRule(string group, MatchKind kind, params string[] patterns)
            : this(group, kind, patterns, Array.Empty<string>())
        {
        }

        public PoiRule(
            string group,
            MatchKind kind,
            string[] patterns,
            string[] excludedFragments)
        {
            Group = group;
            _kind = kind;
            _patterns = patterns;
            _excludedFragments = excludedFragments;
        }

        public string Group { get; }

        public bool Matches(string name)
        {
            for (int index = 0; index < _excludedFragments.Length; index++)
            {
                if (name.IndexOf(
                        _excludedFragments[index],
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }

            for (int index = 0; index < _patterns.Length; index++)
            {
                string pattern = _patterns[index];
                bool matches;
                switch (_kind)
                {
                    case MatchKind.Exact:
                        matches = string.Equals(
                            name,
                            pattern,
                            StringComparison.OrdinalIgnoreCase);
                        break;
                    case MatchKind.StartsWith:
                        matches = name.StartsWith(
                            pattern,
                            StringComparison.OrdinalIgnoreCase);
                        break;
                    case MatchKind.Contains:
                    default:
                        matches = name.IndexOf(
                            pattern,
                            StringComparison.OrdinalIgnoreCase) >= 0;
                        break;
                }

                if (matches)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

internal sealed class PoiGroupDefinition
{
    public PoiGroupDefinition(string key, string label, string category, bool resource)
    {
        Key = key;
        Label = label;
        Category = category;
        Resource = resource;
    }

    public string Key { get; }

    public string Label { get; }

    public string Category { get; }

    public bool Resource { get; }

    public bool Inline => !Resource;
}

internal static class PoiGroups
{
    private static readonly PoiGroupDefinition[] Definitions =
    {
        new PoiGroupDefinition("spawn", "Spawn", "bosses", false),
        new PoiGroupDefinition("boss", "Boss altars", "bosses", false),
        new PoiGroupDefinition("trader", "Traders", "bosses", false),
        new PoiGroupDefinition("dungeon_crypt", "Burial Chambers", "dungeons", false),
        new PoiGroupDefinition("dungeon_sunkencrypt", "Sunken Crypts", "dungeons", false),
        new PoiGroupDefinition("dungeon_trollcave", "Troll Caves", "dungeons", false),
        new PoiGroupDefinition("dungeon_frostcave", "Frost Caves", "dungeons", false),
        new PoiGroupDefinition("dungeon_mine", "Infested Mines", "dungeons", false),
        new PoiGroupDefinition("dungeon_ashlands", "Ashlands Ruins", "dungeons", false),
        new PoiGroupDefinition("spawner_greydwarf", "Greydwarf Nests", "spawners", false),
        new PoiGroupDefinition("spawner_bonepile", "Skeleton Spawners", "spawners", false),
        new PoiGroupDefinition("spawner_draugrpile", "Draugr Spawners", "spawners", false),
        new PoiGroupDefinition("spawner_firehole", "Surtling Geysers", "spawners", false),
        new PoiGroupDefinition("spawner_other", "Other Spawners", "spawners", false),
        new PoiGroupDefinition("ore_copper", "Copper", "ores", true),
        new PoiGroupDefinition("ore_tin", "Tin", "ores", true),
        new PoiGroupDefinition("ore_iron", "Muddy Scrap Piles", "ores", true),
        new PoiGroupDefinition("ore_silver", "Silver Veins", "ores", true),
        new PoiGroupDefinition("ore_obsidian", "Obsidian", "ores", true),
        new PoiGroupDefinition("ore_meteorite", "Meteorite", "ores", true),
        new PoiGroupDefinition("ore_leviathan", "Leviathans", "ores", true),
        new PoiGroupDefinition("forage_berries", "Berry Bushes", "forage", true),
        new PoiGroupDefinition("forage_thistle", "Thistle", "forage", true),
        new PoiGroupDefinition("forage_mushroom", "Mushrooms", "forage", true),
        new PoiGroupDefinition("forage_seeds", "Wild Seeds", "forage", true),
        new PoiGroupDefinition("forage_crops", "Barley & Flax", "forage", true),
        new PoiGroupDefinition("forage_dragonegg", "Dragon Eggs", "forage", true),
        new PoiGroupDefinition("forage_blackcore", "Black Cores", "forage", true),
        new PoiGroupDefinition("structure_camp", "Enemy Camps", "structures", false),
        new PoiGroupDefinition("structure_tarpit", "Tar Pits", "structures", false),
        new PoiGroupDefinition("structure_shipwreck", "Shipwrecks", "structures", false),
        new PoiGroupDefinition("structure_ruins", "Ruins & Villages", "structures", false),
        new PoiGroupDefinition("structure_mistlands", "Mistlands Remains", "structures", false),
        new PoiGroupDefinition("structure_runestone", "Runestones & Lore", "structures", false),
        new PoiGroupDefinition("misc", "Misc", "structures", false),
    };

    public static IReadOnlyList<PoiGroupDefinition> All => Definitions;

    public static bool TryGet(string key, out PoiGroupDefinition? definition)
    {
        for (int index = 0; index < Definitions.Length; index++)
        {
            if (string.Equals(Definitions[index].Key, key, StringComparison.Ordinal))
            {
                definition = Definitions[index];
                return true;
            }
        }

        definition = null;
        return false;
    }

    public static bool IsPublic(string key)
    {
        return string.Equals(key, "spawn", StringComparison.Ordinal) ||
               string.Equals(key, "trader", StringComparison.Ordinal);
    }
}

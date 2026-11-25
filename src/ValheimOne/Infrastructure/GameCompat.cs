using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace ValheimOne.Infrastructure;

// Resolves the vanilla members whose shape changed between Valheim 0.221 and 1.0, at
// runtime, so one build runs on both. Every resolver tries the 1.0 shape first and
// the 0.221 shape second, and reports failure with a null or false instead of
// throwing, so a caller can switch off one capability instead of the whole plugin.
//
// The DLL is compiled against 1.0, but nothing here names a 1.0-only type
// (Vector2s, SimulationDistance): those are reached through reflection so the
// binary's own member references still resolve on 0.221.
internal static class GameCompat
{
    private const BindingFlags AnyInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly Lazy<int> SmelterItemRpcShape = new(
        () => ResolveItemRpcShape(typeof(Smelter), "RPC_AddOre"));
    private static readonly Lazy<int> CookingItemRpcShape = new(
        () => ResolveItemRpcShape(typeof(CookingStation), "RPC_AddItem"));
    private static readonly FieldInfo? ItemCheatedField = AccessTools.Field(typeof(ItemDrop.ItemData), "m_cheated");

    public static bool TryGetSmelterItemArguments(ItemDrop.ItemData item, out object[] arguments) =>
        TryGetItemArguments(SmelterItemRpcShape.Value, item, out arguments);

    public static bool TryGetCookingItemArguments(ItemDrop.ItemData item, out object[] arguments) =>
        TryGetItemArguments(CookingItemRpcShape.Value, item, out arguments);

    private static int ResolveItemRpcShape(Type station, string name)
    {
        MethodInfo? method = AccessTools.Method(station, name);
        ParameterInfo[]? parameters = method?.GetParameters();
        if (parameters == null || parameters.Length < 2 ||
            parameters[0].ParameterType != typeof(long) || parameters[1].ParameterType != typeof(string)) return 0;
        if (parameters.Length == 2) return 1;
        return parameters.Length == 3 && parameters[2].ParameterType == typeof(bool) ? 2 : 0;
    }

    private static bool TryGetItemArguments(int shape, ItemDrop.ItemData item, out object[] arguments)
    {
        arguments = Array.Empty<object>();
        if (item == null || item.m_dropPrefab == null || shape == 0) return false;
        if (shape == 1)
        {
            arguments = new object[] { item.m_dropPrefab.name };
            return true;
        }
        // 1.0 added the item's cheated flag to both RPC payloads. Read it from
        // the actual item, and refuse consumption if a future shape is unknown.
        if (ItemCheatedField?.FieldType != typeof(bool)) return false;
        arguments = new object[] { item.m_dropPrefab.name, (bool)ItemCheatedField.GetValue(item) };
        return true;
    }

    public static void ApplyHeadlessSaveCompatibility(Harmony harmony)
    {
        // This Nintendo-specific rate check was added in 1.0. Older builds have
        // no such method and need no patch.
        MethodInfo? method = AccessTools.Method(typeof(ZNet), "HardSaveBlock", Type.EmptyTypes);
        if (method != null)
        {
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(GameCompat), nameof(HardSaveBlockPrefix)));
        }
    }

    private static bool HardSaveBlockPrefix(ZNet __instance, ref bool __result)
    {
        // A dedicated server has no distribution-platform object. Within 60s of
        // the last save, vanilla dereferences it to test for Nintendo and throws
        // before saving. Preserve the original rate check on player platforms.
        if (__instance.IsDedicated() && PlatformManager.DistributionPlatform == null)
        {
            __result = false;
            return false;
        }
        return true;
    }

    private static readonly Lazy<FieldInfo?> ExploredMapField = new(
        () => TryResolve(() => AccessTools.Field(typeof(Minimap), "m_explored")));

    public static bool TryGetExploredMap(Minimap minimap, out ExplorationBitmap bitmap)
    {
        // Valheim 1.0 uses BitArray; older worlds/builds use bool[]. Resolve the
        // field untyped once per sync operation, never once per map pixel.
        return ExplorationBitmap.TryWrap(ExploredMapField.Value?.GetValue(minimap), out bitmap);
    }

    private static readonly FieldInfo? StationUpgraderField = AccessTools.Field(typeof(CraftingStation), "m_upgrader");
    private static readonly FieldInfo? UpgraderResourceField = AccessTools.Field(typeof(Piece.Requirement), "m_upgraderResource");

    public static bool IsCraftingRequirementActive(Player player, Piece.Requirement requirement)
    {
        // 1.0 distinguishes upgrader ingredients from the normal recipe costs.
        // Older builds have neither field and retain their original full list.
        if (StationUpgraderField == null || UpgraderResourceField == null) return true;
        CraftingStation? station = player.GetCurrentCraftingStation();
        bool upgrader = station != null && (bool)StationUpgraderField.GetValue(station);
        return upgrader == (bool)UpgraderResourceField.GetValue(requirement);
    }

    // ------------ ZoneSystem.m_locationInstances
    // 1.0 keys the dictionary by Vector2s (short x, short y); 0.221 by Vector2i.
    // The field name did not change, only its signature, so a statically bound read
    // throws MissingFieldException on the other build. Read it untyped instead.

    private static readonly Lazy<FieldInfo?> LocationInstancesField = new(
        () => TryResolve(() => AccessTools.Field(typeof(ZoneSystem), "m_locationInstances")));

    private static ZoneKeyReader? _zoneKeyReader;

    public static bool TryGetLocationInstances(
        ZoneSystem zoneSystem,
        List<KeyValuePair<Vector2i, ZoneSystem.LocationInstance>> output)
    {
        output.Clear();
        try
        {
            FieldInfo? field = LocationInstancesField.Value;
            if (field == null || !(field.GetValue(zoneSystem) is IDictionary instances))
            {
                return false;
            }

            foreach (DictionaryEntry entry in instances)
            {
                if (!TryGetZone(entry.Key, out Vector2i zone) ||
                    !(entry.Value is ZoneSystem.LocationInstance instance))
                {
                    output.Clear();
                    return false;
                }

                output.Add(new KeyValuePair<Vector2i, ZoneSystem.LocationInstance>(zone, instance));
            }

            return true;
        }
        catch (Exception)
        {
            output.Clear();
            return false;
        }
    }

    // Reads a zone key of either shape as a Vector2i. Vector2i exists on both builds.
    public static bool TryGetZone(object? key, out Vector2i zone)
    {
        zone = default;
        if (key == null)
        {
            return false;
        }

        if (key is Vector2i vector)
        {
            zone = vector;
            return true;
        }

        Type type = key.GetType();
        ZoneKeyReader? reader = _zoneKeyReader;
        if (reader == null || reader.Type != type)
        {
            FieldInfo? x = AccessTools.Field(type, "x");
            FieldInfo? y = AccessTools.Field(type, "y");
            if (x == null || y == null)
            {
                return false;
            }

            reader = new ZoneKeyReader(type, x, y);
            _zoneKeyReader = reader;
        }

        zone = new Vector2i(
            Convert.ToInt32(reader.X.GetValue(key)),
            Convert.ToInt32(reader.Y.GetValue(key)));
        return true;
    }

    private sealed class ZoneKeyReader
    {
        public ZoneKeyReader(Type type, FieldInfo x, FieldInfo y)
        {
            Type = type;
            X = x;
            Y = y;
        }

        public Type Type { get; }

        public FieldInfo X { get; }

        public FieldInfo Y { get; }
    }

    // ------------ ZDOMan.FindSectorObjects
    // 1.0: (Vector2s sector, SimulationDistance distance, List<ZDO> objects, List<ZDO> distant)
    // 0.221: (Vector2i sector, int area, int distantArea, List<ZDO> objects, List<ZDO> distant)
    // The plugin only ever asks for the one sector. A default SimulationDistance
    // (near 0, far 0, not classic) makes the 1.0 method visit exactly that sector,
    // the same as area 0 / distantArea 0 did.

    private static readonly Lazy<SectorScan?> SectorScanBinding = new(
        () => TryResolve(ResolveSectorScan));

    public static bool TryFindSectorObjects(ZDOMan manager, Vector2i zone, List<ZDO> output)
    {
        SectorScan? scan = SectorScanBinding.Value;
        if (scan == null)
        {
            return false;
        }

        try
        {
            scan.Invoke(manager, zone, output);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SectorScan? ResolveSectorScan()
    {
        SectorScan? legacy = null;
        foreach (MethodInfo method in typeof(ZDOMan).GetMethods(AnyInstance))
        {
            if (method.Name != "FindSectorObjects")
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 4 &&
                parameters[1].ParameterType.IsValueType &&
                parameters[1].ParameterType.Name == "SimulationDistance" &&
                parameters[2].ParameterType == typeof(List<ZDO>))
            {
                ConstructorInfo? keyConstructor = parameters[0].ParameterType.GetConstructor(
                    new[] { typeof(int), typeof(int) });
                if (keyConstructor == null)
                {
                    continue;
                }

                object distance = Activator.CreateInstance(parameters[1].ParameterType);
                return new SectorScan(method, keyConstructor, distance);
            }

            if (parameters.Length == 5 &&
                parameters[0].ParameterType == typeof(Vector2i) &&
                parameters[1].ParameterType == typeof(int) &&
                parameters[2].ParameterType == typeof(int) &&
                parameters[3].ParameterType == typeof(List<ZDO>))
            {
                legacy = new SectorScan(method, null, null);
            }
        }

        return legacy;
    }

    private sealed class SectorScan
    {
        private readonly MethodInfo _method;
        private readonly ConstructorInfo? _keyConstructor;
        private readonly object? _distance;

        public SectorScan(MethodInfo method, ConstructorInfo? keyConstructor, object? distance)
        {
            _method = method;
            _keyConstructor = keyConstructor;
            _distance = distance;
        }

        public void Invoke(ZDOMan manager, Vector2i zone, List<ZDO> output)
        {
            if (_keyConstructor != null)
            {
                object key = _keyConstructor.Invoke(new object[] { zone.x, zone.y });
                _method.Invoke(manager, new object?[] { key, _distance, output, null });
                return;
            }

            _method.Invoke(manager, new object?[] { zone, 0, 0, output, null });
        }
    }

    // ------------ WorldGenerator.GetBiomeHeight
    // 1.0 added a trailing `bool riverPreDN = true`; 0.221 ends at `bool preGeneration`.
    // This runs per map pixel, so it binds to a typed delegate once instead of
    // reflecting per call. Both delegates only name types that exist on both builds.

    private delegate float BiomeHeightModern(
        WorldGenerator generator,
        Heightmap.Biome biome,
        float wx,
        float wy,
        out Color mask,
        bool preGeneration,
        bool riverPreDN);

    private delegate float BiomeHeightLegacy(
        WorldGenerator generator,
        Heightmap.Biome biome,
        float wx,
        float wy,
        out Color mask,
        bool preGeneration);

    private static readonly BiomeHeightModern? ModernBiomeHeight =
        TryResolve(() => BindBiomeHeight<BiomeHeightModern>(typeof(bool), typeof(bool)));

    private static readonly BiomeHeightLegacy? LegacyBiomeHeight = ModernBiomeHeight != null
        ? null
        : TryResolve(() => BindBiomeHeight<BiomeHeightLegacy>(typeof(bool)));

    public static bool IsBiomeHeightAvailable => ModernBiomeHeight != null || LegacyBiomeHeight != null;

    public static float GetBiomeHeight(
        WorldGenerator generator,
        Heightmap.Biome biome,
        float wx,
        float wy,
        out Color mask)
    {
        // The trailing arguments are the vanilla defaults: the game's own in-world
        // callers pass none, so this samples the same terrain the client sees.
        if (ModernBiomeHeight != null)
        {
            return ModernBiomeHeight(generator, biome, wx, wy, out mask, false, true);
        }

        if (LegacyBiomeHeight != null)
        {
            return LegacyBiomeHeight(generator, biome, wx, wy, out mask, false);
        }

        throw new MissingMethodException(typeof(WorldGenerator).FullName, "GetBiomeHeight");
    }

    private static TDelegate? BindBiomeHeight<TDelegate>(params Type[] trailing)
        where TDelegate : class
    {
        var parameterTypes = new List<Type>
        {
            typeof(Heightmap.Biome),
            typeof(float),
            typeof(float),
            typeof(Color).MakeByRefType(),
        };
        parameterTypes.AddRange(trailing);
        MethodInfo? method = AccessTools.Method(
            typeof(WorldGenerator),
            "GetBiomeHeight",
            parameterTypes.ToArray());
        return method == null
            ? null
            : Delegate.CreateDelegate(typeof(TDelegate), method, throwOnBindFailure: false) as TDelegate;
    }

    // ------------ Character.Message
    // 1.0 added a trailing `bool log = false`. Only used for the odd player-facing
    // notice, so a reflective call with the vanilla defaults is fine.

    private static readonly Lazy<MethodInfo?> MessageMethod = new(
        () => TryResolve(() =>
            AccessTools.Method(
                typeof(Character),
                "Message",
                new[] { typeof(MessageHud.MessageType), typeof(string), typeof(int), typeof(Sprite), typeof(bool) }) ??
            AccessTools.Method(
                typeof(Character),
                "Message",
                new[] { typeof(MessageHud.MessageType), typeof(string), typeof(int), typeof(Sprite) })));

    public static bool TryMessage(Character character, MessageHud.MessageType type, string message)
    {
        MethodInfo? method = MessageMethod.Value;
        if (method == null)
        {
            return false;
        }

        try
        {
            ParameterInfo[] parameters = method.GetParameters();
            var arguments = new object?[parameters.Length];
            arguments[0] = type;
            arguments[1] = message;
            for (int index = 2; index < parameters.Length; index++)
            {
                arguments[index] = DefaultArgument(parameters[index]);
            }

            method.Invoke(character, arguments);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------ SoftReference<GameObject>.Name
    // Valheim 1.0.30 renamed the property to `name` (it now reads Runtime.GetAssetName; up to 1.0.21 it read the
    // file name of Runtime.GetAssetPath). The getter is found by either spelling, so the binary names neither and
    // binds on both builds; each build returns its own value. Null when neither is there or the read fails, and
    // the caller falls back to the loaded prefab's own name.

    private static readonly Lazy<MethodInfo?> SoftReferenceNameGetter = new(
        () => TryResolve(() =>
            (typeof(SoftReferenceableAssets.SoftReference<GameObject>).GetProperty(
                 "name",
                 BindingFlags.Public | BindingFlags.Instance) ??
             typeof(SoftReferenceableAssets.SoftReference<GameObject>).GetProperty(
                 "Name",
                 BindingFlags.Public | BindingFlags.Instance))?.GetGetMethod()));

    public static string? TryGetSoftReferenceName(SoftReferenceableAssets.SoftReference<GameObject> reference)
    {
        MethodInfo? getter = SoftReferenceNameGetter.Value;
        if (getter == null)
        {
            return null;
        }

        try
        {
            return getter.Invoke(reference, null) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------ Terminal.ConsoleCommand constructor
    // 1.0 inserted `bool hideBehindDevCommands` before `optionsFetcher`. The
    // constructor is matched by its leading (string, string, ConsoleEvent) shape and
    // the remaining arguments are filled by parameter name, so a reorder or a new
    // optional parameter does not break registration.

    public static Terminal.ConsoleCommand? TryCreateConsoleCommand(
        string command,
        string description,
        Terminal.ConsoleEvent action,
        bool onlyServer,
        Terminal.ConsoleOptionsFetcher? optionsFetcher)
    {
        try
        {
            foreach (ConstructorInfo constructor in typeof(Terminal.ConsoleCommand).GetConstructors())
            {
                ParameterInfo[] parameters = constructor.GetParameters();
                if (parameters.Length < 3 ||
                    parameters[0].ParameterType != typeof(string) ||
                    parameters[1].ParameterType != typeof(string) ||
                    parameters[2].ParameterType != typeof(Terminal.ConsoleEvent))
                {
                    continue;
                }

                var arguments = new object?[parameters.Length];
                arguments[0] = command;
                arguments[1] = description;
                arguments[2] = action;
                for (int index = 3; index < parameters.Length; index++)
                {
                    ParameterInfo parameter = parameters[index];
                    switch (parameter.Name)
                    {
                        case "onlyServer":
                            arguments[index] = onlyServer;
                            break;
                        case "optionsFetcher":
                            arguments[index] = optionsFetcher;
                            break;
                        default:
                            arguments[index] = DefaultArgument(parameter);
                            break;
                    }
                }

                return (Terminal.ConsoleCommand)constructor.Invoke(arguments);
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    // ------------ helpers

    private static object? DefaultArgument(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue)
        {
            return parameter.DefaultValue;
        }

        return parameter.ParameterType.IsValueType
            ? Activator.CreateInstance(parameter.ParameterType)
            : null;
    }

    // Never let a resolver throw: a member that cannot be resolved disables one
    // capability, it must not surface as a TypeInitializationException that takes
    // the whole plugin down.
    private static T? TryResolve<T>(Func<T?> resolve)
        where T : class
    {
        try
        {
            return resolve();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

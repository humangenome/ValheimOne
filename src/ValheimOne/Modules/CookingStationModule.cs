using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class CookingStationModule : IFeatureModule
{
    private const string AddFuelRpc = "RPC_AddFuel";
    private const string AddItemRpc = "RPC_AddItem";

    private static readonly Func<CookingStation, float> GetFuel =
        AccessTools.MethodDelegate<Func<CookingStation, float>>(
            AccessTools.Method(typeof(CookingStation), "GetFuel", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(CookingStation), "GetFuel"));

    private static readonly Func<CookingStation, int> GetFreeSlot =
        AccessTools.MethodDelegate<Func<CookingStation, int>>(
            AccessTools.Method(typeof(CookingStation), "GetFreeSlot", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(CookingStation), "GetFreeSlot"));

    private static readonly Func<CookingStation, bool> IsFireLit =
        AccessTools.MethodDelegate<Func<CookingStation, bool>>(
            AccessTools.Method(typeof(CookingStation), "IsFireLit", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(CookingStation), "IsFireLit"));

    private static readonly Func<CookingStation, Inventory, ItemDrop.ItemData?> FindCookableItem =
        AccessTools.MethodDelegate<Func<CookingStation, Inventory, ItemDrop.ItemData?>>(
            AccessTools.Method(
                typeof(CookingStation),
                "FindCookableItem",
                new[] { typeof(Inventory) })
            ?? throw new MissingMethodException(nameof(CookingStation), "FindCookableItem"));

    private static CookingStationModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryPercent _cookSpeedMultiplier;
    private readonly ConfigEntryBool _ignoreFireRequirement;
    private readonly ConfigEntryBool _autoFuel;
    private readonly ConfigEntryBool _autoFeedRaw;
    private readonly ConfigEntryFloat _range;
    private readonly ConfigEntryFloat _checkIntervalSeconds;
    private readonly List<CookingStationBaseline> _baselines =
        new List<CookingStationBaseline>();
    private readonly ConditionalWeakTable<CookingStation, StationState> _stationStates =
        new ConditionalWeakTable<CookingStation, StationState>();

    public CookingStationModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _cookSpeedMultiplier = _feature.Percent(
            "CookSpeedMultiplier",
            0f,
            "Scale each cooking conversion's per-item cook time.");
        _ignoreFireRequirement = _feature.Bool(
            "IgnoreFireRequirement",
            defaultValue: false,
            "Allow cooking stations to operate without their normal fire requirement.");
        _autoFuel = _feature.Bool(
            "AutoFuel",
            defaultValue: false,
            "Pull fuel from nearby containers for fuel-using cooking stations such as stone ovens.");
        _autoFeedRaw = _feature.Bool(
            "AutoFeedRaw",
            defaultValue: false,
            "Pull accepted raw items from nearby containers into free cooking slots.");
        _range = _feature.Float(
            "Range",
            10f,
            "Maximum distance in metres from the station to a source container. Clamped to 1-50.");
        _checkIntervalSeconds = _feature.Float(
            "CheckIntervalSeconds",
            2f,
            "Seconds between automation checks for each station. Values below 1 are clamped to 1.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Cooking station";

    public string Section => "CookingStation";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        // Cooking simulation runs on the zone owner. Patches remain installed so the effective
        // server overlay can hot-enable this synced module without repatching Harmony.
        _active = this;

        PatchPostfix(harmony, "Awake", nameof(CookingStationAwakePostfix));
        PatchPostfix(harmony, "IsFireLit", nameof(IsFireLitPostfix));

        var updateCooking = AccessTools.Method(
            typeof(CookingStation),
            "UpdateCooking",
            Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(CookingStation), "UpdateCooking");
        harmony.Patch(
            updateCooking,
            prefix: new HarmonyMethod(
                typeof(CookingStationModule),
                nameof(UpdateCookingPrefix)),
            postfix: new HarmonyMethod(
                typeof(CookingStationModule),
                nameof(UpdateCookingPostfix)),
            finalizer: new HarmonyMethod(
                typeof(CookingStationModule),
                nameof(UpdateCookingFinalizer)));
    }

    private static void PatchPostfix(Harmony harmony, string methodName, string postfixName)
    {
        var original = AccessTools.Method(typeof(CookingStation), methodName, Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(CookingStation), methodName);
        harmony.Patch(
            original,
            postfix: new HarmonyMethod(typeof(CookingStationModule), postfixName));
    }

    private static void CookingStationAwakePostfix(CookingStation __instance)
    {
        CookingStationModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToCookingStation(__instance, active.GetOrAddBaseline(__instance));
    }

    private static void IsFireLitPostfix(ref bool __result)
    {
        CookingStationModule? active = _active;
        if (active == null || !active.IsEnabled || !active._ignoreFireRequirement.Value)
        {
            return;
        }

        __result = true;
    }

    private static void UpdateCookingPrefix(
        CookingStation __instance,
        out UpdateCookingState __state)
    {
        __state = default;

        CookingStationModule? active = _active;
        if (active == null || !active.IsEnabled || !active._ignoreFireRequirement.Value)
        {
            return;
        }

        CookingStationBaseline? baseline = active.FindBaseline(__instance);
        if (baseline == null || !baseline.RequireFire || __instance.m_requireFire)
        {
            return;
        }

        // Valheim's active-cooking predicate only calls IsFireLit when m_requireFire is true.
        // Keep the configured field false outside this call, but temporarily enter that vanilla
        // branch and let IsFireLitPostfix satisfy it so non-fuel stations continue cooking.
        __state = new UpdateCookingState(__instance.m_requireFire);
        __instance.m_requireFire = true;
    }

    private static void UpdateCookingPostfix(
        CookingStation __instance,
        UpdateCookingState __state)
    {
        RestoreTemporaryFireRequirement(__instance, __state);

        CookingStationModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        bool autoFuel = active._autoFuel.Value && __instance.m_useFuel;
        bool autoFeedRaw = active._autoFeedRaw.Value;
        if (!autoFuel && !autoFeedRaw)
        {
            return;
        }

        ZNetView? networkView = GetNetworkView(__instance);
        Player? player = Player.m_localPlayer;
        if (networkView == null ||
            !networkView.IsValid() ||
            !networkView.IsOwner() ||
            player == null)
        {
            return;
        }

        StationState state = active._stationStates.GetValue(
            __instance,
            CreateStationState);
        if (!active.TryBeginCheck(state))
        {
            return;
        }

        bool needsFuel = autoFuel && NeedsFuel(__instance);
        bool canFeedRaw = autoFeedRaw && CanFeedRaw(__instance);
        if (!needsFuel && !canFeedRaw)
        {
            return;
        }

        IReadOnlyList<Inventory> inventories = state.Scanner.GetInventories(
            player,
            __instance.transform.position,
            active._range.Value,
            ignoreWardedChests: false,
            active._checkIntervalSeconds.Value);

        if (needsFuel)
        {
            TryConsumeAndInvokeFuel(__instance.m_fuelItem, networkView, inventories);
        }

        if (canFeedRaw)
        {
            TryConsumeAndInvokeRaw(__instance, networkView, inventories);
        }
    }

    private static Exception? UpdateCookingFinalizer(
        CookingStation __instance,
        UpdateCookingState __state,
        Exception? __exception)
    {
        RestoreTemporaryFireRequirement(__instance, __state);
        return __exception;
    }

    private static void RestoreTemporaryFireRequirement(
        CookingStation station,
        UpdateCookingState state)
    {
        if (state.Changed)
        {
            station.m_requireFire = state.RequireFire;
        }
    }

    private void OnEffectiveValuesChanged()
    {
        CookingStation[] stations = UnityEngine.Object.FindObjectsByType<CookingStation>(
            FindObjectsSortMode.None);
        foreach (CookingStation station in stations)
        {
            CookingStationBaseline? baseline = FindBaseline(station);
            if (IsEnabled)
            {
                ApplyToCookingStation(station, baseline ?? AddBaseline(station));
            }
            else
            {
                baseline?.Restore(station);
            }
        }
    }

    private CookingStationBaseline GetOrAddBaseline(CookingStation station)
    {
        return FindBaseline(station) ?? AddBaseline(station);
    }

    private CookingStationBaseline? FindBaseline(CookingStation station)
    {
        for (int index = _baselines.Count - 1; index >= 0; index--)
        {
            CookingStationBaseline baseline = _baselines[index];
            if (!baseline.Station.TryGetTarget(out CookingStation? existing) || existing == null)
            {
                _baselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, station))
            {
                return baseline;
            }
        }

        return null;
    }

    private CookingStationBaseline AddBaseline(CookingStation station)
    {
        var baseline = new CookingStationBaseline(station);
        _baselines.Add(baseline);
        return baseline;
    }

    private void ApplyToCookingStation(
        CookingStation station,
        CookingStationBaseline baseline)
    {
        station.m_requireFire = _ignoreFireRequirement.Value
            ? false
            : baseline.RequireFire;

        foreach (ConversionBaseline conversion in baseline.Conversions)
        {
            conversion.Conversion.m_cookTime = _cookSpeedMultiplier.Apply(conversion.CookTime);
        }
    }

    private bool TryBeginCheck(StationState state)
    {
        float now = Time.realtimeSinceStartup;
        if (now < state.NextCheckAt)
        {
            return false;
        }

        state.NextCheckAt = now + Math.Max(1f, _checkIntervalSeconds.Value);
        return true;
    }

    private static bool NeedsFuel(CookingStation station)
    {
        if (!station.m_useFuel || station.m_fuelItem == null || station.m_maxFuel <= 0)
        {
            return false;
        }

        return Mathf.CeilToInt(GetFuel(station)) < station.m_maxFuel;
    }

    private static bool CanFeedRaw(CookingStation station)
    {
        if (GetFreeSlot(station) == -1)
        {
            return false;
        }

        return !station.m_requireFire || IsFireLit(station);
    }

    private static bool TryConsumeAndInvokeFuel(
        ItemDrop fuelItem,
        ZNetView networkView,
        IReadOnlyList<Inventory> inventories)
    {
        if (fuelItem == null)
        {
            return false;
        }

        string fuelName = fuelItem.m_itemData.m_shared.m_name;
        foreach (Inventory inventory in inventories)
        {
            ItemDrop.ItemData? item = inventory.GetItem(fuelName);
            if (item != null && inventory.RemoveOneItem(item))
            {
                networkView.InvokeRPC(AddFuelRpc);
                return true;
            }
        }

        return false;
    }

    private static bool TryConsumeAndInvokeRaw(
        CookingStation station,
        ZNetView networkView,
        IReadOnlyList<Inventory> inventories)
    {
        if (!CanFeedRaw(station))
        {
            return false;
        }

        foreach (Inventory inventory in inventories)
        {
            ItemDrop.ItemData? item = FindCookableItem(station, inventory);
            if (item == null || item.m_dropPrefab == null)
            {
                continue;
            }

            if (inventory.RemoveOneItem(item))
            {
                networkView.InvokeRPC(AddItemRpc, item.m_dropPrefab.name);
                return true;
            }
        }

        return false;
    }

    private static ZNetView? GetNetworkView(Component station)
    {
        return station.GetComponent<ZNetView>() ?? station.GetComponentInParent<ZNetView>();
    }

    private static StationState CreateStationState<T>(T station)
        where T : class
    {
        return new StationState();
    }

    private readonly struct UpdateCookingState
    {
        public UpdateCookingState(bool requireFire)
        {
            Changed = true;
            RequireFire = requireFire;
        }

        public bool Changed { get; }

        public bool RequireFire { get; }
    }

    private sealed class CookingStationBaseline
    {
        public CookingStationBaseline(CookingStation station)
        {
            Station = new WeakReference<CookingStation>(station);
            RequireFire = station.m_requireFire;
            Conversions = new List<ConversionBaseline>();

            foreach (CookingStation.ItemConversion conversion in station.m_conversion)
            {
                if (conversion != null)
                {
                    Conversions.Add(new ConversionBaseline(conversion));
                }
            }
        }

        public WeakReference<CookingStation> Station { get; }

        public bool RequireFire { get; }

        public List<ConversionBaseline> Conversions { get; }

        public void Restore(CookingStation station)
        {
            station.m_requireFire = RequireFire;
            foreach (ConversionBaseline conversion in Conversions)
            {
                conversion.Conversion.m_cookTime = conversion.CookTime;
            }
        }
    }

    private sealed class ConversionBaseline
    {
        public ConversionBaseline(CookingStation.ItemConversion conversion)
        {
            Conversion = conversion;
            CookTime = conversion.m_cookTime;
        }

        public CookingStation.ItemConversion Conversion { get; }

        public float CookTime { get; }
    }

    private sealed class StationState
    {
        public ChestScanner Scanner { get; } = new ChestScanner();

        public float NextCheckAt { get; set; }
    }
}

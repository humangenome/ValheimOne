using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class StationAutomationModule : IFeatureModule
{
    private const string AddFuelRpc = "RPC_AddFuel";
    private const string AddOreRpc = "RPC_AddOre";

    private static readonly Func<Smelter, float> GetSmelterFuel =
        AccessTools.MethodDelegate<Func<Smelter, float>>(
            AccessTools.Method(typeof(Smelter), "GetFuel", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Smelter), "GetFuel"));

    private static readonly Func<Smelter, int> GetSmelterQueueSize =
        AccessTools.MethodDelegate<Func<Smelter, int>>(
            AccessTools.Method(typeof(Smelter), "GetQueueSize", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Smelter), "GetQueueSize"));

    private static readonly Func<Smelter, Inventory, ItemDrop.ItemData?> FindCookableItem =
        AccessTools.MethodDelegate<Func<Smelter, Inventory, ItemDrop.ItemData?>>(
            AccessTools.Method(
                typeof(Smelter),
                "FindCookableItem",
                new[] { typeof(Inventory) })
            ?? throw new MissingMethodException(nameof(Smelter), "FindCookableItem"));

    private static StationAutomationModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryBool _smelterAutoFuel;
    private readonly ConfigEntryBool _fireplaceAutoFuel;
    private readonly ConfigEntryFloat _range;
    private readonly ConfigEntryBool _ignoreWardedChests;
    private readonly ConfigEntryFloat _checkIntervalSeconds;
    private readonly ConditionalWeakTable<Smelter, StationState> _smelterStates =
        new ConditionalWeakTable<Smelter, StationState>();
    private readonly ConditionalWeakTable<Fireplace, StationState> _fireplaceStates =
        new ConditionalWeakTable<Fireplace, StationState>();

    public StationAutomationModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _smelterAutoFuel = _feature.Bool(
            "SmelterAutoFuel",
            defaultValue: false,
            "Pull fuel and cookable ore from nearby containers for all Smelter-based stations.");
        _fireplaceAutoFuel = _feature.Bool(
            "FireplaceAutoFuel",
            defaultValue: false,
            "Pull fuel from nearby containers for campfires, hearths, torches, and other fireplaces.");
        _range = _feature.Float(
            "Range",
            10f,
            "Maximum distance in metres from the station to a source container. Clamped to 1-50.");
        _ignoreWardedChests = _feature.Bool(
            "IgnoreWardedChests",
            defaultValue: false,
            "Bypass the per-player ward check while retaining container privacy checks.");
        _checkIntervalSeconds = _feature.Float(
            "CheckIntervalSeconds",
            2f,
            "Seconds between automation checks for each station. Values below 1 are clamped to 1.");
    }

    public string Name => "Station automation";

    public string Section => "StationAutomation";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.RequiresClient;

    public void ApplyPatches(Harmony harmony)
    {
        // Station simulation runs on the zone owner. On a dedicated server with vanilla clients,
        // that owner is usually a client, so this feature is synced and requires the client mod.
        // Patches remain installed so the effective server overlay can hot-enable automation.
        _active = this;

        PatchPostfix(
            harmony,
            typeof(Smelter),
            nameof(Smelter.UpdateSmelter),
            nameof(UpdateSmelterPostfix));
        PatchPostfix(
            harmony,
            typeof(Fireplace),
            nameof(Fireplace.UpdateFireplace),
            nameof(UpdateFireplacePostfix));
    }

    private static void PatchPostfix(
        Harmony harmony,
        Type declaringType,
        string methodName,
        string postfixName)
    {
        var original = AccessTools.Method(declaringType, methodName, Type.EmptyTypes)
            ?? throw new MissingMethodException(declaringType.FullName, methodName);
        harmony.Patch(
            original,
            postfix: new HarmonyMethod(typeof(StationAutomationModule), postfixName));
    }

    private static void UpdateSmelterPostfix(Smelter __instance)
    {
        StationAutomationModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (!active._smelterAutoFuel.Value)
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

        StationState state = active._smelterStates.GetValue(
            __instance,
            CreateStationState);
        if (!active.TryBeginCheck(state))
        {
            return;
        }

        IReadOnlyList<Inventory> inventories = state.Scanner.GetInventories(
            player,
            __instance.transform.position,
            active._range.Value,
            active._ignoreWardedChests.Value,
            active._checkIntervalSeconds.Value);

        TryAddSmelterFuel(__instance, networkView, inventories);
        TryAddSmelterOre(__instance, networkView, inventories);
    }

    private static void UpdateFireplacePostfix(Fireplace __instance)
    {
        StationAutomationModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (!active._fireplaceAutoFuel.Value || __instance.m_infiniteFuel)
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

        StationState state = active._fireplaceStates.GetValue(
            __instance,
            CreateStationState);
        if (!active.TryBeginCheck(state))
        {
            return;
        }

        float fuel = networkView.GetZDO().GetFloat(ZDOVars.s_fuel);
        if (__instance.m_maxFuel <= 0f || Mathf.CeilToInt(fuel) >= __instance.m_maxFuel)
        {
            return;
        }

        IReadOnlyList<Inventory> inventories = state.Scanner.GetInventories(
            player,
            __instance.transform.position,
            active._range.Value,
            active._ignoreWardedChests.Value,
            active._checkIntervalSeconds.Value);
        TryConsumeAndInvokeFuel(__instance.m_fuelItem, networkView, inventories);
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

    private static void TryAddSmelterFuel(
        Smelter smelter,
        ZNetView networkView,
        IReadOnlyList<Inventory> inventories)
    {
        if (smelter.m_maxFuel <= 0 ||
            GetSmelterFuel(smelter) > smelter.m_maxFuel - 1f)
        {
            return;
        }

        TryConsumeAndInvokeFuel(smelter.m_fuelItem, networkView, inventories);
    }

    private static void TryAddSmelterOre(
        Smelter smelter,
        ZNetView networkView,
        IReadOnlyList<Inventory> inventories)
    {
        if (smelter.m_maxOre <= 0 || GetSmelterQueueSize(smelter) >= smelter.m_maxOre)
        {
            return;
        }

        foreach (Inventory inventory in inventories)
        {
            ItemDrop.ItemData? item = FindCookableItem(smelter, inventory);
            if (item == null || item.m_dropPrefab == null)
            {
                continue;
            }

            if (inventory.RemoveOneItem(item))
            {
                // This is the vanilla OnAddOre path after item selection: remove one source item
                // (which calls Inventory.Changed) and ask the owning station to queue its prefab.
                networkView.InvokeRPC(AddOreRpc, item.m_dropPrefab.name);
                return;
            }
        }
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
            ItemDrop.ItemData item = inventory.GetItem(fuelName);
            if (item != null && inventory.RemoveOneItem(item))
            {
                // RemoveOneItem uses Inventory.Changed, which marks the owning container dirty.
                networkView.InvokeRPC(AddFuelRpc);
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

    private sealed class StationState
    {
        public ChestScanner Scanner { get; } = new ChestScanner();

        public float NextCheckAt { get; set; }
    }
}

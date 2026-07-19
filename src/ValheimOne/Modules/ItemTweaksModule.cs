using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class ItemTweaksModule : IFeatureModule
{
    private static readonly Dictionary<GameObject, int> VanillaMaxStackSizes =
        new Dictionary<GameObject, int>();

    private static ItemTweaksModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryPercent _stackSizeMultiplier;
    private readonly ConfigEntryPercent _weightMultiplier;
    private readonly ConfigEntryPercent _durabilityMultiplier;

    public ItemTweaksModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _stackSizeMultiplier = _feature.Percent(
            "StackSizeMultiplier",
            0f,
            "Maximum stack sizes for items whose vanilla maximum is greater than one.");
        _weightMultiplier = _feature.Percent(
            "WeightMultiplier",
            0f,
            "Weight of item stacks, including quality-based weight adjustments.");
        _durabilityMultiplier = _feature.Percent(
            "DurabilityMultiplier",
            0f,
            "Maximum durability of tools, weapons, armor, and other durability-using items.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Item tweaks";

    public string Section => "Items";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        // Inventory weight and stack-limit math executes on each participating client, so this
        // feature uses the Synced classification and reads effective values at every patch call.
        _active = this;

        var getWeight = AccessTools.Method(
            typeof(ItemDrop.ItemData),
            nameof(ItemDrop.ItemData.GetWeight),
            new[] { typeof(int) })
            ?? throw new MissingMethodException(
                nameof(ItemDrop.ItemData),
                nameof(ItemDrop.ItemData.GetWeight));
        harmony.Patch(
            getWeight,
            postfix: new HarmonyMethod(typeof(ItemTweaksModule), nameof(GetWeightPostfix)));

        // The parameterless overload delegates to this quality overload in Valheim 0.221.12.
        // Patching only this method covers both entry points without scaling delegated calls twice.
        var getMaxDurability = AccessTools.Method(
            typeof(ItemDrop.ItemData),
            nameof(ItemDrop.ItemData.GetMaxDurability),
            new[] { typeof(int) })
            ?? throw new MissingMethodException(
                nameof(ItemDrop.ItemData),
                nameof(ItemDrop.ItemData.GetMaxDurability));
        harmony.Patch(
            getMaxDurability,
            postfix: new HarmonyMethod(
                typeof(ItemTweaksModule),
                nameof(GetMaxDurabilityPostfix)));

        var objectDbAwake = AccessTools.Method(typeof(ObjectDB), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(ObjectDB), "Awake");
        harmony.Patch(
            objectDbAwake,
            postfix: new HarmonyMethod(typeof(ItemTweaksModule), nameof(ObjectDbReadyPostfix)));

        var copyOtherDb = AccessTools.Method(
            typeof(ObjectDB),
            nameof(ObjectDB.CopyOtherDB),
            new[] { typeof(ObjectDB) })
            ?? throw new MissingMethodException(nameof(ObjectDB), nameof(ObjectDB.CopyOtherDB));
        harmony.Patch(
            copyOtherDb,
            postfix: new HarmonyMethod(typeof(ItemTweaksModule), nameof(ObjectDbReadyPostfix)));
    }

    private static void GetWeightPostfix(ref float __result)
    {
        ItemTweaksModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        // Inventory.UpdateTotalWeight sums GetWeight, Player.IsEncumbered reads that cached total,
        // and auto-pickup also calls GetWeight for its candidate item. Those carry paths are fully
        // covered. Vanilla's separate equipment-weight helper, recipe-weight sorting, and the
        // single-item side of a stacked tooltip read raw m_weight/GetNonStackedWeight; they remain
        // vanilla by design rather than mutating SharedData weight.
        __result = active._weightMultiplier.Apply(__result);
    }

    private static void GetMaxDurabilityPostfix(
        ItemDrop.ItemData __instance,
        ref float __result)
    {
        ItemTweaksModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (!__instance.m_shared.m_useDurability)
        {
            return;
        }

        __result = active._durabilityMultiplier.Apply(__result);
    }

    private static void ObjectDbReadyPostfix(ObjectDB __instance)
    {
        ItemTweaksModule? active = _active;
        if (active == null)
        {
            return;
        }

        // Capture untouched prefab state even while disabled. A later server overlay can then
        // hot-enable the module without requiring ObjectDB to run Awake or CopyOtherDB again.
        active.SnapshotVanillaStackSizes(__instance);
        if (!active.IsEnabled)
        {
            return;
        }

        active.ApplyEffectiveStackSizes(__instance);
    }

    private void OnEffectiveValuesChanged()
    {
        ObjectDB? objectDb = ObjectDB.instance;
        if (objectDb == null)
        {
            return;
        }

        SnapshotVanillaStackSizes(objectDb);
        ApplyEffectiveStackSizes(objectDb);
    }

    private void SnapshotVanillaStackSizes(ObjectDB objectDb)
    {
        foreach (GameObject prefab in objectDb.m_items)
        {
            if (prefab == null || VanillaMaxStackSizes.ContainsKey(prefab))
            {
                continue;
            }

            ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null)
            {
                continue;
            }

            VanillaMaxStackSizes.Add(prefab, itemDrop.m_itemData.m_shared.m_maxStackSize);
        }
    }

    private void ApplyEffectiveStackSizes(ObjectDB objectDb)
    {
        bool enabled = IsEnabled;
        foreach (GameObject prefab in objectDb.m_items)
        {
            if (prefab == null ||
                !VanillaMaxStackSizes.TryGetValue(prefab, out int vanillaMaxStackSize))
            {
                continue;
            }

            ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null)
            {
                continue;
            }

            itemDrop.m_itemData.m_shared.m_maxStackSize = enabled && vanillaMaxStackSize > 1
                ? GetEffectiveStackSize(vanillaMaxStackSize)
                : vanillaMaxStackSize;
        }
    }

    private int GetEffectiveStackSize(int vanillaMaxStackSize)
    {
        float adjustedStackSize = _stackSizeMultiplier.Apply(vanillaMaxStackSize);
        if (float.IsNaN(adjustedStackSize))
        {
            return vanillaMaxStackSize;
        }

        if (adjustedStackSize >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(
            0,
            (int)Math.Round(adjustedStackSize, MidpointRounding.AwayFromZero));
    }
}

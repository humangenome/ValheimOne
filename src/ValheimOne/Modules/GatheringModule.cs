using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class GatheringModule : IFeatureModule
{
    private static readonly MethodInfo AddItemToListMethod = AccessTools.Method(
        typeof(DropTable),
        "AddItemToList",
        new[] { typeof(List<ItemDrop.ItemData>), typeof(DropTable.DropData) })
        ?? throw new MissingMethodException(nameof(DropTable), "AddItemToList");

    private static GatheringModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryPercent _dropChance;
    private readonly Dictionary<string, ConfigEntryPercent> _materialModifiers =
        new Dictionary<string, ConfigEntryPercent>(StringComparer.OrdinalIgnoreCase);

    public GatheringModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _dropChance = _feature.Percent(
            "DropChance",
            0f,
            "Chance of supported materials dropping from non-guaranteed drop tables, capped at 100 percent.");

        // ObjectDB identifies item prefabs by their GameObject name, which follows the .prefab
        // filename in Valheim's 0.221.12 SoftRef manifest. Most config keys match that identifier;
        // the deliberate mappings are CoreWood -> RoundLog, Feather -> Feathers, and the current
        // FlametalOre resource -> FlametalOreNew. The legacy FlametalOre prefab remains an alias.
        RegisterMaterial("Wood", "Wood");
        RegisterMaterial("FineWood", "FineWood");
        RegisterMaterial("CoreWood", "RoundLog");
        RegisterMaterial("ElderBark", "ElderBark");
        RegisterMaterial("YggdrasilWood", "YggdrasilWood");
        RegisterMaterial("Blackwood", "Blackwood");
        RegisterMaterial("Stone", "Stone");
        RegisterMaterial("Grausten", "Grausten");
        RegisterMaterial("BlackMarble", "BlackMarble");
        RegisterMaterial("TinOre", "TinOre");
        RegisterMaterial("CopperOre", "CopperOre");
        RegisterMaterial("CopperScrap", "CopperScrap");
        RegisterMaterial("IronScrap", "IronScrap");
        RegisterMaterial("SilverOre", "SilverOre");
        RegisterMaterial("Chitin", "Chitin");
        RegisterMaterial("Feather", "Feathers");

        ConfigEntryPercent flametalOre = RegisterMaterial("FlametalOre", "FlametalOreNew");
        _materialModifiers.Add("FlametalOre", flametalOre);

        RegisterMaterial("ProustitePowder", "ProustitePowder");
    }

    public string Name => "Gathering";

    public string Section => "Gathering";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.ServerAuthoritative;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var getDropList = AccessTools.Method(
            typeof(DropTable),
            nameof(DropTable.GetDropList),
            Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(DropTable), nameof(DropTable.GetDropList));
        harmony.Patch(
            getDropList,
            postfix: new HarmonyMethod(
                typeof(GatheringModule),
                nameof(GetDropListPostfix)));

        var getDropListItems = AccessTools.Method(
            typeof(DropTable),
            nameof(DropTable.GetDropListItems),
            Type.EmptyTypes)
            ?? throw new MissingMethodException(
                nameof(DropTable),
                nameof(DropTable.GetDropListItems));
        harmony.Patch(
            getDropListItems,
            postfix: new HarmonyMethod(
                typeof(GatheringModule),
                nameof(GetDropListItemsPostfix)));
    }

    private static void GetDropListPostfix(DropTable __instance, List<GameObject> __result)
    {
        GatheringModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (__result == null)
        {
            return;
        }

        active.AdjustDropChance(__instance, __result);
        active.ScaleMaterialCounts(__result);
    }

    private static void GetDropListItemsPostfix(
        DropTable __instance,
        List<ItemDrop.ItemData> __result)
    {
        GatheringModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (__result == null)
        {
            return;
        }

        active.AdjustDropChance(__instance, __result);
        active.ScaleMaterialCounts(__result);
    }

    private ConfigEntryPercent RegisterMaterial(string configKey, string prefabName)
    {
        ConfigEntryPercent modifier = _feature.Percent(
            configKey,
            0f,
            $"Chance-modifier percent applied to {configKey} drop counts.");
        _materialModifiers.Add(prefabName, modifier);
        return modifier;
    }

    private void AdjustDropChance(DropTable table, List<GameObject> result)
    {
        if (!TryGetChanceAdjustment(table, out float originalChance, out float targetChance))
        {
            return;
        }

        if (targetChance < originalChance)
        {
            ThinMaterialDrops(result, targetChance / originalChance);
            return;
        }

        float supplementalChance = (targetChance - originalChance) / (1f - originalChance);
        var presentPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject prefab in result)
        {
            if (TryGetPrefabName(prefab, out string prefabName))
            {
                presentPrefabs.Add(prefabName);
            }
        }

        // Vanilla rolls the table gate before its weighted selection. A postfix cannot replay that
        // missing selection without changing private execution state, so after a miss it rolls the
        // conditional extra chance and appends each absent supported material entry. This produces
        // the configured chance exactly for a single-entry node (for example 20% -> 60% at +200%),
        // but is intentionally an approximation for weighted or multi-pick tables.
        foreach (DropTable.DropData drop in table.m_drops)
        {
            if (!TryGetPrefabName(drop.m_item, out string prefabName) ||
                !_materialModifiers.ContainsKey(prefabName) ||
                presentPrefabs.Contains(prefabName) ||
                !Roll(supplementalChance))
            {
                continue;
            }

            result.Add(drop.m_item);
            presentPrefabs.Add(prefabName);
        }
    }

    private void AdjustDropChance(DropTable table, List<ItemDrop.ItemData> result)
    {
        if (!TryGetChanceAdjustment(table, out float originalChance, out float targetChance))
        {
            return;
        }

        if (targetChance < originalChance)
        {
            ThinMaterialDrops(result, targetChance / originalChance);
            return;
        }

        float supplementalChance = (targetChance - originalChance) / (1f - originalChance);
        var presentPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ItemDrop.ItemData item in result)
        {
            if (item != null && TryGetPrefabName(item.m_dropPrefab, out string prefabName))
            {
                presentPrefabs.Add(prefabName);
            }
        }

        // See the GameObject overload for the post-hoc approximation. AddItemToList is private in
        // the original runtime assembly, so the cached reflection handle above lets vanilla create
        // a valid cloned ItemData without any direct private-member access from this code path.
        foreach (DropTable.DropData drop in table.m_drops)
        {
            if (!TryGetPrefabName(drop.m_item, out string prefabName) ||
                !_materialModifiers.ContainsKey(prefabName) ||
                presentPrefabs.Contains(prefabName) ||
                !Roll(supplementalChance))
            {
                continue;
            }

            AddItemToListMethod.Invoke(table, new object[] { result, drop });
            presentPrefabs.Add(prefabName);
        }
    }

    private bool TryGetChanceAdjustment(
        DropTable table,
        out float originalChance,
        out float targetChance)
    {
        originalChance = Math.Max(0f, Math.Min(1f, table.m_dropChance));
        targetChance = Math.Min(1f, _dropChance.Apply(originalChance));

        // Guaranteed tables are outside Gathering.DropChance's documented scope. A zero-percent
        // table also remains zero under modifier-percent semantics and cannot be supplemented.
        return originalChance > 0f &&
            originalChance < 1f &&
            targetChance != originalChance &&
            table.m_drops != null &&
            table.m_drops.Count != 0;
    }

    private void ThinMaterialDrops(List<GameObject> result, float keepChance)
    {
        var rejectedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var decidedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject prefab in result)
        {
            if (!TryGetPrefabName(prefab, out string prefabName) ||
                !_materialModifiers.ContainsKey(prefabName) ||
                !decidedPrefabs.Add(prefabName))
            {
                continue;
            }

            if (!Roll(keepChance))
            {
                rejectedPrefabs.Add(prefabName);
            }
        }

        if (rejectedPrefabs.Count != 0)
        {
            result.RemoveAll(
                prefab => TryGetPrefabName(prefab, out string prefabName) &&
                    rejectedPrefabs.Contains(prefabName));
        }
    }

    private void ThinMaterialDrops(List<ItemDrop.ItemData> result, float keepChance)
    {
        var rejectedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var decidedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ItemDrop.ItemData item in result)
        {
            if (item == null ||
                !TryGetPrefabName(item.m_dropPrefab, out string prefabName) ||
                !_materialModifiers.ContainsKey(prefabName) ||
                !decidedPrefabs.Add(prefabName))
            {
                continue;
            }

            if (!Roll(keepChance))
            {
                rejectedPrefabs.Add(prefabName);
            }
        }

        if (rejectedPrefabs.Count != 0)
        {
            result.RemoveAll(
                item => item != null &&
                    TryGetPrefabName(item.m_dropPrefab, out string prefabName) &&
                    rejectedPrefabs.Contains(prefabName));
        }
    }

    private void ScaleMaterialCounts(List<GameObject> result)
    {
        if (result.Count == 0)
        {
            return;
        }

        var scaledResult = new List<GameObject>(result.Count);
        foreach (GameObject prefab in result)
        {
            int amount = 1;
            if (TryGetPrefabName(prefab, out string prefabName) &&
                _materialModifiers.TryGetValue(prefabName, out ConfigEntryPercent modifier))
            {
                amount = modifier.ApplyChance(1f);
            }

            for (int copy = 0; copy < amount; copy++)
            {
                scaledResult.Add(prefab);
            }
        }

        result.Clear();
        result.AddRange(scaledResult);
    }

    private void ScaleMaterialCounts(List<ItemDrop.ItemData> result)
    {
        if (result.Count == 0)
        {
            return;
        }

        var adjustedItems = new HashSet<ItemDrop.ItemData>();
        var zeroedItems = new HashSet<ItemDrop.ItemData>();
        foreach (ItemDrop.ItemData item in result)
        {
            if (item == null ||
                !adjustedItems.Add(item) ||
                !TryGetPrefabName(item.m_dropPrefab, out string prefabName) ||
                !_materialModifiers.TryGetValue(prefabName, out ConfigEntryPercent modifier))
            {
                continue;
            }

            item.m_stack = modifier.ApplyChance(item.m_stack);
            if (item.m_stack == 0)
            {
                zeroedItems.Add(item);
            }
        }

        if (zeroedItems.Count != 0)
        {
            result.RemoveAll(item => item != null && zeroedItems.Contains(item));
        }

        // [Drops].DestructibleDrops owns an independent postfix on these same seams. If it runs
        // first, this code scales its expanded result; if it runs second, it duplicates the stacks
        // already scaled here. Either Harmony order therefore composes the two factors
        // multiplicatively. Shared ItemData references are adjusted only once for that reason.
    }

    private static bool TryGetPrefabName(GameObject prefab, out string prefabName)
    {
        if (prefab == null)
        {
            prefabName = string.Empty;
            return false;
        }

        prefabName = prefab.name;
        return !string.IsNullOrEmpty(prefabName);
    }

    private static bool Roll(float chance)
    {
        return chance >= 1f || (chance > 0f && UnityEngine.Random.value < chance);
    }
}

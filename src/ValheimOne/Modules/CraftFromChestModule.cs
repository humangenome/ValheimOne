using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class CraftFromChestModule : IFeatureModule
{
    private static CraftFromChestModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryFloat _range;
    private readonly ConfigEntryBool _ignoreWardedChests;
    private readonly ConfigEntryFloat _cacheSeconds;
    private readonly ConfigEntryBool _includeBuildPlacement;
    private readonly ChestScanner _scanner = new ChestScanner();

    public CraftFromChestModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _range = _feature.Float(
            "Range",
            20f,
            "Maximum distance in metres from the player to a source container. Clamped to 1-50.");
        _ignoreWardedChests = _feature.Bool(
            "IgnoreWardedChests",
            defaultValue: false,
            "Bypass the per-player access gate for containers configured to check a guard stone.");
        _cacheSeconds = _feature.Float(
            "CacheSeconds",
            3f,
            "Seconds between nearby-container scans. Values below 1 are clamped to 1.");
        _includeBuildPlacement = _feature.Bool(
            "IncludeBuildPlacement",
            defaultValue: true,
            "Allow hammer build costs, as well as crafting costs, to pull from nearby containers.");
    }

    public string Name => "Craft from chest";

    public string Section => "CraftFromChest";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.RequiresClient;

    public void ApplyPatches(Harmony harmony)
    {
        // Crafting and build placement are client-owned game logic. Patches stay installed and
        // consult effective values on every call so a server overlay can hot-enable the feature.
        _active = this;

        PatchPostfix(
            harmony,
            typeof(Player),
            nameof(Player.HaveRequirements),
            new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) },
            nameof(HaveRecipeRequirementsPostfix));
        PatchPostfix(
            harmony,
            typeof(Player),
            nameof(Player.HaveRequirements),
            new[] { typeof(Piece), typeof(Player.RequirementMode) },
            nameof(HavePieceRequirementsPostfix));
        PatchPostfix(
            harmony,
            typeof(Player),
            nameof(Player.HaveRequirementItems),
            new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) },
            nameof(HaveRequirementItemsPostfix));

        var consumeResources = AccessTools.Method(
            typeof(Player),
            nameof(Player.ConsumeResources),
            new[] { typeof(Piece.Requirement[]), typeof(int), typeof(int), typeof(int) })
            ?? throw new MissingMethodException(nameof(Player), nameof(Player.ConsumeResources));
        harmony.Patch(
            consumeResources,
            prefix: new HarmonyMethod(
                typeof(CraftFromChestModule),
                nameof(ConsumeResourcesPrefix)));

        PatchPostfix(
            harmony,
            typeof(InventoryGui),
            nameof(InventoryGui.SetupRequirement),
            new[]
            {
                typeof(Transform),
                typeof(Piece.Requirement),
                typeof(Player),
                typeof(bool),
                typeof(int),
                typeof(int),
            },
            nameof(SetupRequirementPostfix));
    }

    private static void PatchPostfix(
        Harmony harmony,
        Type declaringType,
        string methodName,
        Type[] parameterTypes,
        string postfixName)
    {
        var original = AccessTools.Method(declaringType, methodName, parameterTypes)
            ?? throw new MissingMethodException(declaringType.FullName, methodName);
        harmony.Patch(
            original,
            postfix: new HarmonyMethod(typeof(CraftFromChestModule), postfixName));
    }

    private static void HaveRecipeRequirementsPostfix(
        Player __instance,
        Recipe recipe,
        bool discover,
        int qualityLevel,
        int amount,
        ref bool __result)
    {
        CraftFromChestModule? active = _active;
        if (active == null || !active.IsEnabled || __result || discover)
        {
            return;
        }

        // Do not turn a missing station or DLC failure into success. The nested
        // HaveRequirementItems patch normally corrects the material-only failure before this
        // postfix runs; this direct correction also keeps the public overload self-contained.
        if (!__instance.RequiredCraftingStation(recipe, qualityLevel, checkLevel: true))
        {
            return;
        }

        string dlc = recipe.m_item.m_itemData.m_shared.m_dlc;
        if (dlc.Length > 0 && !DLCMan.instance.IsDLCInstalled(dlc))
        {
            return;
        }

        IReadOnlyList<Inventory> chests = active.GetChestInventories(__instance);
        __result = HasRecipeRequirements(__instance.GetInventory(), chests, recipe, qualityLevel, amount);
    }

    private static void HaveRequirementItemsPostfix(
        Player __instance,
        Recipe piece,
        bool discover,
        int qualityLevel,
        int amount,
        ref bool __result)
    {
        CraftFromChestModule? active = _active;
        if (active == null || !active.IsEnabled || __result || discover)
        {
            return;
        }

        IReadOnlyList<Inventory> chests = active.GetChestInventories(__instance);
        __result = HasRecipeRequirements(__instance.GetInventory(), chests, piece, qualityLevel, amount);
    }

    private static void HavePieceRequirementsPostfix(
        Player __instance,
        Piece piece,
        Player.RequirementMode mode,
        ref bool __result)
    {
        CraftFromChestModule? active = _active;
        if (active == null ||
            !active.IsEnabled ||
            !active._includeBuildPlacement.Value ||
            __result ||
            (mode != Player.RequirementMode.CanBuild &&
             mode != Player.RequirementMode.CanAlmostBuild))
        {
            return;
        }

        if (piece.m_craftingStation != null &&
            CraftingStation.HaveBuildStationInRange(
                piece.m_craftingStation.m_name,
                __instance.transform.position) == null &&
            !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench))
        {
            return;
        }

        if (piece.m_dlc.Length > 0 && !DLCMan.instance.IsDLCInstalled(piece.m_dlc))
        {
            return;
        }

        IReadOnlyList<Inventory> chests = active.GetChestInventories(__instance);
        __result = HasPieceRequirements(__instance.GetInventory(), chests, piece, mode);
    }

    private static bool ConsumeResourcesPrefix(
        Player __instance,
        Piece.Requirement[] requirements,
        int qualityLevel,
        int itemQuality,
        int multiplier)
    {
        CraftFromChestModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return true;
        }

        // The verified call sites pass quality 0 for hammer placement and quality 1+ for recipes.
        if (qualityLevel == 0 && !active._includeBuildPlacement.Value)
        {
            return true;
        }

        Inventory playerInventory = __instance.GetInventory();
        IReadOnlyList<Inventory> chests = active.GetChestInventories(__instance);

        foreach (Piece.Requirement requirement in requirements)
        {
            if (requirement.m_resItem == null)
            {
                continue;
            }

            int remaining = requirement.GetAmount(qualityLevel) * multiplier;
            if (remaining <= 0)
            {
                continue;
            }

            string itemName = requirement.m_resItem.m_itemData.m_shared.m_name;
            remaining = RemoveUpTo(playerInventory, itemName, itemQuality, remaining);
            foreach (Inventory chest in chests)
            {
                if (remaining <= 0)
                {
                    break;
                }

                remaining = RemoveUpTo(chest, itemName, itemQuality, remaining);
            }
        }

        return false;
    }

    private static void SetupRequirementPostfix(
        Transform elementRoot,
        Piece.Requirement req,
        Player player,
        bool craft,
        int quality,
        int craftMultiplier,
        bool __result)
    {
        CraftFromChestModule? active = _active;
        if (active == null ||
            !active.IsEnabled ||
            !__result ||
            (!craft && !active._includeBuildPlacement.Value) ||
            req.m_resItem == null)
        {
            return;
        }

        int required = req.GetAmount(quality) * craftMultiplier;
        if (required <= 0)
        {
            return;
        }

        string itemName = req.m_resItem.m_itemData.m_shared.m_name;
        IReadOnlyList<Inventory> chests = active.GetChestInventories(player);
        int available = CountAvailable(player.GetInventory(), chests, itemName, quality: -1);

        Transform amountRoot = elementRoot.transform.Find("res_amount");
        if (amountRoot == null)
        {
            return;
        }

        TMP_Text amountText = amountRoot.GetComponent<TMP_Text>();
        if (amountText == null)
        {
            return;
        }

        amountText.text = $"{available}/{required}";
        if (available >= required)
        {
            amountText.color = Color.white;
        }
    }

    private IReadOnlyList<Inventory> GetChestInventories(Player player)
    {
        return _scanner.GetInventories(
            player,
            _range.Value,
            _ignoreWardedChests.Value,
            _cacheSeconds.Value);
    }

    private static bool HasRecipeRequirements(
        Inventory playerInventory,
        IReadOnlyList<Inventory> chests,
        Recipe recipe,
        int qualityLevel,
        int multiplier)
    {
        foreach (Piece.Requirement requirement in recipe.m_resources)
        {
            if (requirement.m_resItem == null)
            {
                continue;
            }

            int required = requirement.GetAmount(qualityLevel) * multiplier;
            int available = 0;
            int maximumQuality = requirement.m_resItem.m_itemData.m_shared.m_maxQuality;
            for (int quality = 1; quality <= maximumQuality; quality++)
            {
                available = Math.Max(
                    available,
                    CountAvailable(
                        playerInventory,
                        chests,
                        requirement.m_resItem.m_itemData.m_shared.m_name,
                        quality));
            }

            if (recipe.m_requireOnlyOneIngredient)
            {
                if (available >= required)
                {
                    return true;
                }
            }
            else if (available < required)
            {
                return false;
            }
        }

        return !recipe.m_requireOnlyOneIngredient;
    }

    private static bool HasPieceRequirements(
        Inventory playerInventory,
        IReadOnlyList<Inventory> chests,
        Piece piece,
        Player.RequirementMode mode)
    {
        foreach (Piece.Requirement requirement in piece.m_resources)
        {
            if (requirement.m_resItem == null || requirement.m_amount <= 0)
            {
                continue;
            }

            string itemName = requirement.m_resItem.m_itemData.m_shared.m_name;
            int available = CountAvailable(playerInventory, chests, itemName, quality: -1);
            int required = mode == Player.RequirementMode.CanAlmostBuild
                ? 1
                : requirement.m_amount;
            if (available < required)
            {
                return false;
            }
        }

        return true;
    }

    private static int CountAvailable(
        Inventory playerInventory,
        IReadOnlyList<Inventory> chests,
        string itemName,
        int quality)
    {
        long available = playerInventory.CountItems(itemName, quality);
        foreach (Inventory chest in chests)
        {
            available += chest.CountItems(itemName, quality);
            if (available >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)available;
    }

    private static int RemoveUpTo(
        Inventory inventory,
        string itemName,
        int itemQuality,
        int requested)
    {
        int available = inventory.CountItems(itemName, itemQuality);
        int removed = Math.Min(available, requested);
        if (removed > 0)
        {
            inventory.RemoveItem(itemName, removed, itemQuality);
        }

        return requested - removed;
    }
}

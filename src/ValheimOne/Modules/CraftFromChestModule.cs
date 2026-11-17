using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;

namespace ValheimOne.Modules;

public sealed class CraftFromChestModule : IFeatureModule
{
    private static readonly Func<Player, string, int, bool> KnowsStationLevel =
        AccessTools.MethodDelegate<Func<Player, string, int, bool>>(
            AccessTools.Method(
                typeof(Player),
                "KnowStationLevel",
                new[] { typeof(string), typeof(int) })
            ?? throw new MissingMethodException(nameof(Player), "KnowStationLevel"));

    private static CraftFromChestModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryFloat _range;
    private readonly ConfigEntryBool _ignoreWardedChests;
    private readonly ConfigEntryFloat _cacheSeconds;
    private readonly ConfigEntryBool _includeBuildPlacement;
    private readonly ChestScanner _scanner = new ChestScanner(includeRemote: true);
    private readonly List<Inventory> _ownedInventories = new();
    [ThreadStatic] private static bool s_ownedOnly;
    private float _craftWaitStarted = -1f;

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
        ChestOwnership.Install(harmony, Section, () => IsEnabled);
        harmony.Patch(
            AccessTools.Method(typeof(InventoryGui), "UpdateRecipe", new[] { typeof(Player), typeof(float) }),
            prefix: new HarmonyMethod(typeof(CraftFromChestModule), nameof(UpdateRecipePrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(InventoryGui), "DoCrafting", new[] { typeof(Player) }),
            prefix: new HarmonyMethod(typeof(CraftFromChestModule), nameof(DoCraftingPrefix)),
            finalizer: new HarmonyMethod(typeof(CraftFromChestModule), nameof(DoCraftingFinalizer)));
        harmony.Patch(
            AccessTools.Method(typeof(Player), nameof(Player.TryPlacePiece), new[] { typeof(Piece) }),
            prefix: new HarmonyMethod(typeof(CraftFromChestModule), nameof(TryPlacePiecePrefix)));

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

    private static bool UpdateRecipePrefix(
        Player player, ref float dt, Recipe ___m_craftRecipe, ItemDrop.ItemData ___m_craftUpgradeItem,
        bool ___m_multiCrafting, int ___m_multiCraftAmount, ref float ___m_craftTimer)
    {
        CraftFromChestModule? active = _active;
        if (active == null || !active.IsEnabled || ___m_craftTimer < 0f || ___m_craftRecipe == null)
        {
            if (active != null) active._craftWaitStarted = -1f;
            return true;
        }

        ChestOwnership.Result result = active.PrepareRecipe(player, ___m_craftRecipe,
            ___m_craftUpgradeItem, ___m_multiCrafting ? ___m_multiCraftAmount : 1);
        if (result == ChestOwnership.Result.Ready)
        {
            active._craftWaitStarted = -1f;
            return true;
        }

        if (active._craftWaitStarted < 0f) active._craftWaitStarted = Time.realtimeSinceStartup;
        if (result == ChestOwnership.Result.Unavailable ||
            Time.realtimeSinceStartup - active._craftWaitStarted >= 5f)
        {
            ___m_craftTimer = -1f;
            active._craftWaitStarted = -1f;
            GameCompat.TryMessage(player, MessageHud.MessageType.Center, "Couldn't use a nearby chest. Close any open chests and try again.");
            return true;
        }

        // Show the native progress/cancel controls, but keep the craft clock still
        // until the owner and inventory updates arrive. Other UI remains responsive.
        dt = 0f;
        return true;
    }

    private static bool DoCraftingPrefix(
        Player player, Recipe ___m_craftRecipe, ItemDrop.ItemData ___m_craftUpgradeItem,
        bool ___m_multiCrafting, int ___m_multiCraftAmount, out bool __state)
    {
        __state = s_ownedOnly;
        CraftFromChestModule? active = _active;
        if (active == null || !active.IsEnabled || ___m_craftRecipe == null) return true;
        if (active.PrepareRecipe(player, ___m_craftRecipe, ___m_craftUpgradeItem,
                ___m_multiCrafting ? ___m_multiCraftAmount : 1) != ChestOwnership.Result.Ready)
            return false;
        s_ownedOnly = true;
        return true;
    }

    private static Exception? DoCraftingFinalizer(Exception? __exception, bool __state)
    {
        s_ownedOnly = __state;
        return __exception;
    }

    private ChestOwnership.Result PrepareRecipe(Player player, Recipe recipe, ItemDrop.ItemData? upgrade, int amount)
    {
        // Vanilla's one-ingredient recipes select an actual item from the backpack.
        // Leave that existing selection path and no-cost crafting alone.
        if (recipe.m_requireOnlyOneIngredient || player.NoCostCheat() ||
            ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost)) return ChestOwnership.Result.Ready;
        return PrepareResources(player, recipe.m_resources, upgrade == null ? 1 : upgrade.m_quality + 1, amount);
    }

    private static bool TryPlacePiecePrefix(
        Player __instance, Piece piece, bool ___m_noPlacementCost, ref bool __result)
    {
        CraftFromChestModule? active = _active;
        if (active == null || !active.IsEnabled || !active._includeBuildPlacement.Value ||
            ___m_noPlacementCost || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) return true;

        ChestOwnership.Result result = active.PrepareResources(__instance, piece.m_resources, 0, 1);
        if (result == ChestOwnership.Result.Ready) return true;

        // Never replay a build input after an asynchronous transfer: the player may
        // have moved the cursor or selected another piece while waiting for its owner.
        GameCompat.TryMessage(__instance, MessageHud.MessageType.Center,
            result == ChestOwnership.Result.Pending
                ? "Waiting for a nearby chest. Place it again in a moment."
                : "Couldn't use a nearby chest. Close any open chests and try again.");
        __result = false;
        return false;
    }

    private ChestOwnership.Result PrepareResources(Player player, Piece.Requirement[] requirements, int quality, int multiplier)
    {
        IReadOnlyList<Inventory> chests = GetChestInventories(player);
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Piece.Requirement requirement in requirements)
        {
            if (requirement.m_resItem == null ||
                (quality > 0 && !GameCompat.IsCraftingRequirementActive(player, requirement))) continue;
            string name = requirement.m_resItem.m_itemData.m_shared.m_name;
            int count = requirement.GetAmount(quality) * multiplier;
            if (count <= 0) continue;
            totals.TryGetValue(name, out int previous);
            totals[name] = checked(previous + count);
        }

        foreach (KeyValuePair<string, int> requirement in totals)
        {
            int remaining = requirement.Value - player.GetInventory().CountItems(requirement.Key);
            // Use owned sources first. Acquire remote sources one at a time, and
            // recount from their current inventory before authorizing any output.
            for (int pass = 0; pass < 2 && remaining > 0; pass++)
            {
                foreach (Inventory inventory in chests)
                {
                    if (remaining <= 0) break;
                    Container? container = _scanner.FindContainer(inventory);
                    if (container == null || container.IsOwner() != (pass == 0)) continue;
                    if (inventory.CountItems(requirement.Key) <= 0) continue;
                    ChestOwnership.Result state = ChestOwnership.Prepare(container, player);
                    if (state != ChestOwnership.Result.Ready) return state;
                    remaining -= inventory.CountItems(requirement.Key);
                }
            }
            if (remaining > 0) return ChestOwnership.Result.Unavailable;
        }
        return ChestOwnership.Result.Ready;
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
        __result = HasRecipeRequirements(__instance, chests, recipe, qualityLevel, amount);
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
        __result = HasRecipeRequirements(__instance, chests, piece, qualityLevel, amount);
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

        if (piece.m_craftingStation != null)
        {
            if (mode == Player.RequirementMode.CanAlmostBuild)
            {
                // Vanilla only requires this station to be known in CanAlmostBuild mode.
                // KnowStationLevel is private at runtime, so use the cached open-instance
                // delegate rather than a direct call through the publicized compile assembly.
                if (!KnowsStationLevel(__instance, piece.m_craftingStation.m_name, 0))
                {
                    return;
                }
            }
            else if (CraftingStation.HaveBuildStationInRange(
                         piece.m_craftingStation.m_name,
                         __instance.transform.position) == null &&
                     !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench))
            {
                return;
            }
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
        IReadOnlyList<Inventory> chests = active.GetChestInventories(__instance, ownedOnly: true);

        foreach (Piece.Requirement requirement in requirements)
        {
            if (requirement.m_resItem == null ||
                (qualityLevel > 0 && !GameCompat.IsCraftingRequirementActive(__instance, requirement)))
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

    private IReadOnlyList<Inventory> GetChestInventories(Player player, bool ownedOnly = false)
    {
        IReadOnlyList<Inventory> inventories = _scanner.GetInventories(
            player,
            _range.Value,
            _ignoreWardedChests.Value,
            _cacheSeconds.Value);
        if (!ownedOnly && !s_ownedOnly) return inventories;
        _ownedInventories.Clear();
        foreach (Inventory inventory in inventories)
        {
            Container? container = _scanner.FindContainer(inventory);
            if (container != null && container.IsOwner()) _ownedInventories.Add(inventory);
        }
        return _ownedInventories;
    }

    private static bool HasRecipeRequirements(
        Player player,
        IReadOnlyList<Inventory> chests,
        Recipe recipe,
        int qualityLevel,
        int multiplier)
    {
        Inventory playerInventory = player.GetInventory();
        foreach (Piece.Requirement requirement in recipe.m_resources)
        {
            if (requirement.m_resItem == null || !GameCompat.IsCraftingRequirementActive(player, requirement))
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

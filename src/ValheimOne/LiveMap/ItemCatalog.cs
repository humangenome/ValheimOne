/*
 * Dedicated-server ObjectDB prefabs can retain Sprite references, but headless builds do not
 * guarantee them and their imported Texture2D data is normally non-readable. GetPixels therefore
 * throws, while RenderTexture or Graphics.CopyTexture readback needs a graphics device unavailable
 * under -nographics. The catalog intentionally leaves icon rendering to client category glyphs.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class ItemCatalog
{
    public const int SchemaVersion = 1;

    private const int MaximumCacheBytes = 64 * 1024 * 1024;
    private const string CacheFileName = "catalog.json";

    private static ItemCatalog? _current;

    private readonly ConsoleItem[] _consoleItems;
    private readonly Dictionary<string, ConsoleItem> _consoleItemsByToken;
    private readonly Dictionary<string, List<ConsoleItem>> _reverseRecipes;

    private ItemCatalog(
        byte[] content,
        int itemCount,
        int recipeCount,
        int conversionCount,
        int droppedByEdgeCount,
        ConsoleItem[] consoleItems)
    {
        Content = content;
        ETag = ComputeETag(content);
        ItemCount = itemCount;
        RecipeCount = recipeCount;
        ConversionCount = conversionCount;
        DroppedByEdgeCount = droppedByEdgeCount;
        _consoleItems = consoleItems;
        _consoleItemsByToken = BuildTokenLookup(_consoleItems);
        _reverseRecipes = BuildReverseRecipes(_consoleItems);
    }

    public static ItemCatalog? Current => _current;

    public byte[] Content { get; }

    public string ETag { get; }

    public int ItemCount { get; }

    public int RecipeCount { get; }

    public int ConversionCount { get; }

    public int DroppedByEdgeCount { get; }

    public static ItemCatalog LoadOrBuild(
        string dataDirectory,
        string gameVersion,
        ObjectDB objectDb,
        ZNetScene netScene,
        ModLogger log)
    {
        var timer = Stopwatch.StartNew();
        string path = Path.Combine(dataDirectory, CacheFileName);
        if (TryLoad(path, gameVersion, log, out ItemCatalog? cached))
        {
            _current = cached;
            LogSummary(log, cached!, timer.ElapsedMilliseconds, "cache hit");
            return cached!;
        }

        BuildResult result = Build(gameVersion, objectDb, netScene);
        byte[] content = Encoding.UTF8.GetBytes(result.Json);
        var catalog = new ItemCatalog(
            content,
            result.ItemCount,
            result.RecipeCount,
            result.ConversionCount,
            result.DroppedByEdgeCount,
            result.ConsoleItems);
        _current = catalog;
        TryPersist(path, result.Json, log);
        LogSummary(log, catalog, timer.ElapsedMilliseconds, "extracted");
        return catalog;
    }

    public bool TryResolveItem(
        string query,
        out ConsoleItem? item,
        out List<ConsoleItem> candidates)
    {
        item = null;
        candidates = new List<ConsoleItem>();
        string value = (query ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (_consoleItemsByToken.TryGetValue(value, out ConsoleItem? tokenMatch))
        {
            item = tokenMatch;
            return true;
        }

        CollectMatches(
            candidates,
            entry => string.Equals(entry.Name, value, StringComparison.OrdinalIgnoreCase));
        if (ResolveUnique(candidates, out item))
        {
            return true;
        }

        if (candidates.Count > 1)
        {
            return false;
        }

        CollectMatches(
            candidates,
            entry => entry.Name.StartsWith(value, StringComparison.OrdinalIgnoreCase) ||
                     entry.Token.StartsWith(value, StringComparison.OrdinalIgnoreCase));
        if (ResolveUnique(candidates, out item))
        {
            return true;
        }

        if (candidates.Count > 1)
        {
            return false;
        }

        CollectMatches(
            candidates,
            entry => entry.Name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     entry.Token.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
        return ResolveUnique(candidates, out item);
    }

    public IReadOnlyList<ConsoleItem> GetReverseRecipeUses(ConsoleItem item)
    {
        return _reverseRecipes.TryGetValue(item.Token, out List<ConsoleItem>? uses)
            ? uses
            : Array.Empty<ConsoleItem>();
    }

    private void CollectMatches(List<ConsoleItem> matches, Predicate<ConsoleItem> predicate)
    {
        matches.Clear();
        for (int index = 0; index < _consoleItems.Length; index++)
        {
            ConsoleItem entry = _consoleItems[index];
            if (predicate(entry))
            {
                matches.Add(entry);
            }
        }
    }

    private static bool ResolveUnique(
        List<ConsoleItem> candidates,
        out ConsoleItem? item)
    {
        if (candidates.Count == 1)
        {
            item = candidates[0];
            return true;
        }

        item = null;
        return false;
    }

    private static BuildResult Build(
        string gameVersion,
        ObjectDB objectDb,
        ZNetScene netScene)
    {
        var items = new Dictionary<string, CatalogItem>(StringComparer.Ordinal);
        Localization? localization = null;
        string selectedLanguage = "English";
        bool restoreLanguage = false;
        try
        {
            try
            {
                localization = Localization.instance;
                selectedLanguage = localization.GetSelectedLanguage();
                if (!string.Equals(selectedLanguage, "English", StringComparison.Ordinal))
                {
                    localization.SetupLanguage("English");
                    restoreLanguage = true;
                }
            }
            catch (Exception)
            {
                localization = null;
            }

            for (int index = 0; index < objectDb.m_items.Count; index++)
            {
                try
                {
                    GameObject prefab = objectDb.m_items[index];
                    if (prefab == null)
                    {
                        continue;
                    }

                    ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
                    if (itemDrop != null)
                    {
                        GetOrAddItem(items, itemDrop, localization);
                    }
                }
                catch (Exception)
                {
                    // A malformed modded item must not abort catalog extraction.
                }
            }

            int recipeCount = AddRecipes(objectDb, items, localization);
            AddSceneSources(
                netScene,
                items,
                localization,
                out int conversionCount,
                out int droppedByEdgeCount);

            var sortedItems = new List<CatalogItem>(items.Values);
            sortedItems.Sort((left, right) =>
                StringComparer.Ordinal.Compare(left.Token, right.Token));
            for (int index = 0; index < sortedItems.Count; index++)
            {
                CatalogItem item = sortedItems[index];
                item.Recipes.Sort(CompareRecipes);
                item.Sources.Sort(CompareConversions);
                item.Uses.Sort(CompareConversions);
                item.DroppedBy.Sort(CompareDrops);
            }

            string generatedUtc = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture);
            string json = Serialize(gameVersion, generatedUtc, sortedItems);
            ConsoleItem[] consoleItems = BuildConsoleItems(sortedItems);
            return new BuildResult(
                json,
                sortedItems.Count,
                recipeCount,
                conversionCount,
                droppedByEdgeCount,
                consoleItems);
        }
        finally
        {
            if (restoreLanguage && localization != null)
            {
                try
                {
                    localization.SetupLanguage(
                        string.IsNullOrWhiteSpace(selectedLanguage)
                            ? "English"
                            : selectedLanguage);
                }
                catch (Exception)
                {
                    // Catalog extraction must not disturb startup if localization cannot restore.
                }
            }
        }
    }

    private static int AddRecipes(
        ObjectDB objectDb,
        Dictionary<string, CatalogItem> items,
        Localization? localization)
    {
        int resolved = 0;
        for (int index = 0; index < objectDb.m_recipes.Count; index++)
        {
            try
            {
                Recipe recipe = objectDb.m_recipes[index];
                if (recipe == null || recipe.m_item == null)
                {
                    continue;
                }

                CatalogItem output = GetOrAddItem(items, recipe.m_item, localization);
                CraftingStation? station = recipe.m_craftingStation;
                string stationPrefab = station == null
                    ? string.Empty
                    : NormalizeToken(station.gameObject.name);
                string stationName = station == null
                    ? string.Empty
                    : LocalizeText(localization, station.m_name, stationPrefab, false);
                var ingredients = new List<CatalogIngredient>(recipe.m_resources.Length);
                for (int resourceIndex = 0;
                     resourceIndex < recipe.m_resources.Length;
                     resourceIndex++)
                {
                    try
                    {
                        Piece.Requirement requirement = recipe.m_resources[resourceIndex];
                        if (requirement == null || requirement.m_resItem == null)
                        {
                            continue;
                        }

                        CatalogItem resource = GetOrAddItem(
                            items,
                            requirement.m_resItem,
                            localization);
                        ingredients.Add(new CatalogIngredient(
                            resource.Token,
                            resource.Name,
                            requirement.m_amount,
                            requirement.m_amountPerLevel));
                    }
                    catch (Exception)
                    {
                        // Skip only the malformed requirement.
                    }
                }

                output.Recipes.Add(new CatalogRecipe(
                    recipe.m_enabled,
                    Math.Max(1, recipe.m_amount),
                    stationPrefab,
                    stationName,
                    Math.Max(1, recipe.m_minStationLevel),
                    ingredients));
                resolved++;
            }
            catch (Exception)
            {
                // A malformed modded recipe must not abort catalog extraction.
            }
        }

        return resolved;
    }

    private static void AddSceneSources(
        ZNetScene netScene,
        Dictionary<string, CatalogItem> items,
        Localization? localization,
        out int conversionCount,
        out int droppedByEdgeCount)
    {
        conversionCount = 0;
        droppedByEdgeCount = 0;
        var seen = new HashSet<GameObject>();
        AddScenePrefabList(
            netScene.m_prefabs,
            seen,
            items,
            localization,
            ref conversionCount,
            ref droppedByEdgeCount);
        AddScenePrefabList(
            netScene.m_nonNetViewPrefabs,
            seen,
            items,
            localization,
            ref conversionCount,
            ref droppedByEdgeCount);
    }

    private static void AddScenePrefabList(
        List<GameObject> prefabs,
        HashSet<GameObject> seen,
        Dictionary<string, CatalogItem> items,
        Localization? localization,
        ref int conversionCount,
        ref int droppedByEdgeCount)
    {
        for (int index = 0; index < prefabs.Count; index++)
        {
            GameObject prefab = prefabs[index];
            if (prefab == null || !seen.Add(prefab))
            {
                continue;
            }

            try
            {
                Smelter? smelter = prefab.GetComponent<Smelter>();
                if (smelter != null)
                {
                    AddSmelterConversions(
                        smelter,
                        items,
                        localization,
                        ref conversionCount);
                }

                CookingStation? cookingStation = prefab.GetComponent<CookingStation>();
                if (cookingStation != null)
                {
                    AddCookingConversions(
                        cookingStation,
                        items,
                        localization,
                        ref conversionCount);
                }

                Fermenter? fermenter = prefab.GetComponent<Fermenter>();
                if (fermenter != null)
                {
                    AddFermenterConversions(
                        fermenter,
                        items,
                        localization,
                        ref conversionCount);
                }

                CharacterDrop? characterDrop = prefab.GetComponent<CharacterDrop>();
                Character? character = prefab.GetComponent<Character>();
                if (characterDrop != null && character != null)
                {
                    AddCreatureDrops(
                        prefab,
                        character,
                        characterDrop,
                        items,
                        localization,
                        ref droppedByEdgeCount);
                }
            }
            catch (Exception)
            {
                // A malformed modded prefab must not abort the single scene-prefab walk.
            }
        }
    }

    private static void AddSmelterConversions(
        Smelter station,
        Dictionary<string, CatalogItem> items,
        Localization? localization,
        ref int conversionCount)
    {
        string stationPrefab = NormalizeToken(station.gameObject.name);
        string stationName = LocalizeText(localization, station.m_name, stationPrefab, false);
        for (int index = 0; index < station.m_conversion.Count; index++)
        {
            try
            {
                Smelter.ItemConversion conversion = station.m_conversion[index];
                if (conversion != null && conversion.m_from != null && conversion.m_to != null)
                {
                    AddConversion(
                        "smelter",
                        stationPrefab,
                        stationName,
                        conversion.m_from,
                        conversion.m_to,
                        1,
                        items,
                        localization);
                    conversionCount++;
                }
            }
            catch (Exception)
            {
                // Skip only the malformed conversion.
            }
        }
    }

    private static void AddCookingConversions(
        CookingStation station,
        Dictionary<string, CatalogItem> items,
        Localization? localization,
        ref int conversionCount)
    {
        string stationPrefab = NormalizeToken(station.gameObject.name);
        string stationName = LocalizeText(localization, station.m_name, stationPrefab, false);
        for (int index = 0; index < station.m_conversion.Count; index++)
        {
            try
            {
                CookingStation.ItemConversion conversion = station.m_conversion[index];
                if (conversion != null && conversion.m_from != null && conversion.m_to != null)
                {
                    AddConversion(
                        "cooking",
                        stationPrefab,
                        stationName,
                        conversion.m_from,
                        conversion.m_to,
                        1,
                        items,
                        localization);
                    conversionCount++;
                }
            }
            catch (Exception)
            {
                // Skip only the malformed conversion.
            }
        }
    }

    private static void AddFermenterConversions(
        Fermenter station,
        Dictionary<string, CatalogItem> items,
        Localization? localization,
        ref int conversionCount)
    {
        string stationPrefab = NormalizeToken(station.gameObject.name);
        string stationName = LocalizeText(localization, station.m_name, stationPrefab, false);
        for (int index = 0; index < station.m_conversion.Count; index++)
        {
            try
            {
                Fermenter.ItemConversion conversion = station.m_conversion[index];
                if (conversion != null && conversion.m_from != null && conversion.m_to != null)
                {
                    AddConversion(
                        "fermenter",
                        stationPrefab,
                        stationName,
                        conversion.m_from,
                        conversion.m_to,
                        Math.Max(1, conversion.m_producedItems),
                        items,
                        localization);
                    conversionCount++;
                }
            }
            catch (Exception)
            {
                // Skip only the malformed conversion.
            }
        }
    }

    private static void AddConversion(
        string method,
        string stationPrefab,
        string stationName,
        ItemDrop inputDrop,
        ItemDrop outputDrop,
        int outputAmount,
        Dictionary<string, CatalogItem> items,
        Localization? localization)
    {
        CatalogItem input = GetOrAddItem(items, inputDrop, localization);
        CatalogItem output = GetOrAddItem(items, outputDrop, localization);
        output.Sources.Add(new CatalogConversion(
            method,
            stationPrefab,
            stationName,
            input.Token,
            input.Name,
            outputAmount));
        input.Uses.Add(new CatalogConversion(
            method,
            stationPrefab,
            stationName,
            output.Token,
            output.Name,
            outputAmount));
    }

    private static void AddCreatureDrops(
        GameObject creaturePrefab,
        Character character,
        CharacterDrop characterDrop,
        Dictionary<string, CatalogItem> items,
        Localization? localization,
        ref int droppedByEdgeCount)
    {
        string creatureToken = NormalizeToken(creaturePrefab.name);
        string creatureName = LocalizeText(
            localization,
            character.m_name,
            creatureToken,
            false);
        for (int index = 0; index < characterDrop.m_drops.Count; index++)
        {
            try
            {
                CharacterDrop.Drop drop = characterDrop.m_drops[index];
                if (drop == null || drop.m_prefab == null)
                {
                    continue;
                }

                ItemDrop? itemDrop = drop.m_prefab.GetComponent<ItemDrop>();
                if (itemDrop == null)
                {
                    continue;
                }

                CatalogItem item = GetOrAddItem(items, itemDrop, localization);
                item.DroppedBy.Add(new CatalogDrop(
                    creatureToken,
                    creatureName,
                    drop.m_chance));
                droppedByEdgeCount++;
            }
            catch (Exception)
            {
                // Skip only the malformed drop edge.
            }
        }
    }

    private static CatalogItem GetOrAddItem(
        Dictionary<string, CatalogItem> items,
        ItemDrop itemDrop,
        Localization? localization)
    {
        string token = NormalizeToken(itemDrop.gameObject.name);
        if (items.TryGetValue(token, out CatalogItem? existing))
        {
            return existing;
        }

        ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;
        var item = new CatalogItem(
            token,
            LocalizeText(localization, shared.m_name, token, false),
            LocalizeText(localization, shared.m_description, string.Empty, true),
            shared.m_itemType.ToString(),
            Math.Max(1, shared.m_maxQuality),
            shared.m_toolTier,
            shared.m_weight,
            Math.Max(1, shared.m_maxStackSize),
            shared.m_teleportable,
            IsArmorType(shared.m_itemType),
            shared.m_armor,
            shared.m_armorPerLevel,
            shared.m_damages,
            shared.m_damagesPerLevel);
        items.Add(token, item);
        return item;
    }

    private static string NormalizeToken(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string LocalizeText(
        Localization? localization,
        string? value,
        string fallback,
        bool emptyForMissingToken)
    {
        string source = (value ?? string.Empty).Trim();
        if (source.Length == 0)
        {
            return fallback;
        }

        string localized;
        try
        {
            localized = localization?.Localize(source).Trim() ?? source;
        }
        catch (Exception)
        {
            localized = source;
        }

        if (IsMissingLocalization(source, localized))
        {
            return emptyForMissingToken ? string.Empty : fallback;
        }

        return localized.Length == 0 ? fallback : localized;
    }

    private static bool IsMissingLocalization(string source, string localized)
    {
        if (source[0] != '$')
        {
            return false;
        }

        string key = source.Substring(1);
        return string.Equals(localized, source, StringComparison.Ordinal) ||
               string.Equals(localized, "[" + key + "]", StringComparison.Ordinal) ||
               localized.IndexOf("MISSING KEY", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsArmorType(ItemDrop.ItemData.ItemType itemType)
    {
        return itemType == ItemDrop.ItemData.ItemType.Helmet ||
               itemType == ItemDrop.ItemData.ItemType.Chest ||
               itemType == ItemDrop.ItemData.ItemType.Legs ||
               itemType == ItemDrop.ItemData.ItemType.Hands ||
               itemType == ItemDrop.ItemData.ItemType.Shoulder;
    }

    private static string Serialize(
        string gameVersion,
        string generatedUtc,
        List<CatalogItem> items)
    {
        var json = new StringBuilder(256 * 1024);
        json.Append("{\"version\":{\"game\":")
            .Append(JsonWriter.Quote(gameVersion));
        json.Append(",\"mod\":")
            .Append(JsonWriter.Quote(ValheimOnePlugin.PluginVersion));
        json.Append(",\"schema\":")
            .Append(SchemaVersion.ToString(CultureInfo.InvariantCulture));
        json.Append("},\"generatedUtc\":")
            .Append(JsonWriter.Quote(generatedUtc));
        json.Append(",\"items\":[");
        for (int index = 0; index < items.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            AppendItem(json, items[index]);
        }

        json.Append("]}");
        return json.ToString();
    }

    private static void AppendItem(StringBuilder json, CatalogItem item)
    {
        json.Append("{\"token\":").Append(JsonWriter.Quote(item.Token));
        json.Append(",\"name\":").Append(JsonWriter.Quote(item.Name));
        json.Append(",\"description\":").Append(JsonWriter.Quote(item.Description));
        json.Append(",\"type\":").Append(JsonWriter.Quote(item.Type));
        json.Append(",\"maxQuality\":").Append(
            item.MaxQuality.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"toolTier\":").Append(
            item.ToolTier.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"weight\":").Append(JsonWriter.Number(item.Weight));
        json.Append(",\"maxStackSize\":").Append(
            item.MaxStackSize.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"teleportable\":").Append(item.Teleportable ? "true" : "false");
        if (item.HasArmor)
        {
            json.Append(",\"armor\":{\"base\":")
                .Append(JsonWriter.Number(item.Armor));
            json.Append(",\"perLevel\":")
                .Append(JsonWriter.Number(item.ArmorPerLevel));
            json.Append('}');
        }

        if (HasDamage(item.Damage) || HasDamage(item.DamagePerLevel))
        {
            json.Append(",\"damage\":{\"base\":");
            AppendDamage(json, item.Damage);
            json.Append(",\"perLevel\":");
            AppendDamage(json, item.DamagePerLevel);
            json.Append('}');
        }

        json.Append(",\"recipes\":[");
        for (int index = 0; index < item.Recipes.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            AppendRecipe(json, item.Recipes[index]);
        }

        json.Append("],\"sources\":[");
        AppendConversions(json, item.Sources, "input");
        json.Append("],\"uses\":[");
        AppendConversions(json, item.Uses, "output");
        json.Append("],\"droppedBy\":[");
        for (int index = 0; index < item.DroppedBy.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            CatalogDrop drop = item.DroppedBy[index];
            json.Append("{\"creature\":").Append(JsonWriter.Quote(drop.CreatureToken));
            json.Append(",\"name\":").Append(JsonWriter.Quote(drop.CreatureName));
            json.Append(",\"chance\":").Append(JsonWriter.Number(drop.Chance));
            json.Append('}');
        }

        json.Append("]}");
    }

    private static void AppendRecipe(StringBuilder json, CatalogRecipe recipe)
    {
        json.Append("{\"enabled\":").Append(recipe.Enabled ? "true" : "false");
        json.Append(",\"amount\":").Append(recipe.Amount.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"station\":");
        if (recipe.StationPrefab.Length == 0)
        {
            json.Append("null");
        }
        else
        {
            json.Append("{\"prefab\":").Append(JsonWriter.Quote(recipe.StationPrefab));
            json.Append(",\"name\":").Append(JsonWriter.Quote(recipe.StationName));
            json.Append('}');
        }

        json.Append(",\"minStationLevel\":").Append(
            recipe.MinimumStationLevel.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"ingredients\":[");
        for (int index = 0; index < recipe.Ingredients.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            CatalogIngredient ingredient = recipe.Ingredients[index];
            json.Append("{\"prefab\":").Append(JsonWriter.Quote(ingredient.Prefab));
            json.Append(",\"name\":").Append(JsonWriter.Quote(ingredient.Name));
            json.Append(",\"amount\":").Append(
                ingredient.Amount.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"amountPerLevel\":").Append(
                ingredient.AmountPerLevel.ToString(CultureInfo.InvariantCulture));
            json.Append('}');
        }

        json.Append("]}");
    }

    private static void AppendConversions(
        StringBuilder json,
        List<CatalogConversion> conversions,
        string itemProperty)
    {
        for (int index = 0; index < conversions.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            CatalogConversion conversion = conversions[index];
            json.Append("{\"method\":").Append(JsonWriter.Quote(conversion.Method));
            json.Append(",\"station\":{\"prefab\":")
                .Append(JsonWriter.Quote(conversion.StationPrefab));
            json.Append(",\"name\":").Append(JsonWriter.Quote(conversion.StationName));
            json.Append("},").Append(JsonWriter.Quote(itemProperty)).Append(":{\"prefab\":")
                .Append(JsonWriter.Quote(conversion.ItemPrefab));
            json.Append(",\"name\":").Append(JsonWriter.Quote(conversion.ItemName));
            json.Append("},\"amount\":").Append(
                conversion.Amount.ToString(CultureInfo.InvariantCulture));
            json.Append('}');
        }
    }

    private static bool HasDamage(HitData.DamageTypes damage)
    {
        return damage.m_damage != 0f ||
               damage.m_blunt != 0f ||
               damage.m_slash != 0f ||
               damage.m_pierce != 0f ||
               damage.m_chop != 0f ||
               damage.m_pickaxe != 0f ||
               damage.m_fire != 0f ||
               damage.m_frost != 0f ||
               damage.m_lightning != 0f ||
               damage.m_poison != 0f ||
               damage.m_spirit != 0f;
    }

    private static void AppendDamage(StringBuilder json, HitData.DamageTypes damage)
    {
        json.Append('{');
        bool wroteValue = false;
        AppendDamageValue(json, "generic", damage.m_damage, ref wroteValue);
        AppendDamageValue(json, "blunt", damage.m_blunt, ref wroteValue);
        AppendDamageValue(json, "slash", damage.m_slash, ref wroteValue);
        AppendDamageValue(json, "pierce", damage.m_pierce, ref wroteValue);
        AppendDamageValue(json, "chop", damage.m_chop, ref wroteValue);
        AppendDamageValue(json, "pickaxe", damage.m_pickaxe, ref wroteValue);
        AppendDamageValue(json, "fire", damage.m_fire, ref wroteValue);
        AppendDamageValue(json, "frost", damage.m_frost, ref wroteValue);
        AppendDamageValue(json, "lightning", damage.m_lightning, ref wroteValue);
        AppendDamageValue(json, "poison", damage.m_poison, ref wroteValue);
        AppendDamageValue(json, "spirit", damage.m_spirit, ref wroteValue);
        json.Append('}');
    }

    private static void AppendDamageValue(
        StringBuilder json,
        string name,
        float value,
        ref bool wroteValue)
    {
        if (value == 0f)
        {
            return;
        }

        if (wroteValue)
        {
            json.Append(',');
        }

        json.Append(JsonWriter.Quote(name)).Append(':').Append(JsonWriter.Number(value));
        wroteValue = true;
    }

    private static int CompareRecipes(CatalogRecipe left, CatalogRecipe right)
    {
        int comparison = StringComparer.Ordinal.Compare(left.StationPrefab, right.StationPrefab);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.MinimumStationLevel.CompareTo(right.MinimumStationLevel);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Amount.CompareTo(right.Amount);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Enabled.CompareTo(right.Enabled);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Ingredients.Count.CompareTo(right.Ingredients.Count);
        int ingredientCount = Math.Min(left.Ingredients.Count, right.Ingredients.Count);
        for (int index = 0; comparison == 0 && index < ingredientCount; index++)
        {
            CatalogIngredient leftIngredient = left.Ingredients[index];
            CatalogIngredient rightIngredient = right.Ingredients[index];
            comparison = StringComparer.Ordinal.Compare(
                leftIngredient.Prefab,
                rightIngredient.Prefab);
            if (comparison == 0)
            {
                comparison = leftIngredient.Amount.CompareTo(rightIngredient.Amount);
            }

            if (comparison == 0)
            {
                comparison = leftIngredient.AmountPerLevel.CompareTo(
                    rightIngredient.AmountPerLevel);
            }
        }

        return comparison;
    }

    private static int CompareConversions(CatalogConversion left, CatalogConversion right)
    {
        int comparison = StringComparer.Ordinal.Compare(left.Method, right.Method);
        if (comparison == 0)
        {
            comparison = StringComparer.Ordinal.Compare(left.StationPrefab, right.StationPrefab);
        }

        if (comparison == 0)
        {
            comparison = StringComparer.Ordinal.Compare(left.ItemPrefab, right.ItemPrefab);
        }

        return comparison == 0 ? left.Amount.CompareTo(right.Amount) : comparison;
    }

    private static int CompareDrops(CatalogDrop left, CatalogDrop right)
    {
        int comparison = StringComparer.Ordinal.Compare(left.CreatureToken, right.CreatureToken);
        return comparison == 0 ? left.Chance.CompareTo(right.Chance) : comparison;
    }

    private static bool TryLoad(
        string path,
        string gameVersion,
        ModLogger log,
        out ItemCatalog? catalog)
    {
        catalog = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            if (new FileInfo(path).Length > MaximumCacheBytes)
            {
                throw new FormatException("Item catalog cache is too large.");
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            string prefix = BuildVersionPrefix(gameVersion);
            if (!json.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (!json.EndsWith("]}", StringComparison.Ordinal))
            {
                throw new FormatException("Item catalog cache is incomplete.");
            }

            int itemCount = CountOccurrences(json, "{\"token\":");
            int recipeCount = CountOccurrences(json, "{\"enabled\":");
            int conversionEdges = CountOccurrences(json, "{\"method\":");
            int droppedByEdgeCount = CountOccurrences(json, "{\"creature\":");
            if (itemCount == 0 || (conversionEdges & 1) != 0)
            {
                throw new FormatException("Item catalog cache has invalid record counts.");
            }

            ConsoleItem[] consoleItems = ParseConsoleItems(json, itemCount);
            catalog = new ItemCatalog(
                Encoding.UTF8.GetBytes(json),
                itemCount,
                recipeCount,
                conversionEdges / 2,
                droppedByEdgeCount,
                consoleItems);
            return true;
        }
        catch (Exception exception)
        {
            log.Warning(
                $"[LiveMap] item catalog cache could not be loaded " +
                $"({exception.GetType().Name}); rebuilding it.");
            return false;
        }
    }

    private static string BuildVersionPrefix(string gameVersion)
    {
        var prefix = new StringBuilder(96);
        prefix.Append("{\"version\":{\"game\":")
            .Append(JsonWriter.Quote(gameVersion));
        prefix.Append(",\"mod\":")
            .Append(JsonWriter.Quote(ValheimOnePlugin.PluginVersion));
        prefix.Append(",\"schema\":")
            .Append(SchemaVersion.ToString(CultureInfo.InvariantCulture));
        prefix.Append("},\"generatedUtc\":");
        return prefix.ToString();
    }

    private static int CountOccurrences(string value, string marker)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private static ConsoleItem[] BuildConsoleItems(List<CatalogItem> catalogItems)
    {
        var items = new ConsoleItem[catalogItems.Count];
        for (int itemIndex = 0; itemIndex < catalogItems.Count; itemIndex++)
        {
            CatalogItem source = catalogItems[itemIndex];
            var recipes = new ConsoleRecipe[source.Recipes.Count];
            for (int recipeIndex = 0; recipeIndex < source.Recipes.Count; recipeIndex++)
            {
                CatalogRecipe sourceRecipe = source.Recipes[recipeIndex];
                var ingredients =
                    new ConsoleIngredient[sourceRecipe.Ingredients.Count];
                for (int ingredientIndex = 0;
                     ingredientIndex < sourceRecipe.Ingredients.Count;
                     ingredientIndex++)
                {
                    CatalogIngredient sourceIngredient =
                        sourceRecipe.Ingredients[ingredientIndex];
                    ingredients[ingredientIndex] = new ConsoleIngredient
                    {
                        Prefab = sourceIngredient.Prefab,
                        Name = sourceIngredient.Name,
                        Amount = sourceIngredient.Amount,
                        AmountPerLevel = sourceIngredient.AmountPerLevel,
                    };
                }

                recipes[recipeIndex] = new ConsoleRecipe
                {
                    Enabled = sourceRecipe.Enabled,
                    Amount = sourceRecipe.Amount,
                    Station = sourceRecipe.StationPrefab.Length == 0
                        ? null
                        : new ConsoleStation
                        {
                            Prefab = sourceRecipe.StationPrefab,
                            Name = sourceRecipe.StationName,
                        },
                    MinimumStationLevel = sourceRecipe.MinimumStationLevel,
                    Ingredients = ingredients,
                };
            }

            var sources = new ConsoleSource[source.Sources.Count];
            for (int sourceIndex = 0; sourceIndex < source.Sources.Count; sourceIndex++)
            {
                CatalogConversion conversion = source.Sources[sourceIndex];
                sources[sourceIndex] = new ConsoleSource
                {
                    Method = conversion.Method,
                    Station = new ConsoleStation
                    {
                        Prefab = conversion.StationPrefab,
                        Name = conversion.StationName,
                    },
                    Input = new ConsoleItemReference
                    {
                        Prefab = conversion.ItemPrefab,
                        Name = conversion.ItemName,
                    },
                    Amount = conversion.Amount,
                };
            }

            var drops = new ConsoleDrop[source.DroppedBy.Count];
            for (int dropIndex = 0; dropIndex < source.DroppedBy.Count; dropIndex++)
            {
                CatalogDrop drop = source.DroppedBy[dropIndex];
                drops[dropIndex] = new ConsoleDrop
                {
                    Creature = drop.CreatureToken,
                    Name = drop.CreatureName,
                    Chance = drop.Chance,
                };
            }

            items[itemIndex] = new ConsoleItem
            {
                Token = source.Token,
                Name = source.Name,
                Type = source.Type,
                MaxQuality = source.MaxQuality,
                ToolTier = source.ToolTier,
                Weight = source.Weight,
                MaxStackSize = source.MaxStackSize,
                Teleportable = source.Teleportable,
                Armor = source.HasArmor
                    ? new ConsoleArmor
                    {
                        Base = source.Armor,
                        PerLevel = source.ArmorPerLevel,
                    }
                    : null,
                Damage = HasDamage(source.Damage) || HasDamage(source.DamagePerLevel)
                    ? new ConsoleDamageSummary
                    {
                        Base = BuildConsoleDamage(source.Damage),
                        PerLevel = BuildConsoleDamage(source.DamagePerLevel),
                    }
                    : null,
                Recipes = recipes,
                Sources = sources,
                DroppedBy = drops,
            };
        }

        Array.Sort(items, CompareConsoleItems);
        return items;
    }

    private static ConsoleDamage BuildConsoleDamage(HitData.DamageTypes damage)
    {
        return new ConsoleDamage
        {
            Generic = damage.m_damage,
            Blunt = damage.m_blunt,
            Slash = damage.m_slash,
            Pierce = damage.m_pierce,
            Chop = damage.m_chop,
            Pickaxe = damage.m_pickaxe,
            Fire = damage.m_fire,
            Frost = damage.m_frost,
            Lightning = damage.m_lightning,
            Poison = damage.m_poison,
            Spirit = damage.m_spirit,
        };
    }

    private static ConsoleItem[] ParseConsoleItems(string json, int expectedItemCount)
    {
        ConsoleItem[] items = ItemCatalogJsonParser.ParseConsoleItems(json);
        if (items.Length != expectedItemCount)
        {
            throw new FormatException("Item catalog cache has an invalid item count.");
        }

        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < items.Length; index++)
        {
            items[index].Normalize();
            if (items[index].Token.Length == 0 ||
                !seenTokens.Add(items[index].Token))
            {
                throw new FormatException("Item catalog cache contains an invalid item token.");
            }
        }

        Array.Sort(items, CompareConsoleItems);
        return items;
    }

    private static Dictionary<string, ConsoleItem> BuildTokenLookup(ConsoleItem[] items)
    {
        var lookup = new Dictionary<string, ConsoleItem>(
            items.Length,
            StringComparer.Ordinal);
        for (int index = 0; index < items.Length; index++)
        {
            lookup[items[index].Token] = items[index];
        }

        return lookup;
    }

    private static Dictionary<string, List<ConsoleItem>> BuildReverseRecipes(
        ConsoleItem[] items)
    {
        var reverse = new Dictionary<string, List<ConsoleItem>>(StringComparer.Ordinal);
        for (int itemIndex = 0; itemIndex < items.Length; itemIndex++)
        {
            ConsoleItem output = items[itemIndex];
            var seenInputs = new HashSet<string>(StringComparer.Ordinal);
            for (int recipeIndex = 0; recipeIndex < output.Recipes.Length; recipeIndex++)
            {
                ConsoleRecipe recipe = output.Recipes[recipeIndex];
                if (!recipe.Enabled)
                {
                    continue;
                }

                for (int ingredientIndex = 0;
                     ingredientIndex < recipe.Ingredients.Length;
                     ingredientIndex++)
                {
                    string token = recipe.Ingredients[ingredientIndex].Prefab;
                    if (token.Length == 0 || !seenInputs.Add(token))
                    {
                        continue;
                    }

                    if (!reverse.TryGetValue(token, out List<ConsoleItem>? uses))
                    {
                        uses = new List<ConsoleItem>();
                        reverse.Add(token, uses);
                    }

                    uses.Add(output);
                }
            }
        }

        return reverse;
    }

    private static int CompareConsoleItems(ConsoleItem left, ConsoleItem right)
    {
        int comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Token, right.Token);
    }

    private static void TryPersist(string path, string json, ModLogger log)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > MaximumCacheBytes)
            {
                throw new InvalidOperationException("Item catalog exceeded its file cap.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporaryPath = path + ".tmp";
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        catch (Exception exception)
        {
            log.Warning(
                $"[LiveMap] item catalog cache could not be persisted " +
                $"({exception.GetType().Name}: {SingleLineMessage(exception)}).");
        }
    }

    private static string ComputeETag(byte[] content)
    {
        byte[] digest;
        using (SHA256 hash = SHA256.Create())
        {
            digest = hash.ComputeHash(content);
        }

        var value = new StringBuilder(2 + (digest.Length * 2));
        value.Append('"');
        for (int index = 0; index < digest.Length; index++)
        {
            value.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
        }

        value.Append('"');
        return value.ToString();
    }

    private static void LogSummary(
        ModLogger log,
        ItemCatalog catalog,
        long elapsedMilliseconds,
        string source)
    {
        log.Info(
            $"[LiveMap] item catalog: {catalog.ItemCount} items, " +
            $"{catalog.RecipeCount} recipes resolved, " +
            $"{catalog.ConversionCount} conversions, " +
            $"{catalog.DroppedByEdgeCount} droppedBy edges, " +
            $"{elapsedMilliseconds} ms ({source})");
    }

    private static string SingleLineMessage(Exception exception)
    {
        return (exception.Message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    internal sealed class ConsoleItem
    {
        public string Token { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public int MaxQuality { get; set; }

        public int ToolTier { get; set; }

        public float Weight { get; set; }

        public int MaxStackSize { get; set; }

        public bool Teleportable { get; set; }

        public ConsoleArmor? Armor { get; set; }

        public ConsoleDamageSummary? Damage { get; set; }

        public ConsoleRecipe[] Recipes { get; set; } = Array.Empty<ConsoleRecipe>();

        public ConsoleSource[] Sources { get; set; } = Array.Empty<ConsoleSource>();

        public ConsoleDrop[] DroppedBy { get; set; } = Array.Empty<ConsoleDrop>();

        public void Normalize()
        {
            Token = (Token ?? string.Empty).Trim();
            Name = string.IsNullOrWhiteSpace(Name) ? Token : Name.Trim();
            Type = (Type ?? string.Empty).Trim();
            Recipes ??= Array.Empty<ConsoleRecipe>();
            Sources ??= Array.Empty<ConsoleSource>();
            DroppedBy ??= Array.Empty<ConsoleDrop>();
            for (int index = 0; index < Recipes.Length; index++)
            {
                Recipes[index].Normalize();
            }
        }
    }

    internal sealed class ConsoleArmor
    {
        public float Base { get; set; }

        public float PerLevel { get; set; }
    }

    internal sealed class ConsoleDamageSummary
    {
        public ConsoleDamage Base { get; set; } = new ConsoleDamage();

        public ConsoleDamage PerLevel { get; set; } = new ConsoleDamage();
    }

    internal sealed class ConsoleDamage
    {
        public float Generic { get; set; }

        public float Blunt { get; set; }

        public float Slash { get; set; }

        public float Pierce { get; set; }

        public float Chop { get; set; }

        public float Pickaxe { get; set; }

        public float Fire { get; set; }

        public float Frost { get; set; }

        public float Lightning { get; set; }

        public float Poison { get; set; }

        public float Spirit { get; set; }
    }

    internal sealed class ConsoleRecipe
    {
        public bool Enabled { get; set; }

        public int Amount { get; set; }

        public ConsoleStation? Station { get; set; }

        public int MinimumStationLevel { get; set; }

        public ConsoleIngredient[] Ingredients { get; set; } = Array.Empty<ConsoleIngredient>();

        public void Normalize()
        {
            Ingredients ??= Array.Empty<ConsoleIngredient>();
            for (int index = 0; index < Ingredients.Length; index++)
            {
                Ingredients[index].Normalize();
            }
        }
    }

    internal sealed class ConsoleStation
    {
        public string Prefab { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    internal sealed class ConsoleIngredient
    {
        public string Prefab { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int Amount { get; set; }

        public int AmountPerLevel { get; set; }

        public void Normalize()
        {
            Prefab = (Prefab ?? string.Empty).Trim();
            Name = string.IsNullOrWhiteSpace(Name) ? Prefab : Name.Trim();
        }
    }

    internal sealed class ConsoleSource
    {
        public string Method { get; set; } = string.Empty;

        public ConsoleStation? Station { get; set; }

        public ConsoleItemReference? Input { get; set; }

        public int Amount { get; set; }
    }

    internal sealed class ConsoleItemReference
    {
        public string Prefab { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    internal sealed class ConsoleDrop
    {
        public string Creature { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public float Chance { get; set; }
    }

    private sealed class CatalogItem
    {
        public CatalogItem(
            string token,
            string name,
            string description,
            string type,
            int maxQuality,
            int toolTier,
            float weight,
            int maxStackSize,
            bool teleportable,
            bool hasArmor,
            float armor,
            float armorPerLevel,
            HitData.DamageTypes damage,
            HitData.DamageTypes damagePerLevel)
        {
            Token = token;
            Name = name;
            Description = description;
            Type = type;
            MaxQuality = maxQuality;
            ToolTier = toolTier;
            Weight = weight;
            MaxStackSize = maxStackSize;
            Teleportable = teleportable;
            HasArmor = hasArmor;
            Armor = armor;
            ArmorPerLevel = armorPerLevel;
            Damage = damage;
            DamagePerLevel = damagePerLevel;
        }

        public string Token { get; }

        public string Name { get; }

        public string Description { get; }

        public string Type { get; }

        public int MaxQuality { get; }

        public int ToolTier { get; }

        public float Weight { get; }

        public int MaxStackSize { get; }

        public bool Teleportable { get; }

        public bool HasArmor { get; }

        public float Armor { get; }

        public float ArmorPerLevel { get; }

        public HitData.DamageTypes Damage { get; }

        public HitData.DamageTypes DamagePerLevel { get; }

        public List<CatalogRecipe> Recipes { get; } = new List<CatalogRecipe>();

        public List<CatalogConversion> Sources { get; } = new List<CatalogConversion>();

        public List<CatalogConversion> Uses { get; } = new List<CatalogConversion>();

        public List<CatalogDrop> DroppedBy { get; } = new List<CatalogDrop>();
    }

    private sealed class CatalogRecipe
    {
        public CatalogRecipe(
            bool enabled,
            int amount,
            string stationPrefab,
            string stationName,
            int minimumStationLevel,
            List<CatalogIngredient> ingredients)
        {
            Enabled = enabled;
            Amount = amount;
            StationPrefab = stationPrefab;
            StationName = stationName;
            MinimumStationLevel = minimumStationLevel;
            Ingredients = ingredients;
        }

        public bool Enabled { get; }

        public int Amount { get; }

        public string StationPrefab { get; }

        public string StationName { get; }

        public int MinimumStationLevel { get; }

        public List<CatalogIngredient> Ingredients { get; }
    }

    private sealed class CatalogIngredient
    {
        public CatalogIngredient(
            string prefab,
            string name,
            int amount,
            int amountPerLevel)
        {
            Prefab = prefab;
            Name = name;
            Amount = amount;
            AmountPerLevel = amountPerLevel;
        }

        public string Prefab { get; }

        public string Name { get; }

        public int Amount { get; }

        public int AmountPerLevel { get; }
    }

    private sealed class CatalogConversion
    {
        public CatalogConversion(
            string method,
            string stationPrefab,
            string stationName,
            string itemPrefab,
            string itemName,
            int amount)
        {
            Method = method;
            StationPrefab = stationPrefab;
            StationName = stationName;
            ItemPrefab = itemPrefab;
            ItemName = itemName;
            Amount = amount;
        }

        public string Method { get; }

        public string StationPrefab { get; }

        public string StationName { get; }

        public string ItemPrefab { get; }

        public string ItemName { get; }

        public int Amount { get; }
    }

    private sealed class CatalogDrop
    {
        public CatalogDrop(string creatureToken, string creatureName, float chance)
        {
            CreatureToken = creatureToken;
            CreatureName = creatureName;
            Chance = chance;
        }

        public string CreatureToken { get; }

        public string CreatureName { get; }

        public float Chance { get; }
    }

    private readonly struct BuildResult
    {
        public BuildResult(
            string json,
            int itemCount,
            int recipeCount,
            int conversionCount,
            int droppedByEdgeCount,
            ConsoleItem[] consoleItems)
        {
            Json = json;
            ItemCount = itemCount;
            RecipeCount = recipeCount;
            ConversionCount = conversionCount;
            DroppedByEdgeCount = droppedByEdgeCount;
            ConsoleItems = consoleItems;
        }

        public string Json { get; }

        public int ItemCount { get; }

        public int RecipeCount { get; }

        public int ConversionCount { get; }

        public int DroppedByEdgeCount { get; }

        public ConsoleItem[] ConsoleItems { get; }
    }
}

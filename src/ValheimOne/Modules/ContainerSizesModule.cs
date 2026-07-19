using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;

namespace ValheimOne.Modules;

public sealed class ContainerSizesModule : IFeatureModule
{
    private static readonly FieldInfo InventoryWidthField =
        AccessTools.Field(typeof(Inventory), "m_width")
        ?? throw new MissingFieldException(nameof(Inventory), "m_width");
    private static readonly FieldInfo InventoryHeightField =
        AccessTools.Field(typeof(Inventory), "m_height")
        ?? throw new MissingFieldException(nameof(Inventory), "m_height");

    private static ContainerSizesModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ModLogger _log;
    private readonly ContainerSettings _woodChest;
    private readonly ContainerSettings _personalChest;
    private readonly ContainerSettings _reinforcedChest;
    private readonly ContainerSettings _blackmetalChest;
    private readonly ContainerSettings _cart;
    private readonly ContainerSettings _karve;
    private readonly ContainerSettings _longship;
    private readonly List<ContainerBaseline> _baselines = new List<ContainerBaseline>();
    private readonly HashSet<string> _loggedUnmatchedPrefabs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public ContainerSizesModule(FeatureRegistry registry, ModLogger log)
    {
        _feature = registry.Register(Name, Section, Classification);
        _log = log;
        _woodChest = AddSettings("WoodChest");
        _personalChest = AddSettings("PersonalChest");
        _reinforcedChest = AddSettings("ReinforcedChest");
        _blackmetalChest = AddSettings("BlackmetalChest");
        _cart = AddSettings("Cart");
        _karve = AddSettings("Karve");
        _longship = AddSettings("Longship");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Container sizes";

    public string Section => "ContainerSizes";

    public bool IsEnabled => _feature.Enabled.Value;

    // Every viewer must use the same grid dimensions or item slots visually desynchronize.
    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        // Patches stay installed so a synced server overlay can hot-enable the module.
        _active = this;

        var awake = AccessTools.Method(typeof(Container), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Container), "Awake");
        harmony.Patch(
            awake,
            prefix: new HarmonyMethod(typeof(ContainerSizesModule), nameof(ContainerAwakePrefix)),
            postfix: new HarmonyMethod(typeof(ContainerSizesModule), nameof(ContainerAwakePostfix)));
    }

    private static void ContainerAwakePrefix(Container __instance)
    {
        ContainerSizesModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyBeforeAwake(__instance);
    }

    private static void ContainerAwakePostfix(Container __instance)
    {
        ContainerSizesModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        ContainerBaseline? baseline = active.FindBaseline(__instance);
        if (baseline != null &&
            active.TryGetSettings(__instance, out ContainerSettings? settings) &&
            settings != null)
        {
            active.ApplyAfterLoad(__instance, baseline, settings);
        }
    }

    private ContainerSettings AddSettings(string keyPrefix)
    {
        return new ContainerSettings(
            _feature.Int(
                keyPrefix + "Columns",
                0,
                "Absolute container columns. Zero leaves the vanilla value; nonzero values use their absolute value and clamp to 1-8."),
            _feature.Int(
                keyPrefix + "Rows",
                0,
                "Absolute container rows. Zero leaves the vanilla value; nonzero values use their absolute value and clamp to 1-24."));
    }

    private void ApplyBeforeAwake(Container container)
    {
        if (!TryGetSettings(container, out ContainerSettings? settings) || settings == null)
        {
            return;
        }

        ContainerBaseline baseline = GetOrAddBaseline(container);
        int requestedWidth = ResolveDimension(settings.Columns.Value, baseline.Width, 8);
        int requestedHeight = ResolveDimension(settings.Rows.Value, baseline.Height, 24);

        // Never construct a grid smaller than vanilla: saved items load into a grid known to
        // contain all vanilla slots. The postfix may shrink it only after proving it is empty.
        container.m_width = Math.Max(requestedWidth, baseline.Width);
        container.m_height = Math.Max(requestedHeight, baseline.Height);
    }

    private void ApplyAfterLoad(
        Container container,
        ContainerBaseline baseline,
        ContainerSettings settings)
    {
        int requestedWidth = ResolveDimension(settings.Columns.Value, baseline.Width, 8);
        int requestedHeight = ResolveDimension(settings.Rows.Value, baseline.Height, 24);
        ApplyLoadedDimensions(container, baseline, requestedWidth, requestedHeight);
    }

    private void ApplyLoadedDimensions(
        Container container,
        ContainerBaseline baseline,
        int requestedWidth,
        int requestedHeight)
    {
        Inventory inventory = container.GetInventory();
        if (inventory == null)
        {
            return;
        }

        int itemCount = 0;
        int occupiedWidth = 0;
        int occupiedHeight = 0;
        foreach (var item in inventory.GetAllItems())
        {
            itemCount++;
            occupiedWidth = Math.Max(occupiedWidth, item.m_gridPos.x + 1);
            occupiedHeight = Math.Max(occupiedHeight, item.m_gridPos.y + 1);
        }

        int effectiveWidth = requestedWidth;
        int effectiveHeight = requestedHeight;
        if (itemCount != 0)
        {
            // A non-empty container never shrinks below its vanilla grid. Item bounds are also
            // retained for saves made while a previously larger configuration was active.
            effectiveWidth = Math.Max(effectiveWidth, baseline.Width);
            effectiveHeight = Math.Max(effectiveHeight, baseline.Height);
        }

        effectiveWidth = Math.Max(effectiveWidth, occupiedWidth);
        effectiveHeight = Math.Max(effectiveHeight, occupiedHeight);

        container.m_width = effectiveWidth;
        container.m_height = effectiveHeight;
        InventoryWidthField.SetValue(inventory, effectiveWidth);
        InventoryHeightField.SetValue(inventory, effectiveHeight);

        if (effectiveWidth != requestedWidth || effectiveHeight != requestedHeight)
        {
            string prefabName = Utils.GetPrefabName(container.gameObject.name);
            string signature =
                requestedWidth + "x" + requestedHeight + ":" + effectiveWidth + "x" + effectiveHeight;
            if (!string.Equals(baseline.LastWarningSignature, signature, StringComparison.Ordinal))
            {
                _log.Warning(
                    $"ContainerSizes kept '{prefabName}' ({container.m_name}) at " +
                    $"{effectiveWidth}x{effectiveHeight} instead of requested " +
                    $"{requestedWidth}x{requestedHeight} because it contains {itemCount} item(s); " +
                    "the grid was not shrunk so no occupied slot can be lost.");
                baseline.LastWarningSignature = signature;
            }
        }
        else
        {
            baseline.LastWarningSignature = null;
        }
    }

    private void OnEffectiveValuesChanged()
    {
#pragma warning disable CS0618
        Container[] containers = UnityEngine.Object.FindObjectsOfType<Container>();
#pragma warning restore CS0618
        foreach (Container container in containers)
        {
            ContainerBaseline? baseline = FindBaseline(container);
            if (IsEnabled)
            {
                if (TryGetSettings(container, out ContainerSettings? settings) && settings != null)
                {
                    baseline ??= AddBaseline(container);
                    ApplyAfterLoad(container, baseline, settings);
                }
            }
            else if (baseline != null)
            {
                // Disabling/restoring uses the same occupied-slot guard as a config reapply.
                ApplyLoadedDimensions(container, baseline, baseline.Width, baseline.Height);
            }
        }
    }

    private bool TryGetSettings(Container container, out ContainerSettings? settings)
    {
        string prefabName = Utils.GetPrefabName(container.gameObject.name);
        switch (prefabName.ToLowerInvariant())
        {
            case "piece_chest_wood":
                settings = _woodChest;
                return true;
            case "piece_chest_private":
                settings = _personalChest;
                return true;
            case "piece_chest":
                settings = _reinforcedChest;
                return true;
            case "piece_chest_blackmetal":
                settings = _blackmetalChest;
                return true;
            case "cart":
                settings = _cart;
                return true;
            case "karve":
                settings = _karve;
                return true;
            case "vikingship":
                settings = _longship;
                return true;
            default:
                settings = null;
                LogUnmatchedPrefab(prefabName);
                return false;
        }
    }

    private void LogUnmatchedPrefab(string prefabName)
    {
        string displayName = string.IsNullOrEmpty(prefabName) ? "<empty>" : prefabName;
        if (_loggedUnmatchedPrefabs.Add(displayName))
        {
            _log.Debug(
                $"ContainerSizes saw unmatched Container prefab '{displayName}'; " +
                "its grid dimensions were left unchanged.");
        }
    }

    private ContainerBaseline GetOrAddBaseline(Container container)
    {
        return FindBaseline(container) ?? AddBaseline(container);
    }

    private ContainerBaseline? FindBaseline(Container container)
    {
        for (int index = _baselines.Count - 1; index >= 0; index--)
        {
            ContainerBaseline baseline = _baselines[index];
            if (!baseline.Container.TryGetTarget(out Container? existing) || existing == null)
            {
                _baselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, container))
            {
                return baseline;
            }
        }

        return null;
    }

    private ContainerBaseline AddBaseline(Container container)
    {
        var baseline = new ContainerBaseline(container);
        _baselines.Add(baseline);
        return baseline;
    }

    private static int ResolveDimension(int configured, int vanilla, int maximum)
    {
        if (configured == 0)
        {
            return vanilla;
        }

        long absolute = configured < 0 ? -(long)configured : configured;
        if (absolute < 1)
        {
            return 1;
        }

        return absolute > maximum ? maximum : (int)absolute;
    }

    private sealed class ContainerSettings
    {
        public ContainerSettings(ConfigEntryInt columns, ConfigEntryInt rows)
        {
            Columns = columns;
            Rows = rows;
        }

        public ConfigEntryInt Columns { get; }

        public ConfigEntryInt Rows { get; }
    }

    private sealed class ContainerBaseline
    {
        public ContainerBaseline(Container container)
        {
            Container = new WeakReference<Container>(container);
            Width = container.m_width;
            Height = container.m_height;
        }

        public WeakReference<Container> Container { get; }

        public int Width { get; }

        public int Height { get; }

        public string? LastWarningSignature { get; set; }
    }
}

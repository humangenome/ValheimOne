using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;

namespace ValheimOne.Modules;

public sealed class ProductionSpeedsModule : IFeatureModule
{
    private static ProductionSpeedsModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ModLogger _log;
    private readonly StationSettings _smelter;
    private readonly StationSettings _furnace;
    private readonly StationSettings _kiln;
    private readonly StationSettings _windmill;
    private readonly StationSettings _spinningWheel;
    private readonly StationSettings _eitrRefinery;
    private readonly List<SmelterBaseline> _baselines = new List<SmelterBaseline>();
    private readonly HashSet<string> _loggedUnmatchedPrefabs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public ProductionSpeedsModule(FeatureRegistry registry, ModLogger log)
    {
        _feature = registry.Register(Name, Section, Classification);
        _log = log;

        _smelter = new StationSettings(
            _feature.Int(
                "SmelterProductionSeconds",
                0,
                "Absolute seconds required to produce one smelter product. Zero disables this override."),
            _feature.Int(
                "SmelterMaxQueue",
                0,
                "Absolute maximum ore queued in a smelter. Zero disables this override."),
            _feature.Int(
                "SmelterMaxFuel",
                0,
                "Absolute maximum fuel stored by a smelter. Zero disables this override."),
            _feature.Int(
                "SmelterFuelPerProduct",
                0,
                "Absolute fuel consumed per smelter product. Zero disables this override."));
        _furnace = new StationSettings(
            _feature.Int(
                "FurnaceProductionSeconds",
                0,
                "Absolute seconds required to produce one blast-furnace product. Zero disables this override."),
            _feature.Int(
                "FurnaceMaxQueue",
                0,
                "Absolute maximum ore queued in a blast furnace. Zero disables this override."),
            _feature.Int(
                "FurnaceMaxFuel",
                0,
                "Absolute maximum fuel stored by a blast furnace. Zero disables this override."),
            _feature.Int(
                "FurnaceFuelPerProduct",
                0,
                "Absolute fuel consumed per blast-furnace product. Zero disables this override."));
        _kiln = new StationSettings(
            _feature.Int(
                "KilnProductionSeconds",
                0,
                "Absolute seconds required to turn wood into coal. Zero disables this override."),
            _feature.Int(
                "KilnMaxQueue",
                0,
                "Absolute maximum wood queued in a charcoal kiln. Zero disables this override."));
        _windmill = new StationSettings(
            _feature.Int(
                "WindmillProductionSeconds",
                0,
                "Absolute seconds required to turn barley into flour. Zero disables this override."),
            _feature.Int(
                "WindmillMaxQueue",
                0,
                "Absolute maximum barley queued in a windmill. Zero disables this override."));
        _spinningWheel = new StationSettings(
            _feature.Int(
                "SpinningWheelProductionSeconds",
                0,
                "Absolute seconds required to turn flax into linen thread. Zero disables this override."),
            _feature.Int(
                "SpinningWheelMaxQueue",
                0,
                "Absolute maximum flax queued in a spinning wheel. Zero disables this override."));
        _eitrRefinery = new StationSettings(
            _feature.Int(
                "EitrRefineryProductionSeconds",
                0,
                "Absolute seconds required to produce one refined eitr. Zero disables this override."),
            _feature.Int(
                "EitrRefineryMaxQueue",
                0,
                "Absolute maximum sap queued in an eitr refinery. Zero disables this override."));

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Production speeds";

    public string Section => "ProductionSpeeds";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        // Smelter-family simulation runs on the zone owner, so effective settings are synced.
        // The patch remains installed so a server overlay can hot-enable this module.
        _active = this;

        var awake = AccessTools.Method(typeof(Smelter), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Smelter), "Awake");
        harmony.Patch(
            awake,
            postfix: new HarmonyMethod(
                typeof(ProductionSpeedsModule),
                nameof(SmelterAwakePostfix)));
    }

    private static void SmelterAwakePostfix(Smelter __instance)
    {
        ProductionSpeedsModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToSmelter(__instance, active.GetOrAddBaseline(__instance));
    }

    private void OnEffectiveValuesChanged()
    {
#pragma warning disable CS0618
        Smelter[] smelters = UnityEngine.Object.FindObjectsOfType<Smelter>();
#pragma warning restore CS0618
        foreach (Smelter smelter in smelters)
        {
            SmelterBaseline? baseline = FindBaseline(smelter);
            if (IsEnabled)
            {
                ApplyToSmelter(smelter, baseline ?? AddBaseline(smelter));
            }
            else
            {
                baseline?.Restore(smelter);
            }
        }
    }

    private SmelterBaseline GetOrAddBaseline(Smelter smelter)
    {
        return FindBaseline(smelter) ?? AddBaseline(smelter);
    }

    private SmelterBaseline? FindBaseline(Smelter smelter)
    {
        for (int index = _baselines.Count - 1; index >= 0; index--)
        {
            SmelterBaseline baseline = _baselines[index];
            if (!baseline.Smelter.TryGetTarget(out Smelter? existing) || existing == null)
            {
                _baselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, smelter))
            {
                return baseline;
            }
        }

        return null;
    }

    private SmelterBaseline AddBaseline(Smelter smelter)
    {
        var baseline = new SmelterBaseline(smelter);
        _baselines.Add(baseline);
        return baseline;
    }

    private void ApplyToSmelter(Smelter smelter, SmelterBaseline baseline)
    {
        string prefabName = Utils.GetPrefabName(smelter.gameObject.name);
        if (!TryGetStationSettings(prefabName, out StationSettings? settings) || settings == null)
        {
            LogUnmatchedPrefab(prefabName);
            return;
        }

        int productionSeconds = settings.ProductionSeconds.Value;
        smelter.m_secPerProduct = productionSeconds > 0
            ? productionSeconds
            : baseline.SecondsPerProduct;

        int maxQueue = settings.MaxQueue.Value;
        smelter.m_maxOre = maxQueue > 0 ? maxQueue : baseline.MaxOre;

        if (settings.MaxFuel != null && settings.FuelPerProduct != null)
        {
            int maxFuel = settings.MaxFuel.Value;
            smelter.m_maxFuel = maxFuel > 0 ? maxFuel : baseline.MaxFuel;

            int fuelPerProduct = settings.FuelPerProduct.Value;
            smelter.m_fuelPerProduct = fuelPerProduct > 0
                ? fuelPerProduct
                : baseline.FuelPerProduct;
        }
    }

    private bool TryGetStationSettings(string prefabName, out StationSettings? settings)
    {
        if (string.Equals(prefabName, "smelter", StringComparison.OrdinalIgnoreCase))
        {
            settings = _smelter;
            return true;
        }

        if (string.Equals(prefabName, "blastfurnace", StringComparison.OrdinalIgnoreCase))
        {
            settings = _furnace;
            return true;
        }

        if (string.Equals(prefabName, "charcoal_kiln", StringComparison.OrdinalIgnoreCase))
        {
            settings = _kiln;
            return true;
        }

        if (string.Equals(prefabName, "windmill", StringComparison.OrdinalIgnoreCase))
        {
            settings = _windmill;
            return true;
        }

        if (string.Equals(prefabName, "piece_spinningwheel", StringComparison.OrdinalIgnoreCase))
        {
            settings = _spinningWheel;
            return true;
        }

        if (string.Equals(prefabName, "eitrrefinery", StringComparison.OrdinalIgnoreCase))
        {
            settings = _eitrRefinery;
            return true;
        }

        settings = null;
        return false;
    }

    private void LogUnmatchedPrefab(string prefabName)
    {
        string displayName = string.IsNullOrEmpty(prefabName) ? "<empty>" : prefabName;
        if (_loggedUnmatchedPrefabs.Add(displayName))
        {
            _log.Warning(
                $"ProductionSpeeds saw unmatched Smelter prefab '{displayName}'; " +
                "its production settings were left unchanged.");
        }
    }

    private sealed class StationSettings
    {
        public StationSettings(
            ConfigEntryInt productionSeconds,
            ConfigEntryInt maxQueue,
            ConfigEntryInt? maxFuel = null,
            ConfigEntryInt? fuelPerProduct = null)
        {
            ProductionSeconds = productionSeconds;
            MaxQueue = maxQueue;
            MaxFuel = maxFuel;
            FuelPerProduct = fuelPerProduct;
        }

        public ConfigEntryInt ProductionSeconds { get; }

        public ConfigEntryInt MaxQueue { get; }

        public ConfigEntryInt? MaxFuel { get; }

        public ConfigEntryInt? FuelPerProduct { get; }
    }

    private sealed class SmelterBaseline
    {
        public SmelterBaseline(Smelter smelter)
        {
            Smelter = new WeakReference<Smelter>(smelter);
            SecondsPerProduct = smelter.m_secPerProduct;
            MaxOre = smelter.m_maxOre;
            MaxFuel = smelter.m_maxFuel;
            FuelPerProduct = smelter.m_fuelPerProduct;
        }

        public WeakReference<Smelter> Smelter { get; }

        public float SecondsPerProduct { get; }

        public int MaxOre { get; }

        public int MaxFuel { get; }

        public int FuelPerProduct { get; }

        public void Restore(Smelter smelter)
        {
            smelter.m_secPerProduct = SecondsPerProduct;
            smelter.m_maxOre = MaxOre;
            smelter.m_maxFuel = MaxFuel;
            smelter.m_fuelPerProduct = FuelPerProduct;
        }
    }
}

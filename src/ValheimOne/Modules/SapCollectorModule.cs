using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class SapCollectorModule : IFeatureModule
{
    private static SapCollectorModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryInt _productionSeconds;
    private readonly ConfigEntryInt _maxSap;
    private readonly List<SapCollectorBaseline> _baselines =
        new List<SapCollectorBaseline>();

    public SapCollectorModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _productionSeconds = _feature.Int(
            "ProductionSeconds",
            0,
            "Absolute seconds required to produce one sap. Zero disables this override; Valheim's default is 60.");
        _maxSap = _feature.Int(
            "MaxSap",
            0,
            "Absolute maximum stored sap. Zero disables this override; Valheim's default is 10.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Sap collector";

    public string Section => "SapCollector";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var awake = AccessTools.Method(typeof(SapCollector), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(SapCollector), "Awake");
        harmony.Patch(
            awake,
            postfix: new HarmonyMethod(
                typeof(SapCollectorModule),
                nameof(SapCollectorAwakePostfix)));
    }

    private static void SapCollectorAwakePostfix(SapCollector __instance)
    {
        SapCollectorModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToSapCollector(__instance, active.GetOrAddBaseline(__instance));
    }

    private void OnEffectiveValuesChanged()
    {
#pragma warning disable CS0618
        SapCollector[] collectors = UnityEngine.Object.FindObjectsOfType<SapCollector>();
#pragma warning restore CS0618
        foreach (SapCollector collector in collectors)
        {
            SapCollectorBaseline? baseline = FindBaseline(collector);
            if (IsEnabled)
            {
                ApplyToSapCollector(collector, baseline ?? AddBaseline(collector));
            }
            else
            {
                baseline?.Restore(collector);
            }
        }
    }

    private SapCollectorBaseline GetOrAddBaseline(SapCollector collector)
    {
        return FindBaseline(collector) ?? AddBaseline(collector);
    }

    private SapCollectorBaseline? FindBaseline(SapCollector collector)
    {
        for (int index = _baselines.Count - 1; index >= 0; index--)
        {
            SapCollectorBaseline baseline = _baselines[index];
            if (!baseline.Collector.TryGetTarget(out SapCollector? existing) || existing == null)
            {
                _baselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, collector))
            {
                return baseline;
            }
        }

        return null;
    }

    private SapCollectorBaseline AddBaseline(SapCollector collector)
    {
        var baseline = new SapCollectorBaseline(collector);
        _baselines.Add(baseline);
        return baseline;
    }

    private void ApplyToSapCollector(SapCollector collector, SapCollectorBaseline baseline)
    {
        int productionSeconds = _productionSeconds.Value;
        collector.m_secPerUnit = productionSeconds > 0
            ? productionSeconds
            : baseline.SecondsPerUnit;

        int maxSap = _maxSap.Value;
        collector.m_maxLevel = maxSap > 0 ? maxSap : baseline.MaxLevel;
    }

    private sealed class SapCollectorBaseline
    {
        public SapCollectorBaseline(SapCollector collector)
        {
            Collector = new WeakReference<SapCollector>(collector);
            SecondsPerUnit = collector.m_secPerUnit;
            MaxLevel = collector.m_maxLevel;
        }

        public WeakReference<SapCollector> Collector { get; }

        public float SecondsPerUnit { get; }

        public int MaxLevel { get; }

        public void Restore(SapCollector collector)
        {
            collector.m_secPerUnit = SecondsPerUnit;
            collector.m_maxLevel = MaxLevel;
        }
    }
}

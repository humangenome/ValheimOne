using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class BeehiveModule : IFeatureModule
{
    private static BeehiveModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryInt _productionSeconds;
    private readonly ConfigEntryInt _maxHoney;
    private readonly List<BeehiveBaseline> _baselines = new List<BeehiveBaseline>();

    public BeehiveModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _productionSeconds = _feature.Int(
            "ProductionSeconds",
            0,
            "Absolute seconds required to produce one honey. Zero disables this override; Valheim's default is 1200.");
        _maxHoney = _feature.Int(
            "MaxHoney",
            0,
            "Absolute maximum stored honey. Zero disables this override; Valheim's default is 4.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Beehive";

    public string Section => "Beehive";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var awake = AccessTools.Method(typeof(Beehive), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Beehive), "Awake");
        harmony.Patch(
            awake,
            postfix: new HarmonyMethod(typeof(BeehiveModule), nameof(BeehiveAwakePostfix)));
    }

    private static void BeehiveAwakePostfix(Beehive __instance)
    {
        BeehiveModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToBeehive(__instance, active.GetOrAddBaseline(__instance));
    }

    private void OnEffectiveValuesChanged()
    {
#pragma warning disable CS0618
        Beehive[] beehives = UnityEngine.Object.FindObjectsOfType<Beehive>();
#pragma warning restore CS0618
        foreach (Beehive beehive in beehives)
        {
            BeehiveBaseline? baseline = FindBaseline(beehive);
            if (IsEnabled)
            {
                ApplyToBeehive(beehive, baseline ?? AddBaseline(beehive));
            }
            else
            {
                baseline?.Restore(beehive);
            }
        }
    }

    private BeehiveBaseline GetOrAddBaseline(Beehive beehive)
    {
        return FindBaseline(beehive) ?? AddBaseline(beehive);
    }

    private BeehiveBaseline? FindBaseline(Beehive beehive)
    {
        for (int index = _baselines.Count - 1; index >= 0; index--)
        {
            BeehiveBaseline baseline = _baselines[index];
            if (!baseline.Beehive.TryGetTarget(out Beehive? existing) || existing == null)
            {
                _baselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, beehive))
            {
                return baseline;
            }
        }

        return null;
    }

    private BeehiveBaseline AddBaseline(Beehive beehive)
    {
        var baseline = new BeehiveBaseline(beehive);
        _baselines.Add(baseline);
        return baseline;
    }

    private void ApplyToBeehive(Beehive beehive, BeehiveBaseline baseline)
    {
        int productionSeconds = _productionSeconds.Value;
        beehive.m_secPerUnit = productionSeconds > 0
            ? productionSeconds
            : baseline.SecondsPerUnit;

        int maxHoney = _maxHoney.Value;
        beehive.m_maxHoney = maxHoney > 0 ? maxHoney : baseline.MaxHoney;
    }

    private sealed class BeehiveBaseline
    {
        public BeehiveBaseline(Beehive beehive)
        {
            Beehive = new WeakReference<Beehive>(beehive);
            SecondsPerUnit = beehive.m_secPerUnit;
            MaxHoney = beehive.m_maxHoney;
        }

        public WeakReference<Beehive> Beehive { get; }

        public float SecondsPerUnit { get; }

        public int MaxHoney { get; }

        public void Restore(Beehive beehive)
        {
            beehive.m_secPerUnit = SecondsPerUnit;
            beehive.m_maxHoney = MaxHoney;
        }
    }
}

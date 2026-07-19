using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class FermenterModule : IFeatureModule
{
    private static FermenterModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryInt _fermentSeconds;
    private readonly List<FermenterBaseline> _baselines = new List<FermenterBaseline>();

    public FermenterModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _fermentSeconds = _feature.Int(
            "FermentSeconds",
            0,
            "Absolute fermentation duration in seconds. Zero disables this override; Valheim's default is 2400.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Fermenter";

    public string Section => "Fermenter";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var awake = AccessTools.Method(typeof(Fermenter), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Fermenter), "Awake");
        harmony.Patch(
            awake,
            postfix: new HarmonyMethod(typeof(FermenterModule), nameof(FermenterAwakePostfix)));
    }

    private static void FermenterAwakePostfix(Fermenter __instance)
    {
        FermenterModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToFermenter(__instance, active.GetOrAddBaseline(__instance));
    }

    private void OnEffectiveValuesChanged()
    {
#pragma warning disable CS0618
        Fermenter[] fermenters = UnityEngine.Object.FindObjectsOfType<Fermenter>();
#pragma warning restore CS0618
        foreach (Fermenter fermenter in fermenters)
        {
            FermenterBaseline? baseline = FindBaseline(fermenter);
            if (IsEnabled)
            {
                ApplyToFermenter(fermenter, baseline ?? AddBaseline(fermenter));
            }
            else
            {
                baseline?.Restore(fermenter);
            }
        }
    }

    private FermenterBaseline GetOrAddBaseline(Fermenter fermenter)
    {
        return FindBaseline(fermenter) ?? AddBaseline(fermenter);
    }

    private FermenterBaseline? FindBaseline(Fermenter fermenter)
    {
        for (int index = _baselines.Count - 1; index >= 0; index--)
        {
            FermenterBaseline baseline = _baselines[index];
            if (!baseline.Fermenter.TryGetTarget(out Fermenter? existing) || existing == null)
            {
                _baselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, fermenter))
            {
                return baseline;
            }
        }

        return null;
    }

    private FermenterBaseline AddBaseline(Fermenter fermenter)
    {
        var baseline = new FermenterBaseline(fermenter);
        _baselines.Add(baseline);
        return baseline;
    }

    private void ApplyToFermenter(Fermenter fermenter, FermenterBaseline baseline)
    {
        int fermentSeconds = _fermentSeconds.Value;
        fermenter.m_fermentationDuration = fermentSeconds > 0
            ? fermentSeconds
            : baseline.FermentationDuration;
    }

    private sealed class FermenterBaseline
    {
        public FermenterBaseline(Fermenter fermenter)
        {
            Fermenter = new WeakReference<Fermenter>(fermenter);
            FermentationDuration = fermenter.m_fermentationDuration;
        }

        public WeakReference<Fermenter> Fermenter { get; }

        public float FermentationDuration { get; }

        public void Restore(Fermenter fermenter)
        {
            fermenter.m_fermentationDuration = FermentationDuration;
        }
    }
}

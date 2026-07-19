using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class WardsModule : IFeatureModule
{
    private static WardsModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryFloat _radius;
    private readonly List<WardBaseline> _baselines = new List<WardBaseline>();

    public WardsModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _radius = _feature.Float(
            "Radius",
            0f,
            "Absolute ward protection radius. Zero disables this override; Valheim's default is 32.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Wards";

    public string Section => "Wards";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var awake = AccessTools.Method(typeof(PrivateArea), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(PrivateArea), "Awake");
        harmony.Patch(
            awake,
            postfix: new HarmonyMethod(typeof(WardsModule), nameof(PrivateAreaAwakePostfix)));
    }

    private static void PrivateAreaAwakePostfix(PrivateArea __instance)
    {
        WardsModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToWard(__instance, active.GetOrAddBaseline(__instance));
    }

    private void OnEffectiveValuesChanged()
    {
#pragma warning disable CS0618
        PrivateArea[] wards = UnityEngine.Object.FindObjectsOfType<PrivateArea>();
#pragma warning restore CS0618
        foreach (PrivateArea ward in wards)
        {
            WardBaseline? baseline = FindBaseline(ward);
            if (IsEnabled)
            {
                ApplyToWard(ward, baseline ?? AddBaseline(ward));
            }
            else
            {
                baseline?.Restore(ward);
            }
        }
    }

    private WardBaseline GetOrAddBaseline(PrivateArea ward)
    {
        return FindBaseline(ward) ?? AddBaseline(ward);
    }

    private WardBaseline? FindBaseline(PrivateArea ward)
    {
        for (int index = _baselines.Count - 1; index >= 0; index--)
        {
            WardBaseline baseline = _baselines[index];
            if (!baseline.Ward.TryGetTarget(out PrivateArea? existing) || existing == null)
            {
                _baselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, ward))
            {
                return baseline;
            }
        }

        return null;
    }

    private WardBaseline AddBaseline(PrivateArea ward)
    {
        var baseline = new WardBaseline(ward);
        _baselines.Add(baseline);
        return baseline;
    }

    private void ApplyToWard(PrivateArea ward, WardBaseline baseline)
    {
        float radius = _radius.Value;
        ward.m_radius = radius > 0f ? radius : baseline.Radius;
    }

    private sealed class WardBaseline
    {
        public WardBaseline(PrivateArea ward)
        {
            Ward = new WeakReference<PrivateArea>(ward);
            Radius = ward.m_radius;
        }

        public WeakReference<PrivateArea> Ward { get; }

        public float Radius { get; }

        public void Restore(PrivateArea ward)
        {
            ward.m_radius = Radius;
        }
    }
}

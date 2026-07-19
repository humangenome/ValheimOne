using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class TamesModule : IFeatureModule
{
    private static TamesModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryPercent _tamingTime;
    private readonly ConfigEntryPercent _fedDuration;
    private readonly ConfigEntryPercent _growthTime;
    private readonly ConfigEntryPercent _requiredLovePoints;
    private readonly ConfigEntryPercent _pregnancyDuration;
    private readonly ConfigEntryPercent _pregnancyChance;
    private readonly ConfigEntryPercent _partnerCheckRange;
    private readonly ConfigEntryPercent _creatureLimit;
    private readonly List<TameableBaseline> _tameableBaselines =
        new List<TameableBaseline>();
    private readonly List<ProcreationBaseline> _procreationBaselines =
        new List<ProcreationBaseline>();
    private readonly List<GrowupBaseline> _growupBaselines =
        new List<GrowupBaseline>();

    public TamesModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _tamingTime = _feature.Percent(
            "TamingTimeMultiplier",
            0f,
            "Time required to tame a creature.");
        _fedDuration = _feature.Percent(
            "FedDurationMultiplier",
            0f,
            "Duration for which a tameable creature remains fed.");
        _growthTime = _feature.Percent(
            "GrowthTimeMultiplier",
            0f,
            "Time required for a juvenile creature to grow up.");
        _requiredLovePoints = _feature.Percent(
            "RequiredLovePointsMultiplier",
            0f,
            "Love points required for procreation. The adjusted value is rounded and clamped to at least one.");
        _pregnancyDuration = _feature.Percent(
            "PregnancyDurationMultiplier",
            0f,
            "Pregnancy duration for breeding creatures.");
        _pregnancyChance = _feature.Percent(
            "PregnancyChanceMultiplier",
            0f,
            "Chance for an eligible creature to become pregnant.");
        _partnerCheckRange = _feature.Percent(
            "PartnerCheckRangeMultiplier",
            0f,
            "Range within which a creature searches for a breeding partner.");
        _creatureLimit = _feature.Percent(
            "CreatureLimitMultiplier",
            0f,
            "Maximum nearby creatures permitted for procreation. The adjusted value is rounded and clamped to at least one.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Tames";

    public string Section => "Tames";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var tameableAwake = AccessTools.Method(typeof(Tameable), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Tameable), "Awake");
        harmony.Patch(
            tameableAwake,
            postfix: new HarmonyMethod(typeof(TamesModule), nameof(TameableAwakePostfix)));

        var procreationAwake = AccessTools.Method(typeof(Procreation), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Procreation), "Awake");
        harmony.Patch(
            procreationAwake,
            postfix: new HarmonyMethod(typeof(TamesModule), nameof(ProcreationAwakePostfix)));

        var growupStart = AccessTools.Method(typeof(Growup), "Start", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Growup), "Start");
        harmony.Patch(
            growupStart,
            postfix: new HarmonyMethod(typeof(TamesModule), nameof(GrowupStartPostfix)));
    }

    private static void TameableAwakePostfix(Tameable __instance)
    {
        TamesModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToTameable(__instance, active.GetOrAddBaseline(__instance));
    }

    private static void ProcreationAwakePostfix(Procreation __instance)
    {
        TamesModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToProcreation(__instance, active.GetOrAddBaseline(__instance));
    }

    private static void GrowupStartPostfix(Growup __instance)
    {
        TamesModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToGrowup(__instance, active.GetOrAddBaseline(__instance));
    }

    private void OnEffectiveValuesChanged()
    {
#pragma warning disable CS0618
        Tameable[] tameables = UnityEngine.Object.FindObjectsOfType<Tameable>();
        Procreation[] procreations = UnityEngine.Object.FindObjectsOfType<Procreation>();
        Growup[] growups = UnityEngine.Object.FindObjectsOfType<Growup>();
#pragma warning restore CS0618

        foreach (Tameable tameable in tameables)
        {
            TameableBaseline? baseline = FindBaseline(tameable);
            if (IsEnabled)
            {
                ApplyToTameable(tameable, baseline ?? AddBaseline(tameable));
            }
            else
            {
                baseline?.Restore(tameable);
            }
        }

        foreach (Procreation procreation in procreations)
        {
            ProcreationBaseline? baseline = FindBaseline(procreation);
            if (IsEnabled)
            {
                ApplyToProcreation(procreation, baseline ?? AddBaseline(procreation));
            }
            else
            {
                baseline?.Restore(procreation);
            }
        }

        foreach (Growup growup in growups)
        {
            GrowupBaseline? baseline = FindBaseline(growup);
            if (IsEnabled)
            {
                ApplyToGrowup(growup, baseline ?? AddBaseline(growup));
            }
            else
            {
                baseline?.Restore(growup);
            }
        }
    }

    private TameableBaseline GetOrAddBaseline(Tameable tameable)
    {
        return FindBaseline(tameable) ?? AddBaseline(tameable);
    }

    private TameableBaseline? FindBaseline(Tameable tameable)
    {
        for (int index = _tameableBaselines.Count - 1; index >= 0; index--)
        {
            TameableBaseline baseline = _tameableBaselines[index];
            if (!baseline.Tameable.TryGetTarget(out Tameable? existing) || existing == null)
            {
                _tameableBaselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, tameable))
            {
                return baseline;
            }
        }

        return null;
    }

    private TameableBaseline AddBaseline(Tameable tameable)
    {
        var baseline = new TameableBaseline(tameable);
        _tameableBaselines.Add(baseline);
        return baseline;
    }

    private ProcreationBaseline GetOrAddBaseline(Procreation procreation)
    {
        return FindBaseline(procreation) ?? AddBaseline(procreation);
    }

    private ProcreationBaseline? FindBaseline(Procreation procreation)
    {
        for (int index = _procreationBaselines.Count - 1; index >= 0; index--)
        {
            ProcreationBaseline baseline = _procreationBaselines[index];
            if (!baseline.Procreation.TryGetTarget(out Procreation? existing) || existing == null)
            {
                _procreationBaselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, procreation))
            {
                return baseline;
            }
        }

        return null;
    }

    private ProcreationBaseline AddBaseline(Procreation procreation)
    {
        var baseline = new ProcreationBaseline(procreation);
        _procreationBaselines.Add(baseline);
        return baseline;
    }

    private GrowupBaseline GetOrAddBaseline(Growup growup)
    {
        return FindBaseline(growup) ?? AddBaseline(growup);
    }

    private GrowupBaseline? FindBaseline(Growup growup)
    {
        for (int index = _growupBaselines.Count - 1; index >= 0; index--)
        {
            GrowupBaseline baseline = _growupBaselines[index];
            if (!baseline.Growup.TryGetTarget(out Growup? existing) || existing == null)
            {
                _growupBaselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, growup))
            {
                return baseline;
            }
        }

        return null;
    }

    private GrowupBaseline AddBaseline(Growup growup)
    {
        var baseline = new GrowupBaseline(growup);
        _growupBaselines.Add(baseline);
        return baseline;
    }

    private void ApplyToTameable(Tameable tameable, TameableBaseline baseline)
    {
        tameable.m_tamingTime = _tamingTime.Apply(baseline.TamingTime);
        tameable.m_fedDuration = _fedDuration.Apply(baseline.FedDuration);
    }

    private void ApplyToProcreation(Procreation procreation, ProcreationBaseline baseline)
    {
        procreation.m_requiredLovePoints = ApplyRoundedAtLeastOne(
            _requiredLovePoints,
            baseline.RequiredLovePoints);
        procreation.m_pregnancyDuration = _pregnancyDuration.Apply(
            baseline.PregnancyDuration);
        procreation.m_pregnancyChance = _pregnancyChance.Apply(baseline.PregnancyChance);
        procreation.m_partnerCheckRange = _partnerCheckRange.Apply(
            baseline.PartnerCheckRange);
        procreation.m_maxCreatures = ApplyRoundedAtLeastOne(
            _creatureLimit,
            baseline.MaxCreatures);
    }

    private void ApplyToGrowup(Growup growup, GrowupBaseline baseline)
    {
        growup.m_growTime = _growthTime.Apply(baseline.GrowTime);
    }

    private static int ApplyRoundedAtLeastOne(ConfigEntryPercent modifier, int baseValue)
    {
        float adjustedValue = modifier.Apply(baseValue);
        if (adjustedValue >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(
            1,
            (int)Math.Round(adjustedValue, MidpointRounding.AwayFromZero));
    }

    // Mob-AI aggression tweaks are intentionally omitted: there is no stable prefix/postfix seam.
    // ValheimPlus implemented that behavior by rewriting MonsterAI.UpdateAI IL, and this project
    // deliberately does not use transpilers.

    private sealed class TameableBaseline
    {
        public TameableBaseline(Tameable tameable)
        {
            Tameable = new WeakReference<Tameable>(tameable);
            TamingTime = tameable.m_tamingTime;
            FedDuration = tameable.m_fedDuration;
        }

        public WeakReference<Tameable> Tameable { get; }

        public float TamingTime { get; }

        public float FedDuration { get; }

        public void Restore(Tameable tameable)
        {
            tameable.m_tamingTime = TamingTime;
            tameable.m_fedDuration = FedDuration;
        }
    }

    private sealed class ProcreationBaseline
    {
        public ProcreationBaseline(Procreation procreation)
        {
            Procreation = new WeakReference<Procreation>(procreation);
            RequiredLovePoints = procreation.m_requiredLovePoints;
            PregnancyDuration = procreation.m_pregnancyDuration;
            PregnancyChance = procreation.m_pregnancyChance;
            PartnerCheckRange = procreation.m_partnerCheckRange;
            MaxCreatures = procreation.m_maxCreatures;
        }

        public WeakReference<Procreation> Procreation { get; }

        public int RequiredLovePoints { get; }

        public float PregnancyDuration { get; }

        public float PregnancyChance { get; }

        public float PartnerCheckRange { get; }

        public int MaxCreatures { get; }

        public void Restore(Procreation procreation)
        {
            procreation.m_requiredLovePoints = RequiredLovePoints;
            procreation.m_pregnancyDuration = PregnancyDuration;
            procreation.m_pregnancyChance = PregnancyChance;
            procreation.m_partnerCheckRange = PartnerCheckRange;
            procreation.m_maxCreatures = MaxCreatures;
        }
    }

    private sealed class GrowupBaseline
    {
        public GrowupBaseline(Growup growup)
        {
            Growup = new WeakReference<Growup>(growup);
            GrowTime = growup.m_growTime;
        }

        public WeakReference<Growup> Growup { get; }

        public float GrowTime { get; }

        public void Restore(Growup growup)
        {
            growup.m_growTime = GrowTime;
        }
    }
}

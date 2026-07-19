using System;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class DayNightLengthModule : IFeatureModule
{
    private static DayNightLengthModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryPercent _dayLengthMultiplier;
    private readonly ConfigEntryInt _dayLengthSeconds;
    private EnvManBaseline? _baseline;

    public DayNightLengthModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _dayLengthMultiplier = _feature.Percent(
            "DayLengthMultiplier",
            0f,
            "Length of the full day. Valheim's default m_dayLengthSec is 1800 seconds.");
        _dayLengthSeconds = _feature.Int(
            "DayLengthSeconds",
            0,
            "Absolute full-day length in seconds. Zero disables this override; positive values override DayLengthMultiplier.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Day/night length";

    public string Section => "Time";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        // World time is server-owned, but unmodded clients render the wrong day-fraction visuals.
        // Keep this feature synced so every participating client uses the server's full-day length.
        _active = this;

        var awake = AccessTools.Method(typeof(EnvMan), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(EnvMan), "Awake");
        harmony.Patch(
            awake,
            postfix: new HarmonyMethod(typeof(DayNightLengthModule), nameof(EnvManAwakePostfix)));
    }

    private static void EnvManAwakePostfix(EnvMan __instance)
    {
        DayNightLengthModule? active = _active;
        if (active == null)
        {
            return;
        }

        // Capture the untouched per-instance value even while disabled. A later server overlay can
        // then hot-enable the module, and clearing that overlay can restore the same EnvMan.
        EnvManBaseline baseline = active.GetOrAddBaseline(__instance);
        if (!active.IsEnabled)
        {
            return;
        }

        active.ApplyToEnvMan(__instance, baseline);
    }

    private void OnEffectiveValuesChanged()
    {
        EnvManBaseline? baseline = _baseline;
        if (baseline == null ||
            !baseline.EnvMan.TryGetTarget(out EnvMan? envMan) ||
            envMan == null)
        {
            _baseline = null;
            return;
        }

        if (IsEnabled)
        {
            ApplyToEnvMan(envMan, baseline);
        }
        else
        {
            baseline.Restore(envMan);
        }
    }

    private EnvManBaseline GetOrAddBaseline(EnvMan envMan)
    {
        EnvManBaseline? baseline = _baseline;
        if (baseline != null &&
            baseline.EnvMan.TryGetTarget(out EnvMan? existing) &&
            existing != null &&
            ReferenceEquals(existing, envMan))
        {
            return baseline;
        }

        var added = new EnvManBaseline(envMan);
        _baseline = added;
        return added;
    }

    private void ApplyToEnvMan(EnvMan envMan, EnvManBaseline baseline)
    {
        int absoluteSeconds = _dayLengthSeconds.Value;
        if (absoluteSeconds > 0)
        {
            envMan.m_dayLengthSec = absoluteSeconds;
            return;
        }

        float adjustedSeconds = _dayLengthMultiplier.Apply(baseline.DayLengthSeconds);
        if (float.IsNaN(adjustedSeconds))
        {
            adjustedSeconds = baseline.DayLengthSeconds;
        }

        // EnvMan uses m_dayLengthSec as a divisor. The shared modifier helper still resolves
        // values <= -100% to zero; this field-specific safety floor prevents a divide-by-zero.
        envMan.m_dayLengthSec = adjustedSeconds >= long.MaxValue
            ? long.MaxValue
            : Math.Max(1L, (long)Math.Round(adjustedSeconds, MidpointRounding.AwayFromZero));
    }

    private sealed class EnvManBaseline
    {
        public EnvManBaseline(EnvMan envMan)
        {
            EnvMan = new WeakReference<EnvMan>(envMan);
            DayLengthSeconds = envMan.m_dayLengthSec;
        }

        public WeakReference<EnvMan> EnvMan { get; }

        public long DayLengthSeconds { get; }

        public void Restore(EnvMan envMan)
        {
            envMan.m_dayLengthSec = DayLengthSeconds;
        }
    }
}

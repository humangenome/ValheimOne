using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class WorldEventsModule : IFeatureModule
{
    private static WorldEventsModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryBool _disableRandomEvents;
    private readonly ConfigEntryPercent _eventChanceMultiplier;
    private readonly ConfigEntryPercent _eventIntervalMultiplier;
    private readonly ConfigEntryPercent _guardianCooldownMultiplier;
    private readonly ConfigEntryPercent _guardianDurationMultiplier;
    private RandEventSystemBaseline? _baseline;

    public WorldEventsModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _disableRandomEvents = _feature.Bool(
            "DisableRandomEvents",
            defaultValue: false,
            "Prevent scheduled random-event rolls by setting their chance to zero. Console-forced events still work.");
        _eventChanceMultiplier = _feature.Percent(
            "EventChanceMultiplier",
            0f,
            "Chance that a random event starts when the event interval elapses.");
        _eventIntervalMultiplier = _feature.Percent(
            "EventIntervalMultiplier",
            0f,
            "Minimum interval in minutes between random-event rolls.");
        _guardianCooldownMultiplier = _feature.Percent(
            "GuardianCooldownMultiplier",
            0f,
            "Cooldown countdown applied after activating a guardian power.");
        _guardianDurationMultiplier = _feature.Percent(
            "GuardianDurationMultiplier",
            0f,
            "Duration of the guardian status effect applied to nearby players.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "World events and guardian powers";

    public string Section => "Events";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.ServerAuthoritative;

    public void ApplyPatches(Harmony harmony)
    {
        // Random-event scheduling is server-owned. Patches remain installed so a server overlay
        // can hot-enable the feature without changing Harmony state during a session.
        _active = this;

        var randEventSystemAwake = AccessTools.Method(
            typeof(RandEventSystem),
            "Awake",
            Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(RandEventSystem), "Awake");
        harmony.Patch(
            randEventSystemAwake,
            postfix: new HarmonyMethod(
                typeof(WorldEventsModule),
                nameof(RandEventSystemAwakePostfix)));

        // ActivateGuardianPower is the verified seam that adds the status-effect clone and then
        // initializes m_guardianPowerCooldown. StartGuardianPower only starts the animation.
        var activateGuardianPower = AccessTools.Method(
            typeof(Player),
            nameof(Player.ActivateGuardianPower),
            Type.EmptyTypes)
            ?? throw new MissingMethodException(
                nameof(Player),
                nameof(Player.ActivateGuardianPower));
        harmony.Patch(
            activateGuardianPower,
            prefix: new HarmonyMethod(
                typeof(WorldEventsModule),
                nameof(ActivateGuardianPowerPrefix)),
            postfix: new HarmonyMethod(
                typeof(WorldEventsModule),
                nameof(ActivateGuardianPowerPostfix)));
    }

    private static void RandEventSystemAwakePostfix(RandEventSystem __instance)
    {
        WorldEventsModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToRandEventSystem(__instance, active.GetOrAddBaseline(__instance));
    }

    private static void ActivateGuardianPowerPrefix(
        Player __instance,
        out GuardianActivationState __state)
    {
        __state = default;

        WorldEventsModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        __instance.GetGuardianPowerHUD(out StatusEffect guardianPower, out _);
        if (guardianPower == null || __instance.m_guardianPowerCooldown > 0f)
        {
            return;
        }

        __state = new GuardianActivationState(
            guardianPower.NameHash(),
            guardianPower.m_ttl);
    }

    private static void ActivateGuardianPowerPostfix(
        Player __instance,
        GuardianActivationState __state)
    {
        WorldEventsModule? active = _active;
        if (active == null || !active.IsEnabled || !__state.ShouldApply)
        {
            return;
        }

        // Vanilla sets this public field to the selected power's cooldown on successful
        // activation, and UpdateGuardianPower subsequently treats it as a seconds countdown.
        __instance.m_guardianPowerCooldown = active._guardianCooldownMultiplier.Apply(
            __instance.m_guardianPowerCooldown);

        var affectedPlayers = new List<Player>();
        Player.GetPlayersInRange(__instance.transform.position, 10f, affectedPlayers);
        foreach (Player player in affectedPlayers)
        {
            StatusEffect? appliedEffect = player.GetSEMan().GetStatusEffect(__state.PowerHash);
            if (appliedEffect == null)
            {
                continue;
            }

            // SEMan stores the clone returned by AddStatusEffect. Scale that instance from the
            // shared asset's captured TTL so repeat activations never compound, and never mutate
            // the shared guardian StatusEffect asset itself.
            appliedEffect.m_ttl = active._guardianDurationMultiplier.Apply(
                __state.BaseDuration);
        }
    }

    private void OnEffectiveValuesChanged()
    {
        RandEventSystem? randEventSystem = RandEventSystem.instance;
        if (randEventSystem == null)
        {
            _baseline = null;
            return;
        }

        RandEventSystemBaseline baseline = GetOrAddBaseline(randEventSystem);
        if (IsEnabled)
        {
            ApplyToRandEventSystem(randEventSystem, baseline);
        }
        else
        {
            baseline.Restore(randEventSystem);
        }
    }

    private RandEventSystemBaseline GetOrAddBaseline(RandEventSystem randEventSystem)
    {
        RandEventSystemBaseline? baseline = _baseline;
        if (baseline != null &&
            baseline.RandEventSystem.TryGetTarget(out RandEventSystem? existing) &&
            existing != null &&
            ReferenceEquals(existing, randEventSystem))
        {
            return baseline;
        }

        var added = new RandEventSystemBaseline(randEventSystem);
        _baseline = added;
        return added;
    }

    private void ApplyToRandEventSystem(
        RandEventSystem randEventSystem,
        RandEventSystemBaseline baseline)
    {
        randEventSystem.m_eventIntervalMin = _eventIntervalMultiplier.Apply(
            baseline.EventIntervalMinutes);
        randEventSystem.m_eventChance = _disableRandomEvents.Value
            ? 0f
            : _eventChanceMultiplier.Apply(baseline.EventChance);
    }

    private readonly struct GuardianActivationState
    {
        public GuardianActivationState(int powerHash, float baseDuration)
        {
            ShouldApply = true;
            PowerHash = powerHash;
            BaseDuration = baseDuration;
        }

        public bool ShouldApply { get; }

        public int PowerHash { get; }

        public float BaseDuration { get; }
    }

    private sealed class RandEventSystemBaseline
    {
        public RandEventSystemBaseline(RandEventSystem randEventSystem)
        {
            RandEventSystem = new WeakReference<RandEventSystem>(randEventSystem);
            EventIntervalMinutes = randEventSystem.m_eventIntervalMin;
            EventChance = randEventSystem.m_eventChance;
        }

        public WeakReference<RandEventSystem> RandEventSystem { get; }

        public float EventIntervalMinutes { get; }

        public float EventChance { get; }

        public void Restore(RandEventSystem randEventSystem)
        {
            randEventSystem.m_eventIntervalMin = EventIntervalMinutes;
            randEventSystem.m_eventChance = EventChance;
        }
    }
}

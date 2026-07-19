using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class PlayerModule : IFeatureModule
{
    private const float VanillaMegingjordBuff = 150f;

    [ThreadStatic]
    private static bool s_suppressAutoPickupEncumbrance;

    private static PlayerModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryFloat _baseMaximumWeight;
    private readonly ConfigEntryFloat _megingjordBuff;
    private readonly ConfigEntryFloat _autoPickupRange;
    private readonly ConfigEntryBool _pickupWhileEncumbered;
    private readonly ConfigEntryInt _restedSecondsPerComfort;
    private readonly List<PlayerBaseline> _playerBaselines = new List<PlayerBaseline>();
    private readonly List<RestedBaseline> _restedBaselines = new List<RestedBaseline>();

    public PlayerModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _baseMaximumWeight = _feature.Float(
            "BaseMaximumWeight",
            300f,
            "Absolute base player carry weight. Valheim's default is 300.");
        _megingjordBuff = _feature.Float(
            "MegingjordBuff",
            VanillaMegingjordBuff,
            "Absolute carry-weight bonus supplied by Megingjord. Valheim's default is 150.");
        _autoPickupRange = _feature.Float(
            "AutoPickupRange",
            0f,
            "Absolute item-vacuum radius in meters. Zero disables this override; Valheim's default is 2.");
        _pickupWhileEncumbered = _feature.Bool(
            "PickupWhileEncumbered",
            defaultValue: false,
            "Keep the item vacuum working while the player is encumbered without removing the movement penalty.");
        _restedSecondsPerComfort = _feature.Int(
            "RestedSecondsPerComfort",
            0,
            "Absolute rested-bonus seconds per comfort level. Zero disables this override; Valheim's default is 60.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Player conveniences";

    public string Section => "Player";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.ServerAuthoritative;

    public void ApplyPatches(Harmony harmony)
    {
        // Patches stay installed so a server overlay can hot-enable any Player setting.
        _active = this;

        var getMaxCarryWeight = AccessTools.Method(
            typeof(Player),
            nameof(Player.GetMaxCarryWeight),
            Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Player), nameof(Player.GetMaxCarryWeight));
        harmony.Patch(
            getMaxCarryWeight,
            postfix: new HarmonyMethod(typeof(PlayerModule), nameof(GetMaxCarryWeightPostfix)));

        var playerAwake = AccessTools.Method(typeof(Player), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Player), "Awake");
        harmony.Patch(
            playerAwake,
            postfix: new HarmonyMethod(typeof(PlayerModule), nameof(PlayerAwakePostfix)));

        var autoPickup = AccessTools.Method(
            typeof(Player),
            "AutoPickup",
            new[] { typeof(float) })
            ?? throw new MissingMethodException(nameof(Player), "AutoPickup");
        harmony.Patch(
            autoPickup,
            prefix: new HarmonyMethod(typeof(PlayerModule), nameof(AutoPickupPrefix)),
            postfix: new HarmonyMethod(typeof(PlayerModule), nameof(AutoPickupPostfix)),
            finalizer: new HarmonyMethod(typeof(PlayerModule), nameof(AutoPickupFinalizer)));

        var isEncumbered = AccessTools.Method(
            typeof(Character),
            nameof(Character.IsEncumbered),
            Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Character), nameof(Character.IsEncumbered));
        harmony.Patch(
            isEncumbered,
            postfix: new HarmonyMethod(typeof(PlayerModule), nameof(IsEncumberedPostfix)));

        var updateRestedTtl = AccessTools.Method(typeof(SE_Rested), "UpdateTTL", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(SE_Rested), "UpdateTTL");
        harmony.Patch(
            updateRestedTtl,
            prefix: new HarmonyMethod(typeof(PlayerModule), nameof(UpdateRestedTtlPrefix)));
    }

    private static void GetMaxCarryWeightPostfix(Player __instance, ref float __result)
    {
        PlayerModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (active._pickupWhileEncumbered.Value && s_suppressAutoPickupEncumbrance)
        {
            // Valheim 0.221.12's AutoPickup compares total weight directly with this limit instead
            // of consulting IsEncumbered. Keep only that thread-scoped vacuum comparison open.
            __result = float.PositiveInfinity;
            return;
        }

        float additionalWeight = Math.Max(0f, __result - __instance.m_maxCarryWeight);
        if (additionalWeight >= VanillaMegingjordBuff)
        {
            // Preserve any additive bonus beyond the vanilla belt amount, so other effects remain composable.
            additionalWeight += active._megingjordBuff.Value - VanillaMegingjordBuff;
        }

        __result = Math.Max(0f, active._baseMaximumWeight.Value + additionalWeight);
    }

    private static void PlayerAwakePostfix(Player __instance)
    {
        PlayerModule? active = _active;
        if (active == null)
        {
            return;
        }

        // Capture the untouched public field even while disabled. A later server overlay can then
        // apply or clear AutoPickupRange on this same Player without requiring another Awake.
        PlayerBaseline baseline = active.GetOrAddPlayerBaseline(__instance);
        if (!active.IsEnabled)
        {
            return;
        }

        active.ApplyToPlayer(__instance, baseline);
    }

    private static void AutoPickupPrefix(out AutoPickupScopeState __state)
    {
        __state = default;

        PlayerModule? active = _active;
        if (active == null || !active.IsEnabled || !active._pickupWhileEncumbered.Value)
        {
            return;
        }

        __state = new AutoPickupScopeState(s_suppressAutoPickupEncumbrance);
        s_suppressAutoPickupEncumbrance = true;
    }

    private static void AutoPickupPostfix(AutoPickupScopeState __state)
    {
        if (!__state.Entered)
        {
            return;
        }

        s_suppressAutoPickupEncumbrance = __state.PreviousValue;
    }

    private static Exception? AutoPickupFinalizer(
        AutoPickupScopeState __state,
        Exception? __exception)
    {
        if (__state.Entered)
        {
            s_suppressAutoPickupEncumbrance = __state.PreviousValue;
        }

        return __exception;
    }

    private static void IsEncumberedPostfix(ref bool __result)
    {
        PlayerModule? active = _active;
        if (active == null ||
            !active.IsEnabled ||
            !active._pickupWhileEncumbered.Value ||
            !s_suppressAutoPickupEncumbrance)
        {
            return;
        }

        __result = false;
    }

    private static void UpdateRestedTtlPrefix(SE_Rested __instance)
    {
        PlayerModule? active = _active;
        if (active == null)
        {
            return;
        }

        // Setup and ResetTime both flow through UpdateTTL. Snapshot before its first calculation so
        // server overlays can apply and restore the public per-instance factor without reflection.
        RestedBaseline baseline = active.GetOrAddRestedBaseline(__instance);
        if (!active.IsEnabled)
        {
            return;
        }

        active.ApplyToRested(__instance, baseline);
    }

    private void OnEffectiveValuesChanged()
    {
        for (int index = _playerBaselines.Count - 1; index >= 0; index--)
        {
            PlayerBaseline baseline = _playerBaselines[index];
            if (!baseline.Player.TryGetTarget(out Player? player) || player == null)
            {
                _playerBaselines.RemoveAt(index);
                continue;
            }

            if (IsEnabled)
            {
                ApplyToPlayer(player, baseline);
            }
            else
            {
                baseline.Restore(player);
            }
        }

        for (int index = _restedBaselines.Count - 1; index >= 0; index--)
        {
            RestedBaseline baseline = _restedBaselines[index];
            if (!baseline.Rested.TryGetTarget(out SE_Rested? rested) || rested == null)
            {
                _restedBaselines.RemoveAt(index);
                continue;
            }

            if (IsEnabled)
            {
                ApplyToRested(rested, baseline);
            }
            else
            {
                baseline.Restore(rested);
            }
        }
    }

    private PlayerBaseline GetOrAddPlayerBaseline(Player player)
    {
        for (int index = _playerBaselines.Count - 1; index >= 0; index--)
        {
            PlayerBaseline baseline = _playerBaselines[index];
            if (!baseline.Player.TryGetTarget(out Player? existingPlayer) || existingPlayer == null)
            {
                _playerBaselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existingPlayer, player))
            {
                return baseline;
            }
        }

        var added = new PlayerBaseline(player);
        _playerBaselines.Add(added);
        return added;
    }

    private RestedBaseline GetOrAddRestedBaseline(SE_Rested rested)
    {
        for (int index = _restedBaselines.Count - 1; index >= 0; index--)
        {
            RestedBaseline baseline = _restedBaselines[index];
            if (!baseline.Rested.TryGetTarget(out SE_Rested? existingRested) ||
                existingRested == null)
            {
                _restedBaselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existingRested, rested))
            {
                return baseline;
            }
        }

        var added = new RestedBaseline(rested);
        _restedBaselines.Add(added);
        return added;
    }

    private void ApplyToPlayer(Player player, PlayerBaseline baseline)
    {
        float autoPickupRange = _autoPickupRange.Value;
        player.m_autoPickupRange = autoPickupRange > 0f
            ? autoPickupRange
            : baseline.AutoPickupRange;
    }

    private void ApplyToRested(SE_Rested rested, RestedBaseline baseline)
    {
        int secondsPerComfort = _restedSecondsPerComfort.Value;
        rested.m_TTLPerComfortLevel = secondsPerComfort > 0
            ? secondsPerComfort
            : baseline.SecondsPerComfort;
    }

    private readonly struct AutoPickupScopeState
    {
        public AutoPickupScopeState(bool previousValue)
        {
            Entered = true;
            PreviousValue = previousValue;
        }

        public bool Entered { get; }

        public bool PreviousValue { get; }
    }

    private sealed class PlayerBaseline
    {
        public PlayerBaseline(Player player)
        {
            Player = new WeakReference<Player>(player);
            AutoPickupRange = player.m_autoPickupRange;
        }

        public WeakReference<Player> Player { get; }

        public float AutoPickupRange { get; }

        public void Restore(Player player)
        {
            player.m_autoPickupRange = AutoPickupRange;
        }
    }

    private sealed class RestedBaseline
    {
        public RestedBaseline(SE_Rested rested)
        {
            Rested = new WeakReference<SE_Rested>(rested);
            SecondsPerComfort = rested.m_TTLPerComfortLevel;
        }

        public WeakReference<SE_Rested> Rested { get; }

        public float SecondsPerComfort { get; }

        public void Restore(SE_Rested rested)
        {
            rested.m_TTLPerComfortLevel = SecondsPerComfort;
        }
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class PlayerStaminaModule : IFeatureModule
{
    private static PlayerStaminaModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryPercent _regenRate;
    private readonly ConfigEntryPercent _regenDelay;
    private readonly ConfigEntryPercent _runDrain;
    private readonly ConfigEntryPercent _jumpCost;
    private readonly ConfigEntryPercent _dodgeCost;
    private readonly ConfigEntryPercent _swimDrain;
    private readonly ConfigEntryPercent _encumberedDrain;
    private readonly ConfigEntryPercent _attackCost;
    private readonly ConfigEntryPercent _toolCost;
    private readonly List<PlayerStaminaBaseline> _playerBaselines =
        new List<PlayerStaminaBaseline>();

    public PlayerStaminaModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _regenRate = _feature.Percent(
            "RegenRate",
            0f,
            "Player stamina regeneration rate.");
        _regenDelay = _feature.Percent(
            "RegenDelay",
            0f,
            "Delay before player stamina starts regenerating. Negative values shorten the delay.");
        _runDrain = _feature.Percent(
            "RunDrain",
            0f,
            "Stamina drained while running.");
        _jumpCost = _feature.Percent(
            "JumpCost",
            0f,
            "Stamina consumed by jumping.");
        _dodgeCost = _feature.Percent(
            "DodgeCost",
            0f,
            "Stamina consumed by dodging.");
        _swimDrain = _feature.Percent(
            "SwimDrain",
            0f,
            "Minimum- and maximum-skill stamina drain while swimming.");
        _encumberedDrain = _feature.Percent(
            "EncumberedDrain",
            0f,
            "Stamina drained while encumbered.");
        _attackCost = _feature.Percent(
            "AttackCost",
            0f,
            "Stamina consumed by weapon attacks.");
        _toolCost = _feature.Percent(
            "ToolCost",
            0f,
            "Stamina consumed by hammer, hoe, and cultivator placement or terrain actions.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Player stamina";

    public string Section => "Stamina";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.RequiresClient;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var awake = AccessTools.Method(typeof(Player), nameof(Player.Awake), Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Player), nameof(Player.Awake));
        harmony.Patch(
            awake,
            postfix: new HarmonyMethod(typeof(PlayerStaminaModule), nameof(PlayerAwakePostfix)));

        var getAttackStamina = AccessTools.Method(
            typeof(Attack),
            nameof(Attack.GetAttackStamina),
            Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Attack), nameof(Attack.GetAttackStamina));
        harmony.Patch(
            getAttackStamina,
            postfix: new HarmonyMethod(
                typeof(PlayerStaminaModule),
                nameof(GetAttackStaminaPostfix)));

        var useStamina = AccessTools.Method(
            typeof(Player),
            nameof(Player.UseStamina),
            new[] { typeof(float) })
            ?? throw new MissingMethodException(nameof(Player), nameof(Player.UseStamina));
        harmony.Patch(
            useStamina,
            prefix: new HarmonyMethod(typeof(PlayerStaminaModule), nameof(UseStaminaPrefix)));
    }

    private static void PlayerAwakePostfix(Player __instance)
    {
        PlayerStaminaModule? active = _active;
        if (active == null)
        {
            return;
        }

        // Capturing the untouched fields is the only work performed while disabled. It lets a
        // server overlay hot-enable this module after Awake and later restore the same instance.
        PlayerStaminaBaseline baseline = active.GetOrAddBaseline(__instance);
        if (!active.IsEnabled)
        {
            return;
        }

        active.ApplyToPlayer(__instance, baseline);
    }

    private static void GetAttackStaminaPostfix(ref float __result)
    {
        PlayerStaminaModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        __result = active._attackCost.Apply(__result);
    }

    private static void UseStaminaPrefix(Player __instance, ref float v)
    {
        PlayerStaminaModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        ItemDrop.ItemData? rightItem = __instance.RightItem;
        if (rightItem == null || rightItem.m_shared.m_buildPieces == null)
        {
            return;
        }

        v = active._toolCost.Apply(v);
    }

    private void OnEffectiveValuesChanged()
    {
        for (int index = _playerBaselines.Count - 1; index >= 0; index--)
        {
            PlayerStaminaBaseline baseline = _playerBaselines[index];
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
    }

    private PlayerStaminaBaseline GetOrAddBaseline(Player player)
    {
        for (int index = _playerBaselines.Count - 1; index >= 0; index--)
        {
            PlayerStaminaBaseline baseline = _playerBaselines[index];
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

        var added = new PlayerStaminaBaseline(player);
        _playerBaselines.Add(added);
        return added;
    }

    private void ApplyToPlayer(Player player, PlayerStaminaBaseline baseline)
    {
        player.m_staminaRegen = _regenRate.Apply(baseline.StaminaRegen);
        player.m_staminaRegenDelay = _regenDelay.Apply(baseline.StaminaRegenDelay);
        player.m_runStaminaDrain = _runDrain.Apply(baseline.RunStaminaDrain);
        player.m_jumpStaminaUsage = _jumpCost.Apply(baseline.JumpStaminaUsage);
        player.m_dodgeStaminaUsage = _dodgeCost.Apply(baseline.DodgeStaminaUsage);
        player.m_swimStaminaDrainMinSkill = _swimDrain.Apply(baseline.SwimStaminaDrainMinSkill);
        player.m_swimStaminaDrainMaxSkill = _swimDrain.Apply(baseline.SwimStaminaDrainMaxSkill);
        player.m_encumberedStaminaDrain = _encumberedDrain.Apply(
            baseline.EncumberedStaminaDrain);
    }

    private sealed class PlayerStaminaBaseline
    {
        public PlayerStaminaBaseline(Player player)
        {
            Player = new WeakReference<Player>(player);
            StaminaRegen = player.m_staminaRegen;
            StaminaRegenDelay = player.m_staminaRegenDelay;
            RunStaminaDrain = player.m_runStaminaDrain;
            JumpStaminaUsage = player.m_jumpStaminaUsage;
            DodgeStaminaUsage = player.m_dodgeStaminaUsage;
            SwimStaminaDrainMinSkill = player.m_swimStaminaDrainMinSkill;
            SwimStaminaDrainMaxSkill = player.m_swimStaminaDrainMaxSkill;
            EncumberedStaminaDrain = player.m_encumberedStaminaDrain;
        }

        public WeakReference<Player> Player { get; }

        public float StaminaRegen { get; }

        public float StaminaRegenDelay { get; }

        public float RunStaminaDrain { get; }

        public float JumpStaminaUsage { get; }

        public float DodgeStaminaUsage { get; }

        public float SwimStaminaDrainMinSkill { get; }

        public float SwimStaminaDrainMaxSkill { get; }

        public float EncumberedStaminaDrain { get; }

        public void Restore(Player player)
        {
            player.m_staminaRegen = StaminaRegen;
            player.m_staminaRegenDelay = StaminaRegenDelay;
            player.m_runStaminaDrain = RunStaminaDrain;
            player.m_jumpStaminaUsage = JumpStaminaUsage;
            player.m_dodgeStaminaUsage = DodgeStaminaUsage;
            player.m_swimStaminaDrainMinSkill = SwimStaminaDrainMinSkill;
            player.m_swimStaminaDrainMaxSkill = SwimStaminaDrainMaxSkill;
            player.m_encumberedStaminaDrain = EncumberedStaminaDrain;
        }
    }
}

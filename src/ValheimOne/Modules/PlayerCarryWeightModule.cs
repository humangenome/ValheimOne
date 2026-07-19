using System;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class PlayerCarryWeightModule : IFeatureModule
{
    private const float VanillaMegingjordBuff = 150f;
    private static PlayerCarryWeightModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryFloat _baseMaximumWeight;
    private readonly ConfigEntryFloat _megingjordBuff;

    public PlayerCarryWeightModule(FeatureRegistry registry)
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
    }

    public string Name => "Player carry weight";

    public string Section => "Player";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.ServerAuthoritative;

    public void ApplyPatches(Harmony harmony)
    {
        // Carry weight changes simulation and belongs to the server ruleset. Vanilla clients do not
        // see this patch without ValheimOne; the server/client version handshake arrives in a later phase.
        _active = this;

        var original = AccessTools.Method(typeof(Player), nameof(Player.GetMaxCarryWeight), Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Player), nameof(Player.GetMaxCarryWeight));
        var postfix = new HarmonyMethod(
            typeof(PlayerCarryWeightModule),
            nameof(GetMaxCarryWeightPostfix));
        harmony.Patch(original, postfix: postfix);
    }

    private static void GetMaxCarryWeightPostfix(Player __instance, ref float __result)
    {
        PlayerCarryWeightModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
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
}

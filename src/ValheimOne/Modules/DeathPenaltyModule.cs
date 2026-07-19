using System;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class DeathPenaltyModule : IFeatureModule
{
    private static DeathPenaltyModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryPercent _skillLossMultiplier;
    private readonly ConfigEntryBool _keepInventory;

    public DeathPenaltyModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _skillLossMultiplier = _feature.Percent(
            "SkillLossMultiplier",
            0f,
            "Skill loss applied on death. Set to -100 to prevent skill loss.");
        _keepInventory = _feature.Bool(
            "KeepInventory",
            defaultValue: false,
            "Keep carried items through death and do not create a tombstone.");
    }

    public string Name => "Death penalty";

    public string Section => "DeathPenalty";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        // Skill loss executes on the owning client, while KeepInventory is a server-relevant
        // policy that the owning client must also observe. Keep the mixed section synced.
        _active = this;

        var lowerAllSkills = AccessTools.Method(
            typeof(Skills),
            nameof(Skills.LowerAllSkills),
            new[] { typeof(float) })
            ?? throw new MissingMethodException(nameof(Skills), nameof(Skills.LowerAllSkills));
        harmony.Patch(
            lowerAllSkills,
            prefix: new HarmonyMethod(
                typeof(DeathPenaltyModule),
                nameof(LowerAllSkillsPrefix)));

        var createTombStone = AccessTools.Method(
            typeof(Player),
            nameof(Player.CreateTombStone),
            Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Player), nameof(Player.CreateTombStone));
        harmony.Patch(
            createTombStone,
            prefix: new HarmonyMethod(
                typeof(DeathPenaltyModule),
                nameof(CreateTombStonePrefix)));
    }

    private static bool LowerAllSkillsPrefix(ref float factor)
    {
        DeathPenaltyModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return true;
        }

        factor = active._skillLossMultiplier.Apply(factor);
        return factor > 0f;
    }

    private static bool CreateTombStonePrefix()
    {
        DeathPenaltyModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return true;
        }

        // Vanilla performs all death-time inventory mutation and grave transfer inside this
        // method, so skipping it preserves both equipped and unequipped items in place.
        return !active._keepInventory.Value;
    }
}

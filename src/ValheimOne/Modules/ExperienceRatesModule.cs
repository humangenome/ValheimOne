using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class ExperienceRatesModule : IFeatureModule
{
    private static ExperienceRatesModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryPercent _globalMultiplier;
    private readonly Dictionary<Skills.SkillType, ConfigEntryPercent> _skillMultipliers =
        new Dictionary<Skills.SkillType, ConfigEntryPercent>();

    public ExperienceRatesModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _globalMultiplier = _feature.Percent(
            "GlobalMultiplier",
            0f,
            "Experience gained for every skill.");

        RegisterSkill(Skills.SkillType.Swords);
        RegisterSkill(Skills.SkillType.Knives);
        RegisterSkill(Skills.SkillType.Clubs);
        RegisterSkill(Skills.SkillType.Polearms);
        RegisterSkill(Skills.SkillType.Spears);
        RegisterSkill(Skills.SkillType.Blocking);
        RegisterSkill(Skills.SkillType.Axes);
        RegisterSkill(Skills.SkillType.Bows);
        RegisterSkill(Skills.SkillType.ElementalMagic);
        RegisterSkill(Skills.SkillType.BloodMagic);
        RegisterSkill(Skills.SkillType.Unarmed);
        RegisterSkill(Skills.SkillType.Pickaxes);
        RegisterSkill(Skills.SkillType.WoodCutting);
        RegisterSkill(Skills.SkillType.Crossbows);
        RegisterSkill(Skills.SkillType.Jump);
        RegisterSkill(Skills.SkillType.Sneak);
        RegisterSkill(Skills.SkillType.Run);
        RegisterSkill(Skills.SkillType.Swim);
        RegisterSkill(Skills.SkillType.Fishing);
        RegisterSkill(Skills.SkillType.Cooking);
        RegisterSkill(Skills.SkillType.Farming);
        RegisterSkill(Skills.SkillType.Crafting);
        RegisterSkill(Skills.SkillType.Ride);
    }

    public string Name => "Experience rates";

    public string Section => "Experience";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        // Skill experience is calculated by the owning client, so the server's effective rates
        // must be synced to participating clients.
        _active = this;

        var raiseSkill = AccessTools.Method(
            typeof(Skills),
            nameof(Skills.RaiseSkill),
            new[] { typeof(Skills.SkillType), typeof(float) })
            ?? throw new MissingMethodException(nameof(Skills), nameof(Skills.RaiseSkill));
        harmony.Patch(
            raiseSkill,
            prefix: new HarmonyMethod(
                typeof(ExperienceRatesModule),
                nameof(RaiseSkillPrefix)));
    }

    private static void RaiseSkillPrefix(Skills.SkillType skillType, ref float factor)
    {
        ExperienceRatesModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        factor = active._globalMultiplier.Apply(factor);
        if (active._skillMultipliers.TryGetValue(
                skillType,
                out ConfigEntryPercent? skillMultiplier))
        {
            factor = skillMultiplier.Apply(factor);
        }
    }

    private void RegisterSkill(Skills.SkillType skillType)
    {
        string key = skillType.ToString();
        _skillMultipliers.Add(
            skillType,
            _feature.Percent(
                key,
                0f,
                $"Experience gained for the {key} skill."));
    }
}

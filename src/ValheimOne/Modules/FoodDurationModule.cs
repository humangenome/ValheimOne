using System;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class FoodDurationModule : IFeatureModule
{
    private static FoodDurationModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryPercent _durationMultiplier;
    private readonly ConfigEntryBool _noDegradation;

    public FoodDurationModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _durationMultiplier = _feature.Percent(
            "DurationMultiplier",
            0f,
            "Duration of food benefits.");
        _noDegradation = _feature.Bool(
            "NoDegradation",
            defaultValue: false,
            "Keep each food's full health, stamina, and eitr benefit until it expires.");
    }

    public string Name => "Food duration";

    public string Section => "Food";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.RequiresClient;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var eatFood = AccessTools.Method(
            typeof(Player),
            nameof(Player.EatFood),
            new[] { typeof(ItemDrop.ItemData) })
            ?? throw new MissingMethodException(nameof(Player), nameof(Player.EatFood));
        harmony.Patch(
            eatFood,
            postfix: new HarmonyMethod(typeof(FoodDurationModule), nameof(EatFoodPostfix)));

        var updateFood = AccessTools.Method(
            typeof(Player),
            nameof(Player.UpdateFood),
            new[] { typeof(float), typeof(bool) })
            ?? throw new MissingMethodException(nameof(Player), nameof(Player.UpdateFood));
        harmony.Patch(
            updateFood,
            postfix: new HarmonyMethod(typeof(FoodDurationModule), nameof(UpdateFoodPostfix)));
    }

    private static void EatFoodPostfix(
        Player __instance,
        ItemDrop.ItemData item,
        bool __result)
    {
        FoodDurationModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (!__result)
        {
            return;
        }

        foreach (Player.Food food in __instance.GetFoods())
        {
            if (!ReferenceEquals(food.m_item, item))
            {
                continue;
            }

            food.m_time = active._durationMultiplier.Apply(item.m_shared.m_foodBurnTime);
            return;
        }
    }

    private static void UpdateFoodPostfix(Player __instance)
    {
        FoodDurationModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (!active._noDegradation.Value)
        {
            return;
        }

        foreach (Player.Food food in __instance.GetFoods())
        {
            if (food.m_time <= 0f)
            {
                continue;
            }

            ItemDrop.ItemData item = food.m_item;
            food.m_health = item.m_shared.m_food;
            food.m_stamina = item.m_shared.m_foodStamina;
            food.m_eitr = item.m_shared.m_foodEitr;
        }
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class StructuralIntegrityModule : IFeatureModule
{
    private static StructuralIntegrityModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryBool _noWeatherDamage;
    private readonly ConfigEntryFloat _wood;
    private readonly ConfigEntryFloat _stone;
    private readonly ConfigEntryFloat _iron;
    private readonly ConfigEntryFloat _hardWood;
    private readonly ConfigEntryFloat _marble;
    private readonly ConfigEntryFloat _ashstone;
    private readonly ConfigEntryFloat _ancient;
    private readonly List<WearNTearBaseline> _baselines = new List<WearNTearBaseline>();

    public StructuralIntegrityModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _noWeatherDamage = _feature.Bool(
            "NoWeatherDamage",
            defaultValue: false,
            "Prevent rain and water exposure from wearing down building pieces.");
        _wood = Reduction(
            "Wood",
            "Wood support loss reduction percent. 100 prevents support loss over distance.");
        _stone = Reduction(
            "Stone",
            "Stone support loss reduction percent. 100 prevents support loss over distance.");
        _iron = Reduction(
            "Iron",
            "Iron support loss reduction percent. 100 prevents support loss over distance.");
        _hardWood = Reduction(
            "HardWood",
            "Hard-wood support loss reduction percent. 100 prevents support loss over distance.");
        _marble = Reduction(
            "Marble",
            "Marble support loss reduction percent. 100 prevents support loss over distance.");
        _ashstone = Reduction(
            "Ashstone",
            "Ashstone support loss reduction percent. 100 prevents support loss over distance.");
        _ancient = Reduction(
            "Ancient",
            "Ancient-material support loss reduction percent. 100 prevents support loss over distance.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Structural integrity";

    public string Section => "StructuralIntegrity";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var awake = AccessTools.Method(typeof(WearNTear), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(WearNTear), "Awake");
        harmony.Patch(
            awake,
            postfix: new HarmonyMethod(
                typeof(StructuralIntegrityModule),
                nameof(WearNTearAwakePostfix)));

        var getMaterialProperties = AccessTools.Method(
            typeof(WearNTear),
            "GetMaterialProperties",
            new[]
            {
                typeof(float).MakeByRefType(),
                typeof(float).MakeByRefType(),
                typeof(float).MakeByRefType(),
                typeof(float).MakeByRefType(),
            })
            ?? throw new MissingMethodException(nameof(WearNTear), "GetMaterialProperties");
        harmony.Patch(
            getMaterialProperties,
            postfix: new HarmonyMethod(
                typeof(StructuralIntegrityModule),
                nameof(GetMaterialPropertiesPostfix)));
    }

    private ConfigEntryFloat Reduction(string key, string description)
    {
        return _feature.Float(
            key,
            0f,
            description + " Values are clamped to 0-100 at runtime.");
    }

    private static void WearNTearAwakePostfix(WearNTear __instance)
    {
        StructuralIntegrityModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToWearNTear(__instance, active.GetOrAddBaseline(__instance));
    }

    private static void GetMaterialPropertiesPostfix(
        WearNTear __instance,
        ref float horizontalLoss,
        ref float verticalLoss)
    {
        StructuralIntegrityModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        float reduction = active.GetReduction(__instance.m_materialType);
        if (!(reduction > 0f))
        {
            return;
        }

        float clampedReduction = Math.Min(reduction, 100f);
        float lossFactor = 1f - (clampedReduction / 100f);
        horizontalLoss *= lossFactor;
        verticalLoss *= lossFactor;
    }

    private void OnEffectiveValuesChanged()
    {
#pragma warning disable CS0618
        WearNTear[] pieces = UnityEngine.Object.FindObjectsOfType<WearNTear>();
#pragma warning restore CS0618
        foreach (WearNTear piece in pieces)
        {
            WearNTearBaseline? baseline = FindBaseline(piece);
            if (IsEnabled)
            {
                ApplyToWearNTear(piece, baseline ?? AddBaseline(piece));
            }
            else
            {
                baseline?.Restore(piece);
            }
        }
    }

    private WearNTearBaseline GetOrAddBaseline(WearNTear piece)
    {
        return FindBaseline(piece) ?? AddBaseline(piece);
    }

    private WearNTearBaseline? FindBaseline(WearNTear piece)
    {
        for (int index = _baselines.Count - 1; index >= 0; index--)
        {
            WearNTearBaseline baseline = _baselines[index];
            if (!baseline.Piece.TryGetTarget(out WearNTear? existing) || existing == null)
            {
                _baselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, piece))
            {
                return baseline;
            }
        }

        return null;
    }

    private WearNTearBaseline AddBaseline(WearNTear piece)
    {
        var baseline = new WearNTearBaseline(piece);
        _baselines.Add(baseline);
        return baseline;
    }

    private void ApplyToWearNTear(WearNTear piece, WearNTearBaseline baseline)
    {
        // High confidence from the 0.221.12 metadata seam: UpdateWear owns rain state and its
        // HaveRoof path sits beside the explicitly named m_noRoofWear flag. Do not skip UpdateWear,
        // because that would also suppress unrelated vanilla wear logic.
        piece.m_noRoofWear = _noWeatherDamage.Value || baseline.NoRoofWear;
    }

    private float GetReduction(WearNTear.MaterialType materialType)
    {
        switch (materialType)
        {
            case WearNTear.MaterialType.Wood:
                return _wood.Value;
            case WearNTear.MaterialType.Stone:
                return _stone.Value;
            case WearNTear.MaterialType.Iron:
                return _iron.Value;
            case WearNTear.MaterialType.HardWood:
                return _hardWood.Value;
            case WearNTear.MaterialType.Marble:
                return _marble.Value;
            case WearNTear.MaterialType.Ashstone:
                return _ashstone.Value;
            case WearNTear.MaterialType.Ancient:
                return _ancient.Value;
            default:
                return 0f;
        }
    }

    private sealed class WearNTearBaseline
    {
        public WearNTearBaseline(WearNTear piece)
        {
            Piece = new WeakReference<WearNTear>(piece);
            NoRoofWear = piece.m_noRoofWear;
        }

        public WeakReference<WearNTear> Piece { get; }

        public bool NoRoofWear { get; }

        public void Restore(WearNTear piece)
        {
            piece.m_noRoofWear = NoRoofWear;
        }
    }
}

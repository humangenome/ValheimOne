using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;

namespace ValheimOne.Modules;

public sealed class FireSourceModule : IFeatureModule
{
    private static FireSourceModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryBool _infiniteTorches;
    private readonly ConfigEntryBool _infiniteFires;
    private readonly ModLogger _log;
    private readonly List<FireplaceBaseline> _baselines = new List<FireplaceBaseline>();
    private readonly HashSet<string> _loggedUnmatchedPrefabs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public FireSourceModule(FeatureRegistry registry, ModLogger log)
    {
        _feature = registry.Register(Name, Section, Classification);
        _infiniteTorches = _feature.Bool(
            "InfiniteTorches",
            defaultValue: false,
            "Prevent resin-fuelled torches, sconces, and braziers from consuming fuel.");
        _infiniteFires = _feature.Bool(
            "InfiniteFires",
            defaultValue: false,
            "Prevent wood-fuelled campfires, hearths, bonfires, and similar fireplaces from consuming fuel.");
        _log = log;

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Fire sources";

    public string Section => "FireSource";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;

        var awake = AccessTools.Method(typeof(Fireplace), nameof(Fireplace.Awake), Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Fireplace), nameof(Fireplace.Awake));
        harmony.Patch(
            awake,
            postfix: new HarmonyMethod(typeof(FireSourceModule), nameof(FireplaceAwakePostfix)));
    }

    private static void FireplaceAwakePostfix(Fireplace __instance)
    {
        FireSourceModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        active.ApplyToFireplace(__instance, active.GetOrAddBaseline(__instance));
    }

    private void OnEffectiveValuesChanged()
    {
#pragma warning disable CS0618
        Fireplace[] fireplaces = UnityEngine.Object.FindObjectsOfType<Fireplace>();
#pragma warning restore CS0618
        foreach (Fireplace fireplace in fireplaces)
        {
            FireplaceBaseline? baseline = FindBaseline(fireplace);
            if (IsEnabled)
            {
                ApplyToFireplace(fireplace, baseline ?? AddBaseline(fireplace));
            }
            else
            {
                baseline?.Restore(fireplace);
            }
        }
    }

    private FireplaceBaseline GetOrAddBaseline(Fireplace fireplace)
    {
        return FindBaseline(fireplace) ?? AddBaseline(fireplace);
    }

    private FireplaceBaseline? FindBaseline(Fireplace fireplace)
    {
        for (int index = _baselines.Count - 1; index >= 0; index--)
        {
            FireplaceBaseline baseline = _baselines[index];
            if (!baseline.Fireplace.TryGetTarget(out Fireplace? existing) || existing == null)
            {
                _baselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, fireplace))
            {
                return baseline;
            }
        }

        return null;
    }

    private FireplaceBaseline AddBaseline(Fireplace fireplace)
    {
        var baseline = new FireplaceBaseline(fireplace);
        _baselines.Add(baseline);
        return baseline;
    }

    private void ApplyToFireplace(Fireplace fireplace, FireplaceBaseline baseline)
    {
        if (!_infiniteTorches.Value && !_infiniteFires.Value)
        {
            baseline.Restore(fireplace);
            return;
        }

        switch (Classify(fireplace))
        {
            case FireSourceKind.Torch:
                // StationAutomation checks this same public flag and skips infinite sources.
                fireplace.m_infiniteFuel = _infiniteTorches.Value || baseline.InfiniteFuel;
                break;
            case FireSourceKind.Fire:
                fireplace.m_infiniteFuel = _infiniteFires.Value || baseline.InfiniteFuel;
                break;
            default:
                baseline.Restore(fireplace);
                LogUnmatchedPrefab(fireplace);
                break;
        }
    }

    private static FireSourceKind Classify(Fireplace fireplace)
    {
        string fuelName = fireplace.m_fuelItem?.m_itemData?.m_shared?.m_name ?? string.Empty;
        if (Contains(fuelName, "resin"))
        {
            return FireSourceKind.Torch;
        }

        if (Contains(fuelName, "wood"))
        {
            return FireSourceKind.Fire;
        }

        string prefabName = GetPrefabName(fireplace);
        string displayName = fireplace.m_name ?? string.Empty;
        if (IsTorchName(prefabName) || IsTorchName(displayName))
        {
            return FireSourceKind.Torch;
        }

        if (IsFireName(prefabName) || IsFireName(displayName))
        {
            return FireSourceKind.Fire;
        }

        return FireSourceKind.Unknown;
    }

    private void LogUnmatchedPrefab(Fireplace fireplace)
    {
        string prefabName = GetPrefabName(fireplace);
        string logKey = string.IsNullOrEmpty(prefabName) ? "<empty>" : prefabName;
        if (!_loggedUnmatchedPrefabs.Add(logKey))
        {
            return;
        }

        string fuelName = fireplace.m_fuelItem?.m_itemData?.m_shared?.m_name ?? "<none>";
        _log.Warning(
            $"FireSource saw unmatched Fireplace prefab '{logKey}' with fuel '{fuelName}'; " +
            "its fuel behavior was left unchanged.");
    }

    private static string GetPrefabName(Fireplace fireplace)
    {
        string prefabName = fireplace.gameObject.name ?? string.Empty;
        const string cloneSuffix = "(Clone)";
        if (prefabName.EndsWith(cloneSuffix, StringComparison.Ordinal))
        {
            prefabName = prefabName.Substring(0, prefabName.Length - cloneSuffix.Length);
        }

        return prefabName;
    }

    private static bool IsTorchName(string name)
    {
        return Contains(name, "torch") ||
               Contains(name, "sconce") ||
               Contains(name, "brazier");
    }

    private static bool IsFireName(string name)
    {
        return Contains(name, "campfire") ||
               Contains(name, "fire_pit") ||
               Contains(name, "firepit") ||
               Contains(name, "hearth") ||
               Contains(name, "bonfire");
    }

    private static bool Contains(string value, string part)
    {
        return value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private enum FireSourceKind
    {
        Unknown,
        Torch,
        Fire,
    }

    private sealed class FireplaceBaseline
    {
        public FireplaceBaseline(Fireplace fireplace)
        {
            Fireplace = new WeakReference<Fireplace>(fireplace);
            InfiniteFuel = fireplace.m_infiniteFuel;
        }

        public WeakReference<Fireplace> Fireplace { get; }

        public bool InfiniteFuel { get; }

        public void Restore(Fireplace fireplace)
        {
            fireplace.m_infiniteFuel = InfiniteFuel;
        }
    }
}

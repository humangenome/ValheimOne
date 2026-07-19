using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public interface IFeatureModule
{
    string Name { get; }

    string Section { get; }

    bool IsEnabled { get; }

    FeatureClassification Classification { get; }

    void ApplyPatches(Harmony harmony);
}

using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public interface IFeatureModule
{
    string Name { get; }

    string Section { get; }

    bool IsEnabled { get; }

    FeatureClassification Classification { get; }

    // Modules install patches once during plugin startup, even when their feature is disabled.
    // Every patch body must read IsEnabled at call time and return immediately when false. This
    // keeps server-pushed configuration a data-only overlay and allows it to enable a feature
    // without an unsafe mid-session Harmony unpatch/repatch cycle.
    void ApplyPatches(Harmony harmony);
}

using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;
using ValheimOne.Modules;
using ValheimOne.Networking;

namespace ValheimOne;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ValheimOnePlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.humangenome.valheimone";
    public const string PluginName = "ValheimOne";
    public const string PluginVersion = VersionInfo.PluginVersion;

    private Harmony? _harmony;
    private ConfigHotReloadWatcher? _configWatcher;
    private ModLogger? _log;

    private void Awake()
    {
        _log = new ModLogger(Logger);
        LogStartupBanner(_log);

        string configPath = Path.Combine(Paths.ConfigPath, "valheimone.cfg");
        var settings = new ValheimOneConfig(configPath);
        IReadOnlyList<IFeatureModule> modules = new IFeatureModule[]
        {
            new PlayerCarryWeightModule(settings.Features),
        };

        settings.WriteDefaultsIfNeeded();

        _harmony = new Harmony(PluginGuid);
        foreach (IFeatureModule module in modules)
        {
            if (!module.IsEnabled)
            {
                _log.Debug($"Feature disabled: {module.Section}");
                continue;
            }

            module.ApplyPatches(_harmony);
            _log.Info($"Feature enabled: {module.Section} ({module.Classification})");
        }

        _configWatcher = new ConfigHotReloadWatcher(configPath, _log);
        _configWatcher.Start();
        _log.Info($"Configuration: {configPath}");
    }

    private void OnDestroy()
    {
        _configWatcher?.Dispose();
        _configWatcher = null;

        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    private static void LogStartupBanner(ModLogger log)
    {
        string? detectedVersion = GameVersionDetector.TryDetect(log);
        string detectedText = detectedVersion ?? "unavailable";

        log.Info(
            $"{PluginName} v{VersionInfo.PluginVersion} starting | " +
            $"Valheim detected: {detectedText} | supported: {VersionInfo.SupportedGameVersion} | " +
            $"schema: {VersionInfo.NetworkConfigSchema}");

        if (detectedVersion != null &&
            detectedVersion.IndexOf(VersionInfo.SupportedGameVersion, StringComparison.OrdinalIgnoreCase) < 0)
        {
            log.Warning(
                $"This build targets Valheim {VersionInfo.SupportedGameVersion}; " +
                $"detected {detectedVersion}. Disabled features remain unpatched.");
        }
    }
}

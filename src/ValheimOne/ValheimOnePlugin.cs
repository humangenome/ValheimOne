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
    private IVersionHandshake? _versionHandshake;
    private ModLogger? _log;

    private void Awake()
    {
        _log = new ModLogger(Logger);
        LogStartupBanner(_log);

        string configPath = Path.Combine(Paths.ConfigPath, "valheimone.cfg");
        var settings = new ValheimOneConfig(configPath);
        var serverConfig = new ServerConfig(settings.Features);
        IReadOnlyList<IFeatureModule> modules = new IFeatureModule[]
        {
            new PlayerModule(settings.Features),
            new PlayerStaminaModule(settings.Features),
            new BuildingQoLModule(settings.Features),
            new FoodDurationModule(settings.Features),
            new ItemTweaksModule(settings.Features),
            new ItemDropMultiplierModule(settings.Features),
            new GatheringModule(settings.Features),
            new DayNightLengthModule(settings.Features),
            new BeehiveModule(settings.Features),
            new FermenterModule(settings.Features),
            new SapCollectorModule(settings.Features),
            new WardsModule(settings.Features),
            new PortalsModule(settings.Features),
            new CraftFromChestModule(settings.Features),
            new StationAutomationModule(settings.Features),
            new ExperienceRatesModule(settings.Features),
            new DeathPenaltyModule(settings.Features),
            new ValheimOne.LiveMap.LiveMapModule(settings.Features),
        };

        settings.WriteDefaultsIfNeeded();

        _harmony = new Harmony(PluginGuid);
        foreach (IFeatureModule module in modules)
        {
            module.ApplyPatches(_harmony);
            string state = module.IsEnabled ? "enabled" : "disabled";
            _log.Info(
                $"Feature patches ready: {module.Section} ({module.Classification}, {state}).");
        }

        _versionHandshake = new VersionHandshake(settings, serverConfig, _log);
        _versionHandshake.Initialize(_harmony);

        _configWatcher = new ConfigHotReloadWatcher(configPath, _log);
        _configWatcher.Start();
        _log.Info($"Configuration: {configPath}");
    }

    private void OnDestroy()
    {
        _configWatcher?.Dispose();
        _configWatcher = null;

        _versionHandshake?.Shutdown();
        _versionHandshake = null;

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
                $"detected {detectedVersion}. Verify compatibility before enabling gameplay features.");
        }
    }
}

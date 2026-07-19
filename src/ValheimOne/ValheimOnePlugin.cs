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
        var mapSharingModule = new MapSharingModule(settings.Features, _log);
        IReadOnlyList<IFeatureModule> modules = new IFeatureModule[]
        {
            new PlayerModule(settings.Features),
            new PlayerStaminaModule(settings.Features),
            new BuildingQoLModule(settings.Features),
            new StructuralIntegrityModule(settings.Features),
            new FoodDurationModule(settings.Features),
            new ItemTweaksModule(settings.Features),
            new ItemDropMultiplierModule(settings.Features),
            new GatheringModule(settings.Features),
            new TamesModule(settings.Features),
            new WorldEventsModule(settings.Features),
            new DayNightLengthModule(settings.Features),
            new TraderModule(settings.Features),
            new BeehiveModule(settings.Features),
            new FireSourceModule(settings.Features, _log),
            new FermenterModule(settings.Features),
            new SapCollectorModule(settings.Features),
            new ProductionSpeedsModule(settings.Features, _log),
            new ContainerSizesModule(settings.Features, _log),
            new WardsModule(settings.Features),
            new PortalsModule(settings.Features),
            new CraftFromChestModule(settings.Features),
            new StationAutomationModule(settings.Features),
            new CookingStationModule(settings.Features),
            new ExperienceRatesModule(settings.Features),
            new DeathPenaltyModule(settings.Features),
            mapSharingModule,
            new ValheimOne.LiveMap.LiveMapModule(settings.Features),
        };

        settings.WriteDefaultsIfNeeded();

        _harmony = new Harmony(PluginGuid);
        List<string>? contractFailures = ContractDiagnostics.IsEnabled
            ? new List<string>()
            : null;
        int successfulModules = 0;
        foreach (IFeatureModule module in modules)
        {
            try
            {
                module.ApplyPatches(_harmony);
                successfulModules++;
                string state = module.IsEnabled ? "enabled" : "disabled";
                _log.Info(
                    $"Feature patches ready: {module.Section} ({module.Classification}, {state}).");
            }
            catch (Exception exception)
            {
                string failure = ContractDiagnostics.DescribePatchFailure(module, exception);
                contractFailures?.Add(failure);
                _log.Error(
                    $"Feature patch application failed: {failure} " +
                    $"({exception.GetType().Name}: {ContractDiagnostics.SingleLineMessage(exception)}). " +
                    "Continuing with remaining modules.");
            }
        }

        _versionHandshake = new VersionHandshake(
            settings,
            serverConfig,
            _log,
            new IVersionHandshakeExtension[] { mapSharingModule });
        _versionHandshake.Initialize(_harmony);

        if (ContractDiagnostics.IsEnabled)
        {
            ContractDiagnostics.Initialize(
                _harmony,
                _log,
                modules,
                successfulModules,
                contractFailures!);
        }

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

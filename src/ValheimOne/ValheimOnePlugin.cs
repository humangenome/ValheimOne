using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using ValheimOne.Configuration;
using ValheimOne.Discord;
using ValheimOne.Infrastructure;
using ValheimOne.Modules;
using ValheimOne.Networking;

namespace ValheimOne;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ValheimOnePlugin : BaseUnityPlugin
{
    private const int ConfigReloadDebounceMilliseconds = 750;

    public const string PluginGuid = "com.humangenome.valheimone";
    public const string PluginName = "ValheimOne";
    public const string PluginVersion = VersionInfo.PluginVersion;

    private Harmony? _harmony;
    private ConfigHotReloadWatcher? _configWatcher;
    private IVersionHandshake? _versionHandshake;
    private DiscordModule? _discordModule;
    private ValheimOneConfig? _settings;
    private ModLogger? _log;

    private void Awake()
    {
        _log = new ModLogger(Logger);
        LogStartupBanner(_log);

        string configPath = Path.Combine(Paths.ConfigPath, "valheimone.cfg");
        var settings = new ValheimOneConfig(configPath);
        _settings = settings;
        var serverConfig = new ServerConfig(settings.Features);
        var mapSharingModule = new MapSharingModule(settings.Features, _log);
        var discordModule = new DiscordModule(settings.Features, _log);
        _discordModule = discordModule;
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
            new ValheimOne.Query.QueryModule(settings.Features),
            discordModule,
            new ServerHostModule(serverConfig),
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
                settings.Features.RecordPatchSuccess(module.Section);
                successfulModules++;
                string state = module.IsEnabled ? "enabled" : "disabled";
                _log.Info(
                    $"Feature patches ready: {module.Section} ({module.Classification}, {state}).");
            }
            catch (Exception exception)
            {
                string failure = ContractDiagnostics.DescribePatchFailure(module, exception);
                settings.Features.RecordPatchFailure(module.Section, failure);
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

    private void Update()
    {
        ConfigHotReloadWatcher? watcher = _configWatcher;
        ValheimOneConfig? settings = _settings;
        ModLogger? log = _log;
        if (watcher == null || settings == null || log == null ||
            !watcher.TryConsumeChange(ConfigReloadDebounceMilliseconds))
        {
            return;
        }

        Dictionary<IConfigEntry, string> beforeValues = SnapshotEffectiveValues(settings.Features);
        try
        {
            settings.File.Reload();
        }
        catch (Exception exception)
        {
            log.Warning(
                "Config hot-reload failed; effective values were not reapplied: " +
                $"{exception.GetType().Name}: {ContractDiagnostics.SingleLineMessage(exception)}");
            return;
        }

        Dictionary<IConfigEntry, string> afterValues = SnapshotEffectiveValues(settings.Features);
        bool anyChanged = false;
        foreach (FeatureDefinition feature in settings.Features.Features)
        {
            foreach (IConfigEntry entry in feature.Keys)
            {
                string beforeValue = beforeValues[entry];
                string afterValue = afterValues[entry];
                if (string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
                {
                    continue;
                }

                anyChanged = true;
                string featureGateNote = ReferenceEquals(entry, feature.Enabled)
                    ? " (feature gate — patches stay installed; enabling/disabling applies live, " +
                      "new patch topology requires restart)"
                    : string.Empty;
                if (entry.Definition.IsSensitive)
                {
                    log.Info(
                        $"Config hot-reload: [{entry.Definition.Section}] " +
                        $"{entry.Definition.Name}: value changed (applied live){featureGateNote}");
                }
                else
                {
                    log.Info(
                        $"Config hot-reload: [{entry.Definition.Section}] {entry.Definition.Name}: " +
                        $"{beforeValue} -> {afterValue} (applied live){featureGateNote}");
                }
            }
        }

        if (anyChanged)
        {
            settings.Features.NotifyEffectiveValuesChanged();
        }
        else
        {
            log.Debug("Config hot-reload: config file touched; no effective changes");
        }
    }

    private void OnDestroy()
    {
        _configWatcher?.Dispose();
        _configWatcher = null;

        _discordModule?.Shutdown();
        _discordModule = null;
        _settings = null;

        _versionHandshake?.Shutdown();
        _versionHandshake = null;

        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    private static Dictionary<IConfigEntry, string> SnapshotEffectiveValues(FeatureRegistry registry)
    {
        var values = new Dictionary<IConfigEntry, string>();
        foreach (FeatureDefinition feature in registry.Features)
        {
            foreach (IConfigEntry entry in feature.Keys)
            {
                values.Add(entry, entry.GetSerializedValue());
            }
        }

        return values;
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

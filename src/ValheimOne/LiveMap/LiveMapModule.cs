using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;
using ValheimOne.Modules;

namespace ValheimOne.LiveMap;

public sealed class LiveMapModule : IFeatureModule
{
    private readonly FeatureDefinition _feature;
    private readonly LiveMapConfig _config;
    private readonly ModLogger _log;

    public LiveMapModule(FeatureRegistry registry)
    {
        _log = new ModLogger(BepInEx.Logging.Logger.CreateLogSource("ValheimOne.LiveMap"));
        _feature = registry.Register(Name, Section, Classification);

        ConfigEntryInt port = _feature.Int(
            "Port",
            8790,
            "TCP port for the live-map HTTP server.");
        ConfigEntryInt textureSize = _feature.Int(
            "TextureSize",
            2048,
            "World-map texture size. Power-of-two values of 2048 or 4096 are recommended.");
        ConfigEntryFloat playerUpdateSeconds = _feature.Float(
            "PlayerUpdateSeconds",
            2f,
            "Seconds between server-side player snapshot updates.");
        ConfigEntryBool adminSeesAll = _feature.Bool(
            "AdminSeesAll",
            false,
            "When enabled, the player API includes players who disabled in-game public positioning.");

        BindStringSettings(
            registry,
            out ConfigEntry<string>? bindIp,
            out ConfigEntry<string>? accessToken,
            out ConfigEntry<string>? fogMode);
        _config = new LiveMapConfig(
            port,
            textureSize,
            playerUpdateSeconds,
            adminSeesAll,
            bindIp,
            accessToken,
            fogMode);
    }

    public string Name => "Live map";

    public string Section => "LiveMap";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.ServerAuthoritative;

    public void ApplyPatches(Harmony harmony)
    {
        if (LiveMapBehaviour.Instance != null)
        {
            _log.Warning("[LiveMap] behaviour already exists; skipping duplicate initialization.");
            return;
        }

        var host = new GameObject("ValheimOne.LiveMap")
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        UnityEngine.Object.DontDestroyOnLoad(host);
        LiveMapBehaviour.Initialize(host, _config, _log, () => _feature.Enabled.Value);
    }

    private void BindStringSettings(
        FeatureRegistry registry,
        out ConfigEntry<string>? bindIp,
        out ConfigEntry<string>? accessToken,
        out ConfigEntry<string>? fogMode)
    {
        bindIp = null;
        accessToken = null;
        fogMode = null;

        try
        {
            // Temporary seam until the configuration framework grows typed string-key support.
            ConfigFile? configFile = null;
            try
            {
                FieldInfo? settingsField = typeof(FeatureRegistry).GetField(
                    "_settings",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var settings = settingsField?.GetValue(registry) as ValheimOneConfig;
                configFile = settings?.File;
            }
            catch (Exception)
            {
                // Fall through to the legacy FeatureRegistry shape.
            }

            if (configFile == null)
            {
                FieldInfo? configField = typeof(FeatureRegistry).GetField(
                    "_config",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                configFile = configField?.GetValue(registry) as ConfigFile;
            }

            if (configFile == null)
            {
                throw new InvalidOperationException("FeatureRegistry configuration was not available.");
            }

            bindIp = configFile.Bind(
                Section,
                "BindIp",
                string.Empty,
                new ConfigDescription("IP address to bind. Empty listens on all interfaces with a localhost fallback."));
            accessToken = configFile.Bind(
                Section,
                "AccessToken",
                string.Empty,
                new ConfigDescription("Optional token required as ?token= or X-LiveMap-Token on every HTTP request."));
            fogMode = configFile.Bind(
                Section,
                "FogMode",
                "full",
                new ConfigDescription("Reserved fog-of-war mode. P1 renders the full map."));
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] string settings could not be bound ({exception.GetType().Name}); " +
                "using BindIp='', AccessToken='', and FogMode='full'.");
        }
    }
}

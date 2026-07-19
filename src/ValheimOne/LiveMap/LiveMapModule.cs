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
        ConfigEntryString bindIp = _feature.String(
            "BindIp",
            string.Empty,
            "IP address to bind. Empty listens on all interfaces with a localhost fallback.");
        ConfigEntryString accessToken = _feature.String(
            "AccessToken",
            string.Empty,
            "Optional token required as ?token= or X-LiveMap-Token on every HTTP request.");
        ConfigEntryString fogMode = _feature.String(
            "FogMode",
            "off",
            "Fog of war for the map view: off (full map), trails (areas players have traveled), " +
            "explored (player trails plus cartography-table exploration data).");
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
}

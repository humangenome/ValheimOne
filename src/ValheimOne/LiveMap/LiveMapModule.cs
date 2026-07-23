using System;
using HarmonyLib;
using UnityEngine;
using ValheimOne.ActivityLog;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;
using ValheimOne.Modules;

namespace ValheimOne.LiveMap;

public sealed class LiveMapModule : IFeatureModule
{
    private readonly FeatureDefinition _feature;
    private readonly LiveMapConfig _config;
    private readonly ActivityLogModule _activityLog;
    private readonly ServerSessionEventSource _sessionEvents;
    private readonly ModLogger _log;
    private readonly FeatureRegistry _registry;
    private readonly string _dataDirectory;
    private readonly object _storeLock = new object();
    private WebPinStore? _webPinStore;
    private ActivityHeatmap? _activityHeatmap;
    private LeaderboardStore? _leaderboardStore;
    private LeaderboardBehaviour? _leaderboardBehaviour;
    private bool _shutdown;

    public LiveMapModule(
        FeatureRegistry registry,
        ActivityLogModule activityLog,
        ServerSessionEventSource sessionEvents,
        string dataDirectory)
    {
        _registry = registry;
        _activityLog = activityLog;
        _sessionEvents = sessionEvents;
        _dataDirectory = dataDirectory;
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
        ConfigEntryInt ghostRetentionDays = _feature.Int(
            "GhostRetentionDays",
            14,
            "Days to retain last-seen player positions and playtime, clamped to 1..365.");
        ConfigEntryBool adminSeesAll = _feature.Bool(
            "AdminSeesAll",
            false,
            "Retained for compatibility; the token-authenticated admin view always shows all players.");
        ConfigEntryString bindIp = _feature.String(
            "BindIp",
            string.Empty,
            "IP address to bind. Empty listens on all interfaces with a localhost fallback.");
        ConfigEntryString accessToken = _feature.String(
            "AccessToken",
            string.Empty,
            "Optional token granting the admin map view as ?token= or X-LiveMap-Token.");
        ConfigEntryString shareToken = _feature.String(
            "ShareToken",
            string.Empty,
            "Optional token granting a shared spectator map view as ?token= or " +
            "X-LiveMap-Token: player names, all layers, and follow, but never console or admin " +
            "actions.");
        ConfigEntryBool publicView = _feature.Bool(
            "PublicView",
            true,
            "Serve a read-only public map view to tokenless requests.");
        ConfigEntryBool publicPins = _feature.Bool(
            "PublicPins",
            false,
            "Expose player-placed cartography pins on the public map without author names.");
        ConfigEntryBool sharedPinEditing = _feature.Bool(
            "SharedPinEditing",
            true,
            "Shared-view visitors may add web pins and edit/delete their own; " +
            "admins always edit everything.");
        ConfigEntryBool publicWebPins = _feature.Bool(
            "PublicWebPins",
            false,
            "Expose web pins read-only to the public view.");
        ConfigEntryBool mirrorChat = _feature.Bool(
            "MirrorChat",
            false,
            "Mirror player Say and Shout chat onto authenticated live-map views. " +
            "Disabled by default because chat is player speech and the server owner must " +
            "explicitly opt in.");
        ConfigEntryBool respectInGameVisibility = _feature.Bool(
            "RespectInGameVisibility",
            true,
            "Omit players who disabled in-game position sharing from shared and public views. " +
            "The token-authenticated admin view always shows all players.");
        ConfigEntryBool publicShowPlayerNames = _feature.Bool(
            "PublicShowPlayerNames",
            true,
            "Show player names on the public view. " +
            "When disabled, public players render as anonymous markers.");
        ConfigEntryBool entityLayer = _feature.Bool(
            "EntityLayer",
            false,
            "Serve ships, carts, and portals as a toggleable admin map layer.");
        ConfigEntryBool resourceLayers = _feature.Bool(
            "ResourceLayers",
            true,
            "Serve request-gated ore and forage layers on shared and admin maps.");
        ConfigEntryString fogMode = _feature.String(
            "FogMode",
            "off",
            "Fog of war for the map view: off (full map), trails (areas players have traveled), " +
            "explored (player trails plus cartography-table exploration data).");
        ConfigEntryBool consoleEnabled = _feature.Bool(
            "ConsoleEnabled",
            false,
            "Enable the web admin console API and dashboard console tab.");
        ConfigEntryString consoleWhitelist = _feature.String(
            "ConsoleWhitelist",
            "vo save kick ban unban banned lodbias sleep",
            "Space-separated list of console commands the web console may execute. " +
            "The ValheimOne vo command family is always allowed even when omitted.");
        ConfigEntryBool allowAllCommands = _feature.Bool(
            "AllowAllCommands",
            false,
            "Allow the web console to execute any console command, bypassing ConsoleWhitelist.");
        ConfigEntryInt consoleLogLines = _feature.Int(
            "ConsoleLogLines",
            500,
            "Server log ring buffer size for the web console (clamped 50..5000).");
        ConfigEntryBool statusPublic = _feature.Bool(
            "StatusPublic",
            true,
            "Serve /api/status without an access token even when the map itself is token-locked " +
            "(for hosting-panel queries).");
        _config = new LiveMapConfig(
            port,
            textureSize,
            playerUpdateSeconds,
            ghostRetentionDays,
            adminSeesAll,
            bindIp,
            accessToken,
            shareToken,
            publicView,
            publicPins,
            sharedPinEditing,
            publicWebPins,
            mirrorChat,
            respectInGameVisibility,
            publicShowPlayerNames,
            entityLayer,
            resourceLayers,
            fogMode,
            consoleEnabled,
            consoleWhitelist,
            allowAllCommands,
            consoleLogLines,
            statusPublic);
        activityLog.ConfigureGhostRetention(() => _config.GhostRetentionDays);
        registry.EffectiveValuesChanged += MapPingPatch.RefreshChatConfiguration;
        registry.EffectiveValuesChanged += SynchronizeWebPinStore;
        SynchronizeWebPinStore();
        VoCommands.Initialize(registry, _config, activityLog, _log);
    }

    public string Name => "Live map";

    public string Section => "LiveMap";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.ServerAuthoritative;

    internal WebPinStore? WebPinStore
    {
        get
        {
            lock (_storeLock)
            {
                return _webPinStore;
            }
        }
    }

    internal ActivityHeatmap? ActivityHeatmap
    {
        get
        {
            lock (_storeLock)
            {
                return _activityHeatmap;
            }
        }
    }

    internal LeaderboardStore? LeaderboardStore
    {
        get
        {
            lock (_storeLock)
            {
                return _leaderboardStore;
            }
        }
    }

    public void ApplyPatches(Harmony harmony)
    {
        VoCommands.ApplyPatches(harmony);
        MapPingPatch.ApplyPatches(
            harmony,
            () => _feature.Enabled.Value,
            () => _config.MirrorChat,
            _log);
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
        _leaderboardBehaviour = LeaderboardBehaviour.Initialize(
            host,
            () => LeaderboardStore,
            _sessionEvents);
        LiveMapBehaviour.Initialize(
            host,
            _config,
            _activityLog,
            _log,
            _dataDirectory,
            () => _feature.Enabled.Value,
            () => WebPinStore,
            () => ActivityHeatmap,
            () => LeaderboardStore);
    }

    public void Shutdown()
    {
        WebPinStore? store;
        ActivityHeatmap? activityHeatmap;
        LeaderboardStore? leaderboardStore;
        LeaderboardBehaviour? leaderboardBehaviour;
        lock (_storeLock)
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _registry.EffectiveValuesChanged -= SynchronizeWebPinStore;
            store = _webPinStore;
            _webPinStore = null;
            activityHeatmap = _activityHeatmap;
            _activityHeatmap = null;
            leaderboardStore = _leaderboardStore;
            _leaderboardStore = null;
            leaderboardBehaviour = _leaderboardBehaviour;
            _leaderboardBehaviour = null;
        }

        leaderboardBehaviour?.StopPermanently();
        store?.Dispose();
        activityHeatmap?.Dispose();
        leaderboardStore?.Dispose();
    }

    private void SynchronizeWebPinStore()
    {
        WebPinStore? storeToDispose = null;
        ActivityHeatmap? heatmapToDispose = null;
        LeaderboardStore? leaderboardToDispose = null;
        lock (_storeLock)
        {
            if (_shutdown)
            {
                return;
            }

            if (_feature.Enabled.Value)
            {
                _webPinStore ??= new WebPinStore(_dataDirectory, _log);
                _activityHeatmap ??= new ActivityHeatmap(_dataDirectory, _log);
                _leaderboardStore ??= new LeaderboardStore(_dataDirectory, _log);
            }
            else
            {
                storeToDispose = _webPinStore;
                _webPinStore = null;
                heatmapToDispose = _activityHeatmap;
                _activityHeatmap = null;
                leaderboardToDispose = _leaderboardStore;
                _leaderboardStore = null;
            }
        }

        storeToDispose?.Dispose();
        heatmapToDispose?.Dispose();
        leaderboardToDispose?.Dispose();
    }
}

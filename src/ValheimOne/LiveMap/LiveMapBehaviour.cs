using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapBehaviour : MonoBehaviour
{
    private LiveMapConfig? _config;
    private ModLogger? _log;
    private Func<bool>? _enabledCheck;
    private WorldMapRenderer? _renderer;
    private FogTracker? _fogTracker;
    private MapTableReader? _mapTableReader;
    private LiveMapHttpServer? _httpServer;
    private LogRingBuffer? _logRingBuffer;
    private ConsoleBridge? _consoleBridge;
    private volatile LiveMapSnapshot _snapshot = LiveMapSnapshot.Empty;
    private volatile PoiCatalog _poiCatalog = PoiCatalog.Empty;
    private volatile string _fogMode = "off";
    private string _worldName = string.Empty;
    private float _nextPlayerUpdate;
    private float _nextFogUpdate;
    private bool _poiCatalogBuilt;
    private bool _started;
    private bool _stopped;

    public static LiveMapBehaviour? Instance { get; private set; }

    internal LogRingBuffer? LogRingBuffer => _logRingBuffer;

    internal ConsoleBridge? ConsoleBridge => _consoleBridge;

    public static void Initialize(
        GameObject host,
        LiveMapConfig config,
        ModLogger log,
        Func<bool> enabledCheck)
    {
        var behaviour = host.AddComponent<LiveMapBehaviour>();
        behaviour._config = config;
        behaviour._log = log;
        behaviour._enabledCheck = enabledCheck;
        Instance = behaviour;
    }

    private void Update()
    {
        if (_stopped || _config == null || _log == null || _enabledCheck == null)
        {
            return;
        }

        if (!_enabledCheck())
        {
            if (_started)
            {
                StopServices(false);
            }

            return;
        }

        RefreshFogMode();

        if (!_started)
        {
            TryStart();
            return;
        }

        _consoleBridge?.Pump();

        float now = Time.realtimeSinceStartup;
        _mapTableReader?.Tick(now, _fogMode == "explored");
        if (now >= _nextPlayerUpdate)
        {
            RefreshSnapshot();
            _nextPlayerUpdate = now + Math.Max(0.25f, _config.PlayerUpdateSeconds);
        }

        if (now >= _nextFogUpdate)
        {
            _fogTracker?.Tick(_snapshot.Players);
            _nextFogUpdate = now + 2f;
        }
    }

    private void TryStart()
    {
        LiveMapConfig? config = _config;
        ModLogger? log = _log;
        if (config == null || log == null)
        {
            return;
        }

        ZNet? network = ZNet.instance;
        WorldGenerator? generator = WorldGenerator.instance;
        ZoneSystem? zoneSystem = ZoneSystem.instance;
        if (network == null || generator == null || zoneSystem == null ||
            !network.IsServer() || !zoneSystem.LocationsGenerated)
        {
            return;
        }

        World? world = GameAccess.GetWorld();
        if (world == null)
        {
            return;
        }

        if (config.ConsoleEnabled && _logRingBuffer == null)
        {
            try
            {
                _logRingBuffer = new LogRingBuffer(config.ConsoleLogLines);
                _logRingBuffer.Start();
                _consoleBridge = new ConsoleBridge(_logRingBuffer, log);
            }
            catch (Exception exception)
            {
                _consoleBridge?.Stop();
                _consoleBridge = null;
                _logRingBuffer?.Stop();
                _logRingBuffer = null;
                log.Warning(
                    $"[LiveMap] web console could not start: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        _worldName = string.IsNullOrWhiteSpace(world.m_name) ? "world" : world.m_name;
        string seedName = world.m_seedName ?? string.Empty;
        string gameVersion = GameVersionDetector.TryDetect(log) ?? "unknown";
        int textureSize = WorldMapRenderer.NormalizeTextureSize(config.TextureSize);
        if (textureSize != config.TextureSize)
        {
            log.Warning(
                $"[LiveMap] TextureSize {config.TextureSize} is invalid; using {textureSize}. " +
                "Choose a power of two at least 256.");
        }

        int port = config.Port;
        if (port < 1 || port > 65535)
        {
            log.Warning($"[LiveMap] Port {port} is invalid; using 8790.");
            port = 8790;
        }

        if (!_poiCatalogBuilt)
        {
            PoiCatalog poiCatalog = PoiCatalog.Build(zoneSystem);
            _poiCatalog = poiCatalog;
            _poiCatalogBuilt = true;
            log.Info(
                $"[LiveMap] POI catalog: {poiCatalog.TotalLocations} locations, " +
                $"{poiCatalog.ServedPois.Count} served");
        }

        _renderer = new WorldMapRenderer(
            generator,
            world.m_seed,
            seedName,
            _worldName,
            gameVersion,
            textureSize,
            log);
        _renderer.Start();
        _fogTracker = new FogTracker(_renderer.CacheDirectory, log);
        _mapTableReader = new MapTableReader(_fogTracker, log);
        _mapTableReader.Start();

        RefreshSnapshot();
        _fogTracker.Tick(_snapshot.Players);
        _httpServer = new LiveMapHttpServer(
            port,
            config.BindIp,
            config.AccessToken,
            config.AdminSeesAll,
            config.PublicView,
            config.PublicShowPlayerNames,
            () => _snapshot,
            () => _poiCatalog,
            () => _mapTableReader?.Snapshot ?? MapTableSnapshot.Empty,
            () => _fogMode,
            _fogTracker,
            _renderer,
            config,
            _consoleBridge,
            _logRingBuffer,
            log);
        _httpServer.Start();

        float now = Time.realtimeSinceStartup;
        _nextPlayerUpdate = now + Math.Max(0.25f, config.PlayerUpdateSeconds);
        _nextFogUpdate = now + 2f;
        _started = true;
    }

    private void RefreshFogMode()
    {
        LiveMapConfig? config = _config;
        if (config == null)
        {
            return;
        }

        _fogMode = config.FogMode;
    }

    private void RefreshSnapshot()
    {
        ZNet? network = ZNet.instance;
        if (network == null)
        {
            return;
        }

        var players = new List<LiveMapPlayerSnapshot>();
        List<ZNetPeer> peers = GameAccess.GetPeers(network);
        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer peer = peers[index];
            if (peer == null || peer.m_characterID.IsNone())
            {
                continue;
            }

            Vector3 position = peer.m_refPos;
            players.Add(new LiveMapPlayerSnapshot(
                peer.m_playerName ?? string.Empty,
                position.x,
                position.y,
                position.z,
                peer.m_publicRefPos));
        }

        double seconds = network.GetTimeSeconds();
        float dayLength = EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0f
            ? EnvMan.instance.m_dayLengthSec
            : 1800f;
        int day = (int)Math.Floor(seconds / dayLength);
        float timeOfDay = (float)((seconds % dayLength) / dayLength);
        if (timeOfDay < 0f)
        {
            timeOfDay += 1f;
        }

        _snapshot = new LiveMapSnapshot(
            _worldName,
            _worldName,
            day,
            timeOfDay,
            players.ToArray());
    }

    private void OnApplicationQuit()
    {
        StopServices(true);
    }

    private void OnDestroy()
    {
        StopServices(true);
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    private void StopServices(bool permanently)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = permanently;
        _httpServer?.Stop();
        _httpServer = null;
        _consoleBridge?.Stop();
        _consoleBridge = null;
        _logRingBuffer?.Stop();
        _logRingBuffer = null;
        _mapTableReader?.Stop();
        _mapTableReader = null;
        _fogTracker?.Stop();
        _fogTracker = null;
        _renderer?.Stop();
        _renderer = null;
        _started = false;
    }
}

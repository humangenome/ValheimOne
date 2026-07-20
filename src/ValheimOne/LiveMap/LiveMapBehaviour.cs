using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapBehaviour : MonoBehaviour
{
    private const float IdleUpdateSeconds = 30f;
    private const float FogUpdateSeconds = 2f;

    private LiveMapConfig? _config;
    private ModLogger? _log;
    private Func<bool>? _enabledCheck;
    private WorldMapRenderer? _renderer;
    private FogTracker? _fogTracker;
    private MapTableReader? _mapTableReader;
    private EntityTracker? _entityTracker;
    private LiveMapHttpServer? _httpServer;
    private LogRingBuffer? _logRingBuffer;
    private ConsoleBridge? _consoleBridge;
    private volatile LiveMapSnapshot _snapshot = LiveMapSnapshot.Empty;
    private volatile PoiCatalog _poiCatalog = PoiCatalog.Empty;
    private volatile string _fogMode = "off";
    private readonly Dictionary<long, PlayerMotionState> _motion =
        new Dictionary<long, PlayerMotionState>();
    private string _worldName = string.Empty;
    private float _nextPlayerUpdate;
    private float _nextFogUpdate;
    private bool _poiCatalogBuilt;
    private bool _started;
    private bool _stopped;
    private volatile bool _idle;

    public static LiveMapBehaviour? Instance { get; private set; }

    internal LogRingBuffer? LogRingBuffer => _logRingBuffer;

    internal ConsoleBridge? ConsoleBridge => _consoleBridge;

    internal EntityMapSnapshot EntitySnapshot => _entityTracker?.Snapshot ?? EntityMapSnapshot.Empty;

    internal bool EntityTrackerReady => _entityTracker != null;

    internal string ServiceState
    {
        get
        {
            if (_stopped)
            {
                return "stopped";
            }

            if (_started)
            {
                return _httpServer?.IsRunning == true
                    ? "running (HTTP listener active)"
                    : "started (HTTP listener not active)";
            }

            return _enabledCheck?.Invoke() == true
                ? "waiting for server world initialization"
                : "disabled by [LiveMap] Enabled";
        }
    }

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
        behaviour._consoleBridge = new ConsoleBridge(null, log);
        Instance = behaviour;
    }

    internal void NoteEntitiesRequested()
    {
        _entityTracker?.NoteEntitiesRequested();
    }

    private void Update()
    {
        if (_stopped || _config == null || _log == null || _enabledCheck == null)
        {
            return;
        }

        VoCommands.PumpSessionTimes();
        _consoleBridge?.Pump();

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

        float now = Time.realtimeSinceStartup;
        ZNet? network = ZNet.instance;
        bool idle = network == null || GameAccess.GetPeers(network).Count == 0;
        bool idleChanged = idle != _idle;
        if (idleChanged)
        {
            RefreshSnapshot();
            _idle = idle;
            _nextPlayerUpdate = now + GetEffectivePlayerUpdateSeconds();
            if (!idle)
            {
                _fogTracker?.Tick(_snapshot.Players);
            }

            _nextFogUpdate = now + (idle ? IdleUpdateSeconds : FogUpdateSeconds);
        }

        _mapTableReader?.Tick(now, _fogMode == "explored");
        _entityTracker?.Tick(now);
        if (!idleChanged && now >= _nextPlayerUpdate)
        {
            RefreshSnapshot();
            _nextPlayerUpdate = now + GetEffectivePlayerUpdateSeconds();
        }

        if (!idleChanged && now >= _nextFogUpdate)
        {
            _fogTracker?.Tick(_snapshot.Players);
            _nextFogUpdate = now + (_idle ? IdleUpdateSeconds : FogUpdateSeconds);
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
                _consoleBridge?.SetRingBuffer(_logRingBuffer);
            }
            catch (Exception exception)
            {
                _consoleBridge?.SetRingBuffer(null);
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
        EntityTracker entityTracker = new EntityTracker(config, log);
        _entityTracker = entityTracker;

        _idle = GameAccess.GetPeers(network).Count == 0;
        RefreshSnapshot();
        _fogTracker.Tick(_snapshot.Players);
        entityTracker.Tick(Time.realtimeSinceStartup);
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
            () => entityTracker.Snapshot,
            entityTracker.NoteEntitiesRequested,
            () => _fogMode,
            GetEffectivePlayerUpdateSeconds,
            _fogTracker,
            _renderer,
            config,
            _consoleBridge,
            _logRingBuffer,
            log);
        _httpServer.Start();

        float now = Time.realtimeSinceStartup;
        _nextPlayerUpdate = now + GetEffectivePlayerUpdateSeconds();
        _nextFogUpdate = now + (_idle ? IdleUpdateSeconds : FogUpdateSeconds);
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

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double seconds = network.GetTimeSeconds();
        EnvMan? environmentManager = EnvMan.instance;
        float dayLength = environmentManager != null && environmentManager.m_dayLengthSec > 0f
            ? environmentManager.m_dayLengthSec
            : 1800f;
        int day = (int)Math.Floor(seconds / dayLength);
        float timeOfDay = (float)((seconds % dayLength) / dayLength);
        if (timeOfDay < 0f)
        {
            timeOfDay += 1f;
        }

        float windDirDeg = 0f;
        float windIntensity = 0f;
        if (environmentManager != null)
        {
            Vector3 windDir = environmentManager.GetWindDir();
            windDirDeg = (float)(((Math.Atan2(windDir.x, windDir.z) * 180.0 / Math.PI) + 360.0) % 360.0);
            windIntensity = Math.Max(0f, Math.Min(1f, environmentManager.GetWindIntensity()));
        }

        var players = new List<LiveMapPlayerSnapshot>();
        var presentIds = new HashSet<long>();
        List<ZNetPeer> peers = GameAccess.GetPeers(network);
        WorldGenerator? worldGenerator = WorldGenerator.instance;
        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer peer = peers[index];
            if (peer == null || peer.m_characterID.IsNone())
            {
                continue;
            }

            Vector3 position = peer.m_refPos;
            long id = peer.m_characterID.UserID;
            presentIds.Add(id);
            if (!_motion.TryGetValue(id, out PlayerMotionState? motion))
            {
                motion = new PlayerMotionState
                {
                    LastX = position.x,
                    LastZ = position.z,
                    LastUnixMs = nowMs,
                    SessionStartUnixMs = nowMs,
                    LastDay = day,
                };
                _motion.Add(id, motion);
            }
            else
            {
                if (day != motion.LastDay)
                {
                    motion.DistanceTodayM = 0f;
                    motion.LastDay = day;
                }

                double deltaSeconds = (nowMs - motion.LastUnixMs) / 1000.0;
                if (deltaSeconds >= 0.5)
                {
                    float deltaX = position.x - motion.LastX;
                    float deltaZ = position.z - motion.LastZ;
                    float distance = (float)Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
                    if (distance > 200f)
                    {
                        motion.SpeedMps = 0f;
                    }
                    else
                    {
                        float speed = distance / (float)deltaSeconds;
                        motion.SpeedMps = (0.6f * speed) + (0.4f * motion.SpeedMps);
                        if (distance >= 0.05f)
                        {
                            motion.HeadingDeg =
                                (float)(((Math.Atan2(deltaX, deltaZ) * 180.0 / Math.PI) + 360.0) % 360.0);
                        }

                        motion.DistanceTodayM += distance;
                    }

                    motion.LastX = position.x;
                    motion.LastZ = position.z;
                    motion.LastUnixMs = nowMs;
                }
            }

            string biome = worldGenerator == null
                ? string.Empty
                : worldGenerator.GetBiome(position.x, position.z).ToString();
            players.Add(new LiveMapPlayerSnapshot(
                peer.m_playerName ?? string.Empty,
                position.x,
                position.y,
                position.z,
                peer.m_publicRefPos,
                id,
                biome,
                motion.SpeedMps,
                motion.HeadingDeg,
                motion.SessionStartUnixMs,
                motion.DistanceTodayM));
        }

        var departedIds = new List<long>();
        foreach (long id in _motion.Keys)
        {
            if (!presentIds.Contains(id))
            {
                departedIds.Add(id);
            }
        }

        for (int index = 0; index < departedIds.Count; index++)
        {
            _motion.Remove(departedIds[index]);
        }

        _snapshot = new LiveMapSnapshot(
            _worldName,
            _worldName,
            day,
            timeOfDay,
            windDirDeg,
            windIntensity,
            nowMs,
            players.ToArray());
    }

    private float GetEffectivePlayerUpdateSeconds()
    {
        return _idle
            ? IdleUpdateSeconds
            : Math.Max(0.25f, _config?.PlayerUpdateSeconds ?? 0.25f);
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
        _consoleBridge?.SetRingBuffer(null);
        if (permanently)
        {
            _consoleBridge?.Stop();
            _consoleBridge = null;
        }
        _logRingBuffer?.Stop();
        _logRingBuffer = null;
        _mapTableReader?.Stop();
        _mapTableReader = null;
        _entityTracker = null;
        _fogTracker?.Stop();
        _fogTracker = null;
        _renderer?.Stop();
        _renderer = null;
        _started = false;
    }

    private sealed class PlayerMotionState
    {
        public float LastX { get; set; }

        public float LastZ { get; set; }

        public long LastUnixMs { get; set; }

        public long SessionStartUnixMs { get; set; }

        public float DistanceTodayM { get; set; }

        public int LastDay { get; set; }

        public float SpeedMps { get; set; }

        public float HeadingDeg { get; set; }
    }
}

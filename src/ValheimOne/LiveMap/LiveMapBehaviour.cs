using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapBehaviour : MonoBehaviour
{
    private LiveMapConfig? _config;
    private ModLogger? _log;
    private WorldMapRenderer? _renderer;
    private LiveMapHttpServer? _httpServer;
    private volatile LiveMapSnapshot _snapshot = LiveMapSnapshot.Empty;
    private string _worldName = string.Empty;
    private float _nextPlayerUpdate;
    private bool _started;
    private bool _stopped;

    public static LiveMapBehaviour? Instance { get; private set; }

    public static void Initialize(GameObject host, LiveMapConfig config, ModLogger log)
    {
        var behaviour = host.AddComponent<LiveMapBehaviour>();
        behaviour._config = config;
        behaviour._log = log;
        Instance = behaviour;
    }

    private void Update()
    {
        if (_stopped || _config == null || _log == null)
        {
            return;
        }

        if (!_started)
        {
            TryStart();
            return;
        }

        if (Time.realtimeSinceStartup >= _nextPlayerUpdate)
        {
            RefreshSnapshot();
            _nextPlayerUpdate = Time.realtimeSinceStartup + Math.Max(0.25f, _config.PlayerUpdateSeconds);
        }
    }

    private void TryStart()
    {
        ZNet? network = ZNet.instance;
        WorldGenerator? generator = WorldGenerator.instance;
        if (network == null || generator == null || !network.IsServer() || ZNet.m_world == null)
        {
            return;
        }

        World world = ZNet.m_world;
        _worldName = string.IsNullOrWhiteSpace(world.m_name) ? "world" : world.m_name;
        string seedName = world.m_seedName ?? string.Empty;
        string gameVersion = GameVersionDetector.TryDetect(_log) ?? "unknown";
        int textureSize = WorldMapRenderer.NormalizeTextureSize(_config.TextureSize);
        if (textureSize != _config.TextureSize)
        {
            _log.Warning(
                $"[LiveMap] TextureSize {_config.TextureSize} is invalid; using {textureSize}. " +
                "Choose a power of two at least 256.");
        }

        int port = _config.Port;
        if (port < 1 || port > 65535)
        {
            _log.Warning($"[LiveMap] Port {port} is invalid; using 8790.");
            port = 8790;
        }

        _renderer = new WorldMapRenderer(
            generator,
            world.m_seed,
            seedName,
            _worldName,
            gameVersion,
            textureSize,
            _log);
        _renderer.Start();

        RefreshSnapshot();
        _httpServer = new LiveMapHttpServer(
            port,
            _config.BindIp,
            _config.AccessToken,
            _config.AdminSeesAll,
            () => _snapshot,
            _renderer,
            _log);
        _httpServer.Start();

        _nextPlayerUpdate = Time.realtimeSinceStartup + Math.Max(0.25f, _config.PlayerUpdateSeconds);
        _started = true;
    }

    private void RefreshSnapshot()
    {
        ZNet? network = ZNet.instance;
        if (network == null)
        {
            return;
        }

        var players = new List<LiveMapPlayerSnapshot>();
        List<ZNetPeer> peers = network.m_peers;
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
        StopServices();
    }

    private void OnDestroy()
    {
        StopServices();
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    private void StopServices()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _httpServer?.Stop();
        _httpServer = null;
        _renderer?.Stop();
        _renderer = null;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.Query;

internal sealed class QueryBehaviour : MonoBehaviour
{
    private const int DefaultGamePort = 2456;
    private const float SnapshotIntervalSeconds = 2f;
    private const float IdleSnapshotIntervalSeconds = 30f;
    private const float BindRetrySeconds = 30f;

    private QueryConfig? _config;
    private ModLogger? _log;
    private Func<bool>? _enabledCheck;
    private QueryResponder? _responder;
    private volatile QuerySnapshot _snapshot = QuerySnapshot.Empty;
    private ZNet? _sessionNetwork;
    private string _gameVersion = "unknown";
    private DateTime _startTimeUtc;
    private float _nextSnapshotRefresh;
    private float _nextStartAttempt;
    private int _gamePort = DefaultGamePort;
    private int _responderPort;
    private int _lastInvalidQueryPort = int.MinValue;
    private int _deprecatedMaxPlayers;
    private bool _deprecatedMaxPlayersChecked;
    private bool _passworded;
    private bool _versionDetected;
    private bool _sessionActive;
    private bool _idle;
    private bool _stopped;

    public static QueryBehaviour? Instance { get; private set; }

    public static void Initialize(
        GameObject host,
        QueryConfig config,
        ModLogger log,
        Func<bool> enabledCheck)
    {
        var behaviour = host.AddComponent<QueryBehaviour>();
        behaviour._config = config;
        behaviour._log = log;
        behaviour._enabledCheck = enabledCheck;
        behaviour.ReadCommandLine();
        Instance = behaviour;
    }

    private void Update()
    {
        QueryConfig? config = _config;
        ModLogger? log = _log;
        if (_stopped || config == null || log == null || _enabledCheck == null)
        {
            return;
        }

        if (!_enabledCheck())
        {
            StopSession();
            return;
        }

        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer())
        {
            StopSession();
            return;
        }

        World? world = QueryGameAccess.GetWorld();
        if (world == null)
        {
            StopSession();
            return;
        }

        if (!_sessionActive || !ReferenceEquals(_sessionNetwork, network))
        {
            StopSession();
            StartSession(network, log);
        }

        float now = Time.realtimeSinceStartup;
        bool idle = QueryGameAccess.GetPeers(network).Count == 0;
        bool idleChanged = idle != _idle;
        if (idleChanged)
        {
            RefreshSnapshot(network, world, config);
            _idle = idle;
            _nextSnapshotRefresh = now + (idle
                ? IdleSnapshotIntervalSeconds
                : SnapshotIntervalSeconds);
        }
        else if (now >= _nextSnapshotRefresh)
        {
            RefreshSnapshot(network, world, config);
            _nextSnapshotRefresh = now + (_idle
                ? IdleSnapshotIntervalSeconds
                : SnapshotIntervalSeconds);
        }

        int queryPort = GetEffectiveQueryPort(config, log);
        if (_responder == null || _responderPort != queryPort)
        {
            StopResponder();
            _responder = new QueryResponder(queryPort, () => _snapshot, log);
            _responderPort = queryPort;
            _nextStartAttempt = 0f;
        }

        if (!_responder.IsRunning && now >= _nextStartAttempt)
        {
            if (!_responder.Start())
            {
                _nextStartAttempt = now + BindRetrySeconds;
            }
        }
    }

    private void StartSession(ZNet network, ModLogger log)
    {
        _sessionNetwork = network;
        _startTimeUtc = DateTime.UtcNow;
        _nextSnapshotRefresh = 0f;
        _nextStartAttempt = 0f;
        _idle = QueryGameAccess.GetPeers(network).Count == 0;
        _sessionActive = true;

        if (!_versionDetected)
        {
            _gameVersion = GameVersionDetector.TryDetect(log) ?? "unknown";
            _versionDetected = true;
        }
    }

    private void RefreshSnapshot(ZNet network, World world, QueryConfig config)
    {
        string worldName = string.IsNullOrWhiteSpace(world.m_name) ? "world" : world.m_name;
        string serverName = QueryGameAccess.GetServerName();
        if (string.IsNullOrWhiteSpace(serverName))
        {
            serverName = worldName;
        }

        int maxPlayers = Modules.ServerHostModule.EffectiveMaxPlayers();
        if (maxPlayers == Modules.ServerHostModule.VanillaPlayerLimit)
        {
            int deprecated = GetDeprecatedQueryMaxPlayers();
            if (deprecated > 0)
            {
                maxPlayers = deprecated;
            }
        }

        var playerNames = new List<string>();
        List<ZNetPeer> peers = QueryGameAccess.GetPeers(network);
        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer peer = peers[index];
            if (peer == null || peer.m_characterID.IsNone())
            {
                continue;
            }

            string playerName = config.PublicPlayerNames
                ? peer.m_playerName ?? string.Empty
                : $"Player {playerNames.Count + 1}";
            playerNames.Add(playerName);
        }

        string[] names = playerNames.ToArray();
        _snapshot = new QuerySnapshot(
            serverName,
            worldName,
            names.Length,
            names,
            maxPlayers,
            _gamePort,
            _passworded,
            _gameVersion,
            ValheimOnePlugin.PluginVersion,
            _startTimeUtc);
    }

    // Deprecated [Query] MaxPlayers fallback: the key moved to [Server] MaxPlayers. The old
    // key is no longer registered, so read it once from the raw config file and warn.
    private int GetDeprecatedQueryMaxPlayers()
    {
        if (_deprecatedMaxPlayersChecked)
        {
            return _deprecatedMaxPlayers;
        }

        _deprecatedMaxPlayersChecked = true;
        try
        {
            string configPath = Path.Combine(BepInEx.Paths.ConfigPath, "valheimone.cfg");
            if (!File.Exists(configPath))
            {
                return 0;
            }

            bool inQuerySection = false;
            foreach (string rawLine in File.ReadAllLines(configPath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inQuerySection = string.Equals(line, "[Query]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inQuerySection || !line.StartsWith("MaxPlayers", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator < 0 ||
                    !int.TryParse(
                        line.Substring(separator + 1).Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int value) ||
                    value <= 0 || value == 10)
                {
                    continue;
                }

                _deprecatedMaxPlayers = Math.Min(value, 255);
                _log?.Warning(
                    "[Query] MaxPlayers is deprecated; move the value to [Server] MaxPlayers. " +
                    "This fallback will be removed in the next release.");
                break;
            }
        }
        catch (Exception)
        {
            // A malformed config never breaks snapshot refreshes.
        }

        return _deprecatedMaxPlayers;
    }

    private int GetEffectiveQueryPort(QueryConfig config, ModLogger log)
    {
        int configuredPort = config.QueryPort;
        if (configuredPort == 0)
        {
            _lastInvalidQueryPort = int.MinValue;
            return _gamePort + 4;
        }

        if (configuredPort >= 1 && configuredPort <= 65535)
        {
            _lastInvalidQueryPort = int.MinValue;
            return configuredPort;
        }

        if (_lastInvalidQueryPort != configuredPort)
        {
            log.Warning(
                $"[Query] QueryPort {configuredPort} is invalid; using game port + 4 ({_gamePort + 4}).");
            _lastInvalidQueryPort = configuredPort;
        }

        return _gamePort + 4;
    }

    private void ReadCommandLine()
    {
        string[] args = Environment.GetCommandLineArgs();
        string portText = GetCommandLineValue(args, "-port");
        if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) &&
            port >= 1 && port <= 65531)
        {
            _gamePort = port;
        }

        _passworded = !string.IsNullOrEmpty(GetCommandLineValue(args, "-password"));
    }

    private static string GetCommandLineValue(string[] args, string name)
    {
        string prefix = name + "=";
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index] ?? string.Empty;
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument.Substring(prefix.Length);
            }

            if (!string.Equals(argument, name, StringComparison.OrdinalIgnoreCase) ||
                index + 1 >= args.Length)
            {
                continue;
            }

            string value = args[index + 1] ?? string.Empty;
            return value.StartsWith("-", StringComparison.Ordinal) ? string.Empty : value;
        }

        return string.Empty;
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
        StopSession();
    }

    private void StopSession()
    {
        StopResponder();
        _snapshot = QuerySnapshot.Empty;
        _sessionNetwork = null;
        _idle = false;
        _sessionActive = false;
    }

    private void StopResponder()
    {
        _responder?.Stop();
        _responder = null;
        _responderPort = 0;
    }
}

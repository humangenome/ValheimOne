using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimOne.Infrastructure;

public sealed class ServerSessionEventSource : MonoBehaviour
{
    private const float PollIntervalSeconds = 1f;
    private const float FallbackDayLengthSeconds = 1200f;

    private static readonly Lazy<FieldInfo> RandomEventField = new Lazy<FieldInfo>(
        () => AccessTools.Field(typeof(RandEventSystem), "m_randomEvent") ??
              throw new MissingFieldException(
                  typeof(RandEventSystem).FullName,
                  "m_randomEvent"));

    private readonly Dictionary<long, PeerObservation> _peers =
        new Dictionary<long, PeerObservation>();
    private ModLogger? _log;
    private ZNet? _sessionNetwork;
    private RaidObservation? _activeRaid;
    private float _nextPoll;
    private int _lastDay;
    private bool _peersObserved;
    private bool _raidObserved;
    private bool _dayObserved;
    private bool _raidReadWarningLogged;
    private bool _stopped;

    internal event Action<ServerSessionStartedEvent>? SessionStarted;

    internal event Action<ServerPlayerJoinedEvent>? PlayerJoined;

    internal event Action<ServerPlayerLeftEvent>? PlayerLeft;

    internal event Action<ServerPlayerDeathEvent>? PlayerDied;

    internal event Action<ServerRaidEvent>? RaidStarted;

    internal event Action<ServerRaidEvent>? RaidEnded;

    internal event Action<ServerDayChangedEvent>? DayChanged;

    internal static ServerSessionEventSource Initialize(GameObject host, ModLogger log)
    {
        var source = host.AddComponent<ServerSessionEventSource>();
        source._log = log;
        return source;
    }

    internal void ResetObservations()
    {
        _sessionNetwork = null;
        _peers.Clear();
        _activeRaid = null;
        _peersObserved = false;
        _raidObserved = false;
        _dayObserved = false;
        _nextPoll = 0f;
    }

    internal void StopPermanently()
    {
        _stopped = true;
        ResetObservations();
    }

    internal void HandleCharacterIdChanged(ZNet network, ZRpc rpc, ZDOID characterId)
    {
        if (_stopped || !_peersObserved || !ReferenceEquals(_sessionNetwork, network))
        {
            return;
        }

        List<ZNetPeer> peers = network.GetPeers();
        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer? peer = peers[index];
            if (peer == null || !ReferenceEquals(peer.m_rpc, rpc) || peer.m_uid == 0L)
            {
                continue;
            }

            if (!_peers.TryGetValue(peer.m_uid, out PeerObservation? observation))
            {
                return;
            }

            ZDOID previousCharacterId = observation.CharacterId;
            Vector3 lastPosition = observation.Position;
            observation.CharacterId = characterId;
            observation.Position = peer.m_refPos;
            if (!string.IsNullOrWhiteSpace(peer.m_playerName))
            {
                observation.Name = peer.m_playerName;
            }

            if (!previousCharacterId.IsNone() && characterId.IsNone())
            {
                Publish(
                    PlayerDied,
                    new ServerPlayerDeathEvent(
                        observation.Name,
                        observation.SteamId,
                        lastPosition),
                    "player death");
            }

            return;
        }
    }

    private void Update()
    {
        if (_stopped)
        {
            return;
        }

        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer())
        {
            if (_sessionNetwork != null || _peersObserved || _raidObserved || _dayObserved)
            {
                ResetObservations();
            }

            return;
        }

        if (!ReferenceEquals(_sessionNetwork, network))
        {
            ResetObservations();
            _sessionNetwork = network;
            Publish(
                SessionStarted,
                new ServerSessionStartedEvent(network.GetWorldName()),
                "server start");
        }

        float now = Time.realtimeSinceStartup;
        if (now < _nextPoll)
        {
            return;
        }

        _nextPoll = now + PollIntervalSeconds;
        PollPeers(network);
        PollRaid();
        PollDay(network);
    }

    private void PollPeers(ZNet network)
    {
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        List<ZNetPeer> peers = network.GetPeers();
        var present = new HashSet<long>();
        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer? peer = peers[index];
            if (peer == null || peer.m_uid == 0L)
            {
                continue;
            }

            present.Add(peer.m_uid);
            string name = peer.m_playerName ?? string.Empty;
            if (_peers.TryGetValue(peer.m_uid, out PeerObservation? observation))
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    observation.Name = name;
                }

                observation.Position = peer.m_refPos;
                observation.CharacterId = peer.m_characterID;
                continue;
            }

            long steamId = TryGetSteamId(peer);
            _peers.Add(
                peer.m_uid,
                new PeerObservation(
                    name,
                    peer.m_characterID,
                    peer.m_refPos,
                    steamId,
                    nowUnixMs));
            if (_peersObserved)
            {
                Publish(
                    PlayerJoined,
                    new ServerPlayerJoinedEvent(name, steamId),
                    "player join");
            }
        }

        if (_peersObserved && _peers.Count != present.Count)
        {
            var departed = new List<long>();
            foreach (KeyValuePair<long, PeerObservation> pair in _peers)
            {
                if (present.Contains(pair.Key))
                {
                    continue;
                }

                long sessionSeconds = Math.Max(
                    0L,
                    (nowUnixMs - pair.Value.JoinedUnixMs) / 1000L);
                Publish(
                    PlayerLeft,
                    new ServerPlayerLeftEvent(
                        pair.Value.Name,
                        pair.Value.SteamId,
                        sessionSeconds),
                    "player leave");
                departed.Add(pair.Key);
            }

            for (int index = 0; index < departed.Count; index++)
            {
                _peers.Remove(departed[index]);
            }
        }

        _peersObserved = true;
    }

    private void PollRaid()
    {
        if (!TryReadActiveRaid(out RaidObservation? current))
        {
            return;
        }

        if (!_raidObserved)
        {
            _activeRaid = current;
            _raidObserved = true;
            return;
        }

        RaidObservation? previous = _activeRaid;
        if (previous == null && current != null)
        {
            Publish(RaidStarted, new ServerRaidEvent(current.ReadableName), "raid start");
        }
        else if (previous != null && current == null)
        {
            Publish(RaidEnded, new ServerRaidEvent(previous.ReadableName), "raid end");
        }
        else if (previous != null && current != null &&
                 !string.Equals(previous.InternalName, current.InternalName, StringComparison.Ordinal))
        {
            Publish(RaidEnded, new ServerRaidEvent(previous.ReadableName), "raid end");
            Publish(RaidStarted, new ServerRaidEvent(current.ReadableName), "raid start");
        }

        _activeRaid = current;
    }

    private bool TryReadActiveRaid(out RaidObservation? observation)
    {
        RandEventSystem? eventSystem = RandEventSystem.instance;
        if (eventSystem == null)
        {
            observation = null;
            return true;
        }

        try
        {
            var activeEvent = RandomEventField.Value.GetValue(eventSystem) as RandomEvent;
            if (activeEvent == null)
            {
                observation = null;
                return true;
            }

            string internalName = activeEvent.m_name ?? string.Empty;
            string readableName = ReadableRaidName(activeEvent, internalName);
            observation = new RaidObservation(internalName, readableName);
            return true;
        }
        catch (Exception exception)
        {
            if (!_raidReadWarningLogged)
            {
                _raidReadWarningLogged = true;
                _log?.Warning(
                    $"[ServerEvents] active raid event read failed ({exception.GetType().Name}).");
            }

            observation = null;
            return false;
        }
    }

    private void PollDay(ZNet network)
    {
        double seconds = network.GetTimeSeconds();
        EnvMan? environmentManager = EnvMan.instance;
        float dayLength = environmentManager != null && environmentManager.m_dayLengthSec > 0L
            ? environmentManager.m_dayLengthSec
            : FallbackDayLengthSeconds;
        int day = (int)Math.Floor(seconds / dayLength);

        if (_dayObserved && day > _lastDay)
        {
            Publish(DayChanged, new ServerDayChangedEvent(day), "day change");
        }

        _lastDay = day;
        _dayObserved = true;
    }

    private void Publish<T>(Action<T>? callbacks, T value, string eventName)
    {
        if (callbacks == null)
        {
            return;
        }

        foreach (Action<T> callback in callbacks.GetInvocationList())
        {
            try
            {
                callback(value);
            }
            catch (Exception exception)
            {
                try
                {
                    _log?.Warning(
                        $"[ServerEvents] {eventName} callback failed " +
                        $"({exception.GetType().Name}).");
                }
                catch
                {
                    // Session observations must not fail because diagnostics failed.
                }
            }
        }
    }

    private static string ReadableRaidName(RandomEvent activeEvent, string internalName)
    {
        string message = activeEvent.m_startMessage ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(message))
        {
            try
            {
                Localization? localization = Localization.instance;
                string localized = localization == null ? message : localization.Localize(message);
                if (!string.IsNullOrWhiteSpace(localized) && localized[0] != '$')
                {
                    return localized.Trim();
                }
            }
            catch
            {
                // Fall back to the event's internal name when localization is unavailable.
            }
        }

        return ReadableIdentifier(internalName, "Unknown raid");
    }

    private static string ReadableIdentifier(string? value, string fallback)
    {
        string candidate = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return fallback;
        }

        string trimmed = candidate.Trim().Replace('_', ' ').Replace('-', ' ');
        var result = new System.Text.StringBuilder(trimmed.Length + 8);
        for (int index = 0; index < trimmed.Length; index++)
        {
            char character = trimmed[index];
            if (index != 0 && char.IsUpper(character) &&
                !char.IsWhiteSpace(trimmed[index - 1]) &&
                !char.IsUpper(trimmed[index - 1]))
            {
                result.Append(' ');
            }

            result.Append(character);
        }

        return result.ToString();
    }

    private static long TryGetSteamId(ZNetPeer peer)
    {
        try
        {
            string hostName = peer.m_socket?.GetHostName() ?? string.Empty;
            const string prefix = "Steam_";
            string candidate = hostName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? hostName.Substring(prefix.Length)
                : peer.m_socket is ZSteamSocket ? hostName : string.Empty;
            if (candidate.Length > 0 &&
                long.TryParse(
                    candidate,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long steamId) &&
                steamId > 0L)
            {
                return steamId;
            }
        }
        catch
        {
            // Crossplay and unavailable socket identities simply omit steamId.
        }

        return 0L;
    }

    private sealed class PeerObservation
    {
        public PeerObservation(
            string name,
            ZDOID characterId,
            Vector3 position,
            long steamId,
            long joinedUnixMs)
        {
            Name = name;
            CharacterId = characterId;
            Position = position;
            SteamId = steamId;
            JoinedUnixMs = joinedUnixMs;
        }

        public string Name { get; set; }

        public ZDOID CharacterId { get; set; }

        public Vector3 Position { get; set; }

        public long SteamId { get; }

        public long JoinedUnixMs { get; }
    }

    private sealed class RaidObservation
    {
        public RaidObservation(string internalName, string readableName)
        {
            InternalName = internalName;
            ReadableName = readableName;
        }

        public string InternalName { get; }

        public string ReadableName { get; }
    }
}

internal sealed class ServerSessionStartedEvent
{
    public ServerSessionStartedEvent(string? worldName)
    {
        WorldName = worldName ?? string.Empty;
    }

    public string WorldName { get; }
}

internal sealed class ServerPlayerJoinedEvent
{
    public ServerPlayerJoinedEvent(string? name, long steamId)
    {
        Name = name ?? string.Empty;
        SteamId = steamId;
    }

    public string Name { get; }

    public long SteamId { get; }
}

internal sealed class ServerPlayerLeftEvent
{
    public ServerPlayerLeftEvent(string? name, long steamId, long sessionSeconds)
    {
        Name = name ?? string.Empty;
        SteamId = steamId;
        SessionSeconds = sessionSeconds;
    }

    public string Name { get; }

    public long SteamId { get; }

    public long SessionSeconds { get; }
}

internal sealed class ServerPlayerDeathEvent
{
    public ServerPlayerDeathEvent(string? name, long steamId, Vector3 lastPosition)
    {
        Name = name ?? string.Empty;
        SteamId = steamId;
        LastPosition = lastPosition;
    }

    public string Name { get; }

    public long SteamId { get; }

    public Vector3 LastPosition { get; }
}

internal sealed class ServerRaidEvent
{
    public ServerRaidEvent(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

internal sealed class ServerDayChangedEvent
{
    public ServerDayChangedEvent(int day)
    {
        Day = day;
    }

    public int Day { get; }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.Discord;

internal sealed class DiscordBehaviour : MonoBehaviour
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
    private DiscordModule? _module;
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

    public static DiscordBehaviour Initialize(
        GameObject host,
        DiscordModule module,
        ModLogger log)
    {
        var behaviour = host.AddComponent<DiscordBehaviour>();
        behaviour._module = module;
        behaviour._log = log;
        return behaviour;
    }

    public void ResetObservations()
    {
        _sessionNetwork = null;
        _peers.Clear();
        _activeRaid = null;
        _peersObserved = false;
        _raidObserved = false;
        _dayObserved = false;
        _nextPoll = 0f;
    }

    public void StopPermanently()
    {
        _stopped = true;
        ResetObservations();
    }

    public void HandleCharacterIdChanged(ZNet network, ZRpc rpc, ZDOID characterId)
    {
        DiscordModule? module = _module;
        if (_stopped || module == null || !_peersObserved ||
            !ReferenceEquals(_sessionNetwork, network))
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
                module.NotifyDeath(observation.Name, lastPosition);
            }

            return;
        }
    }

    private void Update()
    {
        DiscordModule? module = _module;
        if (_stopped || module == null)
        {
            return;
        }

        if (!module.DeliveryEnabled)
        {
            if (_sessionNetwork != null || _peersObserved || _raidObserved || _dayObserved)
            {
                ResetObservations();
            }

            return;
        }

        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer())
        {
            ResetObservations();
            return;
        }

        if (!ReferenceEquals(_sessionNetwork, network))
        {
            ResetObservations();
            _sessionNetwork = network;
        }

        float now = Time.realtimeSinceStartup;
        if (now < _nextPoll)
        {
            return;
        }

        _nextPoll = now + PollIntervalSeconds;
        module.UpdateWorldName(network.GetWorldName());
        PollPeers(network, module);
        PollRaid(module);
        PollDay(network, module);
    }

    private void PollPeers(ZNet network, DiscordModule module)
    {
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

            _peers.Add(
                peer.m_uid,
                new PeerObservation(name, peer.m_characterID, peer.m_refPos));
            if (_peersObserved && module.NotifyJoin)
            {
                module.NotifyPlayerJoined(name);
            }
        }

        if (_peersObserved && _peers.Count != present.Count)
        {
            var departed = new List<long>();
            foreach (KeyValuePair<long, PeerObservation> pair in _peers)
            {
                if (!present.Contains(pair.Key))
                {
                    if (module.NotifyLeave)
                    {
                        module.NotifyPlayerLeft(pair.Value.Name);
                    }

                    departed.Add(pair.Key);
                }
            }

            for (int index = 0; index < departed.Count; index++)
            {
                _peers.Remove(departed[index]);
            }
        }

        _peersObserved = true;
    }

    private void PollRaid(DiscordModule module)
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
            if (module.NotifyRaid)
            {
                module.NotifyRaidStarted(current.ReadableName);
            }
        }
        else if (previous != null && current == null)
        {
            if (module.NotifyRaid)
            {
                module.NotifyRaidEnded(previous.ReadableName);
            }
        }
        else if (previous != null && current != null &&
                 !string.Equals(previous.InternalName, current.InternalName, StringComparison.Ordinal))
        {
            if (module.NotifyRaid)
            {
                module.NotifyRaidEnded(previous.ReadableName);
                module.NotifyRaidStarted(current.ReadableName);
            }
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
                    $"[Discord] active raid event read failed ({exception.GetType().Name}).");
            }

            observation = null;
            return false;
        }
    }

    private void PollDay(ZNet network, DiscordModule module)
    {
        double seconds = network.GetTimeSeconds();
        EnvMan? environmentManager = EnvMan.instance;
        float dayLength = environmentManager != null && environmentManager.m_dayLengthSec > 0L
            ? environmentManager.m_dayLengthSec
            : FallbackDayLengthSeconds;
        int day = (int)Math.Floor(seconds / dayLength);

        if (_dayObserved && day > _lastDay && module.NotifyDayChange)
        {
            module.NotifyNewDay(day);
        }

        _lastDay = day;
        _dayObserved = true;
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

        return DiscordModule.ReadableIdentifier(internalName, "Unknown raid");
    }

    private sealed class PeerObservation
    {
        public PeerObservation(string name, ZDOID characterId, Vector3 position)
        {
            Name = name;
            CharacterId = characterId;
            Position = position;
        }

        public string Name { get; set; }

        public ZDOID CharacterId { get; set; }

        public Vector3 Position { get; set; }
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

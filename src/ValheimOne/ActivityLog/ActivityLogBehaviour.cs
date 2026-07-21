using UnityEngine;
using ValheimOne.Infrastructure;
using ValheimOne.LiveMap;

namespace ValheimOne.ActivityLog;

internal sealed class ActivityLogBehaviour : MonoBehaviour
{
    private ActivityLogModule? _module;
    private ServerSessionEventSource? _sessionEvents;
    private bool _sessionStarted;
    private bool _stopped;

    public static ActivityLogBehaviour Initialize(
        GameObject host,
        ActivityLogModule module,
        ServerSessionEventSource sessionEvents)
    {
        var behaviour = host.AddComponent<ActivityLogBehaviour>();
        behaviour._module = module;
        behaviour._sessionEvents = sessionEvents;
        sessionEvents.SessionStarted += behaviour.OnSessionStarted;
        sessionEvents.PlayerJoined += behaviour.OnPlayerJoined;
        sessionEvents.PlayerLeft += behaviour.OnPlayerLeft;
        sessionEvents.PlayerDied += behaviour.OnPlayerDied;
        sessionEvents.RaidStarted += behaviour.OnRaidStarted;
        sessionEvents.RaidEnded += behaviour.OnRaidEnded;
        sessionEvents.DayChanged += behaviour.OnDayChanged;
        WorldSavePatch.WorldSaved += behaviour.OnWorldSaved;
        return behaviour;
    }

    public void StopPermanently(bool recordServerStop)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        if (recordServerStop && _sessionStarted)
        {
            _module?.RecordServerStop();
        }

        ServerSessionEventSource? sessionEvents = _sessionEvents;
        if (sessionEvents != null)
        {
            sessionEvents.SessionStarted -= OnSessionStarted;
            sessionEvents.PlayerJoined -= OnPlayerJoined;
            sessionEvents.PlayerLeft -= OnPlayerLeft;
            sessionEvents.PlayerDied -= OnPlayerDied;
            sessionEvents.RaidStarted -= OnRaidStarted;
            sessionEvents.RaidEnded -= OnRaidEnded;
            sessionEvents.DayChanged -= OnDayChanged;
        }

        WorldSavePatch.WorldSaved -= OnWorldSaved;
        _sessionEvents = null;
        _module = null;
    }

    private void OnSessionStarted(ServerSessionStartedEvent value)
    {
        _ = value;
        _sessionStarted = true;
        _module?.RecordServerStart();
    }

    private void OnPlayerJoined(ServerPlayerJoinedEvent value)
    {
        _module?.RecordPlayerJoin(value.Name, value.SteamId);
    }

    private void OnPlayerLeft(ServerPlayerLeftEvent value)
    {
        _module?.RecordPlayerLeave(value.Name, value.SessionSeconds);
    }

    private void OnPlayerDied(ServerPlayerDeathEvent value)
    {
        _module?.RecordPlayerDeath(value.Name);
    }

    private void OnRaidStarted(ServerRaidEvent value)
    {
        _module?.RecordRaidStarted(value.Name);
    }

    private void OnRaidEnded(ServerRaidEvent value)
    {
        _module?.RecordRaidEnded(value.Name);
    }

    private void OnDayChanged(ServerDayChangedEvent value)
    {
        _module?.RecordDayChanged(value.Day);
    }

    private void OnWorldSaved()
    {
        _module?.RecordWorldSave();
    }

    private void OnApplicationQuit()
    {
        StopPermanently(recordServerStop: true);
    }

    private void OnDestroy()
    {
        StopPermanently(recordServerStop: false);
    }
}

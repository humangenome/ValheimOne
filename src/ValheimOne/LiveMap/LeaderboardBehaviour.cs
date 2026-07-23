using System;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class LeaderboardBehaviour : MonoBehaviour
{
    private Func<LeaderboardStore?>? _getStore;
    private ServerSessionEventSource? _sessionEvents;
    private bool _stopped;

    public static LeaderboardBehaviour Initialize(
        GameObject host,
        Func<LeaderboardStore?> getStore,
        ServerSessionEventSource sessionEvents)
    {
        LeaderboardBehaviour behaviour = host.AddComponent<LeaderboardBehaviour>();
        behaviour._getStore = getStore;
        behaviour._sessionEvents = sessionEvents;
        sessionEvents.PlayerJoined += behaviour.OnPlayerJoined;
        sessionEvents.PlayerLeft += behaviour.OnPlayerLeft;
        sessionEvents.PlayerDied += behaviour.OnPlayerDied;
        return behaviour;
    }

    public void StopPermanently()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        ServerSessionEventSource? sessionEvents = _sessionEvents;
        if (sessionEvents != null)
        {
            sessionEvents.PlayerJoined -= OnPlayerJoined;
            sessionEvents.PlayerLeft -= OnPlayerLeft;
            sessionEvents.PlayerDied -= OnPlayerDied;
        }

        _sessionEvents = null;
        _getStore = null;
    }

    private void OnPlayerJoined(ServerPlayerJoinedEvent value)
    {
        _getStore?.Invoke()?.NoteSessionProgress(
            value.Name,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void OnPlayerLeft(ServerPlayerLeftEvent value)
    {
        _getStore?.Invoke()?.NotePlaytime(
            value.Name,
            value.SessionSeconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void OnPlayerDied(ServerPlayerDeathEvent value)
    {
        _getStore?.Invoke()?.NoteDeath(
            value.Name,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void OnApplicationQuit()
    {
        StopPermanently();
    }

    private void OnDestroy()
    {
        StopPermanently();
    }
}

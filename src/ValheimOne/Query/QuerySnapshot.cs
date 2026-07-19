using System;

namespace ValheimOne.Query;

internal sealed class QuerySnapshot
{
    private readonly string[] _playerNames;

    public static readonly QuerySnapshot Empty = new(
        string.Empty,
        string.Empty,
        0,
        Array.Empty<string>(),
        0,
        2456,
        false,
        "unknown",
        ValheimOnePlugin.PluginVersion,
        DateTime.UtcNow);

    public QuerySnapshot(
        string serverName,
        string worldName,
        int playerCount,
        string[] playerNames,
        int maxPlayers,
        int gamePort,
        bool passworded,
        string gameVersion,
        string pluginVersion,
        DateTime startTimeUtc)
    {
        ServerName = serverName;
        WorldName = worldName;
        PlayerCount = playerCount;
        _playerNames = (string[])playerNames.Clone();
        MaxPlayers = maxPlayers;
        GamePort = gamePort;
        Passworded = passworded;
        GameVersion = gameVersion;
        PluginVersion = pluginVersion;
        StartTimeUtc = startTimeUtc;
    }

    public string ServerName { get; }

    public string WorldName { get; }

    public int PlayerCount { get; }

    public int MaxPlayers { get; }

    public int GamePort { get; }

    public bool Passworded { get; }

    public string GameVersion { get; }

    public string PluginVersion { get; }

    public DateTime StartTimeUtc { get; }

    public string GetPlayerName(int index)
    {
        return index >= 0 && index < _playerNames.Length
            ? _playerNames[index]
            : string.Empty;
    }
}

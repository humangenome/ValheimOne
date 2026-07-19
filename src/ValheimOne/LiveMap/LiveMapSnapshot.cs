using System;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapSnapshot
{
    public static readonly LiveMapSnapshot Empty = new LiveMapSnapshot(
        string.Empty,
        string.Empty,
        0,
        0f,
        Array.Empty<LiveMapPlayerSnapshot>());

    public LiveMapSnapshot(
        string serverName,
        string worldName,
        int day,
        float timeOfDay,
        LiveMapPlayerSnapshot[] players)
    {
        ServerName = serverName;
        WorldName = worldName;
        Day = day;
        TimeOfDay = timeOfDay;
        Players = players;
    }

    public string ServerName { get; }

    public string WorldName { get; }

    public int Day { get; }

    public float TimeOfDay { get; }

    public LiveMapPlayerSnapshot[] Players { get; }
}

internal sealed class LiveMapPlayerSnapshot
{
    public LiveMapPlayerSnapshot(string name, float x, float y, float z, bool isPublic)
    {
        Name = name;
        X = x;
        Y = y;
        Z = z;
        IsPublic = isPublic;
    }

    public string Name { get; }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public bool IsPublic { get; }
}

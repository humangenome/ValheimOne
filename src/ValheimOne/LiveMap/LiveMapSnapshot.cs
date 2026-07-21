using System;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapSnapshot
{
    public static readonly LiveMapSnapshot Empty = new LiveMapSnapshot(
        string.Empty,
        string.Empty,
        0,
        0f,
        0f,
        0f,
        0,
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<LiveMapPlayerSnapshot>());

    public LiveMapSnapshot(
        string serverName,
        string worldName,
        int day,
        float timeOfDay,
        float windDirDeg,
        float windIntensity,
        long unixMs,
        string[] globalKeys,
        string[] modifiers,
        LiveMapPlayerSnapshot[] players)
    {
        ServerName = serverName;
        WorldName = worldName;
        Day = day;
        TimeOfDay = timeOfDay;
        WindDirDeg = windDirDeg;
        WindIntensity = windIntensity;
        UnixMs = unixMs;
        GlobalKeys = globalKeys;
        Modifiers = modifiers;
        Players = players;
    }

    public string ServerName { get; }

    public string WorldName { get; }

    public int Day { get; }

    public float TimeOfDay { get; }

    public float WindDirDeg { get; }

    public float WindIntensity { get; }

    public long UnixMs { get; }

    public string[] GlobalKeys { get; }

    public string[] Modifiers { get; }

    public LiveMapPlayerSnapshot[] Players { get; }
}

internal sealed class LiveMapPlayerSnapshot
{
    public LiveMapPlayerSnapshot(
        string name,
        float x,
        float y,
        float z,
        bool isPublic,
        long id,
        string biome,
        float speedMps,
        float headingDeg,
        long sessionStartUnixMs,
        float distanceTodayM,
        float health,
        float maxHealth,
        bool dead,
        bool pvp,
        bool inBed)
    {
        Name = name;
        X = x;
        Y = y;
        Z = z;
        IsPublic = isPublic;
        Id = id;
        Biome = biome;
        SpeedMps = speedMps;
        HeadingDeg = headingDeg;
        SessionStartUnixMs = sessionStartUnixMs;
        DistanceTodayM = distanceTodayM;
        Health = health;
        MaxHealth = maxHealth;
        Dead = dead;
        Pvp = pvp;
        InBed = inBed;
    }

    public string Name { get; }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public bool IsPublic { get; }

    public long Id { get; }

    public string Biome { get; }

    public float SpeedMps { get; }

    public float HeadingDeg { get; }

    public long SessionStartUnixMs { get; }

    public float DistanceTodayM { get; }

    public float Health { get; }

    public float MaxHealth { get; }

    public bool Dead { get; }

    public bool Pvp { get; }

    public bool InBed { get; }
}

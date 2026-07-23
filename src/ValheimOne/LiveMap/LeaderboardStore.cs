using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class LeaderboardStore : IDisposable
{
    private const int MaximumCharacters = 200;
    private const int MaximumNameLength = 64;
    private const int PersistedFileMaximumBytes = 512 * 1024;
    private const int PersistIntervalMilliseconds = 5 * 60 * 1000;
    private const int ShutdownFlushMilliseconds = 2000;
    private const int PersistedVersion = 1;
    private const double MaximumDistanceMeters = 1_000_000_000_000d;

    private readonly object _lock = new object();
    private readonly Dictionary<string, PlayerAggregate> _players =
        new Dictionary<string, PlayerAggregate>(StringComparer.Ordinal);
    private readonly ManualResetEvent _stopSignal = new ManualResetEvent(false);
    private readonly string _path;
    private readonly ModLogger _log;
    private readonly Thread _writerThread;
    private int _worldSeed;
    private int _dirty;
    private int _writeFailureWarningLogged;
    private bool _hasWorldSeed;
    private bool _disposed;

    public LeaderboardStore(string dataDirectory, ModLogger log)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }

        _log = log ?? throw new ArgumentNullException(nameof(log));
        _path = Path.Combine(dataDirectory, "leaderboards.json");
        Load();

        _writerThread = new Thread(RunWriter)
        {
            IsBackground = true,
            Name = "ValheimOne.Leaderboards",
        };
        _writerThread.Start();
    }

    public void ConfigureWorldSeed(int worldSeed)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_hasWorldSeed && _worldSeed == worldSeed)
            {
                return;
            }

            if (_hasWorldSeed && _worldSeed != worldSeed)
            {
                _players.Clear();
                _log.Info(
                    "[LiveMap] leaderboard world seed changed; starting a fresh wipe leaderboard.");
            }

            _worldSeed = worldSeed;
            _hasWorldSeed = true;
            Volatile.Write(ref _dirty, 1);
        }
    }

    public void NoteSessionProgress(string? name, long unixMs)
    {
        string characterName = NormalizeName(name);
        if (characterName.Length == 0 || unixMs <= 0L)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            PlayerAggregate player = GetOrCreateLocked(characterName, unixMs);
            player.LastSeenUnixMs = Math.Max(player.LastSeenUnixMs, unixMs);
            if (!player.Online)
            {
                player.Online = true;
                player.SessionStartUnixMs = unixMs;
            }

            Volatile.Write(ref _dirty, 1);
        }
    }

    public void NotePlaytime(string? name, long sessionSeconds, long unixMs)
    {
        string characterName = NormalizeName(name);
        if (characterName.Length == 0 || unixMs <= 0L)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            PlayerAggregate player = GetOrCreateLocked(characterName, unixMs);
            long liveSeconds = player.Online
                ? SessionSeconds(player.SessionStartUnixMs, unixMs)
                : 0L;
            long creditedSeconds = player.Online
                ? liveSeconds
                : Math.Max(0L, sessionSeconds);
            player.TotalPlaySeconds = SaturatingAdd(
                player.TotalPlaySeconds,
                creditedSeconds);
            player.Online = false;
            player.SessionStartUnixMs = 0L;
            player.LastSeenUnixMs = Math.Max(player.LastSeenUnixMs, unixMs);
            Volatile.Write(ref _dirty, 1);
        }
    }

    public void NoteDeath(string? name, long unixMs)
    {
        string characterName = NormalizeName(name);
        if (characterName.Length == 0 || unixMs <= 0L)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            PlayerAggregate player = GetOrCreateLocked(characterName, unixMs);
            if (player.Deaths < int.MaxValue)
            {
                player.Deaths++;
            }

            player.LastSeenUnixMs = Math.Max(player.LastSeenUnixMs, unixMs);
            Volatile.Write(ref _dirty, 1);
        }
    }

    public void NoteDistance(string? name, double meters, long unixMs)
    {
        string characterName = NormalizeName(name);
        if (characterName.Length == 0 || unixMs <= 0L || meters <= 0d ||
            double.IsNaN(meters) || double.IsInfinity(meters))
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            PlayerAggregate player = GetOrCreateLocked(characterName, unixMs);
            player.DistanceTraveledMeters = Math.Min(
                MaximumDistanceMeters,
                player.DistanceTraveledMeters + meters);
            player.LastSeenUnixMs = Math.Max(player.LastSeenUnixMs, unixMs);
            Volatile.Write(ref _dirty, 1);
        }
    }

    public LeaderboardSnapshot Snapshot(long generatedUnixMs, int maximumPlayers)
    {
        LeaderboardPlayerSnapshot[] players;
        lock (_lock)
        {
            players = new LeaderboardPlayerSnapshot[_players.Count];
            int index = 0;
            foreach (KeyValuePair<string, PlayerAggregate> pair in _players)
            {
                PlayerAggregate player = pair.Value;
                long playSeconds = player.TotalPlaySeconds;
                if (player.Online)
                {
                    playSeconds = SaturatingAdd(
                        playSeconds,
                        SessionSeconds(player.SessionStartUnixMs, generatedUnixMs));
                }

                players[index] = new LeaderboardPlayerSnapshot(
                    pair.Key,
                    playSeconds,
                    player.Deaths,
                    player.DistanceTraveledMeters,
                    player.Online);
                index++;
            }
        }

        Array.Sort(players, CompareSnapshots);
        int resultCount = Math.Min(Math.Max(0, maximumPlayers), players.Length);
        if (resultCount != players.Length)
        {
            LeaderboardPlayerSnapshot[] capped = new LeaderboardPlayerSnapshot[resultCount];
            Array.Copy(players, capped, resultCount);
            players = capped;
        }

        return new LeaderboardSnapshot(generatedUnixMs, players);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopSignal.Set();
        }

        if (!ReferenceEquals(Thread.CurrentThread, _writerThread) &&
            _writerThread.IsAlive &&
            !_writerThread.Join(ShutdownFlushMilliseconds))
        {
            _log.Warning(
                "[LiveMap] leaderboard writer did not exit within the shutdown flush window.");
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _log.Warning(
                "[LiveMap] leaderboard data could not be loaded because leaderboards.json is " +
                "missing; starting with an empty store.");
            return;
        }

        try
        {
            if (new FileInfo(_path).Length > PersistedFileMaximumBytes)
            {
                throw new FormatException("Leaderboard JSON is too large.");
            }

            string json = File.ReadAllText(_path, Encoding.UTF8);
            PersistedLeaderboard loaded = LeaderboardJsonParser.Parse(json);
            for (int index = 0; index < loaded.Players.Count; index++)
            {
                PersistedPlayer persisted = loaded.Players[index];
                string name = NormalizeName(persisted.Name);
                if (name.Length == 0 || !string.Equals(name, persisted.Name, StringComparison.Ordinal) ||
                    persisted.TotalPlaySeconds < 0L || persisted.Deaths < 0 ||
                    persisted.DistanceTraveledMeters < 0d ||
                    persisted.DistanceTraveledMeters > MaximumDistanceMeters ||
                    double.IsNaN(persisted.DistanceTraveledMeters) ||
                    double.IsInfinity(persisted.DistanceTraveledMeters) ||
                    persisted.LastSeenUnixMs < 0L || _players.ContainsKey(name))
                {
                    throw new FormatException("Leaderboard JSON contains invalid player data.");
                }

                _players.Add(
                    name,
                    new PlayerAggregate
                    {
                        TotalPlaySeconds = persisted.TotalPlaySeconds,
                        Deaths = persisted.Deaths,
                        DistanceTraveledMeters = persisted.DistanceTraveledMeters,
                        LastSeenUnixMs = persisted.LastSeenUnixMs,
                    });
            }

            _worldSeed = loaded.WorldSeed;
            _hasWorldSeed = true;
        }
        catch (Exception exception)
        {
            _players.Clear();
            _hasWorldSeed = false;
            _log.Warning(
                $"[LiveMap] leaderboard data could not be loaded ({exception.GetType().Name}); " +
                "starting with an empty store.");
        }
    }

    private void RunWriter()
    {
        while (!_stopSignal.WaitOne(PersistIntervalMilliseconds))
        {
            FlushPendingWrite();
        }

        FlushPendingWrite();
    }

    private void FlushPendingWrite()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        try
        {
            if (!Persist())
            {
                Volatile.Write(ref _dirty, 1);
                return;
            }

            Interlocked.Exchange(ref _writeFailureWarningLogged, 0);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _dirty, 1);
            if (Interlocked.Exchange(ref _writeFailureWarningLogged, 1) == 0)
            {
                _log.Warning(
                    $"[LiveMap] leaderboard data could not be persisted " +
                    $"({exception.GetType().Name}: {SingleLineMessage(exception)}). " +
                    "The writer will retry.");
            }
        }
    }

    private bool Persist()
    {
        if (!TryCapturePersistenceSnapshot(
                out int worldSeed,
                out PersistedPlayer[] players))
        {
            return false;
        }

        StringBuilder json = new StringBuilder(96 + (players.Length * 128));
        json.Append("{\"version\":").Append(
            PersistedVersion.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"worldSeed\":").Append(
            worldSeed.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"players\":[");
        for (int index = 0; index < players.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            PersistedPlayer player = players[index];
            json.Append("{\"name\":").Append(JsonWriter.Quote(player.Name));
            json.Append(",\"totalPlaySeconds\":").Append(
                player.TotalPlaySeconds.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"deaths\":").Append(
                player.Deaths.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"distanceTraveledMeters\":").Append(
                JsonWriter.Number(player.DistanceTraveledMeters));
            json.Append(",\"lastSeenUnixMs\":").Append(
                player.LastSeenUnixMs.ToString(CultureInfo.InvariantCulture));
            json.Append('}');
        }

        json.Append("]}");
        if (Encoding.UTF8.GetByteCount(json.ToString()) > PersistedFileMaximumBytes)
        {
            throw new InvalidOperationException("Leaderboard JSON exceeded its file cap.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            json.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(_path))
        {
            File.Replace(temporaryPath, _path, null);
        }
        else
        {
            File.Move(temporaryPath, _path);
        }

        return true;
    }

    private bool TryCapturePersistenceSnapshot(
        out int worldSeed,
        out PersistedPlayer[] players)
    {
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            if (!_hasWorldSeed)
            {
                worldSeed = 0;
                players = Array.Empty<PersistedPlayer>();
                return false;
            }

            worldSeed = _worldSeed;
            players = new PersistedPlayer[_players.Count];
            int index = 0;
            foreach (KeyValuePair<string, PlayerAggregate> pair in _players)
            {
                PlayerAggregate player = pair.Value;
                long playSeconds = player.TotalPlaySeconds;
                if (player.Online)
                {
                    playSeconds = SaturatingAdd(
                        playSeconds,
                        SessionSeconds(player.SessionStartUnixMs, nowUnixMs));
                }

                players[index] = new PersistedPlayer(
                    pair.Key,
                    playSeconds,
                    player.Deaths,
                    player.DistanceTraveledMeters,
                    player.LastSeenUnixMs);
                index++;
            }
        }

        Array.Sort(players, ComparePersistedPlayers);
        return true;
    }

    private PlayerAggregate GetOrCreateLocked(string name, long unixMs)
    {
        if (_players.TryGetValue(name, out PlayerAggregate? existing))
        {
            return existing;
        }

        if (_players.Count >= MaximumCharacters)
        {
            EvictLeastRecentlySeenLocked();
        }

        PlayerAggregate created = new PlayerAggregate
        {
            LastSeenUnixMs = unixMs,
        };
        _players.Add(name, created);
        return created;
    }

    private void EvictLeastRecentlySeenLocked()
    {
        string? oldestName = null;
        long oldestUnixMs = long.MaxValue;
        foreach (KeyValuePair<string, PlayerAggregate> pair in _players)
        {
            if (pair.Value.LastSeenUnixMs < oldestUnixMs ||
                (pair.Value.LastSeenUnixMs == oldestUnixMs &&
                 (oldestName == null || string.CompareOrdinal(pair.Key, oldestName) < 0)))
            {
                oldestName = pair.Key;
                oldestUnixMs = pair.Value.LastSeenUnixMs;
            }
        }

        if (oldestName != null)
        {
            _players.Remove(oldestName);
        }
    }

    private static long SessionSeconds(long sessionStartUnixMs, long nowUnixMs)
    {
        return sessionStartUnixMs > 0L && nowUnixMs > sessionStartUnixMs
            ? (nowUnixMs - sessionStartUnixMs) / 1000L
            : 0L;
    }

    private static long SaturatingAdd(long left, long right)
    {
        return right > long.MaxValue - left ? long.MaxValue : left + right;
    }

    private static string NormalizeName(string? value)
    {
        string name = (value ?? string.Empty).Trim();
        return name.Length <= MaximumNameLength
            ? name
            : name.Substring(0, MaximumNameLength);
    }

    private static int CompareSnapshots(
        LeaderboardPlayerSnapshot left,
        LeaderboardPlayerSnapshot right)
    {
        int playComparison = right.PlaySeconds.CompareTo(left.PlaySeconds);
        return playComparison != 0
            ? playComparison
            : string.CompareOrdinal(left.Name, right.Name);
    }

    private static int ComparePersistedPlayers(PersistedPlayer left, PersistedPlayer right)
    {
        return string.CompareOrdinal(left.Name, right.Name);
    }

    private static string SingleLineMessage(Exception exception)
    {
        return (exception.Message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private sealed class PlayerAggregate
    {
        public long TotalPlaySeconds { get; set; }

        public int Deaths { get; set; }

        public double DistanceTraveledMeters { get; set; }

        public long LastSeenUnixMs { get; set; }

        public long SessionStartUnixMs { get; set; }

        public bool Online { get; set; }
    }

    private sealed class PersistedLeaderboard
    {
        public PersistedLeaderboard(int worldSeed, List<PersistedPlayer> players)
        {
            WorldSeed = worldSeed;
            Players = players;
        }

        public int WorldSeed { get; }

        public List<PersistedPlayer> Players { get; }
    }

    private sealed class PersistedPlayer
    {
        public PersistedPlayer(
            string name,
            long totalPlaySeconds,
            int deaths,
            double distanceTraveledMeters,
            long lastSeenUnixMs)
        {
            Name = name;
            TotalPlaySeconds = totalPlaySeconds;
            Deaths = deaths;
            DistanceTraveledMeters = distanceTraveledMeters;
            LastSeenUnixMs = lastSeenUnixMs;
        }

        public string Name { get; }

        public long TotalPlaySeconds { get; }

        public int Deaths { get; }

        public double DistanceTraveledMeters { get; }

        public long LastSeenUnixMs { get; }
    }

    private static class LeaderboardJsonParser
    {
        public static PersistedLeaderboard Parse(string json)
        {
            return new Parser(json).ParseDocument();
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _index;

            public Parser(string json)
            {
                _json = json;
            }

            public PersistedLeaderboard ParseDocument()
            {
                Expect('{');
                ExpectProperty("version");
                int version = ReadInt32();
                Expect(',');
                ExpectProperty("worldSeed");
                int worldSeed = ReadInt32();
                Expect(',');
                ExpectProperty("players");
                List<PersistedPlayer> players = ParsePlayers();
                Expect('}');
                EnsureEnd();
                if (version != PersistedVersion)
                {
                    throw new FormatException("Leaderboard JSON has an incompatible version.");
                }

                return new PersistedLeaderboard(worldSeed, players);
            }

            private List<PersistedPlayer> ParsePlayers()
            {
                List<PersistedPlayer> players = new List<PersistedPlayer>();
                Expect('[');
                if (TryConsume(']'))
                {
                    return players;
                }

                while (true)
                {
                    players.Add(ParsePlayer());
                    if (players.Count > MaximumCharacters)
                    {
                        throw new FormatException("Leaderboard JSON contains too many players.");
                    }

                    if (TryConsume(']'))
                    {
                        return players;
                    }

                    Expect(',');
                }
            }

            private PersistedPlayer ParsePlayer()
            {
                Expect('{');
                ExpectProperty("name");
                string name = ReadString();
                Expect(',');
                ExpectProperty("totalPlaySeconds");
                long totalPlaySeconds = ReadInt64();
                Expect(',');
                ExpectProperty("deaths");
                int deaths = ReadInt32();
                Expect(',');
                ExpectProperty("distanceTraveledMeters");
                double distanceTraveledMeters = ReadDouble();
                Expect(',');
                ExpectProperty("lastSeenUnixMs");
                long lastSeenUnixMs = ReadInt64();
                Expect('}');
                return new PersistedPlayer(
                    name,
                    totalPlaySeconds,
                    deaths,
                    distanceTraveledMeters,
                    lastSeenUnixMs);
            }

            private void ExpectProperty(string expected)
            {
                string actual = ReadString();
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    throw new FormatException("Leaderboard JSON has an unexpected property.");
                }

                Expect(':');
            }

            private int ReadInt32()
            {
                long value = ReadInt64();
                if (value < int.MinValue || value > int.MaxValue)
                {
                    throw new FormatException("Leaderboard JSON contains an invalid integer.");
                }

                return (int)value;
            }

            private long ReadInt64()
            {
                string token = ReadNumberToken();
                if (!long.TryParse(
                        token,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long value))
                {
                    throw new FormatException("Leaderboard JSON contains an invalid integer.");
                }

                return value;
            }

            private double ReadDouble()
            {
                string token = ReadNumberToken();
                if (!double.TryParse(
                        token,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double value))
                {
                    throw new FormatException("Leaderboard JSON contains an invalid number.");
                }

                return value;
            }

            private string ReadNumberToken()
            {
                SkipWhitespace();
                int start = _index;
                if (_index < _json.Length && (_json[_index] == '-' || _json[_index] == '+'))
                {
                    _index++;
                }

                while (_index < _json.Length)
                {
                    char character = _json[_index];
                    if (!char.IsDigit(character) && character != '.' &&
                        character != 'e' && character != 'E' &&
                        character != '+' && character != '-')
                    {
                        break;
                    }

                    _index++;
                }

                if (_index == start)
                {
                    throw new FormatException("Leaderboard JSON is missing a number.");
                }

                return _json.Substring(start, _index - start);
            }

            private string ReadString()
            {
                SkipWhitespace();
                if (_index >= _json.Length || _json[_index++] != '"')
                {
                    throw new FormatException("Leaderboard JSON contains an invalid string.");
                }

                StringBuilder value = new StringBuilder();
                while (_index < _json.Length)
                {
                    char character = _json[_index++];
                    if (character == '"')
                    {
                        return value.ToString();
                    }

                    if (character < 0x20)
                    {
                        throw new FormatException("Leaderboard JSON contains a control character.");
                    }

                    if (character != '\\')
                    {
                        value.Append(character);
                        continue;
                    }

                    if (_index >= _json.Length)
                    {
                        break;
                    }

                    char escaped = _json[_index++];
                    switch (escaped)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            value.Append(escaped);
                            break;
                        case 'b':
                            value.Append('\b');
                            break;
                        case 'f':
                            value.Append('\f');
                            break;
                        case 'n':
                            value.Append('\n');
                            break;
                        case 'r':
                            value.Append('\r');
                            break;
                        case 't':
                            value.Append('\t');
                            break;
                        case 'u':
                            value.Append(ReadUnicodeEscape());
                            break;
                        default:
                            throw new FormatException(
                                "Leaderboard JSON contains an invalid escape sequence.");
                    }
                }

                throw new FormatException("Leaderboard JSON contains an unterminated string.");
            }

            private char ReadUnicodeEscape()
            {
                if (_index + 4 > _json.Length ||
                    !ushort.TryParse(
                        _json.Substring(_index, 4),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out ushort value))
                {
                    throw new FormatException("Leaderboard JSON contains an invalid unicode escape.");
                }

                _index += 4;
                return (char)value;
            }

            private bool TryConsume(char expected)
            {
                SkipWhitespace();
                if (_index >= _json.Length || _json[_index] != expected)
                {
                    return false;
                }

                _index++;
                return true;
            }

            private void Expect(char expected)
            {
                if (!TryConsume(expected))
                {
                    throw new FormatException("Leaderboard JSON is malformed.");
                }
            }

            private void SkipWhitespace()
            {
                while (_index < _json.Length && char.IsWhiteSpace(_json[_index]))
                {
                    _index++;
                }
            }

            private void EnsureEnd()
            {
                SkipWhitespace();
                if (_index != _json.Length)
                {
                    throw new FormatException("Leaderboard JSON contains trailing data.");
                }
            }
        }
    }
}

internal sealed class LeaderboardSnapshot
{
    public LeaderboardSnapshot(long generatedUnixMs, LeaderboardPlayerSnapshot[] players)
    {
        GeneratedUnixMs = generatedUnixMs;
        Players = players;
    }

    public long GeneratedUnixMs { get; }

    public LeaderboardPlayerSnapshot[] Players { get; }
}

internal sealed class LeaderboardPlayerSnapshot
{
    public LeaderboardPlayerSnapshot(
        string name,
        long playSeconds,
        int deaths,
        double distanceMeters,
        bool online)
    {
        Name = name;
        PlaySeconds = playSeconds;
        Deaths = deaths;
        DistanceMeters = distanceMeters;
        Online = online;
    }

    public string Name { get; }

    public long PlaySeconds { get; }

    public int Deaths { get; }

    public double DistanceMeters { get; }

    public bool Online { get; }
}

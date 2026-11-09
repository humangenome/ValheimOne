using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using ValheimOne.LiveMap;

namespace ValheimOne.Infrastructure;

// <world>.valheimone.json beside the world's save (worlds_local\, next to <world>.db and the 1.0
// <world>\ folder), written at the end of every world save that worked: what a hosting panel or a
// backup needs to describe the world without loading it. Written to a temporary name and renamed,
// so a backup never holds half a file; a failed save writes nothing. Dedicated servers only.
internal sealed class WorldSaveSummary : MonoBehaviour
{
    public const string Suffix = ".valheimone.json";
    private const float PlayerPollSeconds = 2f;

    private readonly HashSet<string> _playersSinceSave = new HashSet<string>(StringComparer.Ordinal);
    private readonly object _writeLock = new object();
    private ModLogger? _log;
    private string? _gameVersion;
    private float _nextPoll;
    private bool _writeWarningLogged;

    public static WorldSaveSummary Initialize(GameObject host, ModLogger log)
    {
        var summary = host.AddComponent<WorldSaveSummary>();
        summary._log = log;
        WorldSavePatch.WorldSaved += summary.OnWorldSaved;
        return summary;
    }

    private void Update()
    {
        NewWorldSeed.Settle();
        float now = Time.realtimeSinceStartup;
        if (now < _nextPoll)
        {
            return;
        }

        _nextPoll = now + PlayerPollSeconds;
        NotePlayersOnline();
    }

    private void NotePlayersOnline()
    {
        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer())
        {
            return;
        }

        foreach (ZNetPeer peer in network.GetPeers())
        {
            string name = (peer?.m_playerName ?? string.Empty).Trim();
            if (name.Length > 0)
            {
                _playersSinceSave.Add(name);
            }
        }
    }

    private void OnWorldSaved()
    {
        try
        {
            World? world = ZNet.World;
            ZNet? network = ZNet.instance;
            if (world == null || network == null || !world.m_fileSource.IsLocal())
            {
                return;
            }

            NotePlayersOnline();
            _gameVersion ??= GameVersionDetector.TryDetect(_log!) ?? string.Empty;
            EnvMan? environment = EnvMan.instance;
            float dayLength = environment != null && environment.m_dayLengthSec > 0f ? environment.m_dayLengthSec : 1800f;
            int day = (int)Math.Floor(network.GetTimeSeconds() / dayLength);
            var bosses = new List<string>();
            foreach (string line in ZoneSystem.instance != null ? ZoneSystem.instance.GetGlobalKeys() : new List<string>())
            {
                string key = (line ?? string.Empty).Trim();
                int space = key.IndexOf(' ');
                if (space > 0)
                {
                    key = key.Substring(0, space);
                }

                if (key.StartsWith("defeated_", StringComparison.OrdinalIgnoreCase) && !bosses.Contains(key.ToLowerInvariant()))
                {
                    bosses.Add(key.ToLowerInvariant());
                }
            }

            bosses.Sort(StringComparer.Ordinal);
            var players = new List<string>(_playersSinceSave);
            players.Sort(StringComparer.Ordinal);
            string json = BuildJson(
                world.m_name ?? string.Empty,
                world.m_seedName ?? string.Empty,
                day,
                bosses,
                players,
                WorldSavePatch.LastSavedUnixMs,
                ValheimOnePlugin.PluginVersion,
                _gameVersion);
            string path = SummaryPath(SaveSystem.GetWorldsSaveRootPath(world.m_fileSource), world.m_worldName ?? world.m_name ?? "world");

            // Whoever is still on counts toward the next save too.
            _playersSinceSave.Clear();
            NotePlayersOnline();
            ThreadPool.QueueUserWorkItem(_ => Write(path, json));
        }
        catch (Exception exception)
        {
            WarnOnce($"[WorldSummary] could not describe the save ({exception.GetType().Name}: {exception.Message}).");
        }
    }

    private void Write(string path, string json)
    {
        lock (_writeLock)
        {
            try
            {
                WriteAtomically(path, json);
            }
            catch (Exception exception)
            {
                WarnOnce($"[WorldSummary] could not write {Path.GetFileName(path)} ({exception.GetType().Name}: {exception.Message}).");
            }
        }
    }

    private void WarnOnce(string text)
    {
        if (_writeWarningLogged)
        {
            return;
        }

        _writeWarningLogged = true;
        _log?.Warning(text);
    }

    internal static string SummaryPath(string worldsRoot, string worldName)
    {
        return Path.Combine(worldsRoot, worldName + Suffix);
    }

    // The new text goes to <name>.tmp first, then replaces the old file in one step.
    internal static void WriteAtomically(string path, string json)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        try
        {
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    internal static string BuildJson(
        string worldName,
        string seedName,
        int day,
        IList<string> bossesDefeated,
        IList<string> playersSinceLastSave,
        long savedUnixMs,
        string pluginVersion,
        string gameVersion)
    {
        var json = new StringBuilder(256 + (playersSinceLastSave.Count * 24));
        json.Append("{\"worldName\":").Append(JsonWriter.Quote(worldName));
        json.Append(",\"seedName\":").Append(JsonWriter.Quote(seedName));
        json.Append(",\"day\":").Append(day.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"bossesDefeated\":");
        AppendArray(json, bossesDefeated);
        json.Append(",\"playersSinceLastSave\":");
        AppendArray(json, playersSinceLastSave);
        json.Append(",\"savedUnixMs\":").Append(savedUnixMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"pluginVersion\":").Append(JsonWriter.Quote(pluginVersion));
        json.Append(",\"gameVersion\":").Append(JsonWriter.Quote(gameVersion));
        json.Append('}');
        return json.ToString();
    }

    private static void AppendArray(StringBuilder json, IList<string> values)
    {
        json.Append('[');
        for (int index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append(JsonWriter.Quote(values[index]));
        }

        json.Append(']');
    }
}

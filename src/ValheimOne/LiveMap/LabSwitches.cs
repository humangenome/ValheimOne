#if VALHEIMONE_LAB
using System;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

// Lab builds only (dotnet build -p:ValheimOneLab=true): a plain text file next to valheimone.cfg that
// switches measurement settings while the server runs, so one session can measure the entity scan
// both ways without touching the server's own configuration. Release builds contain none of this.
//
//   # BepInEx/config/valheimone-lab.txt, re-read within 5 s of a change; delete it for the defaults
//   EntityLayer = on        # on | off | config (the file's [LiveMap] EntityLayer)
//   EntityScan = walk       # walk (the one-pass scan) | legacy (the game's per-prefab search)
//   ScanLog = on            # one log line per entity scan
//   WalkBudgetMs = 2        # entity walk time per frame, 0.05..50
//   CensusBudgetMs = 2      # object census time per frame, 0.05..50
internal static class LabSwitches
{
    private const float PollSeconds = 5f;

    private static float _nextPoll;
    private static DateTime _lastWrite = DateTime.MinValue;
    private static bool _announced;

    internal static bool? EntityLayer { get; private set; }

    public static string FilePath => Path.Combine(Paths.ConfigPath, "valheimone-lab.txt");

    public static void Poll(ModLogger log)
    {
        if (!_announced)
        {
            _announced = true;
            log.Info("[Lab] lab build " + ValheimOnePlugin.PluginVersion + ": switches are read from " + FilePath);
        }

        float now = Time.realtimeSinceStartup;
        if (now < _nextPoll)
        {
            return;
        }

        _nextPoll = now + PollSeconds;
        try
        {
            if (!File.Exists(FilePath))
            {
                if (_lastWrite != DateTime.MinValue)
                {
                    _lastWrite = DateTime.MinValue;
                    Apply(Array.Empty<string>(), log);
                }

                return;
            }

            DateTime write = File.GetLastWriteTimeUtc(FilePath);
            if (write == _lastWrite)
            {
                return;
            }

            _lastWrite = write;
            Apply(File.ReadAllLines(FilePath), log);
        }
        catch (Exception exception)
        {
            log.Warning("[Lab] switch file could not be read: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void Apply(string[] lines, ModLogger log)
    {
        bool? entityLayer = null;
        bool legacy = false;
        bool scanLog = false;
        double walkBudget = ZdoWorldWalker.DefaultBudgetMilliseconds;
        double censusBudget = ZdoWorldWalker.DefaultBudgetMilliseconds;
        foreach (string raw in lines)
        {
            string line = raw;
            int comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line.Substring(0, comment);
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = line.Substring(0, equals).Trim().ToLowerInvariant();
            string value = line.Substring(equals + 1).Trim().ToLowerInvariant();
            switch (key)
            {
                case "entitylayer":
                    entityLayer = value == "on" || value == "true" ? true :
                        value == "off" || value == "false" ? false : (bool?)null;
                    break;
                case "entityscan":
                    legacy = value == "legacy";
                    break;
                case "scanlog":
                    scanLog = value == "on" || value == "true";
                    break;
                case "walkbudgetms":
                    walkBudget = ReadBudget(value, walkBudget, log);
                    break;
                case "censusbudgetms":
                    censusBudget = ReadBudget(value, censusBudget, log);
                    break;
                default:
                    log.Warning("[Lab] unknown switch '" + key + "' ignored");
                    break;
            }
        }

        EntityLayer = entityLayer;
        EntityTracker.LegacyScanSwitch = legacy;
        EntityTracker.LogScansSwitch = scanLog;
        EntityTracker.WalkBudgetMilliseconds = walkBudget;
        ObjectCensus.BudgetMilliseconds = censusBudget;
        log.Info(
            "[Lab] switches: EntityLayer=" + (entityLayer == null ? "config" : entityLayer.Value ? "on" : "off") +
            " EntityScan=" + (legacy ? "legacy" : "walk") +
            " ScanLog=" + (scanLog ? "on" : "off") +
            " WalkBudgetMs=" + walkBudget.ToString("0.##", CultureInfo.InvariantCulture) +
            " CensusBudgetMs=" + censusBudget.ToString("0.##", CultureInfo.InvariantCulture) +
            (lines.Length == 0 ? " (no switch file: defaults)" : string.Empty));
    }

    private static double ReadBudget(string value, double fallback, ModLogger log)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            parsed >= 0.05d && parsed <= 50d)
        {
            return parsed;
        }

        log.Warning("[Lab] budget '" + value + "' ignored (0.05..50 ms)");
        return fallback;
    }
}
#endif

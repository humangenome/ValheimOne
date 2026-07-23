using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;
using ValheimOne.LiveMap;
using ValheimOne.Modules;

namespace ValheimOne.ActivityLog;

public sealed class ActivityLogModule : IFeatureModule
{
    private readonly FeatureRegistry _registry;
    private readonly FeatureDefinition _feature;
    private readonly ActivityLogConfig _config;
    private readonly ActivityLogWorker _worker;
    private readonly ServerSessionEventSource _sessionEvents;
    private ActivityLogBehaviour? _behaviour;
    private bool _shutdown;

    public ActivityLogModule(
        FeatureRegistry registry,
        string dataDirectory,
        ServerSessionEventSource sessionEvents,
        ModLogger log)
    {
        _registry = registry;
        _sessionEvents = sessionEvents;
        _feature = registry.Register(
            Name,
            Section,
            Classification,
            enabledByDefault: true,
            "Enable the server-side JSONL activity log and authenticated LiveMap Saga feed.");
        ConfigEntryInt retentionDays = _feature.Int(
            "RetentionDays",
            30,
            "Days of UTC activity files to retain, clamped to 1..3650.");
        _config = new ActivityLogConfig(retentionDays);
        _worker = new ActivityLogWorker(dataDirectory, () => _config.RetentionDays, log);
    }

    public string Name => "Activity log";

    public string Section => "ActivityLog";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.ServerOnly;

    public string DataDirectory => _worker.DataDirectory;

    internal void ConfigureGhostRetention(Func<int> getRetentionDays)
    {
        _worker.ConfigureGhostRetention(getRetentionDays);
    }

    internal void RecordPlayerGhost(
        string characterName,
        float x,
        float z,
        long lastSeenUnixMs,
        long lastSessionSeconds,
        bool positionShared)
    {
        if (_shutdown)
        {
            return;
        }

        _worker.UpsertGhost(
            characterName,
            x,
            z,
            lastSeenUnixMs,
            lastSessionSeconds,
            positionShared);
    }

    internal void CopyGhosts(List<PlayerGhostEntry> into)
    {
        _worker.CopyGhosts(into);
    }

    public void ApplyPatches(Harmony harmony)
    {
        _ = harmony;
        _worker.Start();
        var host = new GameObject("ValheimOne.ActivityLog")
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        UnityEngine.Object.DontDestroyOnLoad(host);
        _behaviour = ActivityLogBehaviour.Initialize(host, this, _sessionEvents);
    }

    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        ActivityLogBehaviour? behaviour = _behaviour;
        behaviour?.StopPermanently(recordServerStop: true);
        _shutdown = true;
        _behaviour = null;
        _worker.StopAndFlush(2000);
        if (behaviour != null)
        {
            UnityEngine.Object.Destroy(behaviour.gameObject);
        }
    }

    internal ActivityLogHealthSnapshot GetHealth()
    {
        return _worker.GetHealth(IsEnabled && !_shutdown);
    }

    internal bool ActivityFeedEnabled => IsEnabled && !_shutdown;

    internal long LatestActivityCursor => _worker.LatestActivityCursor;

    internal long CopyActivityAfter(
        long cursor,
        int maximum,
        List<ActivityFeedEntry> into)
    {
        return _worker.CopyActivityAfter(cursor, maximum, into);
    }

    internal long CopyConsoleHistoryAfter(
        long cursor,
        int maximum,
        List<ConsoleHistoryEntry> into)
    {
        return _worker.CopyConsoleHistoryAfter(cursor, maximum, into);
    }

    internal void RecordAdminCommand(
        string operatorName,
        string command,
        string result,
        string reason,
        string source,
        string output,
        bool appendHistory)
    {
        string safeOperator = SanitizeOperator(SanitizeSensitiveText(operatorName));
        string safeCommand = SanitizeSensitiveText(command);
        string safeResult = NormalizeResult(result);
        string safeReason = SanitizeReason(reason);
        string safeSource = NormalizeSource(source);
        string safeOutput = Truncate(SanitizeSensitiveText(output), 300);

        if (IsEnabled && !_shutdown)
        {
            var data = new StringBuilder(192 + safeCommand.Length);
            data.Append('{');
            data.Append("\"operator\":").Append(JsonWriter.Quote(safeOperator));
            data.Append(",\"command\":").Append(JsonWriter.Quote(safeCommand));
            data.Append(",\"result\":").Append(JsonWriter.Quote(safeResult));
            data.Append(",\"reason\":").Append(JsonWriter.Quote(safeReason));
            data.Append(",\"source\":").Append(JsonWriter.Quote(safeSource));
            data.Append('}');
            Enqueue("admin.command", data.ToString());
        }

        if (appendHistory && !_shutdown)
        {
            _worker.AppendConsoleHistory(
                safeOperator,
                safeCommand,
                safeOutput,
                safeResult);
        }
    }

    internal void RecordAdminAction(
        string operatorName,
        string action,
        string? target,
        string result,
        string reason,
        string source)
    {
        if (!IsEnabled || _shutdown)
        {
            return;
        }

        string safeOperator = SanitizeOperator(SanitizeSensitiveText(operatorName));
        string safeAction = Truncate(SanitizeSensitiveText(action).Trim(), 64);
        string safeTarget = SanitizeSensitiveText(target ?? string.Empty);
        string safeResult = NormalizeResult(result);
        string safeReason = SanitizeReason(reason);
        string safeSource = NormalizeSource(source);
        var data = new StringBuilder(192 + safeTarget.Length);
        data.Append('{');
        data.Append("\"operator\":").Append(JsonWriter.Quote(safeOperator));
        data.Append(",\"action\":").Append(JsonWriter.Quote(safeAction));
        if (target != null)
        {
            data.Append(",\"target\":").Append(JsonWriter.Quote(safeTarget));
        }

        data.Append(",\"result\":").Append(JsonWriter.Quote(safeResult));
        data.Append(",\"reason\":").Append(JsonWriter.Quote(safeReason));
        data.Append(",\"source\":").Append(JsonWriter.Quote(safeSource));
        data.Append('}');
        Enqueue("admin.action", data.ToString());
    }

    internal void RecordServerStart()
    {
        RecordEmpty("server.start");
    }

    internal void RecordServerStop()
    {
        RecordEmpty("server.stop");
    }

    internal void RecordPlayerJoin(string name, long steamId)
    {
        if (!IsEnabled || _shutdown)
        {
            return;
        }

        var data = new StringBuilder(96);
        data.Append("{\"name\":").Append(JsonWriter.Quote(SanitizeSensitiveText(name)));
        if (steamId > 0L)
        {
            data.Append(",\"steamId\":").Append(JsonWriter.Quote(
                steamId.ToString(CultureInfo.InvariantCulture)));
        }

        data.Append('}');
        Enqueue("player.join", data.ToString());
    }

    internal void RecordPlayerLeave(string name, long sessionSeconds)
    {
        if (!IsEnabled || _shutdown)
        {
            return;
        }

        var data = new StringBuilder(96);
        data.Append("{\"name\":").Append(JsonWriter.Quote(SanitizeSensitiveText(name)));
        data.Append(",\"sessionSeconds\":").Append(
            Math.Max(0L, sessionSeconds).ToString(CultureInfo.InvariantCulture));
        data.Append('}');
        Enqueue("player.leave", data.ToString());
    }

    internal void RecordPlayerDeath(string name)
    {
        RecordName("player.death", name);
    }

    internal void RecordRaidStarted(string name)
    {
        RecordName("raid.start", name);
    }

    internal void RecordRaidEnded(string name)
    {
        RecordName("raid.end", name);
    }

    internal void RecordWorldSave()
    {
        RecordEmpty("world.save");
    }

    internal void RecordDayChanged(int day)
    {
        if (!IsEnabled || _shutdown)
        {
            return;
        }

        Enqueue(
            "day.change",
            "{\"day\":" + day.ToString(CultureInfo.InvariantCulture) + "}");
    }

    private void RecordName(string type, string name)
    {
        if (!IsEnabled || _shutdown)
        {
            return;
        }

        Enqueue(
            type,
            "{\"name\":" + JsonWriter.Quote(SanitizeSensitiveText(name)) + "}");
    }

    private void RecordEmpty(string type)
    {
        if (IsEnabled && !_shutdown)
        {
            Enqueue(type, "{}");
        }
    }

    private void Enqueue(string type, string dataJson)
    {
        _worker.EnqueueActivity(new ActivityEventRecord(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            type,
            dataJson));
    }

    private string SanitizeSensitiveText(string? value)
    {
        string sanitized = value ?? string.Empty;
        foreach (FeatureDefinition feature in _registry.Features)
        {
            foreach (IConfigEntry entry in feature.Keys)
            {
                if (!entry.Definition.IsSensitive)
                {
                    continue;
                }

                string secret = entry.GetSerializedValue();
                if (secret.Length > 0)
                {
                    sanitized = ReplaceOrdinal(sanitized, secret, "[redacted]");
                }
            }
        }

        sanitized = RedactDelimitedValue(sanitized, "https://discord.com/api/webhooks/");
        sanitized = RedactDelimitedValue(sanitized, "https://discordapp.com/api/webhooks/");
        sanitized = RedactNamedValue(sanitized, "token=");
        sanitized = RedactNamedValue(sanitized, "X-LiveMap-Token:");
        return sanitized;
    }

    private static string SanitizeOperator(string? value)
    {
        string candidate = value ?? string.Empty;
        var sanitized = new StringBuilder(candidate.Length);
        for (int index = 0; index < candidate.Length; index++)
        {
            char character = candidate[index];
            if (!char.IsControl(character))
            {
                sanitized.Append(character);
            }
        }

        string result = sanitized.ToString().Trim();
        if (result.Length > 64)
        {
            result = result.Substring(0, 64).Trim();
        }

        return result.Length == 0 ? "unknown" : result;
    }

    private string SanitizeReason(string? value)
    {
        string candidate = SanitizeSensitiveText(value);
        var sanitized = new StringBuilder(candidate.Length);
        for (int index = 0; index < candidate.Length; index++)
        {
            char character = candidate[index];
            sanitized.Append(char.IsControl(character) ? ' ' : character);
        }

        return Truncate(sanitized.ToString().Trim(), 160);
    }

    private static string NormalizeResult(string? value)
    {
        return string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase)
            ? "ok"
            : "error";
    }

    private static string NormalizeSource(string? value)
    {
        if (string.Equals(value, "panel", StringComparison.OrdinalIgnoreCase))
        {
            return "panel";
        }

        return string.Equals(value, "web", StringComparison.OrdinalIgnoreCase)
            ? "web"
            : "token";
    }

    private static string ReplaceOrdinal(string value, string search, string replacement)
    {
        int index = value.IndexOf(search, StringComparison.Ordinal);
        if (index < 0)
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        int start = 0;
        while (index >= 0)
        {
            result.Append(value, start, index - start);
            result.Append(replacement);
            start = index + search.Length;
            index = value.IndexOf(search, start, StringComparison.Ordinal);
        }

        result.Append(value, start, value.Length - start);
        return result.ToString();
    }

    private static string RedactDelimitedValue(string value, string prefix)
    {
        int index = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            int end = index + prefix.Length;
            while (end < value.Length && !char.IsWhiteSpace(value[end]) &&
                   value[end] != '"' && value[end] != '\'')
            {
                end++;
            }

            value = value.Substring(0, index) + "[redacted-webhook]" + value.Substring(end);
            index = value.IndexOf(prefix, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static string RedactNamedValue(string value, string marker)
    {
        int index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            int valueStart = index + marker.Length;
            while (valueStart < value.Length && char.IsWhiteSpace(value[valueStart]))
            {
                valueStart++;
            }

            int end = valueStart;
            while (end < value.Length && !char.IsWhiteSpace(value[end]) &&
                   value[end] != '&' && value[end] != '"' && value[end] != '\'')
            {
                end++;
            }

            value = value.Substring(0, valueStart) + "[redacted]" + value.Substring(end);
            index = value.IndexOf(marker, valueStart + 1, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength
            ? value
            : value.Substring(0, maximumLength);
    }
}

using System;
using System.Globalization;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class ServerShutdownScheduler
{
    public const int MinimumSeconds = 5;
    public const int MaximumSeconds = 3600;

    private const int MaximumMessageLength = 256;
    private const int ShoutType = 2;

    private static readonly int[] CountdownMarks = { 60, 30, 10 };

    private readonly ModLogger _log;
    private volatile ServerShutdownSnapshot _snapshot = ServerShutdownSnapshot.Empty;
    private float _deadlineRealtime;
    private int _totalSeconds;
    private bool _announcedSixty;
    private bool _announcedThirty;
    private bool _announcedTen;

    public ServerShutdownScheduler(ModLogger log)
    {
        _log = log;
    }

    public ServerShutdownSnapshot Snapshot => _snapshot;

    public ShutdownActionResult Arm(int requestedSeconds, string message)
    {
        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer() || ZRoutedRpc.instance == null)
        {
            return ShutdownActionResult.Failure("server unavailable", _snapshot);
        }

        int seconds = ClampSeconds(requestedSeconds);
        string normalizedMessage = NormalizeMessage(message);
        long deadlineUnixMs = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeMilliseconds();
        _deadlineRealtime = Time.realtimeSinceStartup + seconds;
        _totalSeconds = seconds;
        _announcedSixty = false;
        _announcedThirty = false;
        _announcedTen = false;
        _snapshot = new ServerShutdownSnapshot(
            pending: true,
            deadlineUnixMs,
            seconds,
            normalizedMessage);

        AnnounceCountdown(seconds);
        return ShutdownActionResult.Success(changed: true, _snapshot);
    }

    public ShutdownActionResult Cancel()
    {
        ServerShutdownSnapshot snapshot = _snapshot;
        if (!snapshot.Pending)
        {
            return ShutdownActionResult.Success(changed: false, snapshot);
        }

        Clear();
        const string line = "Server shutdown cancelled";
        BroadcastShout(line);
        LogLine(line);
        return ShutdownActionResult.Success(changed: true, _snapshot);
    }

    public void Tick()
    {
        if (!_snapshot.Pending)
        {
            return;
        }

        float remaining = _deadlineRealtime - Time.realtimeSinceStartup;
        if (remaining <= 0f)
        {
            Clear();
            ForceSaveAndQuit();
            return;
        }

        for (int index = 0; index < CountdownMarks.Length; index++)
        {
            int mark = CountdownMarks[index];
            if (mark >= _totalSeconds || remaining > mark || IsMarkAnnounced(mark))
            {
                continue;
            }

            SetMarkAnnounced(mark);
            AnnounceCountdown(mark);
        }
    }

    public static int ClampSeconds(int seconds)
    {
        return Math.Max(MinimumSeconds, Math.Min(MaximumSeconds, seconds));
    }

    private void AnnounceCountdown(int seconds)
    {
        string line = "Server shutting down in " +
                      seconds.ToString(CultureInfo.InvariantCulture) + "s";
        string message = _snapshot.Message;
        if (message.Length > 0)
        {
            line += ": " + message;
        }

        BroadcastShout(line);
        LogLine(line);
    }

    private static string NormalizeMessage(string message)
    {
        string value = (message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return value.Length <= MaximumMessageLength
            ? value
            : value.Substring(0, MaximumMessageLength);
    }

    private void BroadcastShout(string text)
    {
        try
        {
            ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
            if (routedRpc == null)
            {
                return;
            }

            UserInfo userInfo = ServerUserInfo.Create();
            routedRpc.InvokeRoutedRPC(
                ZRoutedRpc.Everybody,
                "ChatMessage",
                Vector3.zero,
                ShoutType,
                userInfo,
                text);
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] could not broadcast shutdown message: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void ForceSaveAndQuit()
    {
        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer())
        {
            LogFailure("server unavailable at shutdown deadline");
            return;
        }

        try
        {
            LogLine("Shutdown deadline reached; forcing a synchronous world save");
            network.Save(
                sync: true,
                saveOtherPlayerProfiles: true,
                waitForNextFrame: false);
            LogLine("Synchronous world save completed; exiting server");

            // Valheim 0.221.12's publicized assemblies show Application.Quit invoking
            // Game.OnApplicationQuit, which calls Game.Shutdown -> ZNet.Shutdown(save: true)
            // -> StopAll. The explicit blocking ZNet.Save above guarantees the world save
            // finishes before that save-aware game shutdown path begins.
            Application.Quit();
        }
        catch (Exception exception)
        {
            LogFailure(
                $"forced save failed; shutdown aborted: " +
                $"{exception.GetType().Name}: {exception.Message}");
            BroadcastShout("Server shutdown aborted because the world save failed");
        }
    }

    private void Clear()
    {
        _snapshot = ServerShutdownSnapshot.Empty;
        _deadlineRealtime = 0f;
        _totalSeconds = 0;
        _announcedSixty = false;
        _announcedThirty = false;
        _announcedTen = false;
    }

    private bool IsMarkAnnounced(int mark)
    {
        switch (mark)
        {
            case 60:
                return _announcedSixty;
            case 30:
                return _announcedThirty;
            default:
                return _announcedTen;
        }
    }

    private void SetMarkAnnounced(int mark)
    {
        switch (mark)
        {
            case 60:
                _announcedSixty = true;
                break;
            case 30:
                _announcedThirty = true;
                break;
            default:
                _announcedTen = true;
                break;
        }
    }

    private void LogLine(string line)
    {
        _log.Info("[LiveMap] " + line);
        // Web-console commands capture this directly; outside a command, the console's
        // LogRingBuffer receives the same line from the BepInEx log listener.
        ConsoleBridge.CaptureTerminalOutput(line);
    }

    private void LogFailure(string line)
    {
        _log.Error("[LiveMap] " + line);
        ConsoleBridge.CaptureTerminalOutput(line);
    }
}

internal sealed class ServerShutdownSnapshot
{
    public static readonly ServerShutdownSnapshot Empty = new ServerShutdownSnapshot(
        pending: false,
        deadlineUnixMs: 0L,
        totalSeconds: 0,
        message: string.Empty);

    public ServerShutdownSnapshot(
        bool pending,
        long deadlineUnixMs,
        int totalSeconds,
        string message)
    {
        Pending = pending;
        DeadlineUnixMs = deadlineUnixMs;
        TotalSeconds = totalSeconds;
        Message = message;
    }

    public bool Pending { get; }

    public long DeadlineUnixMs { get; }

    public int TotalSeconds { get; }

    public string Message { get; }
}

internal sealed class ShutdownActionResult
{
    private ShutdownActionResult(
        bool ok,
        bool changed,
        string error,
        ServerShutdownSnapshot snapshot)
    {
        Ok = ok;
        Changed = changed;
        Error = error;
        Snapshot = snapshot;
    }

    public bool Ok { get; }

    public bool Changed { get; }

    public string Error { get; }

    public ServerShutdownSnapshot Snapshot { get; }

    public static ShutdownActionResult Success(
        bool changed,
        ServerShutdownSnapshot snapshot)
    {
        return new ShutdownActionResult(true, changed, string.Empty, snapshot);
    }

    public static ShutdownActionResult Failure(
        string error,
        ServerShutdownSnapshot snapshot)
    {
        return new ShutdownActionResult(false, false, error, snapshot);
    }
}

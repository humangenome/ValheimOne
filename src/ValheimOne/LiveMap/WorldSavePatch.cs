using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

// When a world save really ends, and how. The game saves on a background thread
// (ZNet.SaveWorldThread) that ends in exactly one SaveSystem.EndSave call: writeOK true after
// "World save (5/5) done", false after "World save (5/5) FAILED" and in the catch that logs
// "Error saving world!". The postfix there only records the outcome; Pump, on the main thread,
// raises WorldSaved or WorldSaveFailed, so no listener ever runs on the save thread.
// Until this change the save time was taken when a save started, so a failed save read as saved.
internal static class WorldSavePatch
{
    // A failed save logs its reason just after EndSave; wait this long for it before reporting.
    private const double FailureMessageWaitSeconds = 2d;
    private const int MaximumMessageLength = 160;

    private static readonly object MessageLock = new object();
    private static long _lastSavedUnixMs;
    private static long _lastSaveFailedUnixMs;
    private static long _lastSaveDurationMs;
    private static long _saveStartedTimestamp;
    private static int _pendingOutcome;
    private static long _pendingTimestamp;
    private static long _pendingUnixMs;
    private static string _saveMessage = string.Empty;
    private static bool _applied;
    private static ModLogger? _log;

    public static event Action? WorldSaved;

    public static event Action<WorldSaveFailure>? WorldSaveFailed;

    // The end of the last save that worked (0 before the first one).
    public static long LastSavedUnixMs => Interlocked.Read(ref _lastSavedUnixMs);

    public static long LastSaveFailedUnixMs => Interlocked.Read(ref _lastSaveFailedUnixMs);

    // From the game's save start to its end, for the last save that worked.
    public static long LastSaveDurationMs => Interlocked.Read(ref _lastSaveDurationMs);

    public static void ApplyPatches(Harmony harmony, ModLogger log)
    {
        _log = log;
        if (_applied)
        {
            return;
        }

        MethodInfo endSave = AccessTools.Method(
            typeof(SaveSystem),
            nameof(SaveSystem.EndSave),
            new[] { typeof(FileHelpers.FileSource), typeof(bool) }) ??
            throw new MissingMethodException(nameof(SaveSystem), nameof(SaveSystem.EndSave));
        harmony.Patch(
            endSave,
            postfix: new HarmonyMethod(typeof(WorldSavePatch), nameof(EndSavePostfix)));
        ZNet.WorldSaveStarted = (Action)Delegate.Combine(ZNet.WorldSaveStarted, new Action(OnWorldSaveStarted));
        Application.logMessageReceivedThreaded += OnLogMessage;
        _applied = true;
    }

    // Main thread: the game is about to start a save (ZNet.SaveWorld).
    internal static void OnWorldSaveStarted()
    {
        Interlocked.Exchange(ref _saveStartedTimestamp, Stopwatch.GetTimestamp());
        lock (MessageLock)
        {
            _saveMessage = string.Empty;
        }
    }

    // The save thread, as the save ends.
    internal static void EndSavePostfix(bool __1)
    {
        try
        {
            Interlocked.Exchange(ref _pendingTimestamp, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _pendingUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Interlocked.Exchange(ref _pendingOutcome, __1 ? 1 : 2);
        }
        catch
        {
            // Never let bookkeeping escape into the game's save path.
        }
    }

    // Any thread: keep the game's own reason for a failed save.
    internal static void OnLogMessage(string condition, string stackTrace, LogType type)
    {
        if ((type != LogType.Error && type != LogType.Exception) || string.IsNullOrEmpty(condition))
        {
            return;
        }

        bool reason = condition.StartsWith("Error saving world!", StringComparison.Ordinal) ||
                      condition.StartsWith("World save (5/5) FAILED", StringComparison.Ordinal) ||
                      condition.StartsWith("Cloud save to", StringComparison.Ordinal);
        if (!reason)
        {
            return;
        }

        lock (MessageLock)
        {
            // "Error saving world!" names the cause; it wins over the generic FAILED line.
            if (_saveMessage.Length == 0 ||
                (condition.StartsWith("Error saving world!", StringComparison.Ordinal) &&
                 !_saveMessage.StartsWith("Error saving world!", StringComparison.Ordinal)))
            {
                _saveMessage = Shorten(condition);
            }
        }
    }

    // Main thread, every frame.
    public static void Pump()
    {
        Pump(Stopwatch.GetTimestamp());
    }

    internal static void Pump(long nowTimestamp)
    {
        int outcome = Volatile.Read(ref _pendingOutcome);
        if (outcome == 0)
        {
            return;
        }

        long endedTimestamp = Interlocked.Read(ref _pendingTimestamp);
        long endedUnixMs = Interlocked.Read(ref _pendingUnixMs);
        string message;
        lock (MessageLock)
        {
            message = _saveMessage;
        }

        if (outcome == 2 && message.Length == 0 &&
            (nowTimestamp - endedTimestamp) < FailureMessageWaitSeconds * Stopwatch.Frequency)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _pendingOutcome, 0, outcome) != outcome)
        {
            return;
        }

        if (outcome == 1)
        {
            long started = Interlocked.Read(ref _saveStartedTimestamp);
            long duration = started > 0L && endedTimestamp >= started
                ? (long)((endedTimestamp - started) * 1000d / Stopwatch.Frequency)
                : 0L;
            Interlocked.Exchange(ref _lastSaveDurationMs, duration);
            Interlocked.Exchange(ref _lastSavedUnixMs, endedUnixMs);
            Raise(WorldSaved, "world saved");
            return;
        }

        Interlocked.Exchange(ref _lastSaveFailedUnixMs, endedUnixMs);
        var failure = new WorldSaveFailure(
            message.StartsWith("Error saving world!", StringComparison.Ordinal) ? "exception" : "write",
            message,
            endedUnixMs);
        Action<WorldSaveFailure>? callbacks = WorldSaveFailed;
        if (callbacks != null)
        {
            foreach (Action<WorldSaveFailure> callback in callbacks.GetInvocationList())
            {
                try
                {
                    callback(failure);
                }
                catch (Exception exception)
                {
                    LogCallbackFailure("world save failed", exception);
                }
            }
        }
    }

    private static void Raise(Action? callbacks, string name)
    {
        if (callbacks == null)
        {
            return;
        }

        foreach (Action callback in callbacks.GetInvocationList())
        {
            try
            {
                callback();
            }
            catch (Exception exception)
            {
                LogCallbackFailure(name, exception);
            }
        }
    }

    private static void LogCallbackFailure(string name, Exception exception)
    {
        try
        {
            _log?.Warning($"[WorldSave] {name} notification failed: {exception.GetType().Name}.");
        }
        catch
        {
            // Never allow diagnostics to escape.
        }
    }

    // The game's line without its stack trace, one line, at most 160 characters.
    private static string Shorten(string condition)
    {
        string text = condition;
        int stack = text.IndexOf("StackTrace:", StringComparison.Ordinal);
        if (stack >= 0)
        {
            text = text.Substring(0, stack);
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= MaximumMessageLength ? text : text.Substring(0, MaximumMessageLength);
    }
}

internal sealed class WorldSaveFailure
{
    public WorldSaveFailure(string stage, string message, long unixMs)
    {
        Stage = stage;
        Message = message;
        UnixMs = unixMs;
    }

    // "write": the game could not write the files ("World save (5/5) FAILED");
    // "exception": the save threw ("Error saving world!").
    public string Stage { get; }

    public string Message { get; }

    public long UnixMs { get; }
}

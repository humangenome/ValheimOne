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
    // Each save start gets the next number. The game runs saves one at a time: a save asked for
    // while another runs waits (ZNet.SaveWorld joins the running save thread) and starts after it,
    // so a start seen while a save is still running is queued and becomes the current one only
    // when that save ends. Each end is paired with its own save's start, never with a later one.
    private static readonly object StartLock = new object();
    private static long _startSequence;
    private static SaveStart _current;
    private static SaveStart _queued;
    private static bool _inFlight;
    private static SaveStart _pendingStart;
    private static long _lastSavedStartedUnixMs;
    private static long _lastSaveFailedStartedUnixMs;
    private static long _lastSavedStartSequence;
    private static long _lastSaveFailedStartSequence;
    private static volatile WorldSaveFailure? _lastFailure;
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

    // When the last save that worked, and the last one that failed, started.
    public static long LastSavedStartedUnixMs => Interlocked.Read(ref _lastSavedStartedUnixMs);

    public static long LastSaveFailedStartedUnixMs => Interlocked.Read(ref _lastSaveFailedStartedUnixMs);

    // The number of the newest save start. A caller that read N before asking for a save knows its
    // own save has ended once the last save that worked (or failed) has a start number above N: a
    // save already running at the request has N or less, and the game runs the asked-for save after it.
    public static long StartSequence => Interlocked.Read(ref _startSequence);

    public static long LastSavedStartSequence => Interlocked.Read(ref _lastSavedStartSequence);

    public static long LastSaveFailedStartSequence => Interlocked.Read(ref _lastSaveFailedStartSequence);

    // Whether the game's save thread is still running. Replaceable for tests.
    internal static Func<bool> SaveThreadRunning = GameSaveThreadRunning;

    public static bool Applied => _applied;

    internal static bool CanSeeSaveThread => SaveThreadField != null;

    public static WorldSaveFailure? LastFailure => _lastFailure;

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

    // Main thread: the game is about to start a save (ZNet.SaveWorld). The game raises this before it
    // waits for a save that is still running, so that save has not ended yet.
    internal static void OnWorldSaveStarted()
    {
        bool current;
        lock (StartLock)
        {
            var start = new SaveStart(++_startSequence, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            // A start whose save never got going (the game threw before its save thread began) leaves
            // no end behind: with no save thread running, the new start replaces it.
            current = !_inFlight || !IsSaveThreadRunning();
            if (current)
            {
                _current = start;
                _queued = default;
                _inFlight = true;
            }
            else
            {
                _queued = start;
            }
        }

        if (current)
        {
            lock (MessageLock)
            {
                _saveMessage = string.Empty;
            }
        }
    }

    // The save thread, as the save ends.
    internal static void EndSavePostfix(bool __1)
    {
        try
        {
            SaveStart ended;
            lock (StartLock)
            {
                ended = _inFlight ? _current : default;
                if (_queued.Sequence > 0L)
                {
                    // The queued save really starts now, once this one has ended.
                    _current = new SaveStart(_queued.Sequence, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    _queued = default;
                    _inFlight = true;
                }
                else
                {
                    _inFlight = false;
                }

                _pendingStart = ended;
            }

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
        SaveStart start;
        lock (StartLock)
        {
            start = _pendingStart;
        }

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

        lock (MessageLock)
        {
            _saveMessage = string.Empty;
        }

        if (outcome == 1)
        {
            long started = start.Timestamp;
            long duration = started > 0L && endedTimestamp >= started
                ? (long)((endedTimestamp - started) * 1000d / Stopwatch.Frequency)
                : 0L;
            Interlocked.Exchange(ref _lastSaveDurationMs, duration);
            Interlocked.Exchange(ref _lastSavedStartedUnixMs, start.UnixMs);
            Interlocked.Exchange(ref _lastSavedUnixMs, endedUnixMs);
            Interlocked.Exchange(ref _lastSavedStartSequence, start.Sequence);
            Raise(WorldSaved, "world saved");
            return;
        }

        var failure = new WorldSaveFailure(
            message.StartsWith("Error saving world!", StringComparison.Ordinal) ? "exception" : "write",
            message,
            endedUnixMs);
        _lastFailure = failure;
        Interlocked.Exchange(ref _lastSaveFailedStartedUnixMs, start.UnixMs);
        Interlocked.Exchange(ref _lastSaveFailedUnixMs, endedUnixMs);
        Interlocked.Exchange(ref _lastSaveFailedStartSequence, start.Sequence);
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

    private static bool IsSaveThreadRunning()
    {
        try
        {
            return SaveThreadRunning();
        }
        catch
        {
            // Unknown: treat the earlier save as still running. A save then waits for a later end
            // rather than being credited with an earlier save's end.
            return true;
        }
    }

    private static readonly AccessTools.FieldRef<ZNet, Thread?>? SaveThreadField = FindSaveThreadField();

    private static AccessTools.FieldRef<ZNet, Thread?>? FindSaveThreadField()
    {
        try
        {
            return AccessTools.FieldRefAccess<ZNet, Thread?>("m_saveThread");
        }
        catch
        {
            return null;
        }
    }

    private static bool GameSaveThreadRunning()
    {
        ZNet? network = ZNet.instance;
        if (network == null)
        {
            return false;
        }

        if (SaveThreadField == null)
        {
            return true;
        }

        Thread? thread = SaveThreadField(network);
        return thread != null && thread.IsAlive;
    }

    private readonly struct SaveStart
    {
        public SaveStart(long sequence, long timestamp, long unixMs)
        {
            Sequence = sequence;
            Timestamp = timestamp;
            UnixMs = unixMs;
        }

        public long Sequence { get; }

        public long Timestamp { get; }

        public long UnixMs { get; }
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

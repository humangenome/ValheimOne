using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class ConsoleBridge
{
    private const int JobTimeoutMilliseconds = 10000;
    private const float StatsRefreshSeconds = 2f;
    private const float IdleStatsRefreshSeconds = 30f;
    private const float CommandsRefreshSeconds = 60f;
    private const int MaximumCommandOutputLines = 50;
    private const string TimeoutError = "timed out waiting for main thread";

    private static readonly Lazy<FieldInfo> CommandsField = new(
        () => AccessTools.Field(typeof(Terminal), "commands") ??
              throw new MissingFieldException(typeof(Terminal).FullName, "commands"));

    [ThreadStatic]
    private static List<string>? _terminalOutputCapture;

    private LogRingBuffer? _ringBuffer;
    private readonly ModLogger _log;
    private readonly ConcurrentQueue<IBridgeJob> _jobs = new ConcurrentQueue<IBridgeJob>();
    private readonly int _mainThreadId;
    private volatile StatsSnapshot _stats = StatsSnapshot.Empty;
    private volatile ConsoleCommandInfo[] _knownCommands = Array.Empty<ConsoleCommandInfo>();
    private float _nextStatsRefresh;
    private float _nextCommandsRefresh;
    private double _frameTotalSeconds;
    private float _frameMaxSeconds;
    private int _frameSamples;
    private bool _idle;
    private int _stopped;

    public ConsoleBridge(LogRingBuffer? ringBuffer, ModLogger log)
    {
        _ringBuffer = ringBuffer;
        _log = log;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    public StatsSnapshot Stats => _stats;

    public void SetRingBuffer(LogRingBuffer? ringBuffer)
    {
        _ringBuffer = ringBuffer;
    }

    internal static void CaptureTerminalOutput(string line)
    {
        _terminalOutputCapture?.Add(line);
    }

    public ConsoleExecResult ExecuteCommand(string line)
    {
        return Submit(
            () => ExecuteCommandOnMainThread(line),
            error => ConsoleExecResult.Failure(error));
    }

    public ConsoleActionResult Kick(string target)
    {
        return Submit(
            () => KickOnMainThread(target, _log),
            error => ConsoleActionResult.Failure(error));
    }

    public ConsoleActionResult Ban(string target)
    {
        return Submit(
            () => BanOnMainThread(target, _log),
            error => ConsoleActionResult.Failure(error));
    }

    public ConsoleActionResult Unban(string target)
    {
        return Submit(
            () => UnbanOnMainThread(target, _log),
            error => ConsoleActionResult.Failure(error));
    }

    public ConsoleBanListResult BanList()
    {
        return Submit(
            () => GetBanListOnMainThread(_log),
            error => ConsoleBanListResult.Failure(error));
    }

    public ConsoleSaveResult Save()
    {
        return Submit(
            () => SaveOnMainThread(_log),
            error => ConsoleSaveResult.Failure(error));
    }

    public List<ConsoleCommandInfo> GetKnownCommands()
    {
        ConsoleCommandInfo[] snapshot = _knownCommands;
        return new List<ConsoleCommandInfo>(snapshot);
    }

    public void Pump()
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        try
        {
            AccumulateFrameSample();
            DrainJobs();

            float now = Time.realtimeSinceStartup;
            ZNet? network = ZNet.instance;
            bool idle = (network?.GetPeers()?.Count ?? 0) == 0;
            bool idleChanged = idle != _idle;
            if (idleChanged)
            {
                RefreshStats(now);
                _idle = idle;
                _nextStatsRefresh = now + (idle
                    ? IdleStatsRefreshSeconds
                    : StatsRefreshSeconds);
            }
            else if (now >= _nextStatsRefresh)
            {
                RefreshStats(now);
                _nextStatsRefresh = now + (_idle
                    ? IdleStatsRefreshSeconds
                    : StatsRefreshSeconds);
            }

            if (now >= _nextCommandsRefresh)
            {
                _nextCommandsRefresh = now + CommandsRefreshSeconds;
                RefreshKnownCommands();
            }
        }
        catch (Exception exception)
        {
            LogException("console bridge pump failed", exception);
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        while (_jobs.TryDequeue(out IBridgeJob? job))
        {
            try
            {
                job.Fail("console bridge stopped");
            }
            catch (Exception exception)
            {
                LogException("could not stop a queued console job", exception);
            }
        }
    }

    private T Submit<T>(Func<T> action, Func<string, T> failure) where T : class
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return failure("console bridge stopped");
        }

        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            try
            {
                return action();
            }
            catch (Exception exception)
            {
                LogException("console job failed", exception);
                return failure(GetExceptionMessage(exception));
            }
        }

        var job = new BridgeJob<T>(action, failure);
        _jobs.Enqueue(job);
        if (Volatile.Read(ref _stopped) != 0)
        {
            job.Fail("console bridge stopped");
        }

        if (!job.Wait(JobTimeoutMilliseconds))
        {
            job.Cancel();
            return failure(TimeoutError);
        }

        return job.Result ?? failure("console job completed without a result");
    }

    private void DrainJobs()
    {
        while (_jobs.TryDequeue(out IBridgeJob? job))
        {
            try
            {
                job.Execute();
            }
            catch (Exception exception)
            {
                LogException("console job failed", exception);
                try
                {
                    job.Fail(GetExceptionMessage(exception));
                }
                catch (Exception failureException)
                {
                    LogException("could not report a console job failure", failureException);
                }
            }
        }
    }

    private ConsoleExecResult ExecuteCommandOnMainThread(string line)
    {
        string commandLine = (line ?? string.Empty).Trim();
        if (commandLine.Length == 0)
        {
            return ConsoleExecResult.Failure("command is empty");
        }

        int separator = FindWhitespace(commandLine);
        string commandName = separator < 0
            ? commandLine.ToLowerInvariant()
            : commandLine.Substring(0, separator).ToLowerInvariant();
        Dictionary<string, Terminal.ConsoleCommand> commands = GetCommands();
        if (!commands.TryGetValue(commandName, out Terminal.ConsoleCommand? command) || command == null)
        {
            return ConsoleExecResult.Failure("unknown command");
        }

        global::Console? console = global::Console.instance;
        if (console == null)
        {
            return ConsoleExecResult.Failure("console unavailable");
        }

        bool warnCheat = command.IsCheat && !console.IsCheatsEnabled();
        LogRingBuffer? ringBuffer = _ringBuffer;
        long cursor = ringBuffer?.LatestSeq ?? 0L;
        var directOutput = new List<string>();
        List<string>? previousCapture = _terminalOutputCapture;
        _terminalOutputCapture = directOutput;
        try
        {
            console.TryRunCommand(commandLine, silentFail: false, skipAllowedCheck: true);
        }
        finally
        {
            _terminalOutputCapture = previousCapture;
        }

        int logLimit = warnCheat ? MaximumCommandOutputLines - 1 : MaximumCommandOutputLines;
        var output = new List<string>(MaximumCommandOutputLines);
        if (directOutput.Count > 0)
        {
            for (int index = 0; index < directOutput.Count && output.Count < logLimit; index++)
            {
                output.Add(directOutput[index]);
            }
        }
        else if (ringBuffer != null)
        {
            var entries = new List<LogEntry>();
            ringBuffer.CopyAfter(cursor, int.MaxValue, entries);
            for (int index = 0; index < entries.Count; index++)
            {
                LogEntry entry = entries[index];
                if (string.Equals(entry.Source, "Unity", StringComparison.Ordinal) ||
                    string.Equals(entry.Source, "Unity Log", StringComparison.Ordinal))
                {
                    output.Add(StripConsolePrefixes(entry.Message));
                    if (output.Count >= logLimit)
                    {
                        break;
                    }
                }
            }
        }

        if (warnCheat)
        {
            output.Add(
                "note: this is a cheat command; it may require devcommands to be enabled first.");
        }

        return ConsoleExecResult.Success(output);
    }

    internal static ConsoleActionResult KickOnMainThread(string target, ModLogger? log)
    {
        return RunTargetAction(
            target,
            "kick",
            (network, value) => network.Kick(value),
            log);
    }

    internal static ConsoleActionResult BanOnMainThread(string target, ModLogger? log)
    {
        return RunTargetAction(
            target,
            "ban",
            (network, value) => network.Ban(value),
            log);
    }

    internal static ConsoleActionResult UnbanOnMainThread(string target, ModLogger? log)
    {
        return RunTargetAction(
            target,
            "unban",
            (network, value) => network.Unban(value),
            log);
    }

    private static ConsoleActionResult RunTargetAction(
        string target,
        string actionName,
        Action<ZNet, string> action,
        ModLogger? log)
    {
        string value = (target ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return ConsoleActionResult.Failure("target is empty");
        }

        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer())
        {
            return ConsoleActionResult.Failure("server unavailable");
        }

        try
        {
            action(network, value);
            return ConsoleActionResult.Success();
        }
        catch (Exception exception)
        {
            LogException(log, $"{actionName} failed", exception);
            return ConsoleActionResult.Failure(GetExceptionMessage(exception));
        }
    }

    internal static ConsoleBanListResult GetBanListOnMainThread(ModLogger? log)
    {
        ZNet? network = ZNet.instance;
        if (network == null)
        {
            return ConsoleBanListResult.Success(new List<string>());
        }

        try
        {
            return ConsoleBanListResult.Success(new List<string>(network.Banned));
        }
        catch (Exception exception)
        {
            LogException(log, "could not read the ban list", exception);
            return ConsoleBanListResult.Failure(GetExceptionMessage(exception));
        }
    }

    internal static ConsoleSaveResult SaveOnMainThread(ModLogger? log)
    {
        ZNet? network = ZNet.instance;
        if (network == null || !network.IsServer())
        {
            return ConsoleSaveResult.Failure("server unavailable");
        }

        try
        {
            bool alreadySaving = network.IsSaving();
            network.SaveWorldAndPlayerProfiles();
            return ConsoleSaveResult.Success(alreadySaving);
        }
        catch (Exception exception)
        {
            LogException(log, "save failed", exception);
            return ConsoleSaveResult.Failure(GetExceptionMessage(exception));
        }
    }

    private void AccumulateFrameSample()
    {
        float delta = Time.deltaTime;
        if (delta < 0f || float.IsNaN(delta) || float.IsInfinity(delta))
        {
            return;
        }

        _frameTotalSeconds += delta;
        _frameMaxSeconds = Math.Max(_frameMaxSeconds, delta);
        _frameSamples++;
    }

    private void RefreshStats(float now)
    {
        double frameAverageSeconds = _frameSamples > 0
            ? _frameTotalSeconds / _frameSamples
            : 0d;
        float frameMaxSeconds = _frameMaxSeconds;
        _frameTotalSeconds = 0d;
        _frameMaxSeconds = 0f;
        _frameSamples = 0;

        int players = 0;
        int peers = 0;
        ZNet? network = ZNet.instance;
        if (network != null)
        {
            players = network.GetNrOfPlayers();
            List<ZNetPeer>? networkPeers = network.GetPeers();
            peers = networkPeers?.Count ?? 0;
        }

        long zdoCount = 0;
        ZDOMan? zdoManager = ZDOMan.instance;
        if (zdoManager != null)
        {
            zdoCount = zdoManager.NrOfObjects();
        }

        _stats = new StatsSnapshot(
            now,
            players,
            peers,
            zdoCount,
            GC.GetTotalMemory(false),
            (float)(frameAverageSeconds * 1000d),
            frameMaxSeconds * 1000f,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void RefreshKnownCommands()
    {
        try
        {
            Dictionary<string, Terminal.ConsoleCommand> commands = GetCommands();
            var snapshot = new List<ConsoleCommandInfo>(commands.Count);
            foreach (KeyValuePair<string, Terminal.ConsoleCommand> pair in commands)
            {
                Terminal.ConsoleCommand command = pair.Value;
                if (command == null || command.IsSecret)
                {
                    continue;
                }

                string name = string.IsNullOrEmpty(command.Command) ? pair.Key : command.Command;
                snapshot.Add(new ConsoleCommandInfo(
                    name,
                    command.Description ?? string.Empty,
                    command.IsCheat,
                    command.IsSecret));
            }

            snapshot.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            _knownCommands = snapshot.ToArray();
        }
        catch (Exception exception)
        {
            LogException("could not refresh console commands", exception);
        }
    }

    private static Dictionary<string, Terminal.ConsoleCommand> GetCommands()
    {
        return CommandsField.Value.GetValue(null) as Dictionary<string, Terminal.ConsoleCommand> ??
               throw new InvalidOperationException(
                   "Terminal.commands did not contain a console command dictionary.");
    }

    private static int FindWhitespace(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static string StripConsolePrefixes(string message)
    {
        int timestampEnd = message.IndexOf(": ", StringComparison.Ordinal);
        if (timestampEnd > 0 && DateTime.TryParse(
                message.Substring(0, timestampEnd),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            message = message.Substring(timestampEnd + 2);
        }

        if (message.StartsWith("Console: ", StringComparison.Ordinal))
        {
            message = message.Substring("Console: ".Length);
        }

        return message;
    }

    private void LogException(string context, Exception exception)
    {
        LogException(_log, context, exception);
    }

    private static void LogException(ModLogger? log, string context, Exception exception)
    {
        try
        {
            log?.Warning(
                $"[LiveMap] {context}: {exception.GetType().Name}: {exception.Message}");
        }
        catch
        {
            // A logger failure must not escape the main-thread bridge.
        }
    }

    private static string GetExceptionMessage(Exception exception)
    {
        return string.IsNullOrEmpty(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
    }

    private interface IBridgeJob
    {
        void Execute();

        void Fail(string error);
    }

    private sealed class BridgeJob<T> : IBridgeJob where T : class
    {
        private readonly Func<T> _action;
        private readonly Func<string, T> _failure;
        private readonly ManualResetEventSlim _completed = new ManualResetEventSlim(false);
        private T? _result;
        private int _state;

        public BridgeJob(Func<T> action, Func<string, T> failure)
        {
            _action = action;
            _failure = failure;
        }

        public T? Result => _result;

        public bool Wait(int milliseconds)
        {
            return _completed.Wait(milliseconds);
        }

        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 3, 0) == 0)
            {
                _completed.Set();
            }
        }

        public void Execute()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return;
            }

            Complete(_action());
        }

        public void Fail(string error)
        {
            int state = Volatile.Read(ref _state);
            if (state == 0 && Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return;
            }
            else if (state != 0 && state != 1)
            {
                return;
            }

            Complete(_failure(error));
        }

        private void Complete(T result)
        {
            _result = result;
            Volatile.Write(ref _state, 2);
            _completed.Set();
        }
    }
}

internal sealed class ConsoleActionResult
{
    private ConsoleActionResult(bool ok, string error)
    {
        Ok = ok;
        Error = error;
    }

    public bool Ok { get; }

    public string Error { get; }

    public static ConsoleActionResult Success()
    {
        return new ConsoleActionResult(true, string.Empty);
    }

    public static ConsoleActionResult Failure(string error)
    {
        return new ConsoleActionResult(false, error);
    }
}

internal sealed class ConsoleExecResult
{
    private ConsoleExecResult(bool ok, string error, List<string> output)
    {
        Ok = ok;
        Error = error;
        Output = output;
    }

    public bool Ok { get; }

    public string Error { get; }

    public List<string> Output { get; }

    public static ConsoleExecResult Success(List<string> output)
    {
        return new ConsoleExecResult(true, string.Empty, output);
    }

    public static ConsoleExecResult Failure(string error)
    {
        return new ConsoleExecResult(false, error, new List<string>());
    }
}

internal sealed class ConsoleBanListResult
{
    private ConsoleBanListResult(bool ok, string error, List<string> banned)
    {
        Ok = ok;
        Error = error;
        Banned = banned;
    }

    public bool Ok { get; }

    public string Error { get; }

    public List<string> Banned { get; }

    public static ConsoleBanListResult Success(List<string> banned)
    {
        return new ConsoleBanListResult(true, string.Empty, banned);
    }

    public static ConsoleBanListResult Failure(string error)
    {
        return new ConsoleBanListResult(false, error, new List<string>());
    }
}

internal sealed class ConsoleSaveResult
{
    private ConsoleSaveResult(bool ok, string error, bool alreadySaving)
    {
        Ok = ok;
        Error = error;
        AlreadySaving = alreadySaving;
    }

    public bool Ok { get; }

    public string Error { get; }

    public bool AlreadySaving { get; }

    public static ConsoleSaveResult Success(bool alreadySaving)
    {
        return new ConsoleSaveResult(true, string.Empty, alreadySaving);
    }

    public static ConsoleSaveResult Failure(string error)
    {
        return new ConsoleSaveResult(false, error, false);
    }
}

internal sealed class StatsSnapshot
{
    public static readonly StatsSnapshot Empty = new StatsSnapshot(
        0d,
        0,
        0,
        0L,
        0L,
        0f,
        0f,
        0L);

    public StatsSnapshot(
        double uptimeSeconds,
        int players,
        int peers,
        long zdoCount,
        long monoHeapBytes,
        float frameAvgMs,
        float frameMaxMs,
        long snapshotUnixMs)
    {
        UptimeSeconds = uptimeSeconds;
        Players = players;
        Peers = peers;
        ZdoCount = zdoCount;
        MonoHeapBytes = monoHeapBytes;
        FrameAvgMs = frameAvgMs;
        FrameMaxMs = frameMaxMs;
        SnapshotUnixMs = snapshotUnixMs;
    }

    public double UptimeSeconds { get; }

    public int Players { get; }

    public int Peers { get; }

    public long ZdoCount { get; }

    public long MonoHeapBytes { get; }

    public float FrameAvgMs { get; }

    public float FrameMaxMs { get; }

    public long SnapshotUnixMs { get; }
}

internal sealed class ConsoleCommandInfo
{
    public ConsoleCommandInfo(string name, string description, bool isCheat, bool isSecret)
    {
        Name = name;
        Description = description;
        IsCheat = isCheat;
        IsSecret = isSecret;
    }

    public string Name { get; }

    public string Description { get; }

    public bool IsCheat { get; }

    public bool IsSecret { get; }
}

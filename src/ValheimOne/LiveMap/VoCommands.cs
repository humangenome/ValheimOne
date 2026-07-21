using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimOne.ActivityLog;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal static class VoCommands
{
    private const string InitTerminalMethodName = "InitTerminal";

    private static readonly Lazy<MethodInfo> InitTerminalMethod = new(
        () => AccessTools.Method(typeof(Terminal), InitTerminalMethodName, Type.EmptyTypes) ??
              throw new MissingMethodException(
                  typeof(Terminal).FullName,
                  InitTerminalMethodName));

    private static readonly Lazy<FieldInfo> TerminalInitializedField = new(
        () => AccessTools.Field(typeof(Terminal), "m_terminalInitialized") ??
              throw new MissingFieldException(
                  typeof(Terminal).FullName,
                  "m_terminalInitialized"));

    private static readonly Lazy<FieldInfo> ForceEnvironmentField = new(
        () => AccessTools.Field(typeof(EnvMan), "m_forceEnv") ??
              throw new MissingFieldException(typeof(EnvMan).FullName, "m_forceEnv"));

    private static readonly Lazy<FieldInfo> NextEnvironmentField = new(
        () => AccessTools.Field(typeof(EnvMan), "m_nextEnv") ??
              throw new MissingFieldException(typeof(EnvMan).FullName, "m_nextEnv"));

    private static readonly BossDefinition[] Bosses =
    {
        new BossDefinition("Eikthyr", "defeated_eikthyr"),
        new BossDefinition("The Elder", "defeated_gdking"),
        new BossDefinition("Bonemass", "defeated_bonemass"),
        new BossDefinition("Moder", "defeated_dragon"),
        new BossDefinition("Yagluth", "defeated_goblinking"),
        new BossDefinition("The Queen", "defeated_queen"),
        new BossDefinition("Fader", "defeated_fader"),
    };

    private static readonly Dictionary<long, float> PeerConnectTimes =
        new Dictionary<long, float>();

    private static FeatureRegistry? _registry;
    private static LiveMapConfig? _config;
    private static ActivityLogModule? _activityLog;
    private static ModLogger? _log;

    public static void Initialize(
        FeatureRegistry registry,
        LiveMapConfig config,
        ActivityLogModule activityLog,
        ModLogger log)
    {
        _registry = registry;
        _config = config;
        _activityLog = activityLog;
        _log = log;
    }

    public static void ApplyPatches(Harmony harmony)
    {
        harmony.Patch(
            InitTerminalMethod.Value,
            postfix: new HarmonyMethod(typeof(VoCommands), nameof(InitTerminalPostfix)));

        if (TerminalInitializedField.Value.GetValue(null) is bool initialized && initialized)
        {
            RegisterTerminalCommand();
        }
    }

    public static void PumpSessionTimes()
    {
        ZNet? network = ZNet.instance;
        if (network == null)
        {
            PeerConnectTimes.Clear();
            return;
        }

        List<ZNetPeer>? peers = network.GetPeers();
        if (peers == null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        var connected = new HashSet<long>();
        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer? peer = peers[index];
            if (peer == null || peer.m_uid == 0L)
            {
                continue;
            }

            connected.Add(peer.m_uid);
            if (!PeerConnectTimes.ContainsKey(peer.m_uid))
            {
                PeerConnectTimes.Add(peer.m_uid, now);
            }
        }

        if (PeerConnectTimes.Count == connected.Count)
        {
            return;
        }

        var disconnected = new List<long>();
        foreach (long uid in PeerConnectTimes.Keys)
        {
            if (!connected.Contains(uid))
            {
                disconnected.Add(uid);
            }
        }

        for (int index = 0; index < disconnected.Count; index++)
        {
            PeerConnectTimes.Remove(disconnected[index]);
        }
    }

    private static void InitTerminalPostfix()
    {
        try
        {
            RegisterTerminalCommand();
        }
        catch (Exception exception)
        {
            LogWarning("could not register the vo console command", exception);
        }
    }

    private static void RegisterTerminalCommand()
    {
        _ = new Terminal.ConsoleCommand(
            "vo",
            "ValheimOne server administration; use 'vo help' for commands",
            Run,
            isCheat: false,
            isNetwork: false,
            onlyServer: true,
            isSecret: false,
            allowInDevBuild: false,
            optionsFetcher: VoCommandRegistry.GetSubcommandNames,
            alwaysRefreshTabOptions: false,
            remoteCommand: false);
    }

    private static void Run(Terminal.ConsoleEventArgs args)
    {
        try
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Write(args, "Missing vo command. Use 'vo help' for the command list.");
                return;
            }

            string subcommand = args[1].Trim().ToLowerInvariant();
            switch (subcommand)
            {
                case "help":
                    RunHelp(args);
                    break;
                case "players":
                    RunPlayers(args);
                    break;
                case "playerinfo":
                    RunPlayerInfo(args);
                    break;
                case "playtime":
                    RunPlaytime(args);
                    break;
                case "save":
                    RunSave(args);
                    break;
                case "kick":
                    RunTargetAction(args, "kick");
                    break;
                case "ban":
                    RunTargetAction(args, "ban");
                    break;
                case "unban":
                    RunTargetAction(args, "unban");
                    break;
                case "banlist":
                    RunBanList(args);
                    break;
                case "stats":
                    RunStats(args);
                    break;
                case "doctor":
                    RunDoctor(args);
                    break;
                case "weather":
                    RunWeather(args);
                    break;
                case "bosses":
                    RunBosses(args);
                    break;
                case "entities":
                    RunEntities(args);
                    break;
                default:
                    Write(args, $"Unknown vo command '{args[1]}'. Use 'vo help'.");
                    break;
            }
        }
        catch (Exception exception)
        {
            LogWarning("vo command failed", exception);
            Write(
                args,
                $"Command failed: {exception.GetType().Name}: {GetExceptionMessage(exception)}");
        }
    }

    private static void RunHelp(Terminal.ConsoleEventArgs args)
    {
        string requested = JoinArguments(args, 2).Trim();
        if (requested.StartsWith("vo ", StringComparison.OrdinalIgnoreCase))
        {
            requested = requested.Substring(3).Trim();
        }

        if (requested.Length > 0)
        {
            if (!VoCommandRegistry.TryGet(requested, out VoCommandDefinition? command) ||
                command == null)
            {
                Write(args, $"Unknown vo command '{requested}'. Use 'vo help'.");
                return;
            }

            Write(args, $"{command.Usage} — {command.Description}");
            Write(args, $"Category: {command.Category}");
            if (command.Examples.Length > 0)
            {
                Write(args, "Examples:");
                for (int index = 0; index < command.Examples.Length; index++)
                {
                    Write(args, "  " + command.Examples[index]);
                }
            }

            return;
        }

        Write(args, "ValheimOne commands:");
        IReadOnlyList<VoCommandDefinition> commands = VoCommandRegistry.All;
        for (int categoryIndex = 0;
             categoryIndex < VoCommandRegistry.Categories.Length;
             categoryIndex++)
        {
            string category = VoCommandRegistry.Categories[categoryIndex];
            int width = 0;
            for (int commandIndex = 0; commandIndex < commands.Count; commandIndex++)
            {
                VoCommandDefinition command = commands[commandIndex];
                if (string.Equals(command.Category, category, StringComparison.Ordinal))
                {
                    width = Math.Max(width, command.Usage.Length);
                }
            }

            Write(args, $"[{category}]");
            for (int commandIndex = 0; commandIndex < commands.Count; commandIndex++)
            {
                VoCommandDefinition command = commands[commandIndex];
                if (!string.Equals(command.Category, category, StringComparison.Ordinal))
                {
                    continue;
                }

                Write(args, $"  {command.Usage.PadRight(width)} — {command.Description}");
            }
        }

        Write(args, "Use 'vo help <command>' for examples and details.");
    }

    private static void RunPlayers(Terminal.ConsoleEventArgs args)
    {
        List<ZNetPeer> peers = GetConnectedPlayerPeers();
        peers.Sort(ComparePeerNames);
        Write(args, $"Online players ({peers.Count}):");
        float now = Time.realtimeSinceStartup;
        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer peer = peers[index];
            string position = peer.m_publicRefPos
                ? FormatPosition(peer.m_refPos)
                : "position hidden";
            Write(
                args,
                $"  {peer.m_playerName} | {GetHostId(peer)} | " +
                $"{FormatDuration(GetSessionSeconds(peer, now))} | {position}");
        }

        if (peers.Count == 0)
        {
            Write(args, "  No players connected.");
        }
    }

    private static void RunPlayerInfo(Terminal.ConsoleEventArgs args)
    {
        string target = JoinArguments(args, 2).Trim();
        if (target.Length == 0)
        {
            Write(args, "Usage: vo playerinfo <player>");
            return;
        }

        List<ZNetPeer> matches = FindPlayerMatches(target);
        if (matches.Count == 0)
        {
            Write(args, $"No connected player matches '{target}'.");
            return;
        }

        if (matches.Count > 1)
        {
            Write(args, $"Ambiguous player '{target}'. Matches:");
            for (int index = 0; index < matches.Count; index++)
            {
                Write(args, "  " + matches[index].m_playerName);
            }

            return;
        }

        ZNetPeer peer = matches[0];
        Write(args, $"Player: {peer.m_playerName}");
        Write(args, $"  Host ID: {GetHostId(peer)}");
        Write(
            args,
            $"  Session: {FormatDuration(GetSessionSeconds(peer, Time.realtimeSinceStartup))}");
        Write(args, $"  Position: {(peer.m_publicRefPos ? FormatPosition(peer.m_refPos) : "hidden")}");

        ZDOMan? manager = ZDOMan.instance;
        ZDO? zdo = manager?.GetZDO(peer.m_characterID);
        if (zdo == null)
        {
            Write(args, "  Character state: unavailable (character ZDO not found)");
            return;
        }

        bool dead = zdo.GetBool(ZDOVars.s_dead, false);
        Write(args, $"  State: {(dead ? "dead" : "alive")}");
        bool hasHealth = zdo.GetFloat(ZDOVars.s_health, out float health);
        bool hasMaximumHealth = zdo.GetFloat(ZDOVars.s_maxHealth, out float maximumHealth);
        if (hasHealth && hasMaximumHealth)
        {
            Write(
                args,
                $"  Health: {health.ToString("0.0", CultureInfo.InvariantCulture)}/" +
                maximumHealth.ToString("0.0", CultureInfo.InvariantCulture));
        }
        else
        {
            Write(args, "  Health: unavailable");
        }

        if (zdo.GetFloat(ZDOVars.s_stamina, out float stamina))
        {
            Write(
                args,
                $"  Stamina: {stamina.ToString("0.0", CultureInfo.InvariantCulture)} " +
                "(current; maximum is not replicated)");
        }

        Write(args, $"  Character ZDO: {peer.m_characterID}");
    }

    private static void RunPlaytime(Terminal.ConsoleEventArgs args)
    {
        float now = Time.realtimeSinceStartup;
        List<ZNetPeer> peers = GetConnectedPlayerPeers();
        peers.Sort((left, right) =>
        {
            int timeComparison = GetSessionSeconds(right, now).CompareTo(
                GetSessionSeconds(left, now));
            return timeComparison != 0 ? timeComparison : ComparePeerNames(left, right);
        });

        Write(args, $"Server uptime: {FormatDuration(now)}");
        Write(args, $"Player sessions ({peers.Count}, longest first):");
        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer peer = peers[index];
            Write(
                args,
                $"  {FormatDuration(GetSessionSeconds(peer, now))}  {peer.m_playerName}");
        }

        if (peers.Count == 0)
        {
            Write(args, "  No players connected.");
        }
    }

    private static void RunSave(Terminal.ConsoleEventArgs args)
    {
        ConsoleSaveResult result = ConsoleBridge.SaveOnMainThread(_log);
        if (!result.Ok)
        {
            Write(args, "Save failed: " + result.Error);
            return;
        }

        Write(
            args,
            result.AlreadySaving
                ? "A save was already in progress; another save was requested."
                : "World and player profile save requested.");
    }

    private static void RunTargetAction(Terminal.ConsoleEventArgs args, string actionName)
    {
        string target = JoinArguments(args, 2).Trim();
        if (target.Length == 0)
        {
            Write(args, $"Usage: vo {actionName} <target>");
            return;
        }

        ConsoleActionResult result;
        switch (actionName)
        {
            case "kick":
                result = ConsoleBridge.KickOnMainThread(target, _log);
                break;
            case "ban":
                result = ConsoleBridge.BanOnMainThread(target, _log);
                break;
            case "unban":
                result = ConsoleBridge.UnbanOnMainThread(target, _log);
                break;
            default:
                result = ConsoleActionResult.Failure("unsupported moderation action");
                break;
        }

        Write(
            args,
            result.Ok
                ? $"{UppercaseFirst(actionName)} requested for {target}."
                : $"{UppercaseFirst(actionName)} failed: {result.Error}");
    }

    private static void RunBanList(Terminal.ConsoleEventArgs args)
    {
        ConsoleBanListResult result = ConsoleBridge.GetBanListOnMainThread(_log);
        if (!result.Ok)
        {
            Write(args, "Could not read ban list: " + result.Error);
            return;
        }

        result.Banned.Sort(StringComparer.OrdinalIgnoreCase);
        Write(args, $"Banned targets ({result.Banned.Count}):");
        for (int index = 0; index < result.Banned.Count; index++)
        {
            Write(args, "  " + result.Banned[index]);
        }

        if (result.Banned.Count == 0)
        {
            Write(args, "  Ban list is empty.");
        }
    }

    private static void RunStats(Terminal.ConsoleEventArgs args)
    {
        StatsSnapshot stats = LiveMapBehaviour.Instance?.ConsoleBridge?.Stats ?? StatsSnapshot.Empty;
        Write(args, "Server statistics:");
        Write(args, $"  Uptime: {FormatDuration(stats.UptimeSeconds)}");
        Write(args, $"  Players/peers: {stats.Players}/{stats.Peers}");
        Write(args, $"  ZDOs: {stats.ZdoCount.ToString("N0", CultureInfo.InvariantCulture)}");
        Write(args, $"  Mono heap: {FormatBytes(stats.MonoHeapBytes)}");
        Write(
            args,
            $"  Frame avg/max: {stats.FrameAvgMs.ToString("0.00", CultureInfo.InvariantCulture)}/" +
            $"{stats.FrameMaxMs.ToString("0.00", CultureInfo.InvariantCulture)} ms");
        if (stats.SnapshotUnixMs == 0L)
        {
            Write(args, "  Stats snapshot is not ready yet.");
        }
    }

    private static void RunDoctor(Terminal.ConsoleEventArgs args)
    {
        Write(args, $"ValheimOne doctor — plugin {ValheimOnePlugin.PluginVersion}");
        string gameVersion = _log == null
            ? "unavailable"
            : GameVersionDetector.TryDetect(_log) ?? "unavailable";
        Write(args, $"Game version: {gameVersion}");

        FeatureRegistry? registry = _registry;
        if (registry == null)
        {
            Write(args, "Modules: registry unavailable");
        }
        else
        {
            Write(args, $"Modules ({registry.Features.Count}):");
            for (int index = 0; index < registry.Features.Count; index++)
            {
                FeatureDefinition feature = registry.Features[index];
                string enabled = feature.Enabled.Value ? "enabled" : "disabled";
                if (registry.TryGetPatchFailure(feature.Section, out string? failure) &&
                    !string.IsNullOrEmpty(failure))
                {
                    Write(args, $"  {feature.Section}: {enabled}, PATCH FAILED ({failure})");
                }
                else if (registry.IsPatchApplied(feature.Section))
                {
                    Write(args, $"  {feature.Section}: {enabled}, patches ready");
                }
                else
                {
                    Write(args, $"  {feature.Section}: {enabled}, patch state unavailable");
                }
            }
        }

        LiveMapConfig? config = _config;
        var warnings = new List<string>();
        if (config == null)
        {
            warnings.Add("LiveMap configuration is unavailable");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.AccessToken))
            {
                warnings.Add("AccessToken is empty; admin map view is disabled (save panel config to generate a token)");
            }

            if (config.ConsoleEnabled && string.IsNullOrWhiteSpace(config.AccessToken))
            {
                warnings.Add("ConsoleEnabled is true but AccessToken is empty; web console endpoints are disabled");
            }

            if (config.ConsoleEnabled &&
                !config.AllowAllCommands &&
                config.ConsoleWhitelist.Count == 0)
            {
                warnings.Add("ConsoleWhitelist is empty; only the implicit vo family can execute");
            }

            if (config.AllowAllCommands)
            {
                warnings.Add("AllowAllCommands is enabled");
            }
        }

        Write(args, $"Config warnings ({warnings.Count}):");
        if (warnings.Count == 0)
        {
            Write(args, "  none");
        }
        else
        {
            for (int index = 0; index < warnings.Count; index++)
            {
                Write(args, "  " + warnings[index]);
            }
        }

        LiveMapBehaviour? behaviour = LiveMapBehaviour.Instance;
        Write(
            args,
            "LiveMap: " + (behaviour == null ? "behaviour unavailable" : behaviour.ServiceState));

        ActivityLogModule? activityLog = _activityLog;
        if (activityLog == null)
        {
            Write(args, "Activity log: unavailable");
            return;
        }

        ActivityLogHealthSnapshot activityHealth = activityLog.GetHealth();
        string lastWrite = activityHealth.LastWriteAgeSeconds.HasValue
            ? activityHealth.LastWriteAgeSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) +
              "s ago"
            : "never";
        Write(args, $"Activity log: {(activityHealth.Enabled ? "enabled" : "disabled")}");
        Write(args, $"  Current file: {activityHealth.CurrentFileName}");
        Write(
            args,
            $"  Events written today: " +
            activityHealth.EventsWrittenToday.ToString(CultureInfo.InvariantCulture));
        Write(args, $"  Last write: {lastWrite}");
    }

    private static void RunWeather(Terminal.ConsoleEventArgs args)
    {
        EnvMan? environmentManager = EnvMan.instance;
        if (environmentManager == null)
        {
            Write(args, "Weather unavailable: EnvMan is not ready.");
            return;
        }

        EnvSetup? current = environmentManager.GetCurrentEnvironment();
        string? currentName = current?.m_name;
        string environmentName = string.IsNullOrWhiteSpace(currentName)
            ? "unknown"
            : currentName!;
        Vector3 wind = environmentManager.GetWindDir();
        double degrees = Math.Atan2(wind.x, wind.z) * 180d / Math.PI;
        if (degrees < 0d)
        {
            degrees += 360d;
        }

        string compass = GetCompassDirection(degrees);
        float intensity = environmentManager.GetWindIntensity();
        Write(args, $"Environment: {environmentName}");
        Write(
            args,
            $"Wind: {compass} {degrees.ToString("0", CultureInfo.InvariantCulture)}° | " +
            $"intensity {intensity.ToString("0.00", CultureInfo.InvariantCulture)}");
        string? forcedEnvironment =
            ForceEnvironmentField.Value.GetValue(environmentManager) as string;
        if (!string.IsNullOrWhiteSpace(forcedEnvironment))
        {
            Write(args, "Forced environment: " + forcedEnvironment);
        }

        EnvSetup? upcoming =
            NextEnvironmentField.Value.GetValue(environmentManager) as EnvSetup;
        if (upcoming != null && !string.IsNullOrWhiteSpace(upcoming.m_name))
        {
            Write(args, "Upcoming environment: " + upcoming.m_name);
        }
    }

    private static void RunBosses(Terminal.ConsoleEventArgs args)
    {
        ZoneSystem? zoneSystem = ZoneSystem.instance;
        if (zoneSystem == null)
        {
            Write(args, "Boss keys unavailable: ZoneSystem is not ready.");
            return;
        }

        GetGlobalKeyState(
            zoneSystem,
            out string[] keys,
            out string[] modifiers);
        var keyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < keys.Length; index++)
        {
            string keyName = GetGlobalKeyName(keys[index]);
            if (keyName.Length > 0)
            {
                keyNames.Add(keyName);
            }
        }

        int defeated = 0;
        Write(args, "Boss progress:");
        for (int index = 0; index < Bosses.Length; index++)
        {
            BossDefinition boss = Bosses[index];
            bool isDefeated = keyNames.Contains(boss.GlobalKey);
            if (isDefeated)
            {
                defeated++;
            }

            Write(args, $"  {(isDefeated ? "defeated" : "not yet ")}  {boss.Name}");
        }

        Write(args, $"Defeated: {defeated}/{Bosses.Length}");
        Write(args, $"Active world modifiers ({modifiers.Length}):");
        if (modifiers.Length == 0)
        {
            Write(args, "  none");
        }
        else
        {
            for (int index = 0; index < modifiers.Length; index++)
            {
                Write(args, "  " + modifiers[index]);
            }
        }
    }

    private static void RunEntities(Terminal.ConsoleEventArgs args)
    {
        LiveMapBehaviour? behaviour = LiveMapBehaviour.Instance;
        if (behaviour == null)
        {
            Write(args, "Entity scan unavailable: LiveMap behaviour is not initialized.");
            return;
        }

        behaviour.NoteEntitiesRequested();
        if (_config == null || !_config.EntityLayer)
        {
            Write(args, "Entity scanning is disabled; enable [LiveMap] EntityLayer.");
            return;
        }

        if (!behaviour.EntityTrackerReady)
        {
            Write(
                args,
                "Entity scan unavailable: LiveMap is not running, so a refresh cannot start yet.");
            return;
        }

        EntityMapSnapshot snapshot = behaviour.EntitySnapshot;
        if (snapshot.EntitiesUnixMs == 0L)
        {
            Write(args, "No entity scan has completed yet; a fresh scan was requested.");
            return;
        }

        long ageMilliseconds = Math.Max(
            0L,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.EntitiesUnixMs);
        Write(
            args,
            $"Tracked entities: {snapshot.Entities.Length} " +
            $"(scanned {FormatAge(ageMilliseconds)} ago; refresh requested)");

        var groups = new SortedDictionary<string, SortedDictionary<string, int>>(
            StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < snapshot.Entities.Length; index++)
        {
            TrackedEntitySnapshot entity = snapshot.Entities[index];
            if (!groups.TryGetValue(
                    entity.Group,
                    out SortedDictionary<string, int>? prefabs))
            {
                prefabs = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                groups.Add(entity.Group, prefabs);
            }

            prefabs.TryGetValue(entity.Prefab, out int count);
            prefabs[entity.Prefab] = count + 1;
        }

        foreach (KeyValuePair<string, SortedDictionary<string, int>> group in groups)
        {
            int total = 0;
            foreach (int count in group.Value.Values)
            {
                total += count;
            }

            Write(args, $"[{group.Key}] {total}");
            foreach (KeyValuePair<string, int> prefab in group.Value)
            {
                Write(args, $"  {prefab.Key}: {prefab.Value}");
            }
        }

        if (snapshot.Entities.Length == 0)
        {
            Write(args, "  The last completed scan found no tracked ships, carts, or portals.");
        }
    }

    private static List<ZNetPeer> GetConnectedPlayerPeers()
    {
        PumpSessionTimes();
        var result = new List<ZNetPeer>();
        ZNet? network = ZNet.instance;
        List<ZNetPeer>? peers = network?.GetPeers();
        if (peers == null)
        {
            return result;
        }

        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer? peer = peers[index];
            if (peer == null ||
                peer.m_uid == 0L ||
                peer.m_characterID.IsNone() ||
                string.IsNullOrWhiteSpace(peer.m_playerName))
            {
                continue;
            }

            result.Add(peer);
        }

        return result;
    }

    private static List<ZNetPeer> FindPlayerMatches(string target)
    {
        List<ZNetPeer> peers = GetConnectedPlayerPeers();
        var exact = new List<ZNetPeer>();
        var prefix = new List<ZNetPeer>();
        var substring = new List<ZNetPeer>();
        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer peer = peers[index];
            string name = peer.m_playerName ?? string.Empty;
            if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
            {
                exact.Add(peer);
            }
            else if (name.StartsWith(target, StringComparison.OrdinalIgnoreCase))
            {
                prefix.Add(peer);
            }
            else if (name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                substring.Add(peer);
            }
        }

        List<ZNetPeer> result = exact.Count > 0
            ? exact
            : prefix.Count > 0
                ? prefix
                : substring;
        result.Sort(ComparePeerNames);
        return result;
    }

    private static int ComparePeerNames(ZNetPeer left, ZNetPeer right)
    {
        return StringComparer.OrdinalIgnoreCase.Compare(left.m_playerName, right.m_playerName);
    }

    private static double GetSessionSeconds(ZNetPeer peer, float now)
    {
        if (peer.m_uid == 0L)
        {
            return 0d;
        }

        if (!PeerConnectTimes.TryGetValue(peer.m_uid, out float connectedAt))
        {
            connectedAt = now;
            PeerConnectTimes[peer.m_uid] = connectedAt;
        }

        return Math.Max(0d, now - connectedAt);
    }

    private static string GetHostId(ZNetPeer peer)
    {
        try
        {
            string? host = peer.m_socket?.GetHostName();
            if (string.IsNullOrWhiteSpace(host))
            {
                return "host unknown";
            }

            return host!;
        }
        catch (Exception exception)
        {
            LogWarning("could not read peer host ID", exception);
            return "host unavailable";
        }
    }

    internal static void GetGlobalKeyState(
        ZoneSystem zoneSystem,
        out string[] globalKeys,
        out string[] modifiers)
    {
        List<string> source = zoneSystem.GetGlobalKeys();
        var keys = new List<string>(source.Count);
        for (int index = 0; index < source.Count; index++)
        {
            string key = (source[index] ?? string.Empty).Trim();
            if (key.Length > 0)
            {
                keys.Add(key);
            }
        }

        keys.Sort(StringComparer.OrdinalIgnoreCase);
        globalKeys = keys.ToArray();
        modifiers = GetWorldModifierKeys(keys).ToArray();
    }

    private static List<string> GetWorldModifierKeys(List<string> keys)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Array values = Enum.GetValues(typeof(GlobalKeys));
        int end = (int)GlobalKeys.NonServerOption;
        for (int index = 0; index < values.Length; index++)
        {
            var value = (GlobalKeys)values.GetValue(index);
            int numeric = (int)value;
            if (numeric >= 0 && numeric < end)
            {
                names.Add(value.ToString());
            }
        }

        var modifiers = new List<string>();
        for (int index = 0; index < keys.Count; index++)
        {
            if (names.Contains(GetGlobalKeyName(keys[index])))
            {
                modifiers.Add(keys[index]);
            }
        }

        modifiers.Sort(StringComparer.OrdinalIgnoreCase);
        return modifiers;
    }

    private static string GetGlobalKeyName(string key)
    {
        string value = (key ?? string.Empty).Trim();
        int separator = value.IndexOf(' ');
        return separator < 0 ? value : value.Substring(0, separator);
    }

    private static string JoinArguments(Terminal.ConsoleEventArgs args, int startIndex)
    {
        if (args.Length <= startIndex)
        {
            return string.Empty;
        }

        return string.Join(" ", args.Args, startIndex, args.Length - startIndex);
    }

    private static string FormatPosition(Vector3 position)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "({0:0}, {1:0}, {2:0})",
            position.x,
            position.y,
            position.z);
    }

    private static string FormatDuration(double seconds)
    {
        long totalSeconds = (long)Math.Max(0d, Math.Floor(seconds));
        long hours = totalSeconds / 3600L;
        long minutes = totalSeconds % 3600L / 60L;
        long remainingSeconds = totalSeconds % 60L;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:00}:{2:00}",
            hours,
            minutes,
            remainingSeconds);
    }

    private static string FormatAge(long milliseconds)
    {
        if (milliseconds < 1000L)
        {
            return "<1s";
        }

        if (milliseconds < 60000L)
        {
            return (milliseconds / 1000d).ToString("0", CultureInfo.InvariantCulture) + "s";
        }

        return FormatDuration(milliseconds / 1000d);
    }

    private static string FormatBytes(long bytes)
    {
        double mebibytes = Math.Max(0L, bytes) / 1048576d;
        return mebibytes.ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
    }

    private static string GetCompassDirection(double degrees)
    {
        string[] points =
        {
            "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW",
        };
        int index = (int)Math.Floor((degrees + 11.25d) / 22.5d) % points.Length;
        return points[index];
    }

    private static string UppercaseFirst(string value)
    {
        return value.Length == 0
            ? value
            : char.ToUpperInvariant(value[0]) + value.Substring(1);
    }

    private static string GetExceptionMessage(Exception exception)
    {
        return string.IsNullOrEmpty(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
    }

    private static void Write(Terminal.ConsoleEventArgs args, string line)
    {
        ConsoleBridge.CaptureTerminalOutput(line);
        args.Context?.AddString(line);
    }

    private static void LogWarning(string context, Exception exception)
    {
        try
        {
            _log?.Warning(
                $"[LiveMap] {context}: {exception.GetType().Name}: {exception.Message}");
        }
        catch
        {
            // Console diagnostics must not fail because logging failed.
        }
    }

    private sealed class BossDefinition
    {
        public BossDefinition(string name, string globalKey)
        {
            Name = name;
            GlobalKey = globalKey;
        }

        public string Name { get; }

        public string GlobalKey { get; }
    }
}

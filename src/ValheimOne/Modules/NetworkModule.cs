using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Bootstrap;
using HarmonyLib;
using Steamworks;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;

namespace ValheimOne.Modules;

// [Network] Lets the dedicated server keep more world updates on the way to each player.
//
// ZDOMan.SendZDOs decides how much world data one player gets on its turn. It reads that
// player's socket queue, skips the player while the queue is over 10,240 bytes, and otherwise
// sends at most 10,240 bytes minus the queue. Steam counts bytes waiting plus bytes sent and
// not yet acknowledged; PlayFab (crossplay) counts a quarter of its bytes in flight. Players
// are served one per frame, so when several players play near each other each one must also
// receive everything the others change, the 10 KB window runs dry, updates arrive in bursts
// and players rubber-band ("fine apart, laggy together"). That read is the only place the
// game uses GetSendQueueSize (1.0.20), and the game pins Steam's send rate at 150 KB/s.
//
// This module replaces that one read in SendZDOs with QueueForSendGate, which reports the
// queue against a 32 KB window instead (Steam: the real queue less 22,528 bytes; PlayFab:
// the real bytes in flight less 22,528). It never reports more than the game's own figure,
// so a player is never sent less than vanilla would send. On Steam it also opens the
// per-player send rate to 256 KB/s-1 MB/s. Nothing changes on the wire: players need no mod.
// The idea of counting the queue low comes from CW_Jesse's Better Networking; when that mod,
// its 1.0 fork, or any other mod that changes the queue or the send rate is loaded, this
// module stands down.
public sealed class NetworkModule : IFeatureModule
{
    public const string BetterNetworkingGuid = "CW_Jesse.BetterNetworking";

    // Network mods that change the same send limit, by plugin GUID. Any other mod that patches
    // the queue read, SendZDOs or the Steam send-rate setup is caught by its Harmony patches.
    private static readonly KeyValuePair<string, string>[] KnownNetworkMods =
    {
        new KeyValuePair<string, string>(BetterNetworkingGuid, "Better Networking"),
        new KeyValuePair<string, string>("DIT.BetterNetworking10", "Better Networking 1.0"),
        new KeyValuePair<string, string>("org.bepinex.plugins.network", "Network"),
    };

    // Server Devcommands (JereKuusela) puts a void prefix and a postfix on SendZDOs for its ghost mode: on a
    // player's own game they hide the local player while the world is sent and put it back after; on a dedicated
    // server there is no local player and both return at once. Neither can skip SendZDOs, change its result or
    // touch the queue read, so they leave the send limit alone. 22 of the 44 stand-downs on 10/20 were only this.
    public const string ServerDevcommandsGuid = "server_devcommands";

    public const int VanillaQueueLimit = 10240;
    public const int LiftedQueueLimit = 32768;
    public const int QueueHeadroom = LiftedQueueLimit - VanillaQueueLimit;

    public const int VanillaSendRate = 153600;
    public const int LiftedSendRateMin = 262144;
    public const int LiftedSendRateMax = 1048576;

    private static NetworkModule? _active;
    private static AccessTools.FieldRef<ZPlayFabSocket, ZPlayFabSocket.InFlightQueue>? _inFlightQueue;
    private static bool _steamNetworkingStarted;
    private static bool _steamRateWarningLogged;

    private readonly FeatureRegistry _registry;
    private readonly FeatureDefinition _feature;
    private readonly ModLogger _log;
    private bool _patched;
    private bool _resolved;
    private string? _standDownReason;

    public NetworkModule(FeatureRegistry registry, ModLogger log)
    {
        _registry = registry;
        _log = log;
        _feature = registry.Register(
            Name,
            Section,
            Classification,
            enabledByDefault: true,
            "Lets the server send each player three times as much world data at once as Valheim allows on its own, " +
            "which helps when several players are close together. Players install nothing. Turns itself off if the " +
            "Better Networking mod is installed.");
    }

    public string Name => "Network send limit";

    public string Section => "Network";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.ServerAuthoritative;

    // True while the lift is applied: patched on a dedicated server, switched on, and no
    // other mod changes the send queue.
    public static bool IsLiftActive()
    {
        NetworkModule? active = _active;
        if (active == null || !active._patched || !active._feature.Enabled.Value)
        {
            return false;
        }

        active.ResolveOtherMods();
        return active._standDownReason == null;
    }

    // Why the lift stands down, or null when no other mod changes the send queue.
    public static string? StandDownReason => _active?._standDownReason;

    public void ApplyPatches(Harmony harmony)
    {
        _active = this;
        if (!GameProcess.IsDedicatedServer)
        {
            _log.Info("Network send limit skipped: this is not a dedicated server process.");
            return;
        }

        FieldInfo inFlight = AccessTools.Field(typeof(ZPlayFabSocket), "m_inFlightQueue")
            ?? throw new MissingFieldException(nameof(ZPlayFabSocket), "m_inFlightQueue");
        _inFlightQueue = AccessTools.FieldRefAccess<ZPlayFabSocket, ZPlayFabSocket.InFlightQueue>(inFlight);

        MethodInfo sendZdos = AccessTools.Method(typeof(ZDOMan), "SendZDOs")
            ?? throw new MissingMethodException(nameof(ZDOMan), "SendZDOs");
        MethodInfo registerCallbacks = AccessTools.Method(typeof(ZSteamSocket), "RegisterGlobalCallbacks")
            ?? throw new MissingMethodException(nameof(ZSteamSocket), "RegisterGlobalCallbacks");

        harmony.Patch(
            sendZdos,
            transpiler: new HarmonyMethod(typeof(NetworkModule), nameof(SendZDOsTranspiler)));
        harmony.Patch(
            registerCallbacks,
            postfix: new HarmonyMethod(typeof(NetworkModule), nameof(RegisterGlobalCallbacksPostfix)));

        _patched = true;
        _registry.EffectiveValuesChanged += ApplySteamSendRates;
    }

    // Called once every plugin has loaded, so the one line in the log says what applies.
    public void ReportStartupState()
    {
        if (!_patched)
        {
            return;
        }

        ResolveOtherMods();
        if (_standDownReason != null)
        {
            _log.Info($"Network: {_standDownReason}");
        }
        else if (!_feature.Enabled.Value)
        {
            _log.Info("Network: send limit lift is off ([Network] Enabled = false); Valheim's own 10 KB limit applies.");
        }
        else
        {
            _log.Info(
                "Network: each player can now have 32 KB of world updates on the way instead of 10 KB " +
                "(Steam and crossplay), and Steam sends up to 1 MB/s per player instead of 150 KB/s. " +
                "Players install nothing.");
        }
    }

    // Replaces the single ISocket.GetSendQueueSize read in ZDOMan.SendZDOs. The two 10,240
    // literals and the 2,048 minimum stay the game's own.
    public static int QueueForSendGate(ISocket socket)
    {
        int reported = socket.GetSendQueueSize();
        if (!IsLiftActive())
        {
            return reported;
        }

        int real = reported;
        if (socket is ZPlayFabSocket playFab && _inFlightQueue != null)
        {
            ZPlayFabSocket.InFlightQueue? queue = _inFlightQueue(playFab);
            if (queue != null)
            {
                real = (int)Math.Min(queue.Bytes, (uint)int.MaxValue);
            }
        }

        return Math.Min(reported, real - QueueHeadroom);
    }

    private static IEnumerable<CodeInstruction> SendZDOsTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo replacement = AccessTools.Method(typeof(NetworkModule), nameof(QueueForSendGate));
        var codes = new List<CodeInstruction>(instructions);
        int replaced = 0;
        foreach (CodeInstruction code in codes)
        {
            if ((code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call) &&
                code.operand is MethodInfo method &&
                method.Name == nameof(ISocket.GetSendQueueSize) &&
                method.DeclaringType == typeof(ISocket))
            {
                code.opcode = OpCodes.Call;
                code.operand = replacement;
                replaced++;
            }
        }

        if (replaced != 1)
        {
            throw new InvalidOperationException(
                $"[Network] expected exactly one ISocket.GetSendQueueSize call in ZDOMan.SendZDOs; found {replaced}.");
        }

        return codes;
    }

    // The game sets Steam's global send rates the first time a ZSteamSocket is made (and again
    // if every socket was closed). Re-apply ours after it, every time.
    private static void RegisterGlobalCallbacksPostfix()
    {
        _steamNetworkingStarted = true;
        ApplySteamSendRates();
    }

    private static void ApplySteamSendRates()
    {
        if (!_steamNetworkingStarted)
        {
            return;
        }

        bool lift = IsLiftActive();
        bool ok = SetGlobalSteamInt(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                lift ? LiftedSendRateMin : VanillaSendRate) &
            SetGlobalSteamInt(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                lift ? LiftedSendRateMax : VanillaSendRate);
        if (!ok && !_steamRateWarningLogged && _active != null)
        {
            _steamRateWarningLogged = true;
            _active._log.Warning("Network: Steam refused the send rate setting; the send limit lift still applies.");
        }
    }

    private static bool SetGlobalSteamInt(ESteamNetworkingConfigValue key, int value)
    {
        GCHandle handle = GCHandle.Alloc(value, GCHandleType.Pinned);
        try
        {
            return SteamGameServerNetworkingUtils.SetConfigValue(
                key,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                IntPtr.Zero,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                handle.AddrOfPinnedObject());
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
    }

    // Decided once, after every plugin has loaded: a known network mod by its GUID, or any
    // other mod that patches either socket's GetSendQueueSize, SendZDOs or the Steam send-rate
    // setup itself.
    private void ResolveOtherMods()
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        try
        {
            foreach (KeyValuePair<string, string> mod in KnownNetworkMods)
            {
                if (Chainloader.PluginInfos.ContainsKey(mod.Key))
                {
                    _standDownReason = $"{mod.Value} ({mod.Key}) is installed, so ValheimOne leaves the send limit to it.";
                    return;
                }
            }

            var owners = new SortedSet<string>(StringComparer.Ordinal);
            AddForeignOwners(owners, AccessTools.Method(typeof(ZSteamSocket), nameof(ZSteamSocket.GetSendQueueSize)));
            AddForeignOwners(owners, AccessTools.Method(typeof(ZPlayFabSocket), nameof(ZPlayFabSocket.GetSendQueueSize)));
            AddForeignOwners(owners, AccessTools.Method(typeof(ZDOMan), "SendZDOs"), ignoreObservers: true);
            AddForeignOwners(owners, AccessTools.Method(typeof(ZSteamSocket), "RegisterGlobalCallbacks"));
            if (owners.Count > 0)
            {
                _standDownReason =
                    $"another mod ({string.Join(", ", owners)}) already changes the send limit, so ValheimOne leaves it alone.";
            }
        }
        catch (Exception exception)
        {
            _standDownReason =
                $"could not check other mods ({exception.GetType().Name}), so ValheimOne leaves the send limit alone.";
        }
    }

    private static void AddForeignOwners(SortedSet<string> owners, MethodBase? method, bool ignoreObservers = false)
    {
        if (method == null)
        {
            return;
        }

        Patches? info = Harmony.GetPatchInfo(method);
        if (info == null)
        {
            return;
        }

        foreach (string owner in info.Owners.Where(owner => owner != ValheimOnePlugin.PluginGuid))
        {
            if (ignoreObservers && OnlyObservesSendZDOs(info, owner))
            {
                continue;
            }

            owners.Add(owner);
        }
    }

    // True only for Server Devcommands when everything it has on SendZDOs is a void prefix (it cannot skip the
    // method) or a postfix that does not take the result: no transpiler, finalizer or IL manipulator. Any other
    // shape from it, and every other mod, still counts as changing the send limit.
    internal static bool OnlyObservesSendZDOs(Patches info, string owner)
    {
        if (!string.Equals(owner, ServerDevcommandsGuid, StringComparison.Ordinal))
        {
            return false;
        }

        if (info.Transpilers.Any(patch => patch.owner == owner) ||
            info.Finalizers.Any(patch => patch.owner == owner) ||
            info.ILManipulators.Any(patch => patch.owner == owner))
        {
            return false;
        }

        return info.Prefixes.Where(patch => patch.owner == owner).All(ObserverOnly) &&
               info.Postfixes.Where(patch => patch.owner == owner).All(ObserverOnly);
    }

    // A void patch that takes nothing by reference and not the result: it can neither skip the original nor change
    // its arguments or what it returns.
    private static bool ObserverOnly(Patch patch)
    {
        MethodInfo? method = patch.PatchMethod;
        return method != null &&
               method.ReturnType == typeof(void) &&
               method.GetParameters().All(parameter =>
                   !parameter.ParameterType.IsByRef &&
                   !string.Equals(parameter.Name, "__result", StringComparison.Ordinal) &&
                   !string.Equals(parameter.Name, "__runOriginal", StringComparison.Ordinal));
    }
}

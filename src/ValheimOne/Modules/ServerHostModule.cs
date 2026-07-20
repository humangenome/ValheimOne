using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

// Host-level [Server] controls: the gameplay-effective player cap override and the
// no-password-required startup switch. The [Server] section itself is registered by
// ServerConfig, so this module binds to that configuration instead of registering
// a second feature for the same section.
public sealed class ServerHostModule : IFeatureModule
{
    public const int VanillaPlayerLimit = 10;
    public const int PlayerLimitCeiling = 127;

    private static ServerHostModule? _active;

    private readonly ServerConfig _serverConfig;

    public ServerHostModule(ServerConfig serverConfig)
    {
        _serverConfig = serverConfig;
    }

    public string Name => "Server host controls";

    public string Section => "Server";

    public bool IsEnabled => _serverConfig.Enabled;

    public FeatureClassification Classification => FeatureClassification.ServerAuthoritative;

    // Effective join cap read at call time so config hot-reload applies to the join gate.
    public static int EffectiveMaxPlayers()
    {
        ServerHostModule? active = _active;
        if (active == null || !active._serverConfig.Enabled)
        {
            return VanillaPlayerLimit;
        }

        int configured = active._serverConfig.MaxPlayers.Value;
        if (configured <= 0)
        {
            return VanillaPlayerLimit;
        }

        return Math.Min(configured, PlayerLimitCeiling);
    }

    // PlayFab lobby/network capacity counts the server itself as a member.
    public static int EffectiveMaxPlayersPlusOne()
    {
        return EffectiveMaxPlayers() + 1;
    }

    private static bool NoPasswordActive()
    {
        ServerHostModule? active = _active;
        return active != null &&
               active._serverConfig.Enabled &&
               active._serverConfig.NoPasswordRequired.Value;
    }

    public void ApplyPatches(Harmony harmony)
    {
        // Patches stay installed; every body reads the [Server] config at call time.
        _active = this;

        var peerInfo = AccessTools.Method(typeof(ZNet), "RPC_PeerInfo")
            ?? throw new MissingMethodException(nameof(ZNet), "RPC_PeerInfo");
        harmony.Patch(
            peerInfo,
            transpiler: new HarmonyMethod(typeof(ServerHostModule), nameof(JoinCapTranspiler)));

        var createLobby = AccessTools.Method(typeof(ZPlayFabMatchmaking), "CreateLobby")
            ?? throw new MissingMethodException(nameof(ZPlayFabMatchmaking), "CreateLobby");
        harmony.Patch(
            createLobby,
            transpiler: new HarmonyMethod(typeof(ServerHostModule), nameof(LobbyCapacityTranspiler)));

        var createNetwork = AccessTools.Method(typeof(ZPlayFabMatchmaking), "CreateAndJoinNetwork")
            ?? throw new MissingMethodException(nameof(ZPlayFabMatchmaking), "CreateAndJoinNetwork");
        harmony.Patch(
            createNetwork,
            transpiler: new HarmonyMethod(typeof(ServerHostModule), nameof(NetworkCapacityTranspiler)));

        var platformData = AccessTools.Method(typeof(ZPlayFabMatchmaking), "SetPlatformMatchmakingData")
            ?? throw new MissingMethodException(nameof(ZPlayFabMatchmaking), "SetPlatformMatchmakingData");
        harmony.Patch(
            platformData,
            transpiler: new HarmonyMethod(typeof(ServerHostModule), nameof(PlatformCapacityTranspiler)));

        var passwordValid = AccessTools.Method(typeof(FejdStartup), "IsPublicPasswordValid")
            ?? throw new MissingMethodException(nameof(FejdStartup), "IsPublicPasswordValid");
        harmony.Patch(
            passwordValid,
            prefix: new HarmonyMethod(typeof(ServerHostModule), nameof(IsPublicPasswordValidPrefix)));
    }

    // ZNet.RPC_PeerInfo: `if (GetNrOfPlayers() >= 10)` — the 10 is the inlined
    // ZNet.ServerPlayerLimit const, so the load anchored on the GetNrOfPlayers call
    // is rewritten to our provider. The method contains a second, unrelated 10.
    private static IEnumerable<CodeInstruction> JoinCapTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var getNrOfPlayers = AccessTools.Method(typeof(ZNet), nameof(ZNet.GetNrOfPlayers))
            ?? throw new MissingMethodException(nameof(ZNet), nameof(ZNet.GetNrOfPlayers));
        return ReplaceAnchoredConstant(
            instructions,
            "ZNet.RPC_PeerInfo",
            expectedValue: 10,
            replacement: AccessTools.Method(typeof(ServerHostModule), nameof(EffectiveMaxPlayers)),
            isAnchored: (previous, current) =>
                previous != null && previous.Calls(getNrOfPlayers));
    }

    // ZPlayFabMatchmaking.CreateLobby: CreateLobbyRequest.MaxPlayers = 11 (cap + server slot).
    private static IEnumerable<CodeInstruction> LobbyCapacityTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return ReplaceConstantBeforeStore(
            instructions,
            "ZPlayFabMatchmaking.CreateLobby",
            expectedValue: 11,
            storeMemberName: "MaxPlayers",
            replacement: AccessTools.Method(typeof(ServerHostModule), nameof(EffectiveMaxPlayersPlusOne)));
    }

    // ZPlayFabMatchmaking.CreateAndJoinNetwork: PlayFabNetworkConfiguration.MaxPlayerCount = 11.
    private static IEnumerable<CodeInstruction> NetworkCapacityTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return ReplaceConstantBeforeStore(
            instructions,
            "ZPlayFabMatchmaking.CreateAndJoinNetwork",
            expectedValue: 11,
            storeMemberName: "MaxPlayerCount",
            replacement: AccessTools.Method(typeof(ServerHostModule), nameof(EffectiveMaxPlayersPlusOne)));
    }

    // ZPlayFabMatchmaking.SetPlatformMatchmakingData: MultiplayerSessionData.m_maxPlayers = 10.
    private static IEnumerable<CodeInstruction> PlatformCapacityTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return ReplaceConstantBeforeStore(
            instructions,
            "ZPlayFabMatchmaking.SetPlatformMatchmakingData",
            expectedValue: 10,
            storeMemberName: "m_maxPlayers",
            replacement: AccessTools.Method(typeof(ServerHostModule), nameof(EffectiveMaxPlayers)));
    }

    // FejdStartup.IsPublicPasswordValid gates dedicated startup with -public 1; skipping it
    // lets the server boot without a password. Join-side password checks are untouched.
    private static bool IsPublicPasswordValidPrefix(ref bool __result)
    {
        if (!NoPasswordActive())
        {
            return true;
        }

        __result = true;
        return false;
    }

    private static IEnumerable<CodeInstruction> ReplaceConstantBeforeStore(
        IEnumerable<CodeInstruction> instructions,
        string context,
        int expectedValue,
        string storeMemberName,
        MethodInfo replacement)
    {
        return ReplaceAnchoredConstant(
            instructions,
            context,
            expectedValue,
            replacement,
            isAnchored: (previous, current) => false,
            storeMemberName: storeMemberName);
    }

    // Rewrites exactly one ldc.i4 load of expectedValue, identified either by the
    // preceding instruction (isAnchored) or by the member the following instruction
    // stores into (storeMemberName). Mutating the instruction in place preserves labels.
    private static IEnumerable<CodeInstruction> ReplaceAnchoredConstant(
        IEnumerable<CodeInstruction> instructions,
        string context,
        int expectedValue,
        MethodInfo replacement,
        Func<CodeInstruction?, CodeInstruction, bool> isAnchored,
        string? storeMemberName = null)
    {
        var codes = new List<CodeInstruction>(instructions);
        int replaced = 0;
        for (int index = 0; index < codes.Count; index++)
        {
            CodeInstruction code = codes[index];
            if (!LoadsConstant(code, expectedValue))
            {
                continue;
            }

            bool matched = isAnchored(index > 0 ? codes[index - 1] : null, code);
            if (!matched && storeMemberName != null && index + 1 < codes.Count)
            {
                matched = StoresMember(codes[index + 1], storeMemberName);
            }

            if (!matched)
            {
                continue;
            }

            code.opcode = OpCodes.Call;
            code.operand = replacement;
            replaced++;
        }

        if (replaced != 1)
        {
            throw new InvalidOperationException(
                $"[Server] cap transpiler expected exactly one {expectedValue} in {context}; found {replaced}.");
        }

        return codes;
    }

    private static bool LoadsConstant(CodeInstruction code, int value)
    {
        if (code.opcode == OpCodes.Ldc_I4_S)
        {
            return Convert.ToInt32(code.operand) == value;
        }

        if (code.opcode == OpCodes.Ldc_I4)
        {
            return Convert.ToInt32(code.operand) == value;
        }

        return false;
    }

    private static bool StoresMember(CodeInstruction code, string memberName)
    {
        if (code.opcode == OpCodes.Stfld && code.operand is FieldInfo field)
        {
            return field.Name == memberName;
        }

        if ((code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call) &&
            code.operand is MethodInfo method)
        {
            return method.Name == "set_" + memberName;
        }

        return false;
    }
}

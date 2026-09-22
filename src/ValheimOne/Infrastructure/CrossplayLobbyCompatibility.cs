using System;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace ValheimOne.Infrastructure;

// A closed crossplay lobby can outlive its owner. A collision with that lobby
// must take the game's ordinary regeneration path instead of dereferencing Owner.
//
// The game also draws its join code from UnityEngine.Random, whose state at that point is
// whatever the world's location seeding left behind, so the code sequence is a fixed
// property of the world and worlds that share it draw the same codes in the same order.
// Every server on such a list must regenerate past every live lobby ahead of it in the
// chain, PlayFab throttles the regeneration calls after about two dozen, and the game's
// error handler then unregisters the server for good. A code drawn from the OS random
// source is unique on the first draw, so the chain is never walked.
internal static class CrossplayLobbyCompatibility
{
    private const int JoinCodeDigits = 6;
    private static FieldInfo? _serverData;
    private static FieldInfo? _serverJoinCode;
    private static PropertyInfo? _joinCode;
    private static MemberInfo? _lobbies;
    private static MemberInfo? _owner;
    private static MethodInfo? _sessionUpdated;
    private static object? _regenerateState;

    public static void Apply(Harmony harmony)
    {
        MethodInfo check = AccessTools.Method(typeof(ZPlayFabMatchmaking), "OnCheckJoinCodeSuccess")
            ?? throw new MissingMethodException(nameof(ZPlayFabMatchmaking), "OnCheckJoinCodeSuccess");
        ParameterInfo[] parameters = check.GetParameters();
        if (parameters.Length != 1) throw new InvalidOperationException("Unknown crossplay join-code response shape");
        _lobbies = FindMember(parameters[0].ParameterType, "Lobbies");
        Type collectionType = MemberType(_lobbies);
        if (!typeof(IList).IsAssignableFrom(collectionType) || !collectionType.IsGenericType)
            throw new InvalidOperationException("Unknown crossplay lobby collection shape");
        _owner = FindMember(collectionType.GetGenericArguments()[0], "Owner");
        _sessionUpdated = AccessTools.Method(typeof(ZPlayFabMatchmaking), "OnSessionUpdated")
            ?? throw new MissingMethodException(nameof(ZPlayFabMatchmaking), "OnSessionUpdated");
        ParameterInfo[] updateParameters = _sessionUpdated.GetParameters();
        if (updateParameters.Length != 1 || !updateParameters[0].ParameterType.IsEnum)
            throw new InvalidOperationException("Unknown crossplay session state shape");
        _regenerateState = Enum.Parse(updateParameters[0].ParameterType, "RegenerateJoinCode");
        harmony.Patch(check, prefix: new HarmonyMethod(typeof(CrossplayLobbyCompatibility), nameof(CheckJoinCodePrefix)));

        MethodInfo generate = AccessTools.Method(typeof(ZPlayFabMatchmaking), "GenerateJoinCode")
            ?? throw new MissingMethodException(nameof(ZPlayFabMatchmaking), "GenerateJoinCode");
        if (generate.GetParameters().Length != 0 || generate.IsStatic)
            throw new InvalidOperationException("Unknown crossplay join-code generator shape");
        _joinCode = AccessTools.Property(typeof(ZPlayFabMatchmaking), "JoinCode")
            ?? throw new MissingMemberException(nameof(ZPlayFabMatchmaking), "JoinCode");
        if (_joinCode.PropertyType != typeof(string) || _joinCode.GetSetMethod(true) == null)
            throw new InvalidOperationException("Unknown crossplay join-code property shape");
        _serverData = AccessTools.Field(typeof(ZPlayFabMatchmaking), "m_serverData")
            ?? throw new MissingMemberException(nameof(ZPlayFabMatchmaking), "m_serverData");
        _serverJoinCode = AccessTools.Field(_serverData.FieldType, "joinCode")
            ?? throw new MissingMemberException(_serverData.FieldType.FullName, "joinCode");
        if (_serverData.FieldType.IsValueType || _serverJoinCode.FieldType != typeof(string))
            throw new InvalidOperationException("Unknown crossplay server data shape");
        harmony.Patch(generate, postfix: new HarmonyMethod(typeof(CrossplayLobbyCompatibility), nameof(GenerateJoinCodePostfix)));
    }

    // Runs after the game's own draw and replaces it in both places the game reads it from:
    // the static JoinCode (logged, sent to UpdateLobby, and the FindLobbies filter) and the
    // server data copy that CreateLobby's search data is built from.
    private static void GenerateJoinCodePostfix(ZPlayFabMatchmaking __instance)
    {
        string code = RandomJoinCode();
        _joinCode!.SetValue(null, code);
        object? data = _serverData!.GetValue(__instance);
        if (data != null) _serverJoinCode!.SetValue(data, code);
    }

    internal static string RandomJoinCode()
    {
        byte[] bytes = new byte[4];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        uint value = BitConverter.ToUInt32(bytes, 0);
        return (value % 1000000u).ToString("D" + JoinCodeDigits);
    }

    private static bool CheckJoinCodePrefix(ZPlayFabMatchmaking __instance, object __0)
    {
        if (__0 == null || !(Read(_lobbies!, __0) is IList lobbies) || lobbies.Count != 1)
            return true;
        object? lobby = lobbies[0];
        if (lobby != null && Read(_owner!, lobby) != null) return true;

        // The code is already present in the lobby index, even if its owner left.
        // Preserve the result and run the same transition used for any other collision.
        _sessionUpdated!.Invoke(__instance, new[] { _regenerateState });
        return false;
    }

    private static MemberInfo FindMember(Type type, string name) =>
        (MemberInfo?)AccessTools.Field(type, name) ?? AccessTools.Property(type, name)
        ?? throw new MissingMemberException(type.FullName, name);

    private static Type MemberType(MemberInfo member) =>
        member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

    private static object? Read(MemberInfo member, object instance) =>
        member is FieldInfo field ? field.GetValue(instance) : ((PropertyInfo)member).GetValue(instance);
}

using System;
using System.Reflection;
using HarmonyLib;
using ValheimOne.LiveMap;

namespace ValheimOne.Infrastructure;

// A boss defeat as the server sees it. The game that ran the boss sends its defeat key
// (m_defeatSetGlobalKey, "defeated_eikthyr") to the server's ZoneSystem.RPC_SetGlobalKey on every
// kill, also when the key is already set, so a prefix there sees first kills and repeat kills alike.
// An admin's "setkey defeated_..." takes the same path and is reported the same way.
internal static class BossDefeatPatch
{
    private static bool _applied;
    private static ModLogger? _log;
    private static bool _warningLogged;

    public static event Action<ServerBossDefeatedEvent>? BossDefeated;

    public static void ApplyPatches(Harmony harmony, ModLogger log)
    {
        _log = log;
        if (_applied)
        {
            return;
        }

        MethodInfo rpc = AccessTools.Method(
            typeof(ZoneSystem),
            "RPC_SetGlobalKey",
            new[] { typeof(long), typeof(string) }) ??
            throw new MissingMethodException(nameof(ZoneSystem), "RPC_SetGlobalKey");
        harmony.Patch(rpc, prefix: new HarmonyMethod(typeof(BossDefeatPatch), nameof(SetGlobalKeyPrefix)));
        _applied = true;
    }

    // Runs on the main thread, before the game adds the key.
    internal static void SetGlobalKeyPrefix(ZoneSystem __instance, string __1)
    {
        try
        {
            string key = (__1 ?? string.Empty).Trim();
            if (!key.StartsWith("defeated_", StringComparison.OrdinalIgnoreCase) || key.IndexOf(' ') >= 0)
            {
                return;
            }

            bool firstTime = !__instance.GetGlobalKeyExact(key);
            var value = new ServerBossDefeatedEvent(VoCommands.BossNameForKey(key), key.ToLowerInvariant(), firstTime);
            Action<ServerBossDefeatedEvent>? callbacks = BossDefeated;
            if (callbacks == null)
            {
                return;
            }

            foreach (Action<ServerBossDefeatedEvent> callback in callbacks.GetInvocationList())
            {
                try
                {
                    callback(value);
                }
                catch (Exception exception)
                {
                    WarnOnce($"boss defeated notification failed ({exception.GetType().Name})");
                }
            }
        }
        catch (Exception exception)
        {
            WarnOnce($"boss defeat could not be read ({exception.GetType().Name})");
        }
    }

    private static void WarnOnce(string text)
    {
        if (_warningLogged)
        {
            return;
        }

        _warningLogged = true;
        try
        {
            _log?.Warning("[ServerEvents] " + text + ".");
        }
        catch
        {
            // Never allow diagnostics to escape into the game's RPC path.
        }
    }
}

internal sealed class ServerBossDefeatedEvent
{
    public ServerBossDefeatedEvent(string name, string key, bool firstTime)
    {
        Name = name;
        Key = key;
        FirstTime = firstTime;
    }

    public string Name { get; }

    public string Key { get; }

    public bool FirstTime { get; }
}

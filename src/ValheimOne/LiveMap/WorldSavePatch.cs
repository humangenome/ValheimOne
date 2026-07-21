using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal static class WorldSavePatch
{
    private static long _lastSavedUnixMs;
    private static ModLogger? _log;

    public static event Action? WorldSaved;

    public static long LastSavedUnixMs => Interlocked.Read(ref _lastSavedUnixMs);

    public static void ApplyPatches(Harmony harmony, ModLogger log)
    {
        _log = log;
        MethodInfo saveWorld = AccessTools.Method(
            typeof(ZNet),
            nameof(ZNet.SaveWorld),
            new[] { typeof(bool) }) ??
            throw new MissingMethodException(nameof(ZNet), nameof(ZNet.SaveWorld));
        harmony.Patch(
            saveWorld,
            postfix: new HarmonyMethod(typeof(WorldSavePatch), nameof(SaveWorldPostfix)));
    }

    private static void SaveWorldPostfix()
    {
        try
        {
            Interlocked.Exchange(
                ref _lastSavedUnixMs,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            Action? callbacks = WorldSaved;
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
                    try
                    {
                        _log?.Warning(
                            $"[WorldSave] notification callback failed: " +
                            $"{exception.GetType().Name}.");
                    }
                    catch
                    {
                        // Never allow diagnostics to escape into the world-save path.
                    }
                }
            }
        }
        catch (Exception exception)
        {
            try
            {
                _log?.Warning(
                    $"[WorldSave] could not record world save time: " +
                    $"{exception.GetType().Name}.");
            }
            catch
            {
                // Never allow diagnostics to escape into the world-save path.
            }
        }
    }
}

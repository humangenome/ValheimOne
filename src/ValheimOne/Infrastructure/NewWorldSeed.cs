using System;
using System.Reflection;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Infrastructure;

// [Server] NewWorldSeed, a one-shot setting for dedicated servers. The game creates a missing world
// in World.GetCreateWorld with a seed from World.GenerateSeed(), and calls GenerateSeed for nothing
// else on a dedicated server. A prefix there hands it the configured seed instead, only when the
// world named by -world does not exist (GetCreateWorld also replaces a world that failed to load;
// that world keeps the game's own behaviour). Once the server's world is up, Settle logs what
// happened and clears the setting, whether it was used, refused or not needed.
internal static class NewWorldSeed
{
    public const int MaximumLength = 10;

    private static ConfigEntryString? _setting;
    private static ModLogger? _log;
    private static string _worldName = string.Empty;
    private static string? _used;
    private static bool _offered;
    private static bool _settled;
    private static bool _applied;

    // Tests: how GenerateSeed answers when the world exists or not.
    internal static Func<string, bool> WorldExists = World.HaveWorld;

    public static void Apply(Harmony harmony, ConfigEntryString setting, ModLogger log)
    {
        _setting = setting;
        _log = log;
        _worldName = WorldDataDirectory.ReadWorldName(Environment.GetCommandLineArgs());
        if (_applied)
        {
            return;
        }

        MethodInfo generateSeed = AccessTools.Method(typeof(World), nameof(World.GenerateSeed), Type.EmptyTypes) ??
            throw new MissingMethodException(nameof(World), nameof(World.GenerateSeed));
        harmony.Patch(generateSeed, prefix: new HarmonyMethod(typeof(NewWorldSeed), nameof(GenerateSeedPrefix)));
        _applied = true;
    }

    // 1 to 10 letters or digits (the game's own seed field takes 10 characters).
    internal static bool IsValid(string seed)
    {
        if (seed.Length == 0 || seed.Length > MaximumLength)
        {
            return false;
        }

        foreach (char character in seed)
        {
            if (!((character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') ||
                  (character >= '0' && character <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool GenerateSeedPrefix(ref string __result)
    {
        try
        {
            if (_offered || _setting == null)
            {
                return true;
            }

            string seed = (_setting.Value ?? string.Empty).Trim();
            if (seed.Length == 0 || !IsValid(seed) || WorldExists(_worldName))
            {
                return true;
            }

            _offered = true;
            _used = seed;
            __result = seed;
            return false;
        }
        catch
        {
            // Never let the setting stop a world from being created: the game's own seed then.
            return true;
        }
    }

    // Main thread, polled until the server's world is up.
    public static void Settle()
    {
        if (_settled || _setting == null)
        {
            return;
        }

        World? world = ZNet.World;
        if (world == null)
        {
            return;
        }

        _settled = true;
        string configured = (_setting.Value ?? string.Empty).Trim();
        string name = world.m_name ?? _worldName;
        string message;
        if (_used != null && string.Equals(world.m_seedName, _used, StringComparison.Ordinal))
        {
            message = $"[Server] NewWorldSeed: created the new world '{name}' with seed '{_used}'; the setting is now cleared.";
        }
        else if (configured.Length == 0)
        {
            return;
        }
        else if (!IsValid(configured))
        {
            message = $"[Server] NewWorldSeed ignored: '{configured}' is not a seed (1 to {MaximumLength} letters or digits); world '{name}' uses seed '{world.m_seedName}'; the setting is now cleared.";
        }
        else
        {
            message = $"[Server] NewWorldSeed '{configured}' ignored: the world '{name}' already existed (seed '{world.m_seedName}'); the setting is now cleared.";
        }

        try
        {
            _setting.SetLocalValue(string.Empty);
        }
        catch (Exception exception)
        {
            message += $" Clearing it failed ({exception.GetType().Name}); remove it from valheimone.cfg by hand.";
        }

        _log?.Info(message);
    }

    // Tests only: start over as a fresh server would.
    internal static void ResetForTest(ConfigEntryString setting, ModLogger log, string worldName)
    {
        _setting = setting;
        _log = log;
        _worldName = worldName;
        _used = null;
        _offered = false;
        _settled = false;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using ValheimOne.Modules;

namespace ValheimOne.Infrastructure;

internal static class ContractDiagnostics
{
    private const int PointCount = 128;
    private const double WorldRadius = 10000.0;
    private const double GoldenAngleRadians = 2.39996322972865332;
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ModLogger? _log;
    private static int _worldgenReported;

    public static bool IsEnabled { get; } =
        string.Equals(
            Environment.GetEnvironmentVariable("VALHEIMONE_CONTRACT"),
            "1",
            StringComparison.Ordinal);

    public static void Initialize(
        Harmony harmony,
        ModLogger log,
        IReadOnlyList<IFeatureModule> modules,
        int successfulModules,
        IReadOnlyList<string> failedModules)
    {
        _log = log;
        InstallWorldgenPatch(harmony, log);

        int patchedMethods = CountOwnedPatchedMethods(ValheimOnePlugin.PluginGuid);
        string failures = failedModules.Count == 0
            ? "none"
            : string.Join(",", failedModules);
        log.Info(
            $"VO_CONTRACT patches total={modules.Count} ok={successfulModules} " +
            $"failed={failures} patchedMethods={patchedMethods}");

        int enabledModules = 0;
        foreach (IFeatureModule module in modules)
        {
            if (module.IsEnabled)
            {
                enabledModules++;
            }
        }

        log.Info($"VO_CONTRACT modules count={modules.Count} enabled={enabledModules}");
    }

    public static string DescribePatchFailure(IFeatureModule module, Exception exception)
    {
        Exception cause = exception;
        while (cause is TypeInitializationException && cause.InnerException != null)
        {
            cause = cause.InnerException;
        }

        string target = cause.GetType().Name;
        string message = cause.Message;
        int targetStart = message.IndexOf('\'');
        if (targetStart >= 0)
        {
            int targetEnd = message.IndexOf('\'', targetStart + 1);
            if (targetEnd > targetStart + 1)
            {
                target = message.Substring(targetStart + 1, targetEnd - targetStart - 1);
            }
        }

        return SanitizeToken(module.Section) + ":" + SanitizeToken(target);
    }

    public static string SingleLineMessage(Exception exception)
    {
        return exception.Message.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static void InstallWorldgenPatch(Harmony harmony, ModLogger log)
    {
        try
        {
            MethodInfo start = AccessTools.Method(typeof(ZoneSystem), "Start", Type.EmptyTypes)
                ?? throw new MissingMethodException(nameof(ZoneSystem), "Start");
            harmony.Patch(
                start,
                postfix: new HarmonyMethod(
                    typeof(ContractDiagnostics),
                    nameof(ZoneSystemStartPostfix)));
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _worldgenReported, 1);
            log.Info($"VO_CONTRACT worldgen ERROR {SingleLineMessage(exception)}");
        }
    }

    private static void ZoneSystemStartPostfix()
    {
        if (Interlocked.Exchange(ref _worldgenReported, 1) != 0)
        {
            return;
        }

        ModLogger? log = _log;
        if (log == null)
        {
            return;
        }

        try
        {
            WorldGenerator? generator = WorldGenerator.instance;
            if (generator == null)
            {
                throw new InvalidOperationException("WorldGenerator.instance is null");
            }

            ulong hash = FnvOffsetBasis;

            // A golden-angle spiral gives deterministic, uniform disk coverage. The half-step
            // radius samples the interior of 128 equal-area rings without touching the edge.
            for (int index = 0; index < PointCount; index++)
            {
                double radius = WorldRadius * Math.Sqrt((index + 0.5) / PointCount);
                double angle = index * GoldenAngleRadians;
                float wx = (float)(radius * Math.Cos(angle));
                float wy = (float)(radius * Math.Sin(angle));

                int biome = (int)generator.GetBiome(wx, wy, 0.02f, false);
                int heightHundredths = checked((int)Math.Round(
                    generator.GetHeight(wx, wy) * 100.0,
                    MidpointRounding.AwayFromZero));
                AddInt32(ref hash, biome);
                AddInt32(ref hash, heightHundredths);
            }

            string seed = generator.GetSeed().ToString(CultureInfo.InvariantCulture);
            string fingerprint = hash.ToString("x16", CultureInfo.InvariantCulture);
            log.Info($"VO_CONTRACT worldgen seed={seed} points={PointCount} hash={fingerprint}");
        }
        catch (Exception exception)
        {
            log.Info($"VO_CONTRACT worldgen ERROR {SingleLineMessage(exception)}");
        }
    }

    private static void AddInt32(ref ulong hash, int value)
    {
        unchecked
        {
            uint bits = (uint)value;
            hash = (hash ^ (byte)bits) * FnvPrime;
            hash = (hash ^ (byte)(bits >> 8)) * FnvPrime;
            hash = (hash ^ (byte)(bits >> 16)) * FnvPrime;
            hash = (hash ^ (byte)(bits >> 24)) * FnvPrime;
        }
    }

    private static int CountOwnedPatchedMethods(string ownerId)
    {
        int count = 0;
        foreach (MethodBase method in Harmony.GetAllPatchedMethods())
        {
            Patches? patches = Harmony.GetPatchInfo(method);
            if (patches != null && patches.Owners.Contains(ownerId))
            {
                count++;
            }
        }

        return count;
    }

    private static string SanitizeToken(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character) ||
                character == '.' ||
                character == '_' ||
                character == '+' ||
                character == '(' ||
                character == ')')
            {
                result.Append(character);
            }
            else
            {
                result.Append('_');
            }
        }

        return result.ToString();
    }
}

using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace ValheimOne.Infrastructure;

// A crossplay dedicated server quits on its FIRST failed PlayFab login. The game's server handler
// (ZPlayFabMatchmaking.OnServerLoginFinished) answers LoginType.Failed with Application.Quit(), and right
// after raising that event PlayFabManager.OnLoginFailure reads PlatformManager.DistributionPlatform, which a
// dedicated server does not have, so the game's own retry is never scheduled either. A login that PlayFab
// throttles while many servers restart in the same minute ("exceeded the maximum API request rate"), or that
// is lost in a short network cut, therefore ends the server until something restarts it.
//
// On a dedicated server this answers a failed login the way the game answers it on a player's PC: try again.
// The game's own login runs again after a random 5 to 20 seconds (random, so servers that failed in the same
// second do not all come back in the same second), at most five times since the last successful login, with
// one log line each. A banned account is not retried. After the fifth retry the game's own failure path runs,
// so a server whose login can never work still quits as before and is restarted from outside.
internal static class PlayFabLoginRetry
{
    internal const int MaxRetries = 5;
    internal const float MinDelaySeconds = 5f;
    internal const float MaxDelaySeconds = 20f;

    private static ModLogger? _log;
    private static MethodInfo? _login;
    private static FieldInfo? _loginState;
    private static object? _notLoggedIn;
    private static int _retriesSinceSuccess;
    private static int _totalRetries;

    // Test hooks (the runtime regression): how many retries ran, and a shorter wait.
    internal static int RetriesSinceSuccess => _retriesSinceSuccess;
    internal static int TotalRetries => _totalRetries;
    internal static Func<float>? DelayOverride;

    public static void Apply(Harmony harmony, ModLogger log)
    {
        _log = log;
        MethodInfo failure = AccessTools.Method(typeof(PlayFabManager), "OnLoginFailure")
            ?? throw new MissingMethodException(nameof(PlayFabManager), "OnLoginFailure");
        if (failure.GetParameters().Length != 1 || failure.IsStatic)
            throw new InvalidOperationException("Unknown PlayFab login failure shape");
        MethodInfo success = AccessTools.Method(typeof(PlayFabManager), "OnLoginSuccess")
            ?? throw new MissingMethodException(nameof(PlayFabManager), "OnLoginSuccess");
        _login = AccessTools.Method(typeof(PlayFabManager), "Login", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(PlayFabManager), "Login");
        _loginState = AccessTools.Field(typeof(PlayFabManager), "m_loginState")
            ?? throw new MissingFieldException(nameof(PlayFabManager), "m_loginState");
        if (!_loginState.FieldType.IsEnum || !Enum.IsDefined(_loginState.FieldType, "NotLoggedIn"))
            throw new InvalidOperationException("Unknown PlayFab login state shape");
        _notLoggedIn = Enum.Parse(_loginState.FieldType, "NotLoggedIn");
        harmony.Patch(failure, prefix: new HarmonyMethod(typeof(PlayFabLoginRetry), nameof(OnLoginFailurePrefix)));
        harmony.Patch(success, postfix: new HarmonyMethod(typeof(PlayFabLoginRetry), nameof(OnLoginSuccessPostfix)));
    }

    // The error's code name ("APIClientRequestRateLimitExceeded", "AccountBanned", ...) or null for no error object.
    // Read by reflection so ValheimOne needs no compile-time reference to PlayFab's assembly.
    internal static string? ErrorCode(object? error)
    {
        if (error == null) return null;
        object? code = Traverse.Create(error).Field("Error").GetValue();
        return code?.ToString();
    }

    internal static string ErrorMessage(object? error)
    {
        if (error == null) return "no error details";
        object? message = Traverse.Create(error).Field("ErrorMessage").GetValue();
        string text = message as string ?? string.Empty;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 160 ? text.Substring(0, 160) : text;
    }

    internal static bool ShouldRetry(string? errorCode, int retriesSoFar)
    {
        return errorCode != "AccountBanned" && retriesSoFar < MaxRetries;
    }

    internal static float NextDelay()
    {
        Func<float>? overrideDelay = DelayOverride;
        if (overrideDelay != null) return overrideDelay();
        return UnityEngine.Random.Range(MinDelaySeconds, MaxDelaySeconds);
    }

    private static bool OnLoginFailurePrefix(PlayFabManager __instance, object error)
    {
        if (!GameProcess.IsDedicatedServer || __instance == null)
        {
            return true;
        }

        string? code = ErrorCode(error);
        if (!ShouldRetry(code, _retriesSinceSuccess))
        {
            if (code != "AccountBanned")
            {
                _log?.Warning(
                    $"PlayFab login failed again ({code ?? "no error code"}) after {MaxRetries} retries; " +
                    "letting the game handle it, which stops the server.");
            }

            return true;
        }

        _retriesSinceSuccess++;
        _totalRetries++;
        float delay = NextDelay();
        _log?.Warning(
            $"PlayFab login failed ({code ?? "no error code"}: {ErrorMessage(error)}); trying again in " +
            $"{delay:0} s (retry {_retriesSinceSuccess} of {MaxRetries}) instead of stopping the server.");
        _loginState!.SetValue(__instance, _notLoggedIn);
        __instance.StartCoroutine(RetryAfter(__instance, delay));
        return false;
    }

    private static IEnumerator RetryAfter(PlayFabManager manager, float delay)
    {
        DateTime due = DateTime.UtcNow.AddSeconds(delay);
        while (DateTime.UtcNow < due)
        {
            yield return null;
        }

        if (manager == null || PlayFabManager.IsLoggedIn)
        {
            yield break;
        }

        _login!.Invoke(manager, null);
    }

    private static void OnLoginSuccessPostfix()
    {
        if (_retriesSinceSuccess > 0)
        {
            _log?.Info($"PlayFab login succeeded after {_retriesSinceSuccess} retr" +
                       (_retriesSinceSuccess == 1 ? "y." : "ies."));
        }

        _retriesSinceSuccess = 0;
    }
}

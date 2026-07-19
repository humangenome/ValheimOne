using System;
using System.Reflection;

namespace ValheimOne.Infrastructure;

internal static class GameVersionDetector
{
    private static readonly string[] ValueMemberNames =
    {
        "CurrentVersion",
        "currentVersion",
        "m_currentVersion",
        "m_version",
    };

    public static string? TryDetect(ModLogger log)
    {
        try
        {
            Type? versionType = FindGameVersionType();
            if (versionType == null)
            {
                log.Warning("Valheim version API was not found; continuing with the supported-version constant.");
                return null;
            }

            foreach (string memberName in ValueMemberNames)
            {
                PropertyInfo? property = versionType.GetProperty(memberName, StaticFlags);
                if (property?.GetIndexParameters().Length == 0)
                {
                    string? value = property.GetValue(null, null)?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                FieldInfo? field = versionType.GetField(memberName, StaticFlags);
                string? fieldValue = field?.GetValue(null)?.ToString();
                if (!string.IsNullOrWhiteSpace(fieldValue))
                {
                    return fieldValue;
                }
            }

            MethodInfo? method = versionType.GetMethod(
                "GetVersionString",
                StaticFlags,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            string? methodValue = method?.Invoke(null, null)?.ToString();
            if (!string.IsNullOrWhiteSpace(methodValue))
            {
                return methodValue;
            }

            log.Warning("Valheim version API returned no usable value; continuing safely.");
        }
        catch (Exception exception)
        {
            log.Warning($"Valheim version detection failed ({exception.GetType().Name}); continuing safely.");
        }

        return null;
    }

    private const BindingFlags StaticFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private static Type? FindGameVersionType()
    {
        Type? versionType = typeof(Player).Assembly.GetType("Version", throwOnError: false);
        if (versionType != null)
        {
            return versionType;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            versionType = assembly.GetType("Version", throwOnError: false);
            if (versionType != null && versionType != typeof(Version))
            {
                return versionType;
            }
        }

        return null;
    }
}

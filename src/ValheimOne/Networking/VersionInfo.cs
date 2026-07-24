using System;

namespace ValheimOne.Networking;

public static class VersionInfo
{
    public const string PluginVersion = "0.12.2";
    public const string SupportedGameVersion = "0.221.12";

    // Bump monotonically whenever the network payload or synced-config contract changes incompatibly.
    public const int NetworkConfigSchema = 1;

    public static bool IsCompatible(string remotePluginVersion, int remoteSchema)
    {
        if (remoteSchema != NetworkConfigSchema ||
            !System.Version.TryParse(PluginVersion, out System.Version localVersion) ||
            !System.Version.TryParse(remotePluginVersion, out System.Version remoteVersion))
        {
            return false;
        }

        return localVersion.Major == remoteVersion.Major &&
            localVersion.Minor == remoteVersion.Minor;
    }
}

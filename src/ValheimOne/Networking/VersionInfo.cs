namespace ValheimOne.Networking;

public static class VersionInfo
{
    public const string PluginVersion = "0.1.0";
    public const string SupportedGameVersion = "0.221.12";

    // Bump monotonically whenever the network payload or synced-config contract changes incompatibly.
    public const int NetworkConfigSchema = 1;
}

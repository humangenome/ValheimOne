namespace ValheimOne.Configuration;

public sealed class ServerConfig
{
    private readonly FeatureDefinition _feature;

    public ServerConfig(FeatureRegistry registry)
    {
        _feature = registry.Register(
            "server enforcement infrastructure",
            "Server",
            FeatureClassification.ServerAuthoritative,
            enabledByDefault: true,
            "Enable ValheimOne's handshake, enforcement, and server configuration transport infrastructure.");
        EnforceMod = _feature.Bool(
            "EnforceMod",
            defaultValue: false,
            "Require every remote client to run a handshake-compatible ValheimOne version; incompatible or vanilla clients are kicked.");
        SyncConfig = _feature.Bool(
            "SyncConfig",
            defaultValue: true,
            "Push the server's effective non-ClientOnly feature settings to compatible ValheimOne clients without changing their local config files.");
        HandshakeGraceSeconds = _feature.Int(
            "HandshakeGraceSeconds",
            defaultValue: 15,
            "Seconds after peer setup to wait for VO_Hello before classifying a client as vanilla and, when EnforceMod is enabled, kicking it.");
    }

    public bool Enabled => _feature.Enabled.Value;

    public ConfigEntryBool EnforceMod { get; }

    public ConfigEntryBool SyncConfig { get; }

    public ConfigEntryInt HandshakeGraceSeconds { get; }
}

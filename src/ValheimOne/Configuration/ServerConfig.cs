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
        MaxPlayers = _feature.Int(
            "MaxPlayers",
            defaultValue: 0,
            "Gameplay-effective player cap for the dedicated server. Zero keeps Valheim's default cap of 10. " +
            "Values above 10 raise the real join limit (clamped 1..127). The direct join gate hot-reloads; " +
            "on crossplay the PlayFab lobby capacity is applied once at boot.");
        NewWorldSeed = _feature.String(
            "NewWorldSeed",
            string.Empty,
            "One-shot seed for a new world (1 to 10 letters or digits). When the world named on the command line " +
            "does not exist yet, the dedicated server creates it with this seed instead of a random one. Either way " +
            "the setting is cleared after the start and the log says what happened; an existing world is never changed.");
        NoPasswordRequired = _feature.Bool(
            "NoPasswordRequired",
            defaultValue: false,
            "Allow starting a public dedicated server without a join password by skipping the vanilla " +
            "minimum-password startup validation. Does not remove or bypass a password that is set.");
    }

    public bool Enabled => _feature.Enabled.Value;

    public ConfigEntryBool EnforceMod { get; }

    public ConfigEntryString NewWorldSeed { get; }

    public ConfigEntryBool SyncConfig { get; }

    public ConfigEntryInt HandshakeGraceSeconds { get; }

    public ConfigEntryInt MaxPlayers { get; }

    public ConfigEntryBool NoPasswordRequired { get; }
}

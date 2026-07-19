using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;
using ValheimOne.Modules;

namespace ValheimOne.Query;

public sealed class QueryModule : IFeatureModule
{
    private readonly FeatureDefinition _feature;
    private readonly QueryConfig _config;
    private readonly ModLogger _log;

    public QueryModule(FeatureRegistry registry)
    {
        _log = new ModLogger(BepInEx.Logging.Logger.CreateLogSource("ValheimOne.Query"));
        _feature = registry.Register(Name, Section, Classification);

        ConfigEntryInt queryPort = _feature.Int(
            "QueryPort",
            0,
            "UDP port for the A2S query responder. 0 uses game port + 4. " +
            "(Game port + 1 is Valheim's own Steam query port when crossplay is off; " +
            "ValheimOne avoids it.)");
        ConfigEntryBool publicPlayerNames = _feature.Bool(
            "PublicPlayerNames",
            false,
            "Report real player names in A2S_PLAYER replies. When disabled, report generic slots.");
        ConfigEntryInt maxPlayers = _feature.Int(
            "MaxPlayers",
            10,
            "Reported maximum player count. Vanilla dedicated servers allow 10 players.");
        _config = new QueryConfig(queryPort, publicPlayerNames, maxPlayers);
    }

    public string Name => "Server query";

    public string Section => "Query";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.ServerAuthoritative;

    public void ApplyPatches(Harmony harmony)
    {
        if (QueryBehaviour.Instance != null)
        {
            _log.Warning("[Query] behaviour already exists; skipping duplicate initialization.");
            return;
        }

        var host = new GameObject("ValheimOne.Query")
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        UnityEngine.Object.DontDestroyOnLoad(host);
        QueryBehaviour.Initialize(host, _config, _log, () => _feature.Enabled.Value);
    }
}

using ValheimOne.Configuration;

namespace ValheimOne.Query;

internal sealed class QueryConfig
{
    private readonly ConfigEntryInt _queryPort;
    private readonly ConfigEntryBool _publicPlayerNames;
    private readonly ConfigEntryInt _maxPlayers;

    public QueryConfig(
        ConfigEntryInt queryPort,
        ConfigEntryBool publicPlayerNames,
        ConfigEntryInt maxPlayers)
    {
        _queryPort = queryPort;
        _publicPlayerNames = publicPlayerNames;
        _maxPlayers = maxPlayers;
    }

    public int QueryPort => _queryPort.Value;

    public bool PublicPlayerNames => _publicPlayerNames.Value;

    public int MaxPlayers => _maxPlayers.Value;
}

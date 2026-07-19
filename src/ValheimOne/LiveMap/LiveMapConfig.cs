using ValheimOne.Configuration;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapConfig
{
    private readonly ConfigEntryInt _port;
    private readonly ConfigEntryInt _textureSize;
    private readonly ConfigEntryFloat _playerUpdateSeconds;
    private readonly ConfigEntryBool _adminSeesAll;
    private readonly ConfigEntryString _bindIp;
    private readonly ConfigEntryString _accessToken;
    private readonly ConfigEntryString _fogMode;

    public LiveMapConfig(
        ConfigEntryInt port,
        ConfigEntryInt textureSize,
        ConfigEntryFloat playerUpdateSeconds,
        ConfigEntryBool adminSeesAll,
        ConfigEntryString bindIp,
        ConfigEntryString accessToken,
        ConfigEntryString fogMode)
    {
        _port = port;
        _textureSize = textureSize;
        _playerUpdateSeconds = playerUpdateSeconds;
        _adminSeesAll = adminSeesAll;
        _bindIp = bindIp;
        _accessToken = accessToken;
        _fogMode = fogMode;
    }

    public int Port => _port.Value;

    public int TextureSize => _textureSize.Value;

    public float PlayerUpdateSeconds => _playerUpdateSeconds.Value;

    public bool AdminSeesAll => _adminSeesAll.Value;

    public string BindIp => _bindIp.Value;

    public string AccessToken => _accessToken.Value;

    public string FogMode => _fogMode.Value;
}

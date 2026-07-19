using BepInEx.Configuration;
using ValheimOne.Configuration;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapConfig
{
    private readonly ConfigEntryInt _port;
    private readonly ConfigEntryInt _textureSize;
    private readonly ConfigEntryFloat _playerUpdateSeconds;
    private readonly ConfigEntryBool _adminSeesAll;
    private readonly ConfigEntry<string>? _bindIp;
    private readonly ConfigEntry<string>? _accessToken;
    private readonly ConfigEntry<string>? _fogMode;

    public LiveMapConfig(
        ConfigEntryInt port,
        ConfigEntryInt textureSize,
        ConfigEntryFloat playerUpdateSeconds,
        ConfigEntryBool adminSeesAll,
        ConfigEntry<string>? bindIp,
        ConfigEntry<string>? accessToken,
        ConfigEntry<string>? fogMode)
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

    public string BindIp => _bindIp?.Value ?? string.Empty;

    public string AccessToken => _accessToken?.Value ?? string.Empty;

    public string FogMode => _fogMode?.Value ?? "full";
}

using System;
using System.Collections.Generic;
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
    private readonly ConfigEntryBool _publicView;
    private readonly ConfigEntryBool _publicShowPlayerNames;
    private readonly ConfigEntryBool _entityLayer;
    private readonly ConfigEntryString _fogMode;
    private readonly ConfigEntryBool _consoleEnabled;
    private readonly ConfigEntryString _consoleWhitelist;
    private readonly ConfigEntryBool _allowAllCommands;
    private readonly ConfigEntryInt _consoleLogLines;
    private readonly ConfigEntryBool _statusPublic;

    public LiveMapConfig(
        ConfigEntryInt port,
        ConfigEntryInt textureSize,
        ConfigEntryFloat playerUpdateSeconds,
        ConfigEntryBool adminSeesAll,
        ConfigEntryString bindIp,
        ConfigEntryString accessToken,
        ConfigEntryBool publicView,
        ConfigEntryBool publicShowPlayerNames,
        ConfigEntryBool entityLayer,
        ConfigEntryString fogMode,
        ConfigEntryBool consoleEnabled,
        ConfigEntryString consoleWhitelist,
        ConfigEntryBool allowAllCommands,
        ConfigEntryInt consoleLogLines,
        ConfigEntryBool statusPublic)
    {
        _port = port;
        _textureSize = textureSize;
        _playerUpdateSeconds = playerUpdateSeconds;
        _adminSeesAll = adminSeesAll;
        _bindIp = bindIp;
        _accessToken = accessToken;
        _publicView = publicView;
        _publicShowPlayerNames = publicShowPlayerNames;
        _entityLayer = entityLayer;
        _fogMode = fogMode;
        _consoleEnabled = consoleEnabled;
        _consoleWhitelist = consoleWhitelist;
        _allowAllCommands = allowAllCommands;
        _consoleLogLines = consoleLogLines;
        _statusPublic = statusPublic;
    }

    public int Port => _port.Value;

    public int TextureSize => _textureSize.Value;

    public float PlayerUpdateSeconds => _playerUpdateSeconds.Value;

    public bool AdminSeesAll => _adminSeesAll.Value;

    public string BindIp => _bindIp.Value;

    public string AccessToken => _accessToken.Value;

    public bool PublicView => _publicView.Value;

    public bool PublicShowPlayerNames => _publicShowPlayerNames.Value;

    public bool EntityLayer => _entityLayer.Value;

    public bool ConsoleEnabled => _consoleEnabled.Value;

    public HashSet<string> ConsoleWhitelist
    {
        get
        {
            var commands = new HashSet<string>(StringComparer.Ordinal);
            string value = _consoleWhitelist.Value ?? string.Empty;
            string[] parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                commands.Add(parts[index].ToLowerInvariant());
            }

            return commands;
        }
    }

    public bool AllowAllCommands => _allowAllCommands.Value;

    public int ConsoleLogLines => Math.Max(50, Math.Min(5000, _consoleLogLines.Value));

    public bool StatusPublic => _statusPublic.Value;

    public string FogMode
    {
        get
        {
            string value = (_fogMode.Value ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "trails":
                case "explored":
                    return value;
                case "full":
                case "off":
                default:
                    return "off";
            }
        }
    }
}

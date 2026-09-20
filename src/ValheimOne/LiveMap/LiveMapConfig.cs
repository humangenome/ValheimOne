using System;
using System.Collections.Generic;
using ValheimOne.Configuration;

namespace ValheimOne.LiveMap;

internal sealed class LiveMapConfig
{
    private readonly ConfigEntryInt _port;
    private readonly ConfigEntryInt _textureSize;
    private readonly ConfigEntryFloat _playerUpdateSeconds;
    private readonly ConfigEntryInt _ghostRetentionDays;
    private readonly ConfigEntryBool _adminSeesAll;
    private readonly ConfigEntryString _bindIp;
    private readonly ConfigEntryString _accessToken;
    private readonly ConfigEntryString _shareToken;
    private readonly ConfigEntryBool _publicView;
    private readonly ConfigEntryBool _publicPins;
    private readonly ConfigEntryBool _sharedPinEditing;
    private readonly ConfigEntryBool _publicWebPins;
    private readonly ConfigEntryBool _timelapse;
    private readonly ConfigEntryInt _timelapseIntervalMinutes;
    private readonly ConfigEntryBool _publicTimelapse;
    private readonly ConfigEntryBool _mirrorChat;
    private readonly ConfigEntryBool _respectInGameVisibility;
    private readonly ConfigEntryBool _publicShowPlayerNames;
    private readonly ConfigEntryBool _entityLayer;
    private readonly ConfigEntryBool _resourceLayers;
    private readonly ConfigEntryString _fogMode;
    private readonly ConfigEntryBool _fogHideUnexplored;
    private readonly ConfigEntryBool _sharedFog;
    private readonly ConfigEntryString _sharedPoiGroups;
    private readonly ConfigEntryString _publicPoiGroups;
    private readonly ConfigEntryString _publicEntityGroups;
    private readonly ConfigEntryBool _publicChat;
    private readonly ConfigEntryBool _publicLeaderboard;
    private readonly ConfigEntryBool _publicEvents;
    private readonly ConfigEntryBool _consoleEnabled;
    private readonly ConfigEntryString _consoleWhitelist;
    private readonly ConfigEntryBool _allowAllCommands;
    private readonly ConfigEntryInt _consoleLogLines;
    private readonly ConfigEntryBool _statusPublic;

    public LiveMapConfig(
        ConfigEntryInt port,
        ConfigEntryInt textureSize,
        ConfigEntryFloat playerUpdateSeconds,
        ConfigEntryInt ghostRetentionDays,
        ConfigEntryBool adminSeesAll,
        ConfigEntryString bindIp,
        ConfigEntryString accessToken,
        ConfigEntryString shareToken,
        ConfigEntryBool publicView,
        ConfigEntryBool publicPins,
        ConfigEntryBool sharedPinEditing,
        ConfigEntryBool publicWebPins,
        ConfigEntryBool timelapse,
        ConfigEntryInt timelapseIntervalMinutes,
        ConfigEntryBool publicTimelapse,
        ConfigEntryBool mirrorChat,
        ConfigEntryBool respectInGameVisibility,
        ConfigEntryBool publicShowPlayerNames,
        ConfigEntryBool entityLayer,
        ConfigEntryBool resourceLayers,
        ConfigEntryString fogMode,
        ConfigEntryBool fogHideUnexplored,
        ConfigEntryBool sharedFog,
        ConfigEntryString sharedPoiGroups,
        ConfigEntryString publicPoiGroups,
        ConfigEntryString publicEntityGroups,
        ConfigEntryBool publicChat,
        ConfigEntryBool publicLeaderboard,
        ConfigEntryBool publicEvents,
        ConfigEntryBool consoleEnabled,
        ConfigEntryString consoleWhitelist,
        ConfigEntryBool allowAllCommands,
        ConfigEntryInt consoleLogLines,
        ConfigEntryBool statusPublic)
    {
        _port = port;
        _textureSize = textureSize;
        _playerUpdateSeconds = playerUpdateSeconds;
        _ghostRetentionDays = ghostRetentionDays;
        _adminSeesAll = adminSeesAll;
        _bindIp = bindIp;
        _accessToken = accessToken;
        _shareToken = shareToken;
        _publicView = publicView;
        _publicPins = publicPins;
        _sharedPinEditing = sharedPinEditing;
        _publicWebPins = publicWebPins;
        _timelapse = timelapse;
        _timelapseIntervalMinutes = timelapseIntervalMinutes;
        _publicTimelapse = publicTimelapse;
        _mirrorChat = mirrorChat;
        _respectInGameVisibility = respectInGameVisibility;
        _publicShowPlayerNames = publicShowPlayerNames;
        _entityLayer = entityLayer;
        _resourceLayers = resourceLayers;
        _fogMode = fogMode;
        _fogHideUnexplored = fogHideUnexplored;
        _sharedFog = sharedFog;
        _sharedPoiGroups = sharedPoiGroups;
        _publicPoiGroups = publicPoiGroups;
        _publicEntityGroups = publicEntityGroups;
        _publicChat = publicChat;
        _publicLeaderboard = publicLeaderboard;
        _publicEvents = publicEvents;
        _consoleEnabled = consoleEnabled;
        _consoleWhitelist = consoleWhitelist;
        _allowAllCommands = allowAllCommands;
        _consoleLogLines = consoleLogLines;
        _statusPublic = statusPublic;
    }

    public int Port => _port.Value;

    public int TextureSize => _textureSize.Value;

    public float PlayerUpdateSeconds => _playerUpdateSeconds.Value;

    public int GhostRetentionDays => Math.Max(1, Math.Min(365, _ghostRetentionDays.Value));

    public bool AdminSeesAll => _adminSeesAll.Value;

    public string BindIp => _bindIp.Value;

    public string AccessToken => _accessToken.Value;

    public string ShareToken => _shareToken.Value;

    public bool PublicView => _publicView.Value;

    public bool PublicPins => _publicPins.Value;

    public bool SharedPinEditing => _sharedPinEditing.Value;

    public bool PublicWebPins => _publicWebPins.Value;

    public bool Timelapse => _timelapse.Value;

    public int TimelapseIntervalMinutes =>
        Math.Max(5, Math.Min(1440, _timelapseIntervalMinutes.Value));

    public bool PublicTimelapse => _publicTimelapse.Value;

    public bool MirrorChat => _mirrorChat.Value;

    public bool RespectInGameVisibility => _respectInGameVisibility.Value;

    public bool PublicShowPlayerNames => _publicShowPlayerNames.Value;

    public bool EntityLayer => _entityLayer.Value;

    public bool ResourceLayers => _resourceLayers.Value;

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

    public bool FogHideUnexplored => _fogHideUnexplored.Value;

    public bool SharedFog => _sharedFog.Value;

    public string SharedPoiGroups => (_sharedPoiGroups.Value ?? string.Empty).Trim().ToLowerInvariant();

    public string PublicPoiGroups => (_publicPoiGroups.Value ?? string.Empty).Trim().ToLowerInvariant();

    public string PublicEntityGroups => (_publicEntityGroups.Value ?? string.Empty).Trim().ToLowerInvariant();

    public bool PublicChat => _publicChat.Value;

    public bool PublicLeaderboard => _publicLeaderboard.Value;

    public bool PublicEvents => _publicEvents.Value;

    public bool AllowsSharedPoiGroup(string group)
    {
        return AllowsConfiguredGroup(SharedPoiGroups, group);
    }

    public bool AllowsPublicPoiGroup(string group)
    {
        string policy = PublicPoiGroups;
        if (string.IsNullOrEmpty(policy))
        {
            return PoiGroups.IsPublic(group);
        }

        return AllowsConfiguredGroup(policy, group);
    }

    public bool AllowsPublicEntityGroup(string group)
    {
        return AllowsConfiguredGroup(PublicEntityGroups, group);
    }

    public bool AllowsAnyPublicEntityGroup()
    {
        string[] allowed = SplitGroupPolicy(PublicEntityGroups);
        for (int index = 0; index < allowed.Length; index++)
        {
            string key = allowed[index];
            if (key == "none")
            {
                continue;
            }

            if (key == "all" || key.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AllowsConfiguredGroup(string policy, string group)
    {
        string category = "";
        if (PoiGroups.TryGet(group, out PoiGroupDefinition? definition) &&
            definition != null)
        {
            category = definition.Category;
        }

        string[] allowed = SplitGroupPolicy(policy);
        for (int index = 0; index < allowed.Length; index++)
        {
            string key = allowed[index];
            if (key == "all" ||
                key == group ||
                (category.Length > 0 && key == category))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] SplitGroupPolicy(string policy)
    {
        return policy.Split(
            new[] { ' ', ',', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
    }
}

using ValheimOne.Configuration;

namespace ValheimOne.Discord;

internal sealed class DiscordConfig
{
    public DiscordConfig(
        ConfigEntryString webhookUrl,
        ConfigEntryString serverDisplayName,
        ConfigEntryBool notifyJoin,
        ConfigEntryBool notifyLeave,
        ConfigEntryBool notifyDeath,
        ConfigEntryBool notifyRaid,
        ConfigEntryBool notifyWorldSave,
        ConfigEntryBool notifyDayChange)
    {
        WebhookUrlEntry = webhookUrl;
        ServerDisplayNameEntry = serverDisplayName;
        NotifyJoinEntry = notifyJoin;
        NotifyLeaveEntry = notifyLeave;
        NotifyDeathEntry = notifyDeath;
        NotifyRaidEntry = notifyRaid;
        NotifyWorldSaveEntry = notifyWorldSave;
        NotifyDayChangeEntry = notifyDayChange;
    }

    public ConfigEntryString WebhookUrlEntry { get; }

    public ConfigEntryString ServerDisplayNameEntry { get; }

    public ConfigEntryBool NotifyJoinEntry { get; }

    public ConfigEntryBool NotifyLeaveEntry { get; }

    public ConfigEntryBool NotifyDeathEntry { get; }

    public ConfigEntryBool NotifyRaidEntry { get; }

    public ConfigEntryBool NotifyWorldSaveEntry { get; }

    public ConfigEntryBool NotifyDayChangeEntry { get; }

    public string WebhookUrl => WebhookUrlEntry.Value;

    public string ServerDisplayName => ServerDisplayNameEntry.Value;

    public bool NotifyJoin => NotifyJoinEntry.Value;

    public bool NotifyLeave => NotifyLeaveEntry.Value;

    public bool NotifyDeath => NotifyDeathEntry.Value;

    public bool NotifyRaid => NotifyRaidEntry.Value;

    public bool NotifyWorldSave => NotifyWorldSaveEntry.Value;

    public bool NotifyDayChange => NotifyDayChangeEntry.Value;
}

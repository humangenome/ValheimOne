using System;
using ValheimOne.Configuration;

namespace ValheimOne.ActivityLog;

internal sealed class ActivityLogConfig
{
    private readonly ConfigEntryInt _retentionDays;

    public ActivityLogConfig(ConfigEntryInt retentionDays)
    {
        _retentionDays = retentionDays;
    }

    public int RetentionDays => Math.Max(1, Math.Min(3650, _retentionDays.Value));
}

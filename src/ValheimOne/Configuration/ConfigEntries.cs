using BepInEx.Configuration;

namespace ValheimOne.Configuration;

public interface IConfigEntry
{
    ConfigKeyDefinition Definition { get; }
}

public sealed class ConfigEntryBool : IConfigEntry
{
    private readonly ConfigEntry<bool> _entry;

    internal ConfigEntryBool(ConfigEntry<bool> entry, ConfigKeyDefinition definition)
    {
        _entry = entry;
        Definition = definition;
    }

    public ConfigKeyDefinition Definition { get; }

    public bool Value => _entry.Value;
}

public sealed class ConfigEntryInt : IConfigEntry
{
    private readonly ConfigEntry<int> _entry;

    internal ConfigEntryInt(ConfigEntry<int> entry, ConfigKeyDefinition definition)
    {
        _entry = entry;
        Definition = definition;
    }

    public ConfigKeyDefinition Definition { get; }

    public int Value => _entry.Value;
}

public sealed class ConfigEntryFloat : IConfigEntry
{
    private readonly ConfigEntry<float> _entry;

    internal ConfigEntryFloat(ConfigEntry<float> entry, ConfigKeyDefinition definition)
    {
        _entry = entry;
        Definition = definition;
    }

    public ConfigKeyDefinition Definition { get; }

    public float Value => _entry.Value;
}

public sealed class ConfigEntryPercent : IConfigEntry
{
    private readonly ConfigEntry<float> _entry;

    internal ConfigEntryPercent(ConfigEntry<float> entry, ConfigKeyDefinition definition)
    {
        _entry = entry;
        Definition = definition;
    }

    public ConfigKeyDefinition Definition { get; }

    public float Value => _entry.Value;

    public float Apply(float baseValue)
    {
        if (Value <= -100f)
        {
            return 0f;
        }

        float result = baseValue * (1f + (Value / 100f));
        return result < 0f ? 0f : result;
    }
}

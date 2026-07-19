using System.Globalization;
using BepInEx.Configuration;

namespace ValheimOne.Configuration;

public interface IConfigEntry
{
    ConfigKeyDefinition Definition { get; }

    string GetSerializedValue();
}

public sealed class ConfigEntryBool : IConfigEntry
{
    private readonly ConfigEntry<bool> _entry;
    private readonly ValheimOneConfig _settings;

    internal ConfigEntryBool(
        ConfigEntry<bool> entry,
        ConfigKeyDefinition definition,
        ValheimOneConfig settings)
    {
        _entry = entry;
        _settings = settings;
        Definition = definition;
    }

    public ConfigKeyDefinition Definition { get; }

    public bool Value => _settings.GetEffectiveValue(_entry, Definition);

    public string GetSerializedValue() => Value ? "true" : "false";
}

public sealed class ConfigEntryInt : IConfigEntry
{
    private readonly ConfigEntry<int> _entry;
    private readonly ValheimOneConfig _settings;

    internal ConfigEntryInt(
        ConfigEntry<int> entry,
        ConfigKeyDefinition definition,
        ValheimOneConfig settings)
    {
        _entry = entry;
        _settings = settings;
        Definition = definition;
    }

    public ConfigKeyDefinition Definition { get; }

    public int Value => _settings.GetEffectiveValue(_entry, Definition);

    public string GetSerializedValue() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class ConfigEntryFloat : IConfigEntry
{
    private readonly ConfigEntry<float> _entry;
    private readonly ValheimOneConfig _settings;

    internal ConfigEntryFloat(
        ConfigEntry<float> entry,
        ConfigKeyDefinition definition,
        ValheimOneConfig settings)
    {
        _entry = entry;
        _settings = settings;
        Definition = definition;
    }

    public ConfigKeyDefinition Definition { get; }

    public float Value => _settings.GetEffectiveValue(_entry, Definition);

    public string GetSerializedValue() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public sealed class ConfigEntryPercent : IConfigEntry
{
    private readonly ConfigEntry<float> _entry;
    private readonly ValheimOneConfig _settings;

    internal ConfigEntryPercent(
        ConfigEntry<float> entry,
        ConfigKeyDefinition definition,
        ValheimOneConfig settings)
    {
        _entry = entry;
        _settings = settings;
        Definition = definition;
    }

    public ConfigKeyDefinition Definition { get; }

    public float Value => _settings.GetEffectiveValue(_entry, Definition);

    public string GetSerializedValue() => Value.ToString("R", CultureInfo.InvariantCulture);

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

using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace ValheimOne.Configuration;

public sealed class FeatureDefinition
{
    private readonly ConfigFile _config;
    private readonly List<IConfigEntry> _keys = new List<IConfigEntry>();

    internal FeatureDefinition(
        ConfigFile config,
        string name,
        string section,
        FeatureClassification classification)
    {
        _config = config;
        Name = name;
        Section = section;
        Classification = classification;
        Enabled = Bool(
            "Enabled",
            defaultValue: false,
            $"Enable the {name} feature. All ValheimOne features are disabled by default.");
    }

    public string Name { get; }

    public string Section { get; }

    public FeatureClassification Classification { get; }

    public ConfigEntryBool Enabled { get; }

    public IReadOnlyList<IConfigEntry> Keys => _keys;

    public ConfigEntryBool Bool(string key, bool defaultValue, string description)
    {
        var definition = AddDefinition(key, ConfigValueKind.Boolean, description);
        var accessor = new ConfigEntryBool(
            _config.Bind(Section, key, defaultValue, new ConfigDescription(description)),
            definition);
        _keys.Add(accessor);
        return accessor;
    }

    public ConfigEntryInt Int(string key, int defaultValue, string description)
    {
        var definition = AddDefinition(key, ConfigValueKind.Integer, description);
        var accessor = new ConfigEntryInt(
            _config.Bind(Section, key, defaultValue, new ConfigDescription(description)),
            definition);
        _keys.Add(accessor);
        return accessor;
    }

    public ConfigEntryFloat Float(string key, float defaultValue, string description)
    {
        var definition = AddDefinition(key, ConfigValueKind.Float, description);
        var accessor = new ConfigEntryFloat(
            _config.Bind(Section, key, defaultValue, new ConfigDescription(description)),
            definition);
        _keys.Add(accessor);
        return accessor;
    }

    public ConfigEntryPercent Percent(string key, float defaultValue, string description)
    {
        string percentDescription =
            description + " Stored as a modifier percent: new = base * (1 + value / 100); values <= -100 yield 0.";
        var definition = AddDefinition(key, ConfigValueKind.Percent, percentDescription);
        var accessor = new ConfigEntryPercent(
            _config.Bind(Section, key, defaultValue, new ConfigDescription(percentDescription)),
            definition);
        _keys.Add(accessor);
        return accessor;
    }

    private ConfigKeyDefinition AddDefinition(string key, ConfigValueKind kind, string description)
    {
        foreach (IConfigEntry existing in _keys)
        {
            if (string.Equals(existing.Definition.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Duplicate config key [{Section}] {key}.");
            }
        }

        return new ConfigKeyDefinition(key, kind, description);
    }
}

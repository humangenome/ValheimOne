using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace ValheimOne.Configuration;

public sealed class FeatureDefinition
{
    private readonly ValheimOneConfig _settings;
    private readonly List<IConfigEntry> _keys = new List<IConfigEntry>();

    internal FeatureDefinition(
        ValheimOneConfig settings,
        string name,
        string section,
        FeatureClassification classification,
        bool enabledByDefault,
        string enabledDescription)
    {
        _settings = settings;
        Name = name;
        Section = section;
        Classification = classification;
        Enabled = Bool(
            "Enabled",
            enabledByDefault,
            enabledDescription);
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
            _settings.File.Bind(Section, key, defaultValue, new ConfigDescription(description)),
            definition,
            _settings);
        _keys.Add(accessor);
        return accessor;
    }

    public ConfigEntryString String(string key, string defaultValue, string description)
    {
        var definition = AddDefinition(key, ConfigValueKind.Text, description);
        var accessor = new ConfigEntryString(
            _settings.File.Bind(Section, key, defaultValue, new ConfigDescription(description)),
            definition,
            _settings);
        _keys.Add(accessor);
        return accessor;
    }

    public ConfigEntryString SensitiveString(string key, string defaultValue, string description)
    {
        var definition = AddDefinition(
            key,
            ConfigValueKind.Text,
            description,
            isSensitive: true);
        var accessor = new ConfigEntryString(
            _settings.File.Bind(Section, key, defaultValue, new ConfigDescription(description)),
            definition,
            _settings);
        _keys.Add(accessor);
        return accessor;
    }

    public ConfigEntryInt Int(string key, int defaultValue, string description)
    {
        var definition = AddDefinition(key, ConfigValueKind.Integer, description);
        var accessor = new ConfigEntryInt(
            _settings.File.Bind(Section, key, defaultValue, new ConfigDescription(description)),
            definition,
            _settings);
        _keys.Add(accessor);
        return accessor;
    }

    public ConfigEntryFloat Float(string key, float defaultValue, string description)
    {
        var definition = AddDefinition(key, ConfigValueKind.Float, description);
        var accessor = new ConfigEntryFloat(
            _settings.File.Bind(Section, key, defaultValue, new ConfigDescription(description)),
            definition,
            _settings);
        _keys.Add(accessor);
        return accessor;
    }

    public ConfigEntryPercent Percent(string key, float defaultValue, string description)
    {
        string percentDescription =
            description + " Stored as a modifier percent: new = base * (1 + value / 100); values <= -100 yield 0.";
        var definition = AddDefinition(key, ConfigValueKind.Percent, percentDescription);
        var accessor = new ConfigEntryPercent(
            _settings.File.Bind(Section, key, defaultValue, new ConfigDescription(percentDescription)),
            definition,
            _settings);
        _keys.Add(accessor);
        return accessor;
    }

    public bool TryGetKey(string key, out IConfigEntry? entry)
    {
        foreach (IConfigEntry candidate in _keys)
        {
            if (string.Equals(candidate.Definition.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
                return true;
            }
        }

        entry = null;
        return false;
    }

    private ConfigKeyDefinition AddDefinition(
        string key,
        ConfigValueKind kind,
        string description,
        bool isSensitive = false)
    {
        foreach (IConfigEntry existing in _keys)
        {
            if (string.Equals(existing.Definition.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Duplicate config key [{Section}] {key}.");
            }
        }

        return new ConfigKeyDefinition(Section, key, kind, description, isSensitive);
    }
}

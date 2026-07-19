using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;

namespace ValheimOne.Configuration;

public sealed class ValheimOneConfig
{
    private readonly bool _existedAtStartup;
    private readonly ConfigFile _file;
    private Dictionary<string, object> _overlay =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    public ValheimOneConfig(string path)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _existedAtStartup = System.IO.File.Exists(path);
        Path = path;
        _file = new ConfigFile(path, saveOnInit: true);
        Features = new FeatureRegistry(this);
    }

    public string Path { get; }

    public FeatureRegistry Features { get; }

    public bool HasOverlay => _overlay.Count != 0;

    internal ConfigFile File => _file;

    public void WriteDefaultsIfNeeded()
    {
        if (!_existedAtStartup)
        {
            _file.Save();
        }
    }

    public int ApplyOverlay(string serializedConfig)
    {
        var replacement = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        int appliedValues = 0;

        using var reader = new StringReader(serializedConfig);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            ParseLine(line, out string section, out string key, out string serializedValue);
            if (!Features.TryGetFeature(section, out FeatureDefinition? feature) || feature == null)
            {
                continue;
            }

            if (feature.Classification == FeatureClassification.ClientOnly)
            {
                continue;
            }

            if (!feature.TryGetKey(key, out IConfigEntry? entry) || entry == null)
            {
                continue;
            }

            replacement[OverlayKey(section, key)] = ParseValue(entry.Definition.Kind, serializedValue);
            appliedValues++;
        }

        _overlay = replacement;
        return appliedValues;
    }

    public bool ClearOverlay()
    {
        if (_overlay.Count == 0)
        {
            return false;
        }

        _overlay = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        return true;
    }

    internal T GetEffectiveValue<T>(ConfigEntry<T> localEntry, ConfigKeyDefinition definition)
    {
        if (_overlay.TryGetValue(OverlayKey(definition.Section, definition.Name), out object value) &&
            value is T typedValue)
        {
            return typedValue;
        }

        return localEntry.Value;
    }

    private static void ParseLine(
        string line,
        out string section,
        out string key,
        out string serializedValue)
    {
        const string separator = "] / ";
        int sectionEnd = line.IndexOf(separator, StringComparison.Ordinal);
        if (line.Length < 2 || line[0] != '[' || sectionEnd <= 1)
        {
            throw new FormatException("Synced configuration contains an invalid section line.");
        }

        int keyStart = sectionEnd + separator.Length;
        int valueStart = line.IndexOf('=', keyStart);
        if (valueStart <= keyStart)
        {
            throw new FormatException("Synced configuration contains an invalid key/value line.");
        }

        section = line.Substring(1, sectionEnd - 1);
        key = line.Substring(keyStart, valueStart - keyStart);
        serializedValue = line.Substring(valueStart + 1);
    }

    private static object ParseValue(ConfigValueKind kind, string serializedValue)
    {
        switch (kind)
        {
            case ConfigValueKind.Boolean:
                if (bool.TryParse(serializedValue, out bool boolValue))
                {
                    return boolValue;
                }

                break;
            case ConfigValueKind.Integer:
                if (int.TryParse(
                    serializedValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int intValue))
                {
                    return intValue;
                }

                break;
            case ConfigValueKind.Float:
            case ConfigValueKind.Percent:
                if (float.TryParse(
                    serializedValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float floatValue))
                {
                    return floatValue;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown config value kind.");
        }

        throw new FormatException($"Synced configuration value '{serializedValue}' is not a valid {kind}.");
    }

    private static string OverlayKey(string section, string key) => section + "\0" + key;
}

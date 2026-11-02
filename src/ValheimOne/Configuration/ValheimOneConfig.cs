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

    public int ApplyOverlay(string serializedConfig) => ApplyOverlay(serializedConfig, null);

    // A line this build cannot read (not in the "[Section] / Key=value" shape, or a value that is not valid for the
    // key's kind here) is skipped and described in skipped; every other line still applies. Players on an older
    // ValheimOne build than the server are normal (471 of 533 handshakes read on 2026-10-28), and one unreadable value
    // used to throw away the whole server config on that player's PC. Sections and keys this build does not know were
    // already skipped without a word, and still are.
    public int ApplyOverlay(string serializedConfig, ICollection<string>? skipped)
    {
        var replacement = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        int appliedValues = 0;
        int lineNumber = 0;

        using var reader = new StringReader(serializedConfig);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            string section;
            string key;
            string serializedValue;
            try
            {
                ParseLine(line, out section, out key, out serializedValue);
            }
            catch (FormatException exception)
            {
                // the line itself is not logged: it could not be checked for a sensitive key
                skipped?.Add($"line {lineNumber.ToString(CultureInfo.InvariantCulture)}: {exception.Message}");
                continue;
            }

            if (!Features.TryGetFeature(section, out FeatureDefinition? feature) || feature == null)
            {
                continue;
            }

            if (feature.Classification == FeatureClassification.ClientOnly ||
                feature.Classification == FeatureClassification.ServerOnly)
            {
                continue;
            }

            if (!feature.TryGetKey(key, out IConfigEntry? entry) || entry == null)
            {
                continue;
            }

            if (entry.Definition.IsSensitive)
            {
                continue;
            }

            object value;
            try
            {
                value = ParseValue(entry.Definition.Kind, serializedValue);
            }
            catch (FormatException)
            {
                skipped?.Add($"[{section}] / {key}: '{Shorten(serializedValue)}' is not a valid {entry.Definition.Kind} on this build");
                continue;
            }

            replacement[OverlayKey(section, key)] = value;
            appliedValues++;
        }

        _overlay = replacement;
        Features.NotifyEffectiveValuesChanged();
        return appliedValues;
    }

    public bool ClearOverlay()
    {
        if (_overlay.Count == 0)
        {
            return false;
        }

        _overlay = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        Features.NotifyEffectiveValuesChanged();
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
            case ConfigValueKind.Text:
                return serializedValue;
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

    private static string Shorten(string value) => value.Length <= 64 ? value : value.Substring(0, 64) + "...";
}

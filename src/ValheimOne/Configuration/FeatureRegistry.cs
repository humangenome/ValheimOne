using System;
using System.Collections.Generic;

namespace ValheimOne.Configuration;

public sealed class FeatureRegistry
{
    private readonly ValheimOneConfig _settings;
    private readonly List<FeatureDefinition> _features = new List<FeatureDefinition>();

    internal FeatureRegistry(ValheimOneConfig settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<FeatureDefinition> Features => _features;

    public FeatureDefinition Register(
        string name,
        string section,
        FeatureClassification classification)
    {
        return Register(
            name,
            section,
            classification,
            enabledByDefault: false,
            $"Enable the {name} feature. All ValheimOne gameplay features are disabled by default.");
    }

    public FeatureDefinition Register(
        string name,
        string section,
        FeatureClassification classification,
        bool enabledByDefault,
        string enabledDescription)
    {
        foreach (FeatureDefinition existing in _features)
        {
            if (string.Equals(existing.Section, section, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Feature section [{section}] is already registered.");
            }
        }

        var feature = new FeatureDefinition(
            _settings,
            name,
            section,
            classification,
            enabledByDefault,
            enabledDescription);
        _features.Add(feature);
        return feature;
    }

    public bool TryGetFeature(string section, out FeatureDefinition? feature)
    {
        foreach (FeatureDefinition candidate in _features)
        {
            if (string.Equals(candidate.Section, section, StringComparison.OrdinalIgnoreCase))
            {
                feature = candidate;
                return true;
            }
        }

        feature = null;
        return false;
    }

    public bool TryGetClassification(
        string section,
        out FeatureClassification classification)
    {
        if (TryGetFeature(section, out FeatureDefinition? feature) && feature != null)
        {
            classification = feature.Classification;
            return true;
        }

        classification = default;
        return false;
    }
}

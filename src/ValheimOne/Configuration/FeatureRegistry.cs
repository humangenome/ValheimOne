using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace ValheimOne.Configuration;

public sealed class FeatureRegistry
{
    private readonly ConfigFile _config;
    private readonly List<FeatureDefinition> _features = new List<FeatureDefinition>();

    internal FeatureRegistry(ConfigFile config)
    {
        _config = config;
    }

    public IReadOnlyList<FeatureDefinition> Features => _features;

    public FeatureDefinition Register(
        string name,
        string section,
        FeatureClassification classification)
    {
        foreach (FeatureDefinition existing in _features)
        {
            if (string.Equals(existing.Section, section, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Feature section [{section}] is already registered.");
            }
        }

        var feature = new FeatureDefinition(_config, name, section, classification);
        _features.Add(feature);
        return feature;
    }
}

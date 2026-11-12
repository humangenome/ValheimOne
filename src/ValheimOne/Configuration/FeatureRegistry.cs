using System;
using System.Collections.Generic;

namespace ValheimOne.Configuration;

public sealed class FeatureRegistry
{
    private readonly ValheimOneConfig _settings;
    private readonly List<FeatureDefinition> _features = new List<FeatureDefinition>();
    private readonly HashSet<string> _appliedPatches =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _patchFailures =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal FeatureRegistry(ValheimOneConfig settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<FeatureDefinition> Features => _features;

    internal event Action? EffectiveValuesChanged;

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

    // Where a failing change handler is reported (set by the plugin; tests set their own).
    internal Action<string>? Log { get; set; }

    private readonly HashSet<string> _reportedHandlerFailures = new HashSet<string>(StringComparer.Ordinal);

    // Each module's handler runs on its own: one that throws is logged once (per handler) and the
    // others still get the change. A plain multicast call stopped at the first throw, so one module's
    // bug left every later module on the old values until a restart.
    internal void NotifyEffectiveValuesChanged()
    {
        Action? handlers = EffectiveValuesChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler).Invoke();
            }
            catch (Exception exception)
            {
                string name = (handler.Method.DeclaringType?.Name ?? "?") + "." + handler.Method.Name;
                bool first;
                lock (_reportedHandlerFailures)
                {
                    first = _reportedHandlerFailures.Add(name);
                }

                if (first)
                {
                    Log?.Invoke(
                        $"Settings change: {name} failed ({exception.GetType().Name}: " +
                        $"{(exception.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim()}); " +
                        "the other modules still took the change.");
                }
            }
        }
    }

    internal void RecordPatchSuccess(string section)
    {
        _patchFailures.Remove(section);
        _appliedPatches.Add(section);
    }

    internal void RecordPatchFailure(string section, string failure)
    {
        _appliedPatches.Remove(section);
        _patchFailures[section] = failure;
    }

    internal bool IsPatchApplied(string section)
    {
        return _appliedPatches.Contains(section);
    }

    internal bool TryGetPatchFailure(string section, out string? failure)
    {
        return _patchFailures.TryGetValue(section, out failure);
    }
}

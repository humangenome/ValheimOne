namespace ValheimOne.Configuration;

public sealed class ConfigKeyDefinition
{
    public ConfigKeyDefinition(
        string section,
        string name,
        ConfigValueKind kind,
        string description,
        bool isSensitive = false)
    {
        Section = section;
        Name = name;
        Kind = kind;
        Description = description;
        IsSensitive = isSensitive;
    }

    public string Section { get; }

    public string Name { get; }

    public ConfigValueKind Kind { get; }

    public string Description { get; }

    public bool IsSensitive { get; }
}

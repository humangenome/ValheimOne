namespace ValheimOne.Configuration;

public sealed class ConfigKeyDefinition
{
    public ConfigKeyDefinition(string section, string name, ConfigValueKind kind, string description)
    {
        Section = section;
        Name = name;
        Kind = kind;
        Description = description;
    }

    public string Section { get; }

    public string Name { get; }

    public ConfigValueKind Kind { get; }

    public string Description { get; }
}

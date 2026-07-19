namespace ValheimOne.Configuration;

public sealed class ConfigKeyDefinition
{
    public ConfigKeyDefinition(string name, ConfigValueKind kind, string description)
    {
        Name = name;
        Kind = kind;
        Description = description;
    }

    public string Name { get; }

    public ConfigValueKind Kind { get; }

    public string Description { get; }
}

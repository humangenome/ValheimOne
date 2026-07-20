using System;
using System.Collections.Generic;

namespace ValheimOne.LiveMap;

internal sealed class VoCommandDefinition
{
    public VoCommandDefinition(
        string name,
        string usage,
        string description,
        string category,
        string[] examples,
        bool playerArg = false)
    {
        Name = name;
        Usage = usage;
        Description = description;
        Category = category;
        Examples = examples;
        PlayerArg = playerArg;
    }

    public string Name { get; }

    public string Usage { get; }

    public string Description { get; }

    public string Category { get; }

    public string[] Examples { get; }

    public bool PlayerArg { get; }
}

internal static class VoCommandRegistry
{
    public static readonly string[] Categories =
    {
        "server",
        "players",
        "moderation",
        "world",
        "diagnostics",
    };

    private static readonly VoCommandDefinition[] Definitions =
    {
        new VoCommandDefinition(
            "help",
            "vo help [command]",
            "List commands or show detailed help for one command.",
            "server",
            new[] { "vo help", "vo help playerinfo" }),
        new VoCommandDefinition(
            "save",
            "vo save",
            "Save the world and all connected player profiles.",
            "server",
            new[] { "vo save" }),
        new VoCommandDefinition(
            "players",
            "vo players",
            "List connected players, session times, host IDs, and public positions.",
            "players",
            new[] { "vo players" }),
        new VoCommandDefinition(
            "playerinfo",
            "vo playerinfo <player>",
            "Show server-known details for one connected player.",
            "players",
            new[] { "vo playerinfo Alice", "vo playerinfo Some Viking" },
            playerArg: true),
        new VoCommandDefinition(
            "playtime",
            "vo playtime",
            "Show tracked session time for every player and server uptime.",
            "players",
            new[] { "vo playtime" }),
        new VoCommandDefinition(
            "kick",
            "vo kick <player>",
            "Disconnect a player by character name or host ID.",
            "moderation",
            new[] { "vo kick Alice", "vo kick Steam_123456789" },
            playerArg: true),
        new VoCommandDefinition(
            "ban",
            "vo ban <player>",
            "Ban and disconnect a player by character name or host ID.",
            "moderation",
            new[] { "vo ban Alice", "vo ban Steam_123456789" },
            playerArg: true),
        new VoCommandDefinition(
            "unban",
            "vo unban <target>",
            "Remove a host or platform ID from the server ban list.",
            "moderation",
            new[] { "vo unban Steam_123456789" }),
        new VoCommandDefinition(
            "banlist",
            "vo banlist",
            "List banned host and platform IDs.",
            "moderation",
            new[] { "vo banlist" }),
        new VoCommandDefinition(
            "weather",
            "vo weather",
            "Show the current environment and wind conditions.",
            "world",
            new[] { "vo weather" }),
        new VoCommandDefinition(
            "bosses",
            "vo bosses",
            "Show defeated bosses and active world modifiers.",
            "world",
            new[] { "vo bosses" }),
        new VoCommandDefinition(
            "entities",
            "vo entities",
            "Show counts from the latest LiveMap entity scan and request a refresh.",
            "world",
            new[] { "vo entities" }),
        new VoCommandDefinition(
            "stats",
            "vo stats",
            "Show server uptime, population, object, memory, and frame statistics.",
            "diagnostics",
            new[] { "vo stats" }),
        new VoCommandDefinition(
            "doctor",
            "vo doctor",
            "Show plugin, module, configuration, and LiveMap health diagnostics.",
            "diagnostics",
            new[] { "vo doctor" }),
    };

    private static readonly Dictionary<string, VoCommandDefinition> ByName =
        BuildLookup(Definitions);

    private static readonly VoCommandDefinition[] VanillaDefinitions =
    {
        new VoCommandDefinition(
            "save",
            "save",
            "Force-save the world and connected player profiles, resetting the save interval.",
            "world",
            new[] { "save" }),
        new VoCommandDefinition(
            "kick",
            "kick <name|ip|userID>",
            "Disconnect a player by character name, IP address, or platform user ID.",
            "moderation",
            new[] { "kick Alice", "kick Steam_123456789" },
            playerArg: true),
        new VoCommandDefinition(
            "ban",
            "ban <name|ip|userID>",
            "Ban and disconnect a player by character name, IP address, or platform user ID.",
            "moderation",
            new[] { "ban Alice", "ban Steam_123456789" },
            playerArg: true),
        new VoCommandDefinition(
            "unban",
            "unban <ip|userID>",
            "Remove an IP address or platform user ID from the server ban list.",
            "moderation",
            new[] { "unban Steam_123456789" },
            playerArg: true),
        new VoCommandDefinition(
            "banned",
            "banned",
            "List banned IP addresses and platform user IDs.",
            "moderation",
            new[] { "banned" }),
        new VoCommandDefinition(
            "lodbias",
            "lodbias [value]",
            "Show or set the server process's level-of-detail distance bias.",
            "server",
            new[] { "lodbias", "lodbias 2" }),
        new VoCommandDefinition(
            "sleep",
            "sleep",
            "Skip the world clock to the next morning.",
            "world",
            new[] { "sleep" }),
    };

    private static readonly Dictionary<string, VoCommandDefinition> VanillaByName =
        BuildLookup(VanillaDefinitions);

    public static IReadOnlyList<VoCommandDefinition> All => Definitions;

    public static IReadOnlyList<VoCommandDefinition> Vanilla => VanillaDefinitions;

    public static bool TryGet(string name, out VoCommandDefinition? definition)
    {
        return ByName.TryGetValue(name, out definition);
    }

    public static bool TryGetVanilla(string name, out VoCommandDefinition? definition)
    {
        return VanillaByName.TryGetValue(name, out definition);
    }

    public static List<string> GetSubcommandNames()
    {
        var names = new List<string>(Definitions.Length);
        for (int index = 0; index < Definitions.Length; index++)
        {
            names.Add(Definitions[index].Name);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static Dictionary<string, VoCommandDefinition> BuildLookup(
        VoCommandDefinition[] definitions)
    {
        var lookup = new Dictionary<string, VoCommandDefinition>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < definitions.Length; index++)
        {
            lookup.Add(definitions[index].Name, definitions[index]);
        }

        return lookup;
    }
}

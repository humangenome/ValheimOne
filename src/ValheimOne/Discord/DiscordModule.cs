using System;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Configuration;
using ValheimOne.Infrastructure;
using ValheimOne.LiveMap;
using ValheimOne.Modules;

namespace ValheimOne.Discord;

public sealed class DiscordModule : IFeatureModule
{
    private const int JoinColor = 0x4CAF50;
    private const int LeaveColor = 0x8D6E63;
    private const int DeathColor = 0xB71C1C;
    private const int RaidStartColor = 0xF4511E;
    private const int RaidEndColor = 0x90A4AE;
    private const int WorldSaveColor = 0x42A5F5;
    private const int DayChangeColor = 0xFFB300;

    private static DiscordModule? _active;

    private readonly FeatureRegistry _registry;
    private readonly FeatureDefinition _feature;
    private readonly DiscordConfig _config;
    private readonly DiscordWebhookWorker _worker;
    private readonly ModLogger _log;
    private DiscordBehaviour? _behaviour;
    private string _worldName = "Valheim";
    private bool _lastPublishedDeliveryEnabled;
    private bool _worldSaveSubscribed;
    private bool _shutdown;

    public DiscordModule(FeatureRegistry registry, ModLogger log)
    {
        _registry = registry;
        _log = log;
        _feature = registry.Register(Name, Section, Classification);
        ConfigEntryString webhookUrl = _feature.SensitiveString(
            "WebhookUrl",
            string.Empty,
            "Discord webhook URL. Empty disables delivery. The value is never logged or synced to clients.");
        ConfigEntryString serverDisplayName = _feature.String(
            "ServerDisplayName",
            string.Empty,
            "Webhook display name. Empty uses the current world name.");
        ConfigEntryBool notifyJoin = _feature.Bool(
            "NotifyJoin",
            defaultValue: true,
            "Notify when a player joins the server.");
        ConfigEntryBool notifyLeave = _feature.Bool(
            "NotifyLeave",
            defaultValue: true,
            "Notify when a player leaves the server.");
        ConfigEntryBool notifyDeath = _feature.Bool(
            "NotifyDeath",
            defaultValue: true,
            "Notify when a player's character ID transitions to the death/respawn sentinel.");
        ConfigEntryBool notifyRaid = _feature.Bool(
            "NotifyRaid",
            defaultValue: true,
            "Notify when a random raid starts or ends.");
        ConfigEntryBool notifyWorldSave = _feature.Bool(
            "NotifyWorldSave",
            defaultValue: false,
            "Notify when the world is saved. Disabled by default because saves can be noisy.");
        ConfigEntryBool notifyDayChange = _feature.Bool(
            "NotifyDayChange",
            defaultValue: false,
            "Notify when a new in-game day begins.");
        _config = new DiscordConfig(
            webhookUrl,
            serverDisplayName,
            notifyJoin,
            notifyLeave,
            notifyDeath,
            notifyRaid,
            notifyWorldSave,
            notifyDayChange);
        _worker = new DiscordWebhookWorker(log);
        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
        PublishDeliverySettings();
    }

    public string Name => "Discord webhook notifications";

    public string Section => "Discord";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.ServerOnly;

    internal bool DeliveryEnabled =>
        IsEnabled && !string.IsNullOrWhiteSpace(_config.WebhookUrl);

    internal bool NotifyJoin => _config.NotifyJoin;

    internal bool NotifyLeave => _config.NotifyLeave;

    internal bool NotifyRaid => _config.NotifyRaid;

    internal bool NotifyDayChange => _config.NotifyDayChange;

    public void ApplyPatches(Harmony harmony)
    {
        // This is the existing shared SaveWorld postfix; the Live Map consumes its timestamp and
        // Discord subscribes to the same callback path instead of installing another save patch.
        WorldSavePatch.ApplyPatches(harmony, _log);

        var rpcCharacterId = AccessTools.Method(
            typeof(ZNet),
            "RPC_CharacterID",
            new[] { typeof(ZRpc), typeof(ZDOID) }) ??
            throw new MissingMethodException(nameof(ZNet), "RPC_CharacterID");

        _active = this;
        harmony.Patch(
            rpcCharacterId,
            postfix: new HarmonyMethod(
                typeof(DiscordModule),
                nameof(RpcCharacterIdPostfix)));

        WorldSavePatch.WorldSaved += OnWorldSaved;
        _worldSaveSubscribed = true;

        var host = new GameObject("ValheimOne.Discord")
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        UnityEngine.Object.DontDestroyOnLoad(host);
        _behaviour = DiscordBehaviour.Initialize(host, this, _log);
        _worker.Start();
        PublishDeliverySettings();
    }

    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _registry.EffectiveValuesChanged -= OnEffectiveValuesChanged;
        if (_worldSaveSubscribed)
        {
            WorldSavePatch.WorldSaved -= OnWorldSaved;
            _worldSaveSubscribed = false;
        }

        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }

        DiscordBehaviour? behaviour = _behaviour;
        behaviour?.StopPermanently();
        _behaviour = null;
        _worker.StopAndFlush(2000);
        if (behaviour != null)
        {
            UnityEngine.Object.Destroy(behaviour.gameObject);
        }
    }

    internal void UpdateWorldName(string? worldName)
    {
        string candidate = worldName ?? string.Empty;
        string normalized = string.IsNullOrWhiteSpace(candidate)
            ? "Valheim"
            : candidate.Trim();
        if (string.Equals(_worldName, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _worldName = normalized;
        if (string.IsNullOrWhiteSpace(_config.ServerDisplayName))
        {
            PublishDeliverySettings();
        }
    }

    internal void NotifyPlayerJoined(string? playerName)
    {
        Enqueue(
            "Player joined",
            $"⚔️ {SafePlayerName(playerName)} has arrived",
            JoinColor);
    }

    internal void NotifyPlayerLeft(string? playerName)
    {
        Enqueue(
            "Player left",
            $"👋 {SafePlayerName(playerName)} has departed",
            LeaveColor);
    }

    internal void NotifyDeath(string? playerName, Vector3 lastPosition)
    {
        if (!_config.NotifyDeath)
        {
            return;
        }

        string name = SafePlayerName(playerName);
        string biome = TryGetBiomeName(lastPosition);
        string description = string.IsNullOrEmpty(biome)
            ? $"💀 {name} fell"
            : $"💀 {name} fell in the {biome}";
        Enqueue("Player death", description, DeathColor);
    }

    internal void NotifyRaidStarted(string readableName)
    {
        Enqueue(
            "Raid begun",
            $"🔥 A raid has begun: {readableName}",
            RaidStartColor);
    }

    internal void NotifyRaidEnded(string readableName)
    {
        Enqueue(
            "Raid ended",
            $"🕊️ The raid has ended: {readableName}",
            RaidEndColor);
    }

    internal void NotifyNewDay(int day)
    {
        Enqueue(
            "A new day",
            $"🌅 Day {day} has dawned",
            DayChangeColor);
    }

    internal static string ReadableIdentifier(string? value, string fallback)
    {
        string candidate = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return fallback;
        }

        string trimmed = candidate.Trim().Replace('_', ' ').Replace('-', ' ');
        var result = new StringBuilder(trimmed.Length + 8);
        for (int index = 0; index < trimmed.Length; index++)
        {
            char character = trimmed[index];
            if (index != 0 && char.IsUpper(character) &&
                !char.IsWhiteSpace(trimmed[index - 1]) &&
                !char.IsUpper(trimmed[index - 1]))
            {
                result.Append(' ');
            }

            result.Append(character);
        }

        return result.ToString();
    }

    private static void RpcCharacterIdPostfix(
        ZNet __instance,
        ZRpc rpc,
        ZDOID characterID)
    {
        DiscordModule? active = _active;
        if (active == null || !active.DeliveryEnabled || !active._config.NotifyDeath)
        {
            return;
        }

        try
        {
            active._behaviour?.HandleCharacterIdChanged(__instance, rpc, characterID);
        }
        catch (Exception exception)
        {
            try
            {
                active._log.Warning(
                    $"[Discord] death transition detection failed ({exception.GetType().Name}).");
            }
            catch
            {
                // Never allow notification diagnostics to escape into the vanilla RPC path.
            }
        }
    }

    private void OnWorldSaved()
    {
        if (!_config.NotifyWorldSave)
        {
            return;
        }

        Enqueue("World save", "💾 World saved", WorldSaveColor);
    }

    private void OnEffectiveValuesChanged()
    {
        bool wasEnabled = _lastPublishedDeliveryEnabled;
        PublishDeliverySettings();
        if (wasEnabled != DeliveryEnabled)
        {
            _behaviour?.ResetObservations();
        }
    }

    private void PublishDeliverySettings()
    {
        string webhookUrl = (_config.WebhookUrl ?? string.Empty).Trim();
        bool enabled = IsEnabled && webhookUrl.Length != 0 && !_shutdown;
        string configuredName = _config.ServerDisplayName ?? string.Empty;
        string username = string.IsNullOrWhiteSpace(configuredName)
            ? _worldName
            : configuredName.Trim();
        _lastPublishedDeliveryEnabled = enabled;
        _worker.UpdateSettings(new DiscordDeliverySettings(enabled, webhookUrl, username));
    }

    private void Enqueue(string title, string description, int color)
    {
        if (!DeliveryEnabled || _shutdown)
        {
            return;
        }

        _worker.Enqueue(new DiscordEventRecord(title, description, color));
    }

    private static string SafePlayerName(string? playerName)
    {
        string candidate = playerName ?? string.Empty;
        return string.IsNullOrWhiteSpace(candidate) ? "A Viking" : candidate.Trim();
    }

    private static string TryGetBiomeName(Vector3 position)
    {
        try
        {
            if (float.IsNaN(position.x) || float.IsNaN(position.z) ||
                float.IsInfinity(position.x) || float.IsInfinity(position.z))
            {
                return string.Empty;
            }

            WorldGenerator? generator = WorldGenerator.instance;
            if (generator == null)
            {
                return string.Empty;
            }

            Heightmap.Biome biome = generator.GetBiome(position);
            switch (biome)
            {
                case Heightmap.Biome.BlackForest:
                    return "Black Forest";
                case Heightmap.Biome.AshLands:
                    return "Ashlands";
                case Heightmap.Biome.DeepNorth:
                    return "Deep North";
                case Heightmap.Biome.None:
                    return string.Empty;
                default:
                    return ReadableIdentifier(biome.ToString(), string.Empty);
            }
        }
        catch
        {
            return string.Empty;
        }
    }
}

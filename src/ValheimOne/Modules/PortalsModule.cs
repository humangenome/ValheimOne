using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimOne.Configuration;

namespace ValheimOne.Modules;

public sealed class PortalsModule : IFeatureModule
{
    private static PortalsModule? _active;

    private readonly FeatureDefinition _feature;
    private readonly ConfigEntryBool _disablePortals;
    private readonly ConfigEntryBool _unrestrictedTeleport;
    private readonly List<PortalBaseline> _portalBaselines = new List<PortalBaseline>();

    public PortalsModule(FeatureRegistry registry)
    {
        _feature = registry.Register(Name, Section, Classification);
        _disablePortals = _feature.Bool(
            "DisablePortals",
            defaultValue: false,
            "Prevent players from teleporting through portals.");
        _unrestrictedTeleport = _feature.Bool(
            "UnrestrictedTeleport",
            defaultValue: false,
            "Allow normally restricted items, including ores and eggs, through portals.");

        registry.EffectiveValuesChanged += OnEffectiveValuesChanged;
    }

    public string Name => "Portals";

    public string Section => "Portals";

    public bool IsEnabled => _feature.Enabled.Value;

    public FeatureClassification Classification => FeatureClassification.Synced;

    public void ApplyPatches(Harmony harmony)
    {
        // DisablePortals is server-authoritative-leaning, while unrestricted inventory checks
        // execute on participating clients. The shared section is therefore classified Synced.
        _active = this;

        var teleport = AccessTools.Method(
            typeof(TeleportWorld),
            nameof(TeleportWorld.Teleport),
            new[] { typeof(Player) })
            ?? throw new MissingMethodException(nameof(TeleportWorld), nameof(TeleportWorld.Teleport));
        harmony.Patch(
            teleport,
            prefix: new HarmonyMethod(typeof(PortalsModule), nameof(TeleportPrefix)));

        var getHoverText = AccessTools.Method(
            typeof(TeleportWorld),
            nameof(TeleportWorld.GetHoverText),
            Type.EmptyTypes)
            ?? throw new MissingMethodException(
                nameof(TeleportWorld),
                nameof(TeleportWorld.GetHoverText));
        harmony.Patch(
            getHoverText,
            postfix: new HarmonyMethod(typeof(PortalsModule), nameof(GetHoverTextPostfix)));

        var isTeleportable = AccessTools.Method(
            typeof(Inventory),
            nameof(Inventory.IsTeleportable),
            Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(Inventory), nameof(Inventory.IsTeleportable));
        harmony.Patch(
            isTeleportable,
            postfix: new HarmonyMethod(typeof(PortalsModule), nameof(IsTeleportablePostfix)));

        var awake = AccessTools.Method(typeof(TeleportWorld), "Awake", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(TeleportWorld), "Awake");
        harmony.Patch(
            awake,
            postfix: new HarmonyMethod(typeof(PortalsModule), nameof(TeleportWorldAwakePostfix)));
    }

    private static bool TeleportPrefix(Player player)
    {
        PortalsModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return true;
        }

        if (!active._disablePortals.Value)
        {
            return true;
        }

        player.Message(MessageHud.MessageType.Center, "Portals are disabled.");
        return false;
    }

    private static void GetHoverTextPostfix(ref string __result)
    {
        PortalsModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (!active._disablePortals.Value)
        {
            return;
        }

        __result += "\n(portals disabled)";
    }

    private static void IsTeleportablePostfix(ref bool __result)
    {
        PortalsModule? active = _active;
        if (active == null || !active.IsEnabled)
        {
            return;
        }

        if (active._unrestrictedTeleport.Value)
        {
            __result = true;
        }
    }

    private static void TeleportWorldAwakePostfix(TeleportWorld __instance)
    {
        PortalsModule? active = _active;
        if (active == null)
        {
            return;
        }

        // Capture prefab state while disabled so a server overlay can hot-enable or restore every
        // already-awake portal without retaining a stale allow-all-items value.
        PortalBaseline baseline = active.GetOrAddBaseline(__instance);
        if (!active.IsEnabled)
        {
            return;
        }

        active.ApplyToPortal(__instance, baseline);
    }

    private void OnEffectiveValuesChanged()
    {
        for (int index = _portalBaselines.Count - 1; index >= 0; index--)
        {
            PortalBaseline baseline = _portalBaselines[index];
            if (!baseline.Portal.TryGetTarget(out TeleportWorld? portal) || portal == null)
            {
                _portalBaselines.RemoveAt(index);
                continue;
            }

            if (IsEnabled)
            {
                ApplyToPortal(portal, baseline);
            }
            else
            {
                baseline.Restore(portal);
            }
        }
    }

    private PortalBaseline GetOrAddBaseline(TeleportWorld portal)
    {
        for (int index = _portalBaselines.Count - 1; index >= 0; index--)
        {
            PortalBaseline baseline = _portalBaselines[index];
            if (!baseline.Portal.TryGetTarget(out TeleportWorld? existing) || existing == null)
            {
                _portalBaselines.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, portal))
            {
                return baseline;
            }
        }

        var added = new PortalBaseline(portal);
        _portalBaselines.Add(added);
        return added;
    }

    private void ApplyToPortal(TeleportWorld portal, PortalBaseline baseline)
    {
        portal.m_allowAllItems = baseline.AllowAllItems || _unrestrictedTeleport.Value;
    }

    private sealed class PortalBaseline
    {
        public PortalBaseline(TeleportWorld portal)
        {
            Portal = new WeakReference<TeleportWorld>(portal);
            AllowAllItems = portal.m_allowAllItems;
        }

        public WeakReference<TeleportWorld> Portal { get; }

        public bool AllowAllItems { get; }

        public void Restore(TeleportWorld portal)
        {
            portal.m_allowAllItems = AllowAllItems;
        }
    }
}

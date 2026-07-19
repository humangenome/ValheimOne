using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimOne.Modules;

internal sealed class ChestScanner
{
    private static readonly Func<Container, long, bool> CheckContainerAccess =
        AccessTools.MethodDelegate<Func<Container, long, bool>>(
            AccessTools.Method(typeof(Container), "CheckAccess", new[] { typeof(long) })
            ?? throw new MissingMethodException(nameof(Container), "CheckAccess"));

    private readonly List<Container> _cachedContainers = new List<Container>();
    private readonly List<Inventory> _accessibleInventories = new List<Inventory>();
    private readonly HashSet<Inventory> _seenInventories = new HashSet<Inventory>();

    private Player? _cachedPlayer;
    private float _cachedRange;
    private bool _cachedIgnoreWardedChests;
    private float _refreshAt;

    public IReadOnlyList<Inventory> GetInventories(
        Player player,
        float range,
        bool ignoreWardedChests,
        float cacheSeconds)
    {
        float clampedRange = Math.Max(1f, Math.Min(50f, range));
        float clampedCacheSeconds = Math.Max(1f, cacheSeconds);
        float now = Time.realtimeSinceStartup;

        if (!ReferenceEquals(_cachedPlayer, player) ||
            _cachedRange != clampedRange ||
            _cachedIgnoreWardedChests != ignoreWardedChests ||
            now >= _refreshAt)
        {
            Refresh(
                player,
                clampedRange,
                ignoreWardedChests,
                now + clampedCacheSeconds);
        }

        RebuildAccessibleInventories(player, ignoreWardedChests);
        return _accessibleInventories;
    }

    private void Refresh(
        Player player,
        float range,
        bool ignoreWardedChests,
        float refreshAt)
    {
        _cachedContainers.Clear();

        float rangeSquared = range * range;
        Vector3 playerPosition = player.transform.position;
        foreach (Container container in UnityEngine.Object.FindObjectsByType<Container>(
                     FindObjectsSortMode.None))
        {
            if (container == null)
            {
                continue;
            }

            Vector3 offset = container.transform.position - playerPosition;
            if (offset.sqrMagnitude <= rangeSquared)
            {
                _cachedContainers.Add(container);
            }
        }

        _cachedPlayer = player;
        _cachedRange = range;
        _cachedIgnoreWardedChests = ignoreWardedChests;
        _refreshAt = refreshAt;
    }

    private void RebuildAccessibleInventories(Player player, bool ignoreWardedChests)
    {
        _accessibleInventories.Clear();
        _seenInventories.Clear();

        long playerId = player.GetPlayerID();
        foreach (Container container in _cachedContainers)
        {
            ZNetView? networkView = container == null
                ? null
                : GetNetworkView(container);
            if (container == null ||
                networkView == null ||
                !networkView.IsValid() ||
                !container.IsOwner())
            {
                continue;
            }

            // Container ownership is required so its normal inventory-changed callback saves
            // removals. IgnoreWardedChests only bypasses ward protection; container privacy is
            // still enforced independently.
            if (container.m_checkGuardStone &&
                !ignoreWardedChests &&
                !PrivateArea.CheckAccess(
                    container.transform.position,
                    radius: 0f,
                    flash: false))
            {
                continue;
            }

            // Container.CheckAccess is private in the runtime assembly. Resolve its open-instance
            // delegate once so access checks retain vanilla privacy semantics without emitting a
            // direct private-member call that Unity 6 Mono would reject.
            if (!CheckContainerAccess(container, playerId))
            {
                continue;
            }

            Inventory inventory = container.GetInventory();
            if (inventory != null && _seenInventories.Add(inventory))
            {
                _accessibleInventories.Add(inventory);
            }
        }
    }

    private static ZNetView? GetNetworkView(Container container)
    {
        return container.m_rootObjectOverride != null
            ? container.m_rootObjectOverride
            : container.GetComponent<ZNetView>();
    }
}

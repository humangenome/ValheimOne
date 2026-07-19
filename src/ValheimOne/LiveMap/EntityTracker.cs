using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class EntityTracker
{
    private const float RefreshIntervalSeconds = 5f;
    private const int MaximumEntities = 500;

    private static readonly Lazy<FieldInfo> RandomEventField = new(
        () => AccessTools.Field(typeof(RandEventSystem), "m_randomEvent") ??
              throw new MissingFieldException(typeof(RandEventSystem).FullName, "m_randomEvent"));

    private static readonly PrefabDefinition[] Prefabs =
    {
        new PrefabDefinition("ship", "Raft"),
        new PrefabDefinition("ship", "Karve"),
        new PrefabDefinition("ship", "VikingShip"),
        new PrefabDefinition("ship", "VikingShip_Ashlands"),
        new PrefabDefinition("cart", "Cart"),
        new PrefabDefinition("portal", "portal_wood"),
        new PrefabDefinition("portal", "portal_stone"),
        new PrefabDefinition("portal", "portal"),
    };

    private readonly LiveMapConfig _config;
    private readonly ModLogger _log;
    private readonly List<ZDO> _scanResults = new List<ZDO>();
    private readonly List<TrackedEntitySnapshot> _entities =
        new List<TrackedEntitySnapshot>(MaximumEntities);
    private volatile EntityMapSnapshot _snapshot = EntityMapSnapshot.Empty;
    private float _nextRefresh;
    private int _revision;
    private bool _eventWarningLogged;

    public EntityTracker(LiveMapConfig config, ModLogger log)
    {
        _config = config;
        _log = log;
    }

    public EntityMapSnapshot Snapshot => _snapshot;

    public void Tick(float now)
    {
        if (now < _nextRefresh)
        {
            return;
        }

        _nextRefresh = now + RefreshIntervalSeconds;
        _entities.Clear();
        if (_config.EntityLayer)
        {
            CollectEntities();
        }

        RaidEventSnapshot? activeEvent = ReadActiveEvent();
        _revision = unchecked(_revision + 1);
        _snapshot = new EntityMapSnapshot(
            _revision,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            _entities.ToArray(),
            activeEvent);
    }

    private void CollectEntities()
    {
        ZDOMan? manager = ZDOMan.instance;
        if (manager == null)
        {
            return;
        }

        for (int prefabIndex = 0;
             prefabIndex < Prefabs.Length && _entities.Count < MaximumEntities;
             prefabIndex++)
        {
            PrefabDefinition prefab = Prefabs[prefabIndex];
            _scanResults.Clear();
            int scanIndex = 0;
            try
            {
                while (!manager.GetAllZDOsWithPrefabIterative(
                           prefab.Name,
                           _scanResults,
                           ref scanIndex))
                {
                }
            }
            catch (Exception exception)
            {
                _log.Warning(
                    $"[LiveMap] entity ZDO scan for {prefab.Name} failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                continue;
            }

            for (int entityIndex = 0;
                 entityIndex < _scanResults.Count && _entities.Count < MaximumEntities;
                 entityIndex++)
            {
                ZDO? zdo = _scanResults[entityIndex];
                if (zdo == null)
                {
                    continue;
                }

                Vector3 position = zdo.GetPosition();
                _entities.Add(new TrackedEntitySnapshot(
                    prefab.Group,
                    prefab.Name,
                    position.x,
                    position.y,
                    position.z));
            }
        }

        _scanResults.Clear();
    }

    private RaidEventSnapshot? ReadActiveEvent()
    {
        RandEventSystem? eventSystem = RandEventSystem.instance;
        if (eventSystem == null)
        {
            return null;
        }

        try
        {
            var activeEvent = RandomEventField.Value.GetValue(eventSystem) as RandomEvent;
            if (activeEvent == null)
            {
                return null;
            }

            Vector3 position = activeEvent.m_pos;
            return new RaidEventSnapshot(
                activeEvent.m_name ?? string.Empty,
                position.x,
                position.z,
                activeEvent.m_eventRange,
                activeEvent.m_time,
                activeEvent.m_duration);
        }
        catch (Exception exception)
        {
            if (!_eventWarningLogged)
            {
                _eventWarningLogged = true;
                _log.Warning(
                    $"[LiveMap] active raid event read failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }

            return null;
        }
    }

    private sealed class PrefabDefinition
    {
        public PrefabDefinition(string group, string name)
        {
            Group = group;
            Name = name;
        }

        public string Group { get; }

        public string Name { get; }
    }
}

internal sealed class EntityMapSnapshot
{
    public static readonly EntityMapSnapshot Empty = new EntityMapSnapshot(
        0,
        0L,
        Array.Empty<TrackedEntitySnapshot>(),
        null);

    public EntityMapSnapshot(
        int revision,
        long unixMs,
        TrackedEntitySnapshot[] entities,
        RaidEventSnapshot? activeEvent)
    {
        Revision = revision;
        UnixMs = unixMs;
        Entities = entities;
        Event = activeEvent;
    }

    public int Revision { get; }

    public long UnixMs { get; }

    public TrackedEntitySnapshot[] Entities { get; }

    public RaidEventSnapshot? Event { get; }
}

internal sealed class TrackedEntitySnapshot
{
    public TrackedEntitySnapshot(string group, string prefab, float x, float y, float z)
    {
        Group = group;
        Prefab = prefab;
        X = x;
        Y = y;
        Z = z;
    }

    public string Group { get; }

    public string Prefab { get; }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }
}

internal sealed class RaidEventSnapshot
{
    public RaidEventSnapshot(
        string name,
        float x,
        float z,
        float radius,
        float elapsed,
        float duration)
    {
        Name = name;
        X = x;
        Z = z;
        Radius = radius;
        Elapsed = elapsed;
        Duration = duration;
    }

    public string Name { get; }

    public float X { get; }

    public float Z { get; }

    public float Radius { get; }

    public float Elapsed { get; }

    public float Duration { get; }
}

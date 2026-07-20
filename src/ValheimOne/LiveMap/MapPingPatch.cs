using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal readonly struct MapPingSnapshot
{
    public MapPingSnapshot(long sequence, float x, float z, string label, long unixMs)
    {
        Sequence = sequence;
        X = x;
        Z = z;
        Label = label;
        UnixMs = unixMs;
    }

    public long Sequence { get; }

    public float X { get; }

    public float Z { get; }

    public string Label { get; }

    public long UnixMs { get; }
}

internal static class MapPingPatch
{
    private const int PingType = 3;
    private const int RecentPingCapacity = 16;

    private static readonly int ChatMessageHash = "ChatMessage".GetStableHashCode();
    private static readonly object RecentPingsLock = new object();
    private static readonly MapPingSnapshot[] RecentPings =
        new MapPingSnapshot[RecentPingCapacity];

    private static Func<bool>? _enabledCheck;
    private static ModLogger? _log;
    private static long _nextSequence;
    private static int _nextIndex;
    private static int _count;
    private static int _failureLogged;

    public static void ApplyPatches(
        Harmony harmony,
        Func<bool> enabledCheck,
        ModLogger log)
    {
        _enabledCheck = enabledCheck;
        _log = log;
        MethodInfo handleRoutedRpc = AccessTools.Method(
            typeof(ZRoutedRpc),
            "HandleRoutedRPC",
            new[] { typeof(ZRoutedRpc.RoutedRPCData) }) ??
            throw new MissingMethodException(nameof(ZRoutedRpc), "HandleRoutedRPC");
        harmony.Patch(
            handleRoutedRpc,
            postfix: new HarmonyMethod(typeof(MapPingPatch), nameof(HandleRoutedRpcPostfix)));
    }

    public static long LatestCursor
    {
        get
        {
            lock (RecentPingsLock)
            {
                return _nextSequence;
            }
        }
    }

    public static long CopyAfter(long cursor, List<MapPingSnapshot> destination)
    {
        destination.Clear();
        lock (RecentPingsLock)
        {
            int firstIndex = (_nextIndex - _count + RecentPingCapacity) % RecentPingCapacity;
            for (int offset = 0; offset < _count; offset++)
            {
                MapPingSnapshot ping = RecentPings[(firstIndex + offset) % RecentPingCapacity];
                if (ping.Sequence > cursor)
                {
                    destination.Add(ping);
                }
            }

            return _nextSequence;
        }
    }

    private static void HandleRoutedRpcPostfix(ZRoutedRpc.RoutedRPCData data)
    {
        try
        {
            if (data.m_methodHash != ChatMessageHash)
            {
                return;
            }

            if (_enabledCheck?.Invoke() != true)
            {
                return;
            }

            var parameters = new ZPackage(data.m_parameters.GetArray());
            Vector3 position = parameters.ReadVector3();
            int type = parameters.ReadInt();
            if (type != PingType)
            {
                return;
            }

            string name = parameters.ReadString();
            string userId = parameters.ReadString();
            string text = parameters.ReadString();
            _ = userId;
            _ = text;

            long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (RecentPingsLock)
            {
                long sequence = ++_nextSequence;
                RecentPings[_nextIndex] = new MapPingSnapshot(
                    sequence,
                    position.x,
                    position.z,
                    name,
                    unixMs);
                _nextIndex = (_nextIndex + 1) % RecentPingCapacity;
                if (_count < RecentPingCapacity)
                {
                    _count++;
                }
            }
        }
        catch (Exception exception)
        {
            try
            {
                if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
                {
                    _log?.Warning(
                        $"[LiveMap] routed ping mirroring failed: " +
                        $"{exception.GetType().Name}: {exception.Message}");
                }
            }
            catch
            {
                // Never allow diagnostics to escape into the routed-RPC hot path.
            }
        }
    }
}

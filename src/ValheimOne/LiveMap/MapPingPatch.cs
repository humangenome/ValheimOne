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

internal readonly struct MapChatSnapshot
{
    public MapChatSnapshot(
        long sequence,
        float x,
        float z,
        string playerName,
        string text,
        bool shout,
        long unixMs)
    {
        Sequence = sequence;
        X = x;
        Z = z;
        PlayerName = playerName;
        Text = text;
        Shout = shout;
        UnixMs = unixMs;
    }

    public long Sequence { get; }

    public float X { get; }

    public float Z { get; }

    public string PlayerName { get; }

    public string Text { get; }

    public bool Shout { get; }

    public long UnixMs { get; }
}

internal static class MapPingPatch
{
    private const int SayType = 1;
    private const int ShoutType = 2;
    private const int PingType = 3;
    private const int RecentPingCapacity = 16;
    private const int RecentChatCapacity = 32;
    private const int MaximumChatTextLength = 256;

    private static readonly int ChatMessageHash = "ChatMessage".GetStableHashCode();
    private static readonly object RecentPingsLock = new object();
    private static readonly MapPingSnapshot[] RecentPings =
        new MapPingSnapshot[RecentPingCapacity];
    private static readonly object RecentChatsLock = new object();
    private static readonly MapChatSnapshot[] RecentChats =
        new MapChatSnapshot[RecentChatCapacity];

    private static Func<bool>? _enabledCheck;
    private static Func<bool>? _mirrorChatCheck;
    private static ModLogger? _log;
    private static long _nextSequence;
    private static int _nextIndex;
    private static int _count;
    private static long _nextChatSequence;
    private static int _nextChatIndex;
    private static int _chatCount;
    private static int _failureLogged;

    public static void ApplyPatches(
        Harmony harmony,
        Func<bool> enabledCheck,
        Func<bool> mirrorChatCheck,
        ModLogger log)
    {
        _enabledCheck = enabledCheck;
        _mirrorChatCheck = mirrorChatCheck;
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

    public static long LatestChatCursor
    {
        get
        {
            ClearChatBufferWhenDisabled();
            lock (RecentChatsLock)
            {
                return _nextChatSequence;
            }
        }
    }

    public static long CopyChatAfter(long cursor, List<MapChatSnapshot> destination)
    {
        destination.Clear();
        if (!ChatMirroringEnabled())
        {
            ClearRecentChats();
            lock (RecentChatsLock)
            {
                return _nextChatSequence;
            }
        }

        lock (RecentChatsLock)
        {
            int firstIndex =
                (_nextChatIndex - _chatCount + RecentChatCapacity) % RecentChatCapacity;
            for (int offset = 0; offset < _chatCount; offset++)
            {
                MapChatSnapshot chat = RecentChats[(firstIndex + offset) % RecentChatCapacity];
                if (chat.Sequence > cursor)
                {
                    destination.Add(chat);
                }
            }

            return _nextChatSequence;
        }
    }

    public static void RefreshChatConfiguration()
    {
        ClearChatBufferWhenDisabled();
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
            bool mirrorChat = ChatMirroringEnabled();
            if (!mirrorChat)
            {
                ClearRecentChats();
            }

            if (type != PingType && type != SayType && type != ShoutType)
            {
                return;
            }

            if (type != PingType && !mirrorChat)
            {
                // Do not even materialize player speech from the package while disabled.
                return;
            }

            string name = parameters.ReadString();
            _ = parameters.ReadString();
            string text = parameters.ReadString();

            long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (type != PingType)
            {
                text = TrimAndLimitChatText(text);
                if (text.Length == 0)
                {
                    return;
                }

                // Recheck after decoding so a hot reload that disables mirroring cannot race a write.
                if (!ChatMirroringEnabled())
                {
                    ClearRecentChats();
                    return;
                }

                lock (RecentChatsLock)
                {
                    long sequence = ++_nextChatSequence;
                    RecentChats[_nextChatIndex] = new MapChatSnapshot(
                        sequence,
                        position.x,
                        position.z,
                        (name ?? string.Empty).Trim(),
                        text,
                        type == ShoutType,
                        unixMs);
                    _nextChatIndex = (_nextChatIndex + 1) % RecentChatCapacity;
                    if (_chatCount < RecentChatCapacity)
                    {
                        _chatCount++;
                    }
                }

                return;
            }

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
                        $"[LiveMap] routed map-message mirroring failed: " +
                        $"{exception.GetType().Name}: {exception.Message}");
                }
            }
            catch
            {
                // Never allow diagnostics to escape into the routed-RPC hot path.
            }
        }
    }

    private static bool ChatMirroringEnabled()
    {
        return _enabledCheck?.Invoke() == true && _mirrorChatCheck?.Invoke() == true;
    }

    private static void ClearChatBufferWhenDisabled()
    {
        if (!ChatMirroringEnabled())
        {
            ClearRecentChats();
        }
    }

    private static void ClearRecentChats()
    {
        lock (RecentChatsLock)
        {
            if (_chatCount == 0)
            {
                return;
            }

            Array.Clear(RecentChats, 0, RecentChats.Length);
            _nextChatIndex = 0;
            _chatCount = 0;
        }
    }

    private static string TrimAndLimitChatText(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length <= MaximumChatTextLength)
        {
            return text;
        }

        int length = MaximumChatTextLength;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text.Substring(0, length).TrimEnd();
    }
}

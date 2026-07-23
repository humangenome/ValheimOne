using System.Collections.Generic;
using Splatform;
using UnityEngine;

namespace ValheimOne.LiveMap;

internal static class ServerUserInfo
{
    private const int ShoutType = 2;

    // UserInfo serializes this as "Server_0"; both parser components must be non-empty.
    private static readonly PlatformUserID UserId = new PlatformUserID("Server", "0");

    public static UserInfo Create(string displayName = "Server")
    {
        return new UserInfo
        {
            Name = displayName,
            UserId = UserId,
        };
    }

    public static void BroadcastShoutToPlayers(
        ZNet network,
        ZRoutedRpc routedRpc,
        string text,
        string displayName = "Server")
    {
        UserInfo userInfo = Create(displayName);
        List<ZNetPeer> peers = network.GetPeers();
        for (int index = 0; index < peers.Count; index++)
        {
            ZNetPeer? peer = peers[index];
            if (peer == null || peer.m_uid == 0L)
            {
                continue;
            }

            routedRpc.InvokeRoutedRPC(
                peer.m_uid,
                "ChatMessage",
                Vector3.zero,
                ShoutType,
                userInfo,
                text);
        }
    }
}

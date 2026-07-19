using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimOne.Query;

internal static class QueryGameAccess
{
    private static readonly Lazy<FieldInfo> WorldField = new(
        () => AccessTools.Field(typeof(ZNet), "m_world") ??
              throw new MissingFieldException(typeof(ZNet).FullName, "m_world"));

    private static readonly Lazy<FieldInfo> PeersField = new(
        () => AccessTools.Field(typeof(ZNet), "m_peers") ??
              throw new MissingFieldException(typeof(ZNet).FullName, "m_peers"));

    private static readonly Lazy<FieldInfo> ServerNameField = new(
        () => AccessTools.Field(typeof(ZNet), "m_ServerName") ??
              throw new MissingFieldException(typeof(ZNet).FullName, "m_ServerName"));

    public static World? GetWorld()
    {
        return WorldField.Value.GetValue(null) as World;
    }

    public static List<ZNetPeer> GetPeers(ZNet network)
    {
        return PeersField.Value.GetValue(network) as List<ZNetPeer> ??
               throw new InvalidOperationException("ZNet.m_peers did not contain a peer list.");
    }

    public static string GetServerName()
    {
        return ServerNameField.Value.GetValue(null) as string ?? string.Empty;
    }
}

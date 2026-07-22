using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimOne.LiveMap;

internal static class GameAccess
{
    private static readonly Lazy<FieldInfo> WorldField = new(
        () => AccessTools.Field(typeof(ZNet), "m_world") ??
              throw new MissingFieldException(typeof(ZNet).FullName, "m_world"));

    private static readonly Lazy<FieldInfo> PeersField = new(
        () => AccessTools.Field(typeof(ZNet), "m_peers") ??
              throw new MissingFieldException(typeof(ZNet).FullName, "m_peers"));

    private static readonly Lazy<FieldInfo?> ObjectsBySectorField = new(
        () => AccessTools.Field(typeof(ZDOMan), "m_objectsBySector"));

    public static World? GetWorld()
    {
        return WorldField.Value.GetValue(null) as World;
    }

    public static List<ZNetPeer> GetPeers(ZNet network)
    {
        return PeersField.Value.GetValue(network) as List<ZNetPeer> ??
               throw new InvalidOperationException("ZNet.m_peers did not contain a peer list.");
    }

    public static bool TryGetZdoSectorCount(ZDOMan manager, out int sectorCount)
    {
        sectorCount = 0;
        try
        {
            Array? sectors = ObjectsBySectorField.Value?.GetValue(manager) as Array;
            if (sectors == null)
            {
                return false;
            }

            sectorCount = sectors.Length;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

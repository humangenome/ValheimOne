using System;
using System.Collections;
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

    public static bool TryCopyZdoBatch(
        ZDOMan manager,
        List<ZDO> output,
        ref int sectorIndex,
        ref int objectIndex,
        int maximumObjects,
        int maximumSectors,
        out int sectorCount,
        out bool complete)
    {
        sectorCount = 0;
        complete = false;
        if (maximumObjects <= 0 || maximumSectors <= 0)
        {
            return false;
        }

        try
        {
            Array? sectors = ObjectsBySectorField.Value?.GetValue(manager) as Array;
            if (sectors == null)
            {
                return false;
            }

            sectorCount = sectors.Length;
            int inspected = 0;
            int visitedSectors = 0;
            while (sectorIndex < sectors.Length &&
                   inspected < maximumObjects &&
                   visitedSectors < maximumSectors)
            {
                object? sector = sectors.GetValue(sectorIndex);
                if (sector == null)
                {
                    sectorIndex++;
                    objectIndex = 0;
                    visitedSectors++;
                    continue;
                }

                if (!(sector is IList objects))
                {
                    return false;
                }

                while (objectIndex < objects.Count && inspected < maximumObjects)
                {
                    object? value = objects[objectIndex++];
                    inspected++;
                    if (value is ZDO zdo)
                    {
                        output.Add(zdo);
                    }
                }

                if (objectIndex >= objects.Count)
                {
                    sectorIndex++;
                    objectIndex = 0;
                    visitedSectors++;
                }
            }

            complete = sectorIndex >= sectors.Length;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

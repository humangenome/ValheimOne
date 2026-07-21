using System;
using System.Collections.Generic;

namespace ValheimOne.LiveMap;

internal static class BiomeRegionCatalog
{
    private const int GridSize = 160;
    private const int MinimumRegionCells = 12;
    private const int MaximumRegions = 60;

    public static BiomeRegionSnapshot[] Build(
        WorldGenerator generator,
        Func<bool> isStopping)
    {
        int cellCount = GridSize * GridSize;
        var biomes = new Heightmap.Biome[cellCount];
        float cellSize = WorldMapRenderer.WorldRadius * 2f / GridSize;
        for (int z = 0; z < GridSize; z++)
        {
            ThrowIfStopping(isStopping);
            float worldZ = -WorldMapRenderer.WorldRadius + ((z + 0.5f) * cellSize);
            for (int x = 0; x < GridSize; x++)
            {
                float worldX = -WorldMapRenderer.WorldRadius + ((x + 0.5f) * cellSize);
                biomes[(z * GridSize) + x] =
                    MapShading.EdgeOceanFactor(worldX, worldZ) >= 1f
                        ? Heightmap.Biome.Ocean
                        : generator.GetBiome(worldX, worldZ);
            }
        }

        var visited = new bool[cellCount];
        var membership = new int[cellCount];
        var queue = new int[cellCount];
        var componentCells = new List<int>();
        var regions = new List<BiomeRegionSnapshot>();
        int componentId = 0;
        for (int start = 0; start < cellCount; start++)
        {
            if (visited[start])
            {
                continue;
            }

            ThrowIfStopping(isStopping);
            componentId++;
            componentCells.Clear();
            Heightmap.Biome biome = biomes[start];
            int head = 0;
            int tail = 0;
            long sumX = 0L;
            long sumZ = 0L;
            visited[start] = true;
            queue[tail++] = start;
            while (head < tail)
            {
                int cell = queue[head++];
                int x = cell % GridSize;
                int z = cell / GridSize;
                membership[cell] = componentId;
                componentCells.Add(cell);
                sumX += x;
                sumZ += z;

                TryEnqueue(x - 1, z, biome, biomes, visited, queue, ref tail);
                TryEnqueue(x + 1, z, biome, biomes, visited, queue, ref tail);
                TryEnqueue(x, z - 1, biome, biomes, visited, queue, ref tail);
                TryEnqueue(x, z + 1, biome, biomes, visited, queue, ref tail);
            }

            if (componentCells.Count < MinimumRegionCells ||
                !BiomePalette.TryDescribe(biome, out string biomeKey, out string displayName))
            {
                continue;
            }

            double centroidX = sumX / (double)componentCells.Count;
            double centroidZ = sumZ / (double)componentCells.Count;
            int centroidCellX = Math.Max(
                0,
                Math.Min(
                    GridSize - 1,
                    (int)Math.Round(centroidX, MidpointRounding.AwayFromZero)));
            int centroidCellZ = Math.Max(
                0,
                Math.Min(
                    GridSize - 1,
                    (int)Math.Round(centroidZ, MidpointRounding.AwayFromZero)));
            int centroidCell = (centroidCellZ * GridSize) + centroidCellX;
            double labelX = centroidX;
            double labelZ = centroidZ;
            if (membership[centroidCell] != componentId)
            {
                int nearestCell = FindNearestCell(componentCells, centroidX, centroidZ);
                labelX = nearestCell % GridSize;
                labelZ = nearestCell / GridSize;
            }

            float worldLabelX = -WorldMapRenderer.WorldRadius +
                                ((float)labelX + 0.5f) * cellSize;
            float worldLabelZ = -WorldMapRenderer.WorldRadius +
                                ((float)labelZ + 0.5f) * cellSize;
            long area = (long)Math.Round(
                componentCells.Count * cellSize * cellSize,
                MidpointRounding.AwayFromZero);
            regions.Add(new BiomeRegionSnapshot(
                displayName,
                biomeKey,
                worldLabelX,
                worldLabelZ,
                area));
        }

        regions.Sort(CompareRegions);
        if (regions.Count > MaximumRegions)
        {
            regions.RemoveRange(MaximumRegions, regions.Count - MaximumRegions);
        }

        return regions.ToArray();
    }

    private static void TryEnqueue(
        int x,
        int z,
        Heightmap.Biome biome,
        Heightmap.Biome[] biomes,
        bool[] visited,
        int[] queue,
        ref int tail)
    {
        if (x < 0 || x >= GridSize || z < 0 || z >= GridSize)
        {
            return;
        }

        int index = (z * GridSize) + x;
        if (visited[index] || biomes[index] != biome)
        {
            return;
        }

        visited[index] = true;
        queue[tail++] = index;
    }

    private static int FindNearestCell(
        List<int> cells,
        double centroidX,
        double centroidZ)
    {
        int nearestCell = cells[0];
        double nearestDistance = double.MaxValue;
        for (int index = 0; index < cells.Count; index++)
        {
            int cell = cells[index];
            double deltaX = (cell % GridSize) - centroidX;
            double deltaZ = (cell / GridSize) - centroidZ;
            double distance = (deltaX * deltaX) + (deltaZ * deltaZ);
            if (distance < nearestDistance)
            {
                nearestCell = cell;
                nearestDistance = distance;
            }
        }

        return nearestCell;
    }

    private static int CompareRegions(
        BiomeRegionSnapshot left,
        BiomeRegionSnapshot right)
    {
        int areaComparison = right.Area.CompareTo(left.Area);
        if (areaComparison != 0)
        {
            return areaComparison;
        }

        int biomeComparison = string.Compare(
            left.Biome,
            right.Biome,
            StringComparison.Ordinal);
        return biomeComparison != 0 ? biomeComparison : left.X.CompareTo(right.X);
    }

    private static void ThrowIfStopping(Func<bool> isStopping)
    {
        if (isStopping())
        {
            throw new OperationCanceledException();
        }
    }
}

internal sealed class BiomeRegionSnapshot
{
    public BiomeRegionSnapshot(string name, string biome, float x, float z, long area)
    {
        Name = name;
        Biome = biome;
        X = x;
        Z = z;
        Area = area;
    }

    public string Name { get; }

    public string Biome { get; }

    public float X { get; }

    public float Z { get; }

    public long Area { get; }
}

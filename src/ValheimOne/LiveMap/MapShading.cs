using System;
using UnityEngine;

namespace ValheimOne.LiveMap;

// Shared per-pixel shading for the base overview render and on-demand detail tiles.
// The structure mirrors the game's own minimap pixel logic: biome base color plus
// per-biome forest rules (InForest for Meadows, forest factor < 0.8 for Plains,
// always-forest for Black Forest, smooth-stepped forest factor for Mistlands),
// zoom-aware forest stippling that becomes soft world-anchored tree dots at deep
// zooms, and a sub-water depth ramp with the Ashlands ocean gradient.
internal static class MapShading
{
    public const float WaterLevel = 30f;

    // World-edge treatment shared by the base render and detail tiles: beyond the
    // playable circle WorldGenerator returns garbage biomes (an Ashlands-red band
    // past the southern edge and square texture corners), so the last stretch
    // inside the edge fades to deep ocean and everything past EdgeOceanRadius is
    // pure deep ocean. EdgeOceanRadius sits inside WorldRadius so no garbage
    // sample survives the clamp, and the fade reads like the game's edge mist.
    public const float EdgeFadeStartRadius = 10150f;
    public const float EdgeOceanRadius = 10470f;

    private const float FineStippleMetersPerPixel = 1.5f;
    private const float CoarseStippleMetersPerPixel = 3f;
    private const float TreeCellMeters = 6f;
    private const float TreeJitterMeters = 2.2f;
    private const float TreeFullRadiusMeters = 1.2f;
    private const float TreeFadeRadiusMeters = 2.4f;

    private static readonly MapColor ShallowWater = new MapColor(0.290f, 0.446f, 0.600f);
    private static readonly MapColor DeepWater = new MapColor(0.088f, 0.140f, 0.240f);
    private static readonly MapColor LavaWater = new MapColor(0.350f, 0.080f, 0.040f);
    private static readonly MapColor MistlandsSpeck = new MapColor(0.300f, 0.300f, 0.340f);
    private static readonly MapColor AshlandsGlow = new MapColor(0.850f, 0.320f, 0.150f);

    // 0 = inside the fade band, 1 = fully beyond the playable edge (deep ocean only).
    public static float EdgeOceanFactor(float worldX, float worldZ)
    {
        float distanceSquared = (worldX * worldX) + (worldZ * worldZ);
        if (distanceSquared <= EdgeFadeStartRadius * EdgeFadeStartRadius)
        {
            return 0f;
        }

        return SmoothStep(EdgeFadeStartRadius, EdgeOceanRadius, (float)Math.Sqrt(distanceSquared));
    }

    public static MapColor Compose(
        Heightmap.Biome biome,
        float height,
        float lavaMask,
        float worldX,
        float worldZ,
        float metersPerPixel)
    {
        float edge = EdgeOceanFactor(worldX, worldZ);
        if (edge >= 1f)
        {
            return DeepWater;
        }

        MapColor landColor = BiomePalette.Get(biome, height);
        MapColor color = height < WaterLevel
            ? ComposeWater(landColor, height, worldX, worldZ)
            : ComposeLand(landColor, biome, lavaMask, worldX, worldZ, metersPerPixel);
        return edge > 0f ? MapColor.Lerp(color, DeepWater, edge) : color;
    }

    private static MapColor ComposeLand(
        MapColor color,
        Heightmap.Biome biome,
        float lavaMask,
        float worldX,
        float worldZ,
        float metersPerPixel)
    {
        switch (biome)
        {
            case Heightmap.Biome.Meadows:
                if (WorldGenerator.InForest(new Vector3(worldX, 0f, worldZ)))
                {
                    return ApplyForestStipple(color, 0.82f, worldX, worldZ, metersPerPixel);
                }

                return color;
            case Heightmap.Biome.Plains:
                if (WorldGenerator.GetForestFactor(new Vector3(worldX, 0f, worldZ)) < 0.8f)
                {
                    return ApplyForestStipple(color, 0.88f, worldX, worldZ, metersPerPixel);
                }

                return color;
            case Heightmap.Biome.BlackForest:
                return ApplyForestStipple(color, 0.86f, worldX, worldZ, metersPerPixel);
            case Heightmap.Biome.Swamp:
                if (WorldGenerator.InForest(new Vector3(worldX, 0f, worldZ)))
                {
                    return ApplyForestStipple(color, 0.90f, worldX, worldZ, metersPerPixel);
                }

                return color;
            case Heightmap.Biome.Mistlands:
            {
                float forestFactor = WorldGenerator.GetForestFactor(new Vector3(worldX, 0f, worldZ));
                float open = 1f - SmoothStep(1.1f, 1.3f, forestFactor);
                float speckDensity = 1f - open;
                if (speckDensity > 0f)
                {
                    return ApplyMistlandsStipple(
                        color,
                        speckDensity * 0.45f,
                        worldX,
                        worldZ,
                        metersPerPixel);
                }

                return color;
            }
            case Heightmap.Biome.AshLands:
                if (lavaMask > 0f)
                {
                    return MapColor.Lerp(color, AshlandsGlow, Clamp01(lavaMask));
                }

                return color;
            default:
                return color;
        }
    }

    private static MapColor ComposeWater(
        MapColor landColor,
        float height,
        float worldX,
        float worldZ)
    {
        float depth = SmoothCurve(Clamp01((WaterLevel - height) / 60f));
        MapColor water = MapColor.Lerp(ShallowWater, DeepWater, depth);

        // The gradient saturates far outside the playable circle; only the southern
        // lava sea inside the world edge should read red.
        float distance = (float)Math.Sqrt((worldX * worldX) + (worldZ * worldZ));
        if (distance <= WorldMapRenderer.WorldRadius)
        {
            float ashlands = Clamp01(WorldGenerator.GetAshlandsOceanGradient(worldX, worldZ));
            if (ashlands > 0f)
            {
                water = MapColor.Lerp(water, LavaWater, ashlands);
            }
        }

        // Keep the beach lip to roughly one meter of depth so coastlines stay
        // crisp at detail zooms instead of smearing biome tint across shallows.
        float shore = SmoothCurve(Clamp01((WaterLevel - height) / 1.5f));
        return MapColor.Lerp(landColor, water, 0.5f + (0.5f * shore));
    }

    internal static MapColor ApplyForestStipple(
        MapColor color,
        float multiplier,
        float worldX,
        float worldZ,
        float metersPerPixel)
    {
        MapColor darkened = color.Multiply(multiplier);
        if (metersPerPixel >= CoarseStippleMetersPerPixel)
        {
            return IsCoarseStipplePixel(worldX, worldZ, metersPerPixel) ? darkened : color;
        }

        float fineCoverage = FineStippleCoverage(worldX, worldZ, 0.75f, true);
        if (metersPerPixel <= FineStippleMetersPerPixel)
        {
            return MapColor.Lerp(color, darkened, fineCoverage);
        }

        float coarseCoverage = IsCoarseStipplePixel(worldX, worldZ, metersPerPixel) ? 1f : 0f;
        float coarseBlend = SmoothStep(
            FineStippleMetersPerPixel,
            CoarseStippleMetersPerPixel,
            metersPerPixel);
        float coverage = fineCoverage + ((coarseCoverage - fineCoverage) * coarseBlend);
        return MapColor.Lerp(color, darkened, coverage);
    }

    internal static MapColor ApplyMistlandsStipple(
        MapColor color,
        float activationProbability,
        float worldX,
        float worldZ,
        float metersPerPixel)
    {
        float amount;
        if (metersPerPixel >= CoarseStippleMetersPerPixel)
        {
            amount = StippleNoise(worldX, worldZ, metersPerPixel) < activationProbability
                ? 0.85f
                : 0f;
        }
        else
        {
            float fineAmount = FineStippleCoverage(
                worldX,
                worldZ,
                activationProbability,
                false) * 0.85f;
            if (metersPerPixel <= FineStippleMetersPerPixel)
            {
                amount = fineAmount;
            }
            else
            {
                float coarseAmount = StippleNoise(worldX, worldZ, metersPerPixel) < activationProbability
                    ? 0.85f
                    : 0f;
                float coarseBlend = SmoothStep(
                    FineStippleMetersPerPixel,
                    CoarseStippleMetersPerPixel,
                    metersPerPixel);
                amount = fineAmount + ((coarseAmount - fineAmount) * coarseBlend);
            }
        }

        return amount > 0f ? MapColor.Lerp(color, MistlandsSpeck, amount) : color;
    }

    // Coarse pixels hash cells the size of their own footprint; below 3 m/px the
    // result blends into deterministic, jittered soft dots anchored on a 6 m
    // world grid. Explicit floors keep negative coordinates aligned correctly.
    internal static bool IsCoarseStipplePixel(float worldX, float worldZ, float metersPerPixel)
    {
        return (CellHash(worldX, worldZ, metersPerPixel) & 3u) != 0u;
    }

    internal static float FineStippleCoverage(
        float worldX,
        float worldZ,
        float activationProbability,
        bool useThreeQuarterMask)
    {
        int centerCellX = (int)Math.Floor(worldX / TreeCellMeters);
        int centerCellZ = (int)Math.Floor(worldZ / TreeCellMeters);
        float nearestDistanceSquared = float.MaxValue;

        for (int cellZ = centerCellZ - 1; cellZ <= centerCellZ + 1; cellZ++)
        {
            for (int cellX = centerCellX - 1; cellX <= centerCellX + 1; cellX++)
            {
                uint hash = CellHash(cellX, cellZ);
                bool active = useThreeQuarterMask
                    ? (hash & 3u) != 0u
                    : HashNoise(hash) < activationProbability;
                if (!active)
                {
                    continue;
                }

                float treeX = ((cellX + 0.5f) * TreeCellMeters) + HashJitter(hash, 2);
                float treeZ = ((cellZ + 0.5f) * TreeCellMeters) + HashJitter(hash, 17);
                float deltaX = worldX - treeX;
                float deltaZ = worldZ - treeZ;
                float distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                }
            }
        }

        if (nearestDistanceSquared >= TreeFadeRadiusMeters * TreeFadeRadiusMeters)
        {
            return 0f;
        }

        float distance = (float)Math.Sqrt(nearestDistanceSquared);
        return 1f - SmoothStep(TreeFullRadiusMeters, TreeFadeRadiusMeters, distance);
    }

    private static float HashJitter(uint hash, int shift)
    {
        const uint jitterMask = 0x7FFFu;
        float normalized = ((hash >> shift) & jitterMask) / (float)jitterMask;
        return ((normalized * 2f) - 1f) * TreeJitterMeters;
    }

    internal static float StippleNoise(float worldX, float worldZ, float cellMeters)
    {
        return HashNoise(CellHash(worldX, worldZ, cellMeters));
    }

    internal static float HashNoise(uint hash)
    {
        return (hash & 0xFFFFu) / 65535f;
    }

    private static uint CellHash(float worldX, float worldZ, float cellMeters)
    {
        int cellX = (int)Math.Floor(worldX / cellMeters);
        int cellZ = (int)Math.Floor(worldZ / cellMeters);
        return CellHash(cellX, cellZ);
    }

    internal static uint CellHash(int cellX, int cellZ)
    {
        uint hash = (uint)unchecked((cellX * 73856093) ^ (cellZ * 19349663));
        return hash * 2654435761u;
    }

    internal static float SmoothStep(float edgeLow, float edgeHigh, float value)
    {
        float t = Clamp01((value - edgeLow) / (edgeHigh - edgeLow));
        return t * t * (3f - (2f * t));
    }

    internal static float SmoothCurve(float t)
    {
        return t * t * (3f - (2f * t));
    }

    internal static float Clamp01(float value)
    {
        return Math.Max(0f, Math.Min(1f, value));
    }
}

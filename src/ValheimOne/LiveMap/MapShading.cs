using System;
using UnityEngine;

namespace ValheimOne.LiveMap;

// Shared per-pixel shading for the base overview render and on-demand detail tiles.
// The structure mirrors the game's own minimap pixel logic: biome base color plus
// per-biome forest rules (InForest for Meadows, forest factor < 0.8 for Plains,
// always-forest for Black Forest, smooth-stepped forest factor for Mistlands) and
// a sub-water depth ramp with the Ashlands ocean gradient.
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

    private const float StippleCellMeters = 6f;

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
        float worldZ)
    {
        float edge = EdgeOceanFactor(worldX, worldZ);
        if (edge >= 1f)
        {
            return DeepWater;
        }

        MapColor landColor = BiomePalette.Get(biome, height);
        MapColor color = height < WaterLevel
            ? ComposeWater(landColor, height, worldX, worldZ)
            : ComposeLand(landColor, biome, lavaMask, worldX, worldZ);
        return edge > 0f ? MapColor.Lerp(color, DeepWater, edge) : color;
    }

    private static MapColor ComposeLand(
        MapColor color,
        Heightmap.Biome biome,
        float lavaMask,
        float worldX,
        float worldZ)
    {
        switch (biome)
        {
            case Heightmap.Biome.Meadows:
                if (WorldGenerator.InForest(new Vector3(worldX, 0f, worldZ)) &&
                    IsStippleCell(worldX, worldZ))
                {
                    return color.Multiply(0.82f);
                }

                return color;
            case Heightmap.Biome.Plains:
                if (WorldGenerator.GetForestFactor(new Vector3(worldX, 0f, worldZ)) < 0.8f &&
                    IsStippleCell(worldX, worldZ))
                {
                    return color.Multiply(0.88f);
                }

                return color;
            case Heightmap.Biome.BlackForest:
                return IsStippleCell(worldX, worldZ) ? color.Multiply(0.86f) : color;
            case Heightmap.Biome.Swamp:
                if (WorldGenerator.InForest(new Vector3(worldX, 0f, worldZ)) &&
                    IsStippleCell(worldX, worldZ))
                {
                    return color.Multiply(0.90f);
                }

                return color;
            case Heightmap.Biome.Mistlands:
            {
                float forestFactor = WorldGenerator.GetForestFactor(new Vector3(worldX, 0f, worldZ));
                float open = 1f - SmoothStep(1.1f, 1.3f, forestFactor);
                float speckDensity = 1f - open;
                if (speckDensity > 0f && StippleNoise(worldX, worldZ) < speckDensity * 0.45f)
                {
                    return MapColor.Lerp(color, MistlandsSpeck, 0.85f);
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

    // Deterministic world-anchored stipple so patterns stay identical across zoom levels.
    private static bool IsStippleCell(float worldX, float worldZ)
    {
        return (CellHash(worldX, worldZ) & 3u) != 0u;
    }

    private static float StippleNoise(float worldX, float worldZ)
    {
        return (CellHash(worldX, worldZ) & 0xFFFFu) / 65535f;
    }

    private static uint CellHash(float worldX, float worldZ)
    {
        int cellX = (int)Math.Floor(worldX / StippleCellMeters);
        int cellZ = (int)Math.Floor(worldZ / StippleCellMeters);
        uint hash = (uint)unchecked((cellX * 73856093) ^ (cellZ * 19349663));
        return hash * 2654435761u;
    }

    private static float SmoothStep(float edgeLow, float edgeHigh, float value)
    {
        float t = Clamp01((value - edgeLow) / (edgeHigh - edgeLow));
        return t * t * (3f - (2f * t));
    }

    private static float SmoothCurve(float t)
    {
        return t * t * (3f - (2f * t));
    }

    private static float Clamp01(float value)
    {
        return Math.Max(0f, Math.Min(1f, value));
    }
}

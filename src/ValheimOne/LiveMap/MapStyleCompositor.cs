using System;
using UnityEngine;

namespace ValheimOne.LiveMap;

internal static class MapStyleCompositor
{
    public const int StyleVersion = 1;

    private const float FineStippleMetersPerPixel = 1.5f;
    private const float CoarseStippleMetersPerPixel = 3f;

    private static readonly MapColor TopoPaper = HexColor(0xe9, 0xe4, 0xd6);
    private static readonly MapColor TopoShallowWater = HexColor(0xcf, 0xe2, 0xea);
    private static readonly MapColor TopoDeepWater = HexColor(0x9b, 0xbf, 0xd4);
    private static readonly MapColor TopoWarmWater = HexColor(0xc9, 0x8a, 0x6a);
    private static readonly MapColor TopoContourInk = HexColor(0x4a, 0x3b, 0x28);
    private static readonly MapColor TopoIsobathInk = HexColor(0x71, 0x8d, 0x9c);
    private static readonly MapColor TopoLava = HexColor(0xb0, 0x55, 0x2e);

    private static readonly MapColor ChartParchment = HexColor(0xe8, 0xdc, 0xc4);
    private static readonly MapColor ChartAgedInk = HexColor(0x2f, 0x24, 0x18);
    private static readonly MapColor ChartSepiaShadow = HexColor(0x8a, 0x74, 0x54);
    private static readonly MapColor ChartGoldLeaf = HexColor(0xd9, 0xb1, 0x68);
    private static readonly MapColor ChartSepiaDark = HexColor(0x6b, 0x57, 0x3c);
    private static readonly MapColor ChartSepiaLight = HexColor(0xef, 0xe6, 0xcf);
    private static readonly MapColor ChartForestInk = HexColor(0x5c, 0x4a, 0x30);
    private static readonly MapColor ChartContourInk = HexColor(0x6b, 0x56, 0x38);
    private static readonly MapColor ChartShallowWater = HexColor(0xdd, 0xd3, 0xb8);
    private static readonly MapColor ChartDeepWater = HexColor(0xc2, 0xbf, 0xa2);
    private static readonly MapColor ChartIsobathInk = HexColor(0x7a, 0x6a, 0x4e);
    private static readonly MapColor ChartBurntWater = HexColor(0xb8, 0x79, 0x55);
    private static readonly MapColor ChartWorldEdge = HexColor(0xdc, 0xd0, 0xb2);

    // originWorldX/originWorldZ are the upper-left bounds of the output patch.
    // Sample arrays may include an even border around that patch for seamless
    // neighbor reads; the output dimensions are the grid dimensions minus it.
    public static void ComposeInto(
        MapStyle style,
        float[] heights,
        Heightmap.Biome[] biomes,
        float[] lavaMasks,
        int gridWidth,
        int gridHeight,
        int border,
        float originWorldX,
        float originWorldZ,
        float metersPerPixel,
        int seed,
        byte[] output,
        Func<bool>? shouldStop = null)
    {
        ValidateArguments(
            style,
            heights,
            biomes,
            lavaMasks,
            gridWidth,
            gridHeight,
            border,
            metersPerPixel,
            output);

        int outputWidth = gridWidth - (border * 2);
        int outputHeight = gridHeight - (border * 2);
        float contourInterval = ContourInterval(style, metersPerPixel);
        float isobathInterval = style == MapStyle.Topo
            ? contourInterval * 2f
            : contourInterval;

        for (int py = 0; py < outputHeight; py++)
        {
            if (shouldStop?.Invoke() == true)
            {
                throw new OperationCanceledException();
            }

            int sy = py + border;
            float worldZ = originWorldZ - ((py + 0.5f) * metersPerPixel);
            for (int px = 0; px < outputWidth; px++)
            {
                int sx = px + border;
                int sampleIndex = (sy * gridWidth) + sx;
                float worldX = originWorldX + ((px + 0.5f) * metersPerPixel);
                float height = heights[sampleIndex];
                MapColor color = style == MapStyle.Topo
                    ? ComposeTopographicBase(
                        biomes[sampleIndex],
                        height,
                        lavaMasks[sampleIndex],
                        heights,
                        gridWidth,
                        gridHeight,
                        sx,
                        sy,
                        worldX,
                        worldZ,
                        metersPerPixel)
                    : ComposeChartBase(
                        biomes[sampleIndex],
                        height,
                        lavaMasks[sampleIndex],
                        heights,
                        gridWidth,
                        gridHeight,
                        sx,
                        sy,
                        worldX,
                        worldZ,
                        metersPerPixel,
                        seed);

                float edge = MapShading.EdgeOceanFactor(worldX, worldZ);
                if (edge < 1f)
                {
                    if (height >= MapShading.WaterLevel)
                    {
                        ContourStroke contour = FindLandContour(
                            heights,
                            gridWidth,
                            gridHeight,
                            sx,
                            sy,
                            contourInterval);
                        if (contour.Strength > 0f)
                        {
                            color = ApplyLandContour(style, color, contour);
                        }
                    }
                    else
                    {
                        float strength = FindIsobath(
                            heights,
                            gridWidth,
                            gridHeight,
                            sx,
                            sy,
                            isobathInterval);
                        if (strength > 0f)
                        {
                            MapColor ink = style == MapStyle.Topo
                                ? TopoIsobathInk
                                : ChartIsobathInk;
                            color = MapColor.Lerp(color, ink, 0.30f * strength);
                        }
                    }
                }

                color.WriteRgba(output, ((py * outputWidth) + px) * 4);
            }
        }
    }

    public static MapColor ComposeFarOcean(MapStyle style)
    {
        switch (style)
        {
            case MapStyle.Default:
                return MapShading.Compose(
                    Heightmap.Biome.Ocean,
                    -100f,
                    0f,
                    WorldMapRenderer.WorldRadius + 1000f,
                    0f,
                    WorldMapRenderer.PixelSize);
            case MapStyle.Topo:
                return TopoDeepWater;
            case MapStyle.Chart:
                return ChartWorldEdge;
            default:
                throw new ArgumentOutOfRangeException(nameof(style));
        }
    }

    public static MapColor ComposeChartFog(float worldX, float worldZ, int seed)
    {
        return ApplyPaperGrain(ChartParchment, worldX, worldZ, seed);
    }

    private static MapColor ComposeTopographicBase(
        Heightmap.Biome biome,
        float height,
        float lavaMask,
        float[] heights,
        int gridWidth,
        int gridHeight,
        int sx,
        int sy,
        float worldX,
        float worldZ,
        float metersPerPixel)
    {
        float edge = MapShading.EdgeOceanFactor(worldX, worldZ);
        if (edge >= 1f)
        {
            return TopoDeepWater;
        }

        MapColor palette = BiomePalette.Get(biome, height);
        MapColor color;
        if (height < MapShading.WaterLevel)
        {
            float depth = MapShading.SmoothCurve(
                MapShading.Clamp01((MapShading.WaterLevel - height) / 60f));
            MapColor water = MapColor.Lerp(TopoShallowWater, TopoDeepWater, depth);
            water = ApplyAshlandsWaterTint(water, TopoWarmWater, 0.5f, worldX, worldZ);

            float shore = MapShading.SmoothCurve(
                MapShading.Clamp01((MapShading.WaterLevel - height) / 1.5f));
            MapColor shoreTint = MapColor.Lerp(palette, TopoPaper, 0.55f);
            color = MapColor.Lerp(shoreTint, water, 0.5f + (0.5f * shore));
        }
        else
        {
            color = MapColor.Lerp(palette, TopoPaper, 0.55f);
            color = ApplyTopographicForest(color, biome, worldX, worldZ, metersPerPixel);
            if (lavaMask > 0f)
            {
                color = MapColor.Lerp(color, TopoLava, MapShading.Clamp01(lavaMask) * 0.5f);
            }

            float shade = ReliefShade(
                heights,
                gridWidth,
                gridHeight,
                sx,
                sy,
                metersPerPixel);
            color = color.Multiply(1f + ((shade - 1f) * 0.45f));
        }

        return edge > 0f ? MapColor.Lerp(color, TopoDeepWater, edge) : color;
    }

    private static MapColor ComposeChartBase(
        Heightmap.Biome biome,
        float height,
        float lavaMask,
        float[] heights,
        int gridWidth,
        int gridHeight,
        int sx,
        int sy,
        float worldX,
        float worldZ,
        float metersPerPixel,
        int seed)
    {
        float edge = MapShading.EdgeOceanFactor(worldX, worldZ);
        MapColor color;
        if (edge >= 1f)
        {
            color = ChartWorldEdge;
        }
        else
        {
            MapColor palette = BiomePalette.Get(biome, height);
            if (height < MapShading.WaterLevel)
            {
                float depth = MapShading.SmoothCurve(
                    MapShading.Clamp01((MapShading.WaterLevel - height) / 60f));
                MapColor water = MapColor.Lerp(ChartShallowWater, ChartDeepWater, depth);
                water = ApplyAshlandsWaterTint(water, ChartBurntWater, 0.25f, worldX, worldZ);

                float shore = MapShading.SmoothCurve(
                    MapShading.Clamp01((MapShading.WaterLevel - height) / 1.5f));
                MapColor shoreTint = ChartLandTint(palette);
                color = MapColor.Lerp(shoreTint, water, 0.5f + (0.5f * shore));
            }
            else
            {
                color = ChartLandTint(palette);
                color = ApplyChartForest(color, biome, worldX, worldZ, metersPerPixel);
                if (lavaMask > 0f)
                {
                    color = MapColor.Lerp(
                        color,
                        ChartGoldLeaf,
                        MapShading.Clamp01(lavaMask) * 0.35f);
                }

                float shade = ReliefShade(
                    heights,
                    gridWidth,
                    gridHeight,
                    sx,
                    sy,
                    metersPerPixel);
                if (shade < 1f)
                {
                    color = MapColor.Lerp(color, ChartSepiaShadow, (1f - shade) * 0.7f);
                }
                else if (shade > 1f)
                {
                    color = MapColor.Lerp(color, ChartSepiaLight, (shade - 1f) * 0.7f);
                }
            }

            if (edge > 0f)
            {
                color = MapColor.Lerp(color, ChartWorldEdge, edge);
            }
        }

        return ApplyPaperGrain(color, worldX, worldZ, seed);
    }

    private static MapColor ApplyTopographicForest(
        MapColor color,
        Heightmap.Biome biome,
        float worldX,
        float worldZ,
        float metersPerPixel)
    {
        MapColor stippled;
        switch (biome)
        {
            case Heightmap.Biome.Meadows:
                if (!WorldGenerator.InForest(new Vector3(worldX, 0f, worldZ)))
                {
                    return color;
                }

                stippled = MapShading.ApplyForestStipple(
                    color,
                    0.82f,
                    worldX,
                    worldZ,
                    metersPerPixel);
                break;
            case Heightmap.Biome.Plains:
                if (WorldGenerator.GetForestFactor(new Vector3(worldX, 0f, worldZ)) >= 0.8f)
                {
                    return color;
                }

                stippled = MapShading.ApplyForestStipple(
                    color,
                    0.88f,
                    worldX,
                    worldZ,
                    metersPerPixel);
                break;
            case Heightmap.Biome.BlackForest:
                stippled = MapShading.ApplyForestStipple(
                    color,
                    0.86f,
                    worldX,
                    worldZ,
                    metersPerPixel);
                break;
            case Heightmap.Biome.Swamp:
                if (!WorldGenerator.InForest(new Vector3(worldX, 0f, worldZ)))
                {
                    return color;
                }

                stippled = MapShading.ApplyForestStipple(
                    color,
                    0.90f,
                    worldX,
                    worldZ,
                    metersPerPixel);
                break;
            case Heightmap.Biome.Mistlands:
            {
                float forestFactor = WorldGenerator.GetForestFactor(new Vector3(worldX, 0f, worldZ));
                float open = 1f - MapShading.SmoothStep(1.1f, 1.3f, forestFactor);
                float speckDensity = 1f - open;
                if (speckDensity <= 0f)
                {
                    return color;
                }

                stippled = MapShading.ApplyMistlandsStipple(
                    color,
                    speckDensity * 0.45f,
                    worldX,
                    worldZ,
                    metersPerPixel);
                break;
            }
            default:
                return color;
        }

        return MapColor.Lerp(color, stippled, 0.5f);
    }

    private static MapColor ApplyChartForest(
        MapColor color,
        Heightmap.Biome biome,
        float worldX,
        float worldZ,
        float metersPerPixel)
    {
        float coverage;
        switch (biome)
        {
            case Heightmap.Biome.Meadows:
                if (!WorldGenerator.InForest(new Vector3(worldX, 0f, worldZ)))
                {
                    return color;
                }

                coverage = RegularForestCoverage(worldX, worldZ, metersPerPixel);
                break;
            case Heightmap.Biome.Plains:
                if (WorldGenerator.GetForestFactor(new Vector3(worldX, 0f, worldZ)) >= 0.8f)
                {
                    return color;
                }

                coverage = RegularForestCoverage(worldX, worldZ, metersPerPixel);
                break;
            case Heightmap.Biome.BlackForest:
                coverage = RegularForestCoverage(worldX, worldZ, metersPerPixel);
                break;
            case Heightmap.Biome.Swamp:
                if (!WorldGenerator.InForest(new Vector3(worldX, 0f, worldZ)))
                {
                    return color;
                }

                coverage = RegularForestCoverage(worldX, worldZ, metersPerPixel);
                break;
            case Heightmap.Biome.Mistlands:
            {
                float forestFactor = WorldGenerator.GetForestFactor(new Vector3(worldX, 0f, worldZ));
                float open = 1f - MapShading.SmoothStep(1.1f, 1.3f, forestFactor);
                float speckDensity = 1f - open;
                if (speckDensity <= 0f)
                {
                    return color;
                }

                coverage = MistlandsForestCoverage(
                    speckDensity * 0.45f,
                    worldX,
                    worldZ,
                    metersPerPixel);
                break;
            }
            default:
                return color;
        }

        return coverage > 0f
            ? MapColor.Lerp(color, ChartForestInk, coverage * 0.12f)
            : color;
    }

    private static float RegularForestCoverage(
        float worldX,
        float worldZ,
        float metersPerPixel)
    {
        if (metersPerPixel >= CoarseStippleMetersPerPixel)
        {
            return MapShading.IsCoarseStipplePixel(worldX, worldZ, metersPerPixel) ? 1f : 0f;
        }

        float fineCoverage = MapShading.FineStippleCoverage(worldX, worldZ, 0.75f, true);
        if (metersPerPixel <= FineStippleMetersPerPixel)
        {
            return fineCoverage;
        }

        float coarseCoverage = MapShading.IsCoarseStipplePixel(worldX, worldZ, metersPerPixel)
            ? 1f
            : 0f;
        float coarseBlend = MapShading.SmoothStep(
            FineStippleMetersPerPixel,
            CoarseStippleMetersPerPixel,
            metersPerPixel);
        return fineCoverage + ((coarseCoverage - fineCoverage) * coarseBlend);
    }

    private static float MistlandsForestCoverage(
        float activationProbability,
        float worldX,
        float worldZ,
        float metersPerPixel)
    {
        if (metersPerPixel >= CoarseStippleMetersPerPixel)
        {
            return MapShading.StippleNoise(worldX, worldZ, metersPerPixel) < activationProbability
                ? 0.85f
                : 0f;
        }

        float fineAmount = MapShading.FineStippleCoverage(
            worldX,
            worldZ,
            activationProbability,
            false) * 0.85f;
        if (metersPerPixel <= FineStippleMetersPerPixel)
        {
            return fineAmount;
        }

        float coarseAmount = MapShading.StippleNoise(worldX, worldZ, metersPerPixel) < activationProbability
            ? 0.85f
            : 0f;
        float coarseBlend = MapShading.SmoothStep(
            FineStippleMetersPerPixel,
            CoarseStippleMetersPerPixel,
            metersPerPixel);
        return fineAmount + ((coarseAmount - fineAmount) * coarseBlend);
    }

    private static MapColor ChartLandTint(MapColor palette)
    {
        MapColor color = MapColor.Lerp(palette, ChartParchment, 0.72f);
        float luminance = (color.Red * 0.2126f) +
                          (color.Green * 0.7152f) +
                          (color.Blue * 0.0722f);
        MapColor sepia = MapColor.Lerp(ChartSepiaDark, ChartSepiaLight, luminance);
        return MapColor.Lerp(color, sepia, 0.4f);
    }

    private static MapColor ApplyAshlandsWaterTint(
        MapColor water,
        MapColor tint,
        float strength,
        float worldX,
        float worldZ)
    {
        float distance = (float)Math.Sqrt((worldX * worldX) + (worldZ * worldZ));
        if (distance <= WorldMapRenderer.WorldRadius)
        {
            float ashlands = MapShading.Clamp01(
                WorldGenerator.GetAshlandsOceanGradient(worldX, worldZ));
            if (ashlands > 0f)
            {
                return MapColor.Lerp(water, tint, ashlands * strength);
            }
        }

        return water;
    }

    private static MapColor ApplyPaperGrain(
        MapColor color,
        float worldX,
        float worldZ,
        int seed)
    {
        float grain = (ValueNoise(worldX, worldZ, 160f, seed) * 6f) +
                      (ValueNoise(worldX, worldZ, 40f, seed ^ unchecked((int)0x9e3779b9u)) * 3f);
        float adjustment = grain / 255f;
        return new MapColor(
            MapShading.Clamp01(color.Red + adjustment),
            MapShading.Clamp01(color.Green + adjustment),
            MapShading.Clamp01(color.Blue + adjustment));
    }

    private static float ValueNoise(
        float worldX,
        float worldZ,
        float cellMeters,
        int seed)
    {
        float cellPositionX = worldX / cellMeters;
        float cellPositionZ = worldZ / cellMeters;
        int cellX = (int)Math.Floor(cellPositionX);
        int cellZ = (int)Math.Floor(cellPositionZ);
        float blendX = MapShading.SmoothCurve(cellPositionX - cellX);
        float blendZ = MapShading.SmoothCurve(cellPositionZ - cellZ);

        float topLeft = SeededCellNoise(cellX, cellZ, seed);
        float topRight = SeededCellNoise(cellX + 1, cellZ, seed);
        float bottomLeft = SeededCellNoise(cellX, cellZ + 1, seed);
        float bottomRight = SeededCellNoise(cellX + 1, cellZ + 1, seed);
        float top = topLeft + ((topRight - topLeft) * blendX);
        float bottom = bottomLeft + ((bottomRight - bottomLeft) * blendX);
        return top + ((bottom - top) * blendZ);
    }

    private static float SeededCellNoise(int cellX, int cellZ, int seed)
    {
        uint hash = MapShading.CellHash(cellX, cellZ) ^ (uint)seed;
        hash ^= hash >> 16;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        return (MapShading.HashNoise(hash) * 2f) - 1f;
    }

    private static MapColor ApplyLandContour(
        MapStyle style,
        MapColor color,
        ContourStroke contour)
    {
        if (style == MapStyle.Topo)
        {
            float classStrength = contour.IsCoast || contour.Level % 5 == 0 ? 1f : 0.45f;
            return MapColor.Lerp(color, TopoContourInk, classStrength * contour.Strength);
        }

        if (contour.IsCoast)
        {
            return MapColor.Lerp(color, ChartAgedInk, 0.75f * contour.Strength);
        }

        float contourClass = contour.Level % 5 == 0 ? 1f : 0.45f;
        return MapColor.Lerp(
            color,
            ChartContourInk,
            0.35f * contourClass * contour.Strength);
    }

    private static ContourStroke FindLandContour(
        float[] heights,
        int gridWidth,
        int gridHeight,
        int sx,
        int sy,
        float interval)
    {
        int level = LandContourLevel(heights[(sy * gridWidth) + sx], interval);
        if (level < 0)
        {
            return default;
        }

        int previousX = Math.Max(0, sx - 1);
        int nextX = Math.Min(gridWidth - 1, sx + 1);
        int previousY = Math.Max(0, sy - 1);
        int nextY = Math.Min(gridHeight - 1, sy + 1);
        int left = LandContourLevel(heights[(sy * gridWidth) + previousX], interval);
        int right = LandContourLevel(heights[(sy * gridWidth) + nextX], interval);
        int up = LandContourLevel(heights[(previousY * gridWidth) + sx], interval);
        int down = LandContourLevel(heights[(nextY * gridWidth) + sx], interval);
        int upLeft = LandContourLevel(heights[(previousY * gridWidth) + previousX], interval);
        int upRight = LandContourLevel(heights[(previousY * gridWidth) + nextX], interval);
        int downLeft = LandContourLevel(heights[(nextY * gridWidth) + previousX], interval);
        int downRight = LandContourLevel(heights[(nextY * gridWidth) + nextX], interval);
        bool coast = left < 0 || right < 0 || up < 0 || down < 0 ||
                     upLeft < 0 || upRight < 0 || downLeft < 0 || downRight < 0;
        if (left < level || right < level || up < level || down < level)
        {
            return new ContourStroke(level, 1f, coast);
        }

        if (upLeft < level || upRight < level || downLeft < level || downRight < level)
        {
            return new ContourStroke(level, 0.5f, coast);
        }

        return default;
    }

    private static float FindIsobath(
        float[] heights,
        int gridWidth,
        int gridHeight,
        int sx,
        int sy,
        float interval)
    {
        int level = IsobathLevel(heights[(sy * gridWidth) + sx], interval);
        if (level < 0)
        {
            return 0f;
        }

        int previousX = Math.Max(0, sx - 1);
        int nextX = Math.Min(gridWidth - 1, sx + 1);
        int previousY = Math.Max(0, sy - 1);
        int nextY = Math.Min(gridHeight - 1, sy + 1);
        if (IsLowerIsobath(heights[(sy * gridWidth) + previousX], interval, level) ||
            IsLowerIsobath(heights[(sy * gridWidth) + nextX], interval, level) ||
            IsLowerIsobath(heights[(previousY * gridWidth) + sx], interval, level) ||
            IsLowerIsobath(heights[(nextY * gridWidth) + sx], interval, level))
        {
            return 1f;
        }

        return IsLowerIsobath(heights[(previousY * gridWidth) + previousX], interval, level) ||
               IsLowerIsobath(heights[(previousY * gridWidth) + nextX], interval, level) ||
               IsLowerIsobath(heights[(nextY * gridWidth) + previousX], interval, level) ||
               IsLowerIsobath(heights[(nextY * gridWidth) + nextX], interval, level)
            ? 0.5f
            : 0f;
    }

    private static bool IsLowerIsobath(float height, float interval, int level)
    {
        int neighborLevel = IsobathLevel(height, interval);
        return neighborLevel >= 0 && neighborLevel < level;
    }

    private static int LandContourLevel(float height, float interval)
    {
        if (height < MapShading.WaterLevel)
        {
            return -1;
        }

        return Math.Max(
            0,
            (int)Math.Floor((height - MapShading.WaterLevel) / interval));
    }

    private static int IsobathLevel(float height, float interval)
    {
        if (height >= MapShading.WaterLevel)
        {
            return -1;
        }

        return Math.Max(
            0,
            (int)Math.Floor((MapShading.WaterLevel - height) / interval));
    }

    private static float ReliefShade(
        float[] heights,
        int gridWidth,
        int gridHeight,
        int sx,
        int sy,
        float metersPerPixel)
    {
        const float lightX = -0.5298129f;
        const float lightY = 0.6622662f;
        const float lightZ = -0.5298129f;
        int previousX = Math.Max(0, sx - 1);
        int nextX = Math.Min(gridWidth - 1, sx + 1);
        int previousY = Math.Max(0, sy - 1);
        int nextY = Math.Min(gridHeight - 1, sy + 1);
        float slopeX = (heights[(sy * gridWidth) + nextX] -
                        heights[(sy * gridWidth) + previousX]) / (2f * metersPerPixel);
        float slopeZ = (heights[(previousY * gridWidth) + sx] -
                        heights[(nextY * gridWidth) + sx]) / (2f * metersPerPixel);
        float normalX = -slopeX;
        float normalY = 1f;
        float normalZ = -slopeZ;
        float inverseLength = 1f / (float)Math.Sqrt(
            (normalX * normalX) + (normalY * normalY) + (normalZ * normalZ));
        float light = ((normalX * lightX) + (normalY * lightY) + (normalZ * lightZ)) * inverseLength;
        return Math.Max(0.75f, Math.Min(1.25f, 1f + ((light - lightY) * 0.55f)));
    }

    private static float ContourInterval(MapStyle style, float metersPerPixel)
    {
        if (style == MapStyle.Topo)
        {
            if (metersPerPixel >= 6f)
            {
                return 40f;
            }

            return metersPerPixel >= 3f ? 20f : 10f;
        }

        return metersPerPixel >= 6f ? 50f : 25f;
    }

    private static void ValidateArguments(
        MapStyle style,
        float[] heights,
        Heightmap.Biome[] biomes,
        float[] lavaMasks,
        int gridWidth,
        int gridHeight,
        int border,
        float metersPerPixel,
        byte[] output)
    {
        if (style != MapStyle.Topo && style != MapStyle.Chart)
        {
            throw new ArgumentOutOfRangeException(nameof(style));
        }

        if (gridWidth <= 0 || gridHeight <= 0 || border < 0 ||
            gridWidth <= border * 2 || gridHeight <= border * 2)
        {
            throw new ArgumentOutOfRangeException(nameof(gridWidth));
        }

        int sampleCount = checked(gridWidth * gridHeight);
        if (heights.Length != sampleCount ||
            biomes.Length != sampleCount ||
            lavaMasks.Length != sampleCount)
        {
            throw new ArgumentException("Sample buffer dimensions do not match their lengths.");
        }

        int outputWidth = gridWidth - (border * 2);
        int outputHeight = gridHeight - (border * 2);
        if (output.Length != checked(outputWidth * outputHeight * 4))
        {
            throw new ArgumentException("RGBA buffer dimensions do not match its length.", nameof(output));
        }

        if (metersPerPixel <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(metersPerPixel));
        }
    }

    private static MapColor HexColor(byte red, byte green, byte blue)
    {
        return new MapColor(red / 255f, green / 255f, blue / 255f);
    }

    private readonly struct ContourStroke
    {
        public ContourStroke(int level, float strength, bool isCoast)
        {
            Level = level;
            Strength = strength;
            IsCoast = isCoast;
        }

        public int Level { get; }

        public float Strength { get; }

        public bool IsCoast { get; }
    }
}

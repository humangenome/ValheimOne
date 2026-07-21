using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

internal sealed class WorldMapRenderer
{
    public const float PixelSize = 12f;
    public const int WorldRadius = 10500;
    public const int RendererVersion = 5;

    // Deepest zoom served through on-demand detail tiles (about 0.375 m/px for a
    // 2048 base at 12 m/px, providing sub-meter detail).
    public const int DetailZoomTarget = 8;

    private const int StyleUnrendered = 0;
    private const int StyleRendering = 1;
    private const int StyleReady = 2;
    private const int StyleFailed = 3;

    private readonly WorldGenerator _generator;
    private readonly int _seed;
    private readonly string _seedName;
    private readonly string _worldName;
    private readonly string _gameVersion;
    private readonly ModLogger _log;
    private readonly DetailTileRenderer _detailRenderer;
    private readonly object _styleSync = new object();
    private readonly Queue<MapStyle> _styleQueue = new Queue<MapStyle>();
    private readonly StyleRenderStatus _topoStatus = new StyleRenderStatus();
    private readonly StyleRenderStatus _chartStatus = new StyleRenderStatus();
    private Thread? _thread;
    private Thread? _styleThread;
    private volatile bool _stopRequested;
    private int _state;
    private int _completedRows;
    private string? _renderRevision;
    private BiomeRegionSnapshot[] _regions = Array.Empty<BiomeRegionSnapshot>();

    public WorldMapRenderer(
        WorldGenerator generator,
        int seed,
        string seedName,
        string worldName,
        string gameVersion,
        int textureSize,
        ModLogger log)
    {
        _generator = generator;
        _seed = seed;
        _seedName = seedName;
        _worldName = worldName;
        _gameVersion = gameVersion;
        TextureSize = textureSize;
        BaseMaximumZoom = CalculateMaximumZoom(textureSize);
        MaximumZoom = Math.Max(BaseMaximumZoom, DetailZoomTarget);
        _log = log;

        string mapRoot = Path.Combine(Paths.ConfigPath, "ValheimOne", "map");
        CacheDirectory = Path.Combine(mapRoot, SanitizeWorldName(worldName));
        _detailRenderer = new DetailTileRenderer(
            generator,
            seed,
            CacheDirectory,
            textureSize,
            BaseMaximumZoom,
            MaximumZoom,
            () => IsReady,
            log);
    }

    public string CacheDirectory { get; }

    public int TextureSize { get; }

    public int Seed => _seed;

    // Deepest zoom whose tiles come from the pre-rendered base pyramid.
    public int BaseMaximumZoom { get; }

    // Deepest zoom served overall, including on-demand detail tiles.
    public int MaximumZoom { get; }

    public BiomeRegionSnapshot[] Regions => Volatile.Read(ref _regions);

    public string StateName
    {
        get
        {
            switch (Volatile.Read(ref _state))
            {
                case 1:
                    return "ready";
                case 2:
                    return "failed";
                default:
                    return "generating";
            }
        }
    }

    public bool IsReady => Volatile.Read(ref _state) == 1;

    public string RenderRevision
    {
        get
        {
            if (!IsReady)
            {
                return "0";
            }

            string? revision = Volatile.Read(ref _renderRevision);
            if (revision != null)
            {
                return revision;
            }

            string basePath = Path.Combine(CacheDirectory, "base.png");
            long lastWriteTicks = File.GetLastWriteTimeUtc(basePath).Ticks;
            string calculated = RendererVersion.ToString(CultureInfo.InvariantCulture) + "-" +
                                lastWriteTicks.ToString(CultureInfo.InvariantCulture);
            Interlocked.CompareExchange(ref _renderRevision, calculated, null);
            return Volatile.Read(ref _renderRevision) ?? "0";
        }
    }

    public float Progress
    {
        get
        {
            if (IsReady)
            {
                return 1f;
            }

            return Math.Max(0f, Math.Min(1f, Volatile.Read(ref _completedRows) / (float)TextureSize));
        }
    }

    public string GetStyleStateName(MapStyle style)
    {
        if (style == MapStyle.Default)
        {
            return StateName;
        }

        StyleRenderStatus status = GetStyleStatus(style);
        switch (Volatile.Read(ref status.State))
        {
            case StyleRendering:
                return "rendering";
            case StyleReady:
                return "ready";
            case StyleFailed:
                return "failed";
            default:
                return "unrendered";
        }
    }

    public float GetStyleProgress(MapStyle style)
    {
        if (style == MapStyle.Default)
        {
            return Progress;
        }

        StyleRenderStatus status = GetStyleStatus(style);
        if (Volatile.Read(ref status.State) == StyleReady)
        {
            return 1f;
        }

        return Math.Max(
            0f,
            Math.Min(1f, Volatile.Read(ref status.CompletedRows) / (float)TextureSize));
    }

    public string GetStyleRevision(MapStyle style)
    {
        if (style == MapStyle.Default)
        {
            return RenderRevision;
        }

        StyleRenderStatus status = GetStyleStatus(style);
        if (Volatile.Read(ref status.State) != StyleReady)
        {
            return "0";
        }

        string? revision = Volatile.Read(ref status.Revision);
        if (revision != null)
        {
            return revision;
        }

        long lastWriteTicks = File.GetLastWriteTimeUtc(GetBasePath(style)).Ticks;
        string calculated = RendererVersion.ToString(CultureInfo.InvariantCulture) + "-" +
                            MapStyleCompositor.StyleVersion.ToString(CultureInfo.InvariantCulture) + "-" +
                            lastWriteTicks.ToString(CultureInfo.InvariantCulture);
        Interlocked.CompareExchange(ref status.Revision, calculated, null);
        return Volatile.Read(ref status.Revision) ?? "0";
    }

    public static int NormalizeTextureSize(int requested)
    {
        if (requested <= TilePyramid.TileSize)
        {
            return TilePyramid.TileSize;
        }

        int normalized = TilePyramid.TileSize;
        while (normalized < 4096 && normalized <= requested / 2)
        {
            normalized *= 2;
        }

        return normalized;
    }

    public void Start()
    {
        if (_thread != null || IsReady)
        {
            return;
        }

        bool useCache = TryUseCache();
        InitializeStyleCaches();
        if (useCache)
        {
            Volatile.Write(ref _completedRows, TextureSize);
            _thread = new Thread(PrepareCachedMap)
            {
                IsBackground = true,
                Name = "ValheimOne.LiveMap.Regions",
                Priority = System.Threading.ThreadPriority.BelowNormal,
            };
            _thread.Start();
            _detailRenderer.Start();
            return;
        }

        _thread = new Thread(Render)
        {
            IsBackground = true,
            Name = "ValheimOne.LiveMap.Render",
            Priority = System.Threading.ThreadPriority.BelowNormal,
        };
        _thread.Start();
        _detailRenderer.Start();
    }

    public void Stop()
    {
        _stopRequested = true;
        lock (_styleSync)
        {
            Monitor.PulseAll(_styleSync);
        }

        _detailRenderer.Stop();
        Thread? thread = _thread;
        if (thread != null && thread.IsAlive && !ReferenceEquals(Thread.CurrentThread, thread))
        {
            thread.Join();
        }

        _thread = null;
        Thread? styleThread = _styleThread;
        if (styleThread != null &&
            styleThread.IsAlive &&
            !ReferenceEquals(Thread.CurrentThread, styleThread))
        {
            styleThread.Join();
        }

        _styleThread = null;
    }

    // Called from HTTP worker threads for zooms beyond the base pyramid.
    public bool TryGetDetailTile(MapStyle style, int zoom, int x, int y, out string path)
    {
        return _detailRenderer.TryGetTile(style, zoom, x, y, out path);
    }

    public bool IsStyleReady(MapStyle style)
    {
        return style == MapStyle.Default
            ? IsReady
            : Volatile.Read(ref GetStyleStatus(style).State) == StyleReady;
    }

    public void RequestStyleRender(MapStyle style)
    {
        if (style == MapStyle.Default)
        {
            return;
        }

        StyleRenderStatus status = GetStyleStatus(style);
        lock (_styleSync)
        {
            if (_stopRequested || Volatile.Read(ref status.State) != StyleUnrendered)
            {
                return;
            }

            Volatile.Write(ref status.CompletedRows, 0);
            Volatile.Write(ref status.Revision, null);
            Volatile.Write(ref status.State, StyleRendering);
            _styleQueue.Enqueue(style);
            if (_styleThread == null)
            {
                _styleThread = new Thread(StyleWorkerLoop)
                {
                    IsBackground = true,
                    Name = "ValheimOne.LiveMap.Styles",
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                };
                _styleThread.Start();
            }

            Monitor.PulseAll(_styleSync);
        }
    }

    public string GetBasePath(MapStyle style)
    {
        return style == MapStyle.Default
            ? Path.Combine(CacheDirectory, "base.png")
            : Path.Combine(CacheDirectory, $"base-{MapStyles.Token(style)}.png");
    }

    public string GetTilePath(MapStyle style, int zoom, int x, int y)
    {
        return MapStyles.TilePath(CacheDirectory, style, zoom, x, y);
    }

    public string GetOceanTilePath(MapStyle style)
    {
        return MapStyles.OceanTilePath(CacheDirectory, style);
    }

    private void StyleWorkerLoop()
    {
        while (!_stopRequested)
        {
            MapStyle style;
            lock (_styleSync)
            {
                while (!_stopRequested && (_styleQueue.Count == 0 || !IsReady))
                {
                    Monitor.Wait(_styleSync, 500);
                }

                if (_stopRequested)
                {
                    return;
                }

                style = _styleQueue.Dequeue();
            }

            RenderStyle(style);
        }
    }

    private void RenderStyle(MapStyle style)
    {
        StyleRenderStatus status = GetStyleStatus(style);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ThrowIfStopping();
            DeleteStyleCacheArtifacts(style);
            int pixelCount = checked(TextureSize * TextureSize);
            var heights = new float[pixelCount];
            var land = new bool[pixelCount];
            var biomes = new Heightmap.Biome[pixelCount];
            var lavaMasks = new float[pixelCount];
            var pixels = new byte[checked(pixelCount * 4)];

            SampleStyleWorld(status, heights, land, biomes, lavaMasks);
            float half = TextureSize / 2f;
            MapStyleCompositor.ComposeInto(
                style,
                heights,
                biomes,
                lavaMasks,
                TextureSize,
                TextureSize,
                0,
                -half * PixelSize,
                half * PixelSize,
                PixelSize,
                _seed,
                pixels,
                IsStopping);

            ThrowIfStopping();
            PngEncoder.WriteRgba(
                GetBasePath(style),
                pixels,
                TextureSize,
                TextureSize,
                IsStopping);
            int tileCount = TilePyramid.Write(
                CacheDirectory,
                style,
                pixels,
                TextureSize,
                BaseMaximumZoom,
                IsStopping);
            WriteStyleOceanTile(style);

            ThrowIfStopping();
            stopwatch.Stop();
            WriteStyleMetadata(style, stopwatch.Elapsed.TotalMilliseconds);
            Volatile.Write(ref status.CompletedRows, TextureSize);
            Volatile.Write(ref status.Revision, null);
            Volatile.Write(ref status.State, StyleReady);
            _log.Info(
                $"[LiveMap] {MapStyles.Token(style)} style render complete in " +
                $"{stopwatch.Elapsed.TotalSeconds:F1}s ({TextureSize}x{TextureSize}, {tileCount} tiles)");
        }
        catch (OperationCanceledException)
        {
            DeleteTemporaryStyleMetadata(style);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref status.State, StyleFailed);
            DeleteTemporaryStyleMetadata(style);
            _log.Error(
                $"[LiveMap] {MapStyles.Token(style)} style render failed: {exception}");
        }
    }

    private void SampleStyleWorld(
        StyleRenderStatus status,
        float[] heights,
        bool[] land,
        Heightmap.Biome[] biomes,
        float[] lavaMasks)
    {
        float half = TextureSize / 2f;
        for (int py = 0; py < TextureSize; py++)
        {
            ThrowIfStopping();
            float worldZ = ((half - py) * PixelSize) - (PixelSize / 2f);
            for (int px = 0; px < TextureSize; px++)
            {
                float worldX = ((px - half) * PixelSize) + (PixelSize / 2f);
                int pixelIndex = (py * TextureSize) + px;

                // Beyond the playable edge WorldGenerator returns garbage biomes;
                // clamp to flat deep ocean and skip relief (constant height).
                if (MapShading.EdgeOceanFactor(worldX, worldZ) >= 1f)
                {
                    heights[pixelIndex] = -100f;
                    land[pixelIndex] = false;
                    biomes[pixelIndex] = Heightmap.Biome.Ocean;
                    lavaMasks[pixelIndex] = 0f;
                    continue;
                }

                Heightmap.Biome biome = _generator.GetBiome(worldX, worldZ);
                float height = _generator.GetBiomeHeight(biome, worldX, worldZ, out Color mask);
                heights[pixelIndex] = height;

                bool isLand = height >= MapShading.WaterLevel;
                land[pixelIndex] = isLand;
                biomes[pixelIndex] = biome;
                lavaMasks[pixelIndex] = biome == Heightmap.Biome.AshLands ? mask.a : 0f;
            }

            Volatile.Write(ref status.CompletedRows, py + 1);
        }
    }

    private void WriteStyleOceanTile(MapStyle style)
    {
        int pixelCount = TilePyramid.TileSize * TilePyramid.TileSize;
        var pixels = new byte[pixelCount * 4];
        MapColor ocean = MapStyleCompositor.ComposeFarOcean(style);
        for (int index = 0; index < pixelCount; index++)
        {
            ocean.WriteRgba(pixels, index * 4);
        }

        PngEncoder.WriteRgba(
            GetOceanTilePath(style),
            pixels,
            TilePyramid.TileSize,
            TilePyramid.TileSize,
            IsStopping);
    }

    private void SignalStyleWorker()
    {
        lock (_styleSync)
        {
            Monitor.PulseAll(_styleSync);
        }
    }

    private void Render()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            int pixelCount = checked(TextureSize * TextureSize);
            var heights = new float[pixelCount];
            var land = new bool[pixelCount];
            var pixels = new byte[checked(pixelCount * 4)];

            SampleWorld(heights, land, pixels);
            ApplyRelief(heights, land, pixels);

            ThrowIfStopping();
            string basePath = Path.Combine(CacheDirectory, "base.png");
            PngEncoder.WriteRgba(basePath, pixels, TextureSize, TextureSize, IsStopping);
            int tileCount = TilePyramid.Write(
                CacheDirectory,
                pixels,
                TextureSize,
                BaseMaximumZoom,
                IsStopping);

            BuildRegions();
            ThrowIfStopping();
            stopwatch.Stop();
            WriteMetadata(stopwatch.Elapsed.TotalMilliseconds);
            Volatile.Write(ref _completedRows, TextureSize);
            Volatile.Write(ref _state, 1);
            SignalStyleWorker();
            _log.Info(
                $"[LiveMap] world render complete in {stopwatch.Elapsed.TotalSeconds:F1}s " +
                $"({TextureSize}x{TextureSize}, {tileCount} tiles)");
        }
        catch (OperationCanceledException)
        {
            DeleteTemporaryMetadata();
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _state, 2);
            DeleteTemporaryMetadata();
            _log.Error($"[LiveMap] world render failed: {exception}");
        }
    }

    private void PrepareCachedMap()
    {
        try
        {
            BuildRegions();
            ThrowIfStopping();
            Volatile.Write(ref _state, 1);
            SignalStyleWorker();
            _log.Info(
                $"[LiveMap] using cached world render for {_worldName} " +
                $"({TextureSize}x{TextureSize}).");
        }
        catch (OperationCanceledException)
        {
            // The renderer is stopping.
        }
    }

    private void BuildRegions()
    {
        try
        {
            BiomeRegionSnapshot[] regions = BiomeRegionCatalog.Build(_generator, IsStopping);
            Volatile.Write(ref _regions, regions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] biome region labels could not be generated: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void SampleWorld(float[] heights, bool[] land, byte[] pixels)
    {
        float half = TextureSize / 2f;
        for (int py = 0; py < TextureSize; py++)
        {
            ThrowIfStopping();
            float worldZ = ((half - py) * PixelSize) - (PixelSize / 2f);
            for (int px = 0; px < TextureSize; px++)
            {
                float worldX = ((px - half) * PixelSize) + (PixelSize / 2f);
                int pixelIndex = (py * TextureSize) + px;

                // Beyond the playable edge WorldGenerator returns garbage biomes;
                // clamp to flat deep ocean and skip relief (constant height).
                if (MapShading.EdgeOceanFactor(worldX, worldZ) >= 1f)
                {
                    heights[pixelIndex] = -100f;
                    land[pixelIndex] = false;
                    MapShading.Compose(
                            Heightmap.Biome.Ocean,
                            -100f,
                            0f,
                            worldX,
                            worldZ,
                            PixelSize)
                        .WriteRgba(pixels, pixelIndex * 4);
                    continue;
                }

                Heightmap.Biome biome = _generator.GetBiome(worldX, worldZ);
                float height = _generator.GetBiomeHeight(biome, worldX, worldZ, out Color mask);
                heights[pixelIndex] = height;

                bool isLand = height >= MapShading.WaterLevel;
                land[pixelIndex] = isLand;
                float lavaMask = biome == Heightmap.Biome.AshLands ? mask.a : 0f;
                MapColor color = MapShading.Compose(
                    biome,
                    height,
                    lavaMask,
                    worldX,
                    worldZ,
                    PixelSize);
                color.WriteRgba(pixels, pixelIndex * 4);
            }

            Volatile.Write(ref _completedRows, py + 1);
        }
    }

    private void ApplyRelief(float[] heights, bool[] land, byte[] pixels)
    {
        const float lightX = -0.5298129f;
        const float lightY = 0.6622662f;
        const float lightZ = -0.5298129f;
        for (int y = 0; y < TextureSize; y++)
        {
            ThrowIfStopping();
            int previousY = Math.Max(0, y - 1);
            int nextY = Math.Min(TextureSize - 1, y + 1);
            for (int x = 0; x < TextureSize; x++)
            {
                int index = (y * TextureSize) + x;
                if (!land[index])
                {
                    continue;
                }

                int previousX = Math.Max(0, x - 1);
                int nextX = Math.Min(TextureSize - 1, x + 1);
                float slopeX = (heights[(y * TextureSize) + nextX] -
                                heights[(y * TextureSize) + previousX]) / (2f * PixelSize);
                float slopeZ = (heights[(previousY * TextureSize) + x] -
                                heights[(nextY * TextureSize) + x]) / (2f * PixelSize);
                float normalX = -slopeX;
                float normalY = 1f;
                float normalZ = -slopeZ;
                float inverseLength = 1f / (float)Math.Sqrt(
                    (normalX * normalX) + (normalY * normalY) + (normalZ * normalZ));
                float light = ((normalX * lightX) + (normalY * lightY) + (normalZ * lightZ)) * inverseLength;
                float shade = Math.Max(0.75f, Math.Min(1.25f, 1f + ((light - lightY) * 0.55f)));

                int offset = index * 4;
                pixels[offset] = ShadeChannel(pixels[offset], shade);
                pixels[offset + 1] = ShadeChannel(pixels[offset + 1], shade);
                pixels[offset + 2] = ShadeChannel(pixels[offset + 2], shade);
            }
        }
    }

    private void InitializeStyleCaches()
    {
        for (int index = 0; index < MapStyles.NonDefaultStyles.Length; index++)
        {
            MapStyle style = MapStyles.NonDefaultStyles[index];
            StyleRenderStatus status = GetStyleStatus(style);
            if (TryUseStyleCache(style))
            {
                Volatile.Write(ref status.CompletedRows, TextureSize);
                Volatile.Write(ref status.Revision, null);
                Volatile.Write(ref status.State, StyleReady);
                continue;
            }

            DeleteStyleCacheArtifacts(style);
            Volatile.Write(ref status.CompletedRows, 0);
            Volatile.Write(ref status.Revision, null);
            Volatile.Write(ref status.State, StyleUnrendered);
        }
    }

    private bool TryUseStyleCache(MapStyle style)
    {
        string metadataPath = StyleMetadataPath(style);
        return File.Exists(metadataPath) &&
               File.Exists(GetBasePath(style)) &&
               StylePyramidExists(style) &&
               StyleMetadataMatches(File.ReadAllText(metadataPath));
    }

    private bool StylePyramidExists(MapStyle style)
    {
        string styleDirectory = MapStyles.TilesDirectory(CacheDirectory, style);
        if (!Directory.Exists(styleDirectory))
        {
            return false;
        }

        for (int zoom = 0; zoom <= BaseMaximumZoom; zoom++)
        {
            int tilesAcross = 1 << zoom;
            for (int y = 0; y < tilesAcross; y++)
            {
                for (int x = 0; x < tilesAcross; x++)
                {
                    if (!File.Exists(GetTilePath(style, zoom, x, y)))
                    {
                        return false;
                    }
                }
            }
        }

        return File.Exists(GetOceanTilePath(style));
    }

    private bool TryUseCache()
    {
        string metadataPath = Path.Combine(CacheDirectory, "meta.json");
        if (Directory.Exists(CacheDirectory))
        {
            bool valid = File.Exists(metadataPath) &&
                         File.Exists(Path.Combine(CacheDirectory, "base.png")) &&
                         Directory.Exists(Path.Combine(CacheDirectory, "tiles")) &&
                         MetadataMatches(File.ReadAllText(metadataPath));
            if (valid)
            {
                return true;
            }

            DeleteMapCacheArtifacts(metadataPath);
        }

        Directory.CreateDirectory(CacheDirectory);
        return false;
    }

    private void DeleteMapCacheArtifacts(string metadataPath)
    {
        DeleteFile(metadataPath);
        DeleteFile(metadataPath + ".tmp");
        DeleteFile(Path.Combine(CacheDirectory, "base.png"));
        DeleteFile(Path.Combine(CacheDirectory, "base.png.tmp"));

        DeleteFilesMatching("base-*.png");
        DeleteFilesMatching("base-*.png.tmp");
        DeleteFilesMatching("meta-*.json");
        DeleteFilesMatching("meta-*.json.tmp");

        string tilesDirectory = Path.Combine(CacheDirectory, "tiles");
        if (Directory.Exists(tilesDirectory))
        {
            Directory.Delete(tilesDirectory, recursive: true);
        }
    }

    private void DeleteStyleCacheArtifacts(MapStyle style)
    {
        string metadataPath = StyleMetadataPath(style);
        DeleteFile(metadataPath);
        DeleteFile(metadataPath + ".tmp");
        string basePath = GetBasePath(style);
        DeleteFile(basePath);
        DeleteFile(basePath + ".tmp");

        string tilesDirectory = MapStyles.TilesDirectory(CacheDirectory, style);
        if (Directory.Exists(tilesDirectory))
        {
            Directory.Delete(tilesDirectory, recursive: true);
        }
    }

    private void DeleteFilesMatching(string searchPattern)
    {
        if (!Directory.Exists(CacheDirectory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(
                     CacheDirectory,
                     searchPattern,
                     SearchOption.TopDirectoryOnly))
        {
            DeleteFile(path);
        }
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private bool MetadataMatches(string metadata)
    {
        return metadata.IndexOf($"\"seed\":{_seed.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal) >= 0 &&
               metadata.IndexOf($"\"gameVersion\":{JsonWriter.Quote(_gameVersion)}", StringComparison.Ordinal) >= 0 &&
               metadata.IndexOf($"\"rendererVersion\":{RendererVersion}", StringComparison.Ordinal) >= 0 &&
               metadata.IndexOf($"\"textureSize\":{TextureSize}", StringComparison.Ordinal) >= 0;
    }

    private bool StyleMetadataMatches(string metadata)
    {
        return metadata.IndexOf($"\"seed\":{_seed.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal) >= 0 &&
               metadata.IndexOf($"\"gameVersion\":{JsonWriter.Quote(_gameVersion)}", StringComparison.Ordinal) >= 0 &&
               metadata.IndexOf($"\"rendererVersion\":{RendererVersion}", StringComparison.Ordinal) >= 0 &&
               metadata.IndexOf(
                   $"\"styleVersion\":{MapStyleCompositor.StyleVersion}",
                   StringComparison.Ordinal) >= 0 &&
               metadata.IndexOf($"\"textureSize\":{TextureSize}", StringComparison.Ordinal) >= 0;
    }

    private void WriteMetadata(double renderMilliseconds)
    {
        var json = new StringBuilder(256);
        json.Append('{');
        json.Append("\"seed\":").Append(_seed.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"seedName\":").Append(JsonWriter.Quote(_seedName));
        json.Append(",\"worldName\":").Append(JsonWriter.Quote(_worldName));
        json.Append(",\"gameVersion\":").Append(JsonWriter.Quote(_gameVersion));
        json.Append(",\"rendererVersion\":").Append(RendererVersion);
        json.Append(",\"textureSize\":").Append(TextureSize);
        json.Append(",\"renderMs\":").Append(renderMilliseconds.ToString("0.###", CultureInfo.InvariantCulture));
        json.Append('}');

        string metadataPath = Path.Combine(CacheDirectory, "meta.json");
        string temporaryPath = metadataPath + ".tmp";
        File.WriteAllText(temporaryPath, json.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
        }

        File.Move(temporaryPath, metadataPath);
    }

    private void WriteStyleMetadata(MapStyle style, double renderMilliseconds)
    {
        var json = new StringBuilder(224);
        json.Append('{');
        json.Append("\"seed\":").Append(_seed.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"gameVersion\":").Append(JsonWriter.Quote(_gameVersion));
        json.Append(",\"rendererVersion\":").Append(
            RendererVersion.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"styleVersion\":").Append(
            MapStyleCompositor.StyleVersion.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"textureSize\":").Append(TextureSize.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"renderMs\":").Append(
            renderMilliseconds.ToString("0.###", CultureInfo.InvariantCulture));
        json.Append('}');

        string metadataPath = StyleMetadataPath(style);
        string temporaryPath = metadataPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            json.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
        }

        File.Move(temporaryPath, metadataPath);
    }

    private void DeleteTemporaryMetadata()
    {
        try
        {
            string temporaryPath = Path.Combine(CacheDirectory, "meta.json.tmp");
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch
        {
            // Cleanup is best effort while the server is shutting down or reporting a render failure.
        }
    }

    private void DeleteTemporaryStyleMetadata(MapStyle style)
    {
        try
        {
            DeleteFile(StyleMetadataPath(style) + ".tmp");
        }
        catch
        {
            // Cleanup is best effort while the server is shutting down or reporting a render failure.
        }
    }

    private string StyleMetadataPath(MapStyle style)
    {
        return Path.Combine(CacheDirectory, $"meta-{MapStyles.Token(style)}.json");
    }

    private StyleRenderStatus GetStyleStatus(MapStyle style)
    {
        switch (style)
        {
            case MapStyle.Topo:
                return _topoStatus;
            case MapStyle.Chart:
                return _chartStatus;
            default:
                throw new ArgumentOutOfRangeException(nameof(style));
        }
    }

    private void ThrowIfStopping()
    {
        if (_stopRequested)
        {
            throw new OperationCanceledException();
        }
    }

    private bool IsStopping()
    {
        return _stopRequested;
    }

    private static int CalculateMaximumZoom(int textureSize)
    {
        int zoom = 0;
        for (int size = textureSize; size > TilePyramid.TileSize; size /= 2)
        {
            zoom++;
        }

        return zoom;
    }

    private static string SanitizeWorldName(string worldName)
    {
        var sanitized = new StringBuilder(Math.Min(worldName.Length, 80));
        for (int index = 0; index < worldName.Length && sanitized.Length < 80; index++)
        {
            char character = worldName[index];
            if (char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.')
            {
                sanitized.Append(character);
            }
            else
            {
                sanitized.Append('_');
            }
        }

        string result = sanitized.ToString().Trim();
        return string.IsNullOrEmpty(result) || result == "." || result == ".." ? "world" : result;
    }

    private static byte ShadeChannel(byte value, float shade)
    {
        return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value * shade)));
    }

    private sealed class StyleRenderStatus
    {
        public int State;
        public int CompletedRows;
        public string? Revision;
    }
}

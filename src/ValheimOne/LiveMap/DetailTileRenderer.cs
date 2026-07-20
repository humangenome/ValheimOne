using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;
using ValheimOne.Infrastructure;

namespace ValheimOne.LiveMap;

// Lazily renders deep-zoom tiles by re-sampling WorldGenerator at each tile's own
// resolution. A single worker thread drains an on-demand queue (HTTP requests,
// coalesced per tile) ahead of a low-priority pre-render queue for the first
// detail zoom. Finished tiles are cached on disk next to the pyramid tiles.
internal sealed class DetailTileRenderer
{
    private const int RequestTimeoutMilliseconds = 10000;
    private const float OceanMargin = 500f;
    private const long DetailTileCacheMaximumBytes = 512L * 1024L * 1024L;
    private const long DetailTileCacheEvictionTargetBytes = DetailTileCacheMaximumBytes * 9L / 10L;

    private readonly WorldGenerator _generator;
    private readonly string _cacheDirectory;
    private readonly int _textureSize;
    private readonly Func<bool> _isBaseReady;
    private readonly ModLogger _log;
    private readonly object _sync = new object();
    private readonly Queue<TileKey> _demandQueue = new Queue<TileKey>();
    private readonly Queue<TileKey> _prerenderQueue = new Queue<TileKey>();
    private readonly HashSet<TileKey> _pending = new HashSet<TileKey>();
    private readonly Dictionary<TileKey, ManualResetEventSlim> _waiters =
        new Dictionary<TileKey, ManualResetEventSlim>();
    private Thread? _worker;
    private volatile bool _stopping;
    private bool _prerenderQueued;
    private bool _detailCacheSizeInitialized;
    private long _detailCacheSizeBytes;

    public DetailTileRenderer(
        WorldGenerator generator,
        string cacheDirectory,
        int textureSize,
        int baseZoom,
        int maximumZoom,
        Func<bool> isBaseReady,
        ModLogger log)
    {
        _generator = generator;
        _cacheDirectory = cacheDirectory;
        _textureSize = textureSize;
        BaseZoom = baseZoom;
        MaximumZoom = maximumZoom;
        _isBaseReady = isBaseReady;
        _log = log;
    }

    public int BaseZoom { get; }

    public int MaximumZoom { get; }

    public void Start()
    {
        if (_worker != null)
        {
            return;
        }

        _stopping = false;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "ValheimOne.LiveMap.DetailTiles",
            Priority = System.Threading.ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    public void Stop()
    {
        _stopping = true;
        lock (_sync)
        {
            Monitor.PulseAll(_sync);
            foreach (ManualResetEventSlim waiter in _waiters.Values)
            {
                waiter.Set();
            }

            _waiters.Clear();
            _demandQueue.Clear();
            _prerenderQueue.Clear();
            _pending.Clear();
        }

        Thread? worker = _worker;
        if (worker != null && worker.IsAlive && !ReferenceEquals(Thread.CurrentThread, worker))
        {
            worker.Join();
        }

        _worker = null;
    }

    // Called from HTTP worker threads. Returns the path of a rendered tile, waiting
    // for the shared worker when the tile is not cached yet.
    public bool TryGetTile(int zoom, int x, int y, out string path)
    {
        path = TilePath(zoom, x, y);
        if (zoom <= BaseZoom || zoom > MaximumZoom)
        {
            return zoom <= BaseZoom && File.Exists(path);
        }

        if (File.Exists(path))
        {
            RefreshCacheRecency(path);
            return true;
        }

        if (IsAllOcean(zoom, x, y))
        {
            path = OceanTilePath();
            return File.Exists(path) || TryWriteOceanTile(path);
        }

        if (_stopping || !_isBaseReady())
        {
            return false;
        }

        var key = new TileKey(zoom, x, y);
        ManualResetEventSlim waiter;
        lock (_sync)
        {
            if (_stopping)
            {
                return false;
            }

            if (!_waiters.TryGetValue(key, out waiter!))
            {
                waiter = new ManualResetEventSlim(false);
                _waiters[key] = waiter;
            }

            if (_pending.Add(key))
            {
                _demandQueue.Enqueue(key);
                Monitor.PulseAll(_sync);
            }
        }

        waiter.Wait(RequestTimeoutMilliseconds);
        return File.Exists(path);
    }

    private void WorkerLoop()
    {
        while (!_stopping)
        {
            TileKey key;
            bool isDemand;
            lock (_sync)
            {
                while (!_stopping && _demandQueue.Count == 0 &&
                       (_prerenderQueue.Count == 0 || !_isBaseReady()))
                {
                    if (!_prerenderQueued && _isBaseReady())
                    {
                        break;
                    }

                    Monitor.Wait(_sync, 500);
                }

                if (_stopping)
                {
                    return;
                }

                if (!_prerenderQueued && _isBaseReady())
                {
                    QueuePrerenderLocked();
                    _prerenderQueued = true;
                }

                if (_demandQueue.Count > 0)
                {
                    key = _demandQueue.Dequeue();
                    isDemand = true;
                }
                else if (_prerenderQueue.Count > 0)
                {
                    key = _prerenderQueue.Dequeue();
                    isDemand = false;
                }
                else
                {
                    continue;
                }
            }

            try
            {
                EnsureDetailCacheSizeInitialized();
                string path = TilePath(key.Zoom, key.X, key.Y);
                if (!File.Exists(path))
                {
                    var stopwatch = Stopwatch.StartNew();
                    RenderTile(key.Zoom, key.X, key.Y, path);
                    stopwatch.Stop();
                    TrackNewDetailTile(path);
                    if (isDemand)
                    {
                        _log.Debug(
                            $"[LiveMap] detail tile {key.Zoom}/{key.X}-{key.Y} rendered in " +
                            $"{stopwatch.ElapsedMilliseconds}ms");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The server is stopping; leave the tile for the next request.
            }
            catch (Exception exception)
            {
                _log.Warning(
                    $"[LiveMap] detail tile {key.Zoom}/{key.X}-{key.Y} failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                lock (_sync)
                {
                    _pending.Remove(key);
                    if (_waiters.TryGetValue(key, out ManualResetEventSlim? waiter))
                    {
                        waiter.Set();
                        _waiters.Remove(key);
                    }
                }
            }
        }
    }

    private void QueuePrerenderLocked()
    {
        int zoom = BaseZoom + 1;
        if (zoom > MaximumZoom)
        {
            return;
        }

        int tilesAcross = 1 << zoom;
        int queued = 0;
        for (int y = 0; y < tilesAcross; y++)
        {
            for (int x = 0; x < tilesAcross; x++)
            {
                if (IsAllOcean(zoom, x, y) ||
                    File.Exists(TilePath(zoom, x, y)))
                {
                    continue;
                }

                _prerenderQueue.Enqueue(new TileKey(zoom, x, y));
                queued++;
            }
        }

        if (queued > 0)
        {
            _log.Info($"[LiveMap] pre-rendering {queued} zoom-{zoom} detail tiles in the background");
        }
    }

    private void RenderTile(int zoom, int x, int y, string path)
    {
        const int tileSize = TilePyramid.TileSize;
        const int sampleSize = tileSize + 2;
        float worldSpan = _textureSize * WorldMapRenderer.PixelSize;
        float tileSpan = worldSpan / (1 << zoom);
        float pixelSpan = tileSpan / tileSize;
        float originX = (-worldSpan / 2f) + (x * tileSpan);
        float originZ = (worldSpan / 2f) - (y * tileSpan);

        var heights = new float[sampleSize * sampleSize];
        var biomes = new Heightmap.Biome[sampleSize * sampleSize];
        var lavaMasks = new float[sampleSize * sampleSize];
        for (int sy = 0; sy < sampleSize; sy++)
        {
            ThrowIfStopping();
            float worldZ = originZ - ((sy - 1 + 0.5f) * pixelSpan);
            for (int sx = 0; sx < sampleSize; sx++)
            {
                float worldX = originX + ((sx - 1 + 0.5f) * pixelSpan);
                int index = (sy * sampleSize) + sx;

                // Match the base render's world-edge clamp: beyond the playable
                // circle the generator returns garbage biomes, so sample flat
                // deep ocean instead (also skips relief via the height gate).
                if (MapShading.EdgeOceanFactor(worldX, worldZ) >= 1f)
                {
                    biomes[index] = Heightmap.Biome.Ocean;
                    heights[index] = -100f;
                    lavaMasks[index] = 0f;
                    continue;
                }

                Heightmap.Biome biome = _generator.GetBiome(worldX, worldZ);
                float height = _generator.GetBiomeHeight(biome, worldX, worldZ, out Color mask);
                biomes[index] = biome;
                heights[index] = height;
                lavaMasks[index] = biome == Heightmap.Biome.AshLands ? mask.a : 0f;
            }
        }

        var pixels = new byte[tileSize * tileSize * 4];
        for (int py = 0; py < tileSize; py++)
        {
            ThrowIfStopping();
            float worldZ = originZ - ((py + 0.5f) * pixelSpan);
            for (int px = 0; px < tileSize; px++)
            {
                float worldX = originX + ((px + 0.5f) * pixelSpan);
                int sampleIndex = ((py + 1) * sampleSize) + px + 1;
                Heightmap.Biome biome = biomes[sampleIndex];
                float height = heights[sampleIndex];
                MapColor color = MapShading.Compose(
                    biome,
                    height,
                    lavaMasks[sampleIndex],
                    worldX,
                    worldZ,
                    pixelSpan);
                int offset = ((py * tileSize) + px) * 4;
                color.WriteRgba(pixels, offset);

                if (height >= MapShading.WaterLevel)
                {
                    ApplyReliefPixel(pixels, offset, heights, sampleSize, px + 1, py + 1, pixelSpan);
                }
            }
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        PngEncoder.WriteRgba(path, pixels, tileSize, tileSize, () => _stopping);
    }

    private void EnsureDetailCacheSizeInitialized()
    {
        if (_detailCacheSizeInitialized)
        {
            return;
        }

        long totalBytes = 0;
        foreach (string path in EnumerateDetailTilePaths())
        {
            try
            {
                totalBytes += new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // A stale file can disappear while the cache is being inspected.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep rendering even if an individual stale cache file is inaccessible.
            }
        }

        _detailCacheSizeBytes = totalBytes;
        _detailCacheSizeInitialized = true;
    }

    private void TrackNewDetailTile(string path)
    {
        _detailCacheSizeBytes += new FileInfo(path).Length;
        if (_detailCacheSizeBytes > DetailTileCacheMaximumBytes)
        {
            EvictDetailTileCache();
        }
    }

    private void EvictDetailTileCache()
    {
        var tiles = new List<CachedTile>();
        long totalBytes = 0;
        foreach (string path in EnumerateDetailTilePaths())
        {
            try
            {
                var file = new FileInfo(path);
                long size = file.Length;
                tiles.Add(new CachedTile(path, size, file.LastWriteTimeUtc));
                totalBytes += size;
            }
            catch (IOException)
            {
                // The cache is best effort; skip files that disappear during the scan.
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible file cannot be safely evicted.
            }
        }

        _detailCacheSizeBytes = totalBytes;
        tiles.Sort(static (left, right) =>
        {
            int comparison = left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc);
            return comparison != 0
                ? comparison
                : string.CompareOrdinal(left.Path, right.Path);
        });

        int evictedTiles = 0;
        long evictedBytes = 0;
        foreach (CachedTile tile in tiles)
        {
            if (_detailCacheSizeBytes <= DetailTileCacheEvictionTargetBytes)
            {
                break;
            }

            try
            {
                File.Delete(tile.Path);
                _detailCacheSizeBytes -= tile.Size;
                evictedTiles++;
                evictedBytes += tile.Size;
            }
            catch (IOException)
            {
                // Leave failed deletions in the running total.
            }
            catch (UnauthorizedAccessException)
            {
                // Leave failed deletions in the running total.
            }
        }

        _log.Info(
            $"[LiveMap] detail tile cache evicted {evictedTiles} tiles " +
            $"({evictedBytes.ToString(CultureInfo.InvariantCulture)} bytes)");
    }

    private IEnumerable<string> EnumerateDetailTilePaths()
    {
        string tilesDirectory = Path.Combine(_cacheDirectory, "tiles");
        if (!Directory.Exists(tilesDirectory))
        {
            yield break;
        }

        foreach (string zoomDirectory in Directory.EnumerateDirectories(tilesDirectory))
        {
            string zoomName = Path.GetFileName(zoomDirectory);
            if (!int.TryParse(
                    zoomName,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int zoom) ||
                zoom <= BaseZoom)
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(
                         zoomDirectory,
                         "*.png",
                         SearchOption.TopDirectoryOnly))
            {
                yield return path;
            }
        }
    }

    private static void RefreshCacheRecency(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // Cache recency updates are best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Cache recency updates are best effort.
        }
    }

    private static void ApplyReliefPixel(
        byte[] pixels,
        int offset,
        float[] heights,
        int sampleSize,
        int sx,
        int sy,
        float pixelSpan)
    {
        const float lightX = -0.5298129f;
        const float lightY = 0.6622662f;
        const float lightZ = -0.5298129f;
        float slopeX = (heights[(sy * sampleSize) + sx + 1] -
                        heights[(sy * sampleSize) + sx - 1]) / (2f * pixelSpan);
        float slopeZ = (heights[((sy - 1) * sampleSize) + sx] -
                        heights[((sy + 1) * sampleSize) + sx]) / (2f * pixelSpan);
        float normalX = -slopeX;
        float normalY = 1f;
        float normalZ = -slopeZ;
        float inverseLength = 1f / (float)Math.Sqrt(
            (normalX * normalX) + (normalY * normalY) + (normalZ * normalZ));
        float light = ((normalX * lightX) + (normalY * lightY) + (normalZ * lightZ)) * inverseLength;
        float shade = Math.Max(0.75f, Math.Min(1.25f, 1f + ((light - lightY) * 0.55f)));
        pixels[offset] = ShadeChannel(pixels[offset], shade);
        pixels[offset + 1] = ShadeChannel(pixels[offset + 1], shade);
        pixels[offset + 2] = ShadeChannel(pixels[offset + 2], shade);
    }

    private bool IsAllOcean(int zoom, int x, int y)
    {
        float worldSpan = _textureSize * WorldMapRenderer.PixelSize;
        float tileSpan = worldSpan / (1 << zoom);
        float minX = (-worldSpan / 2f) + (x * tileSpan);
        float maxX = minX + tileSpan;
        float maxZ = (worldSpan / 2f) - (y * tileSpan);
        float minZ = maxZ - tileSpan;

        float nearestX = Math.Max(minX, Math.Min(0f, maxX));
        float nearestZ = Math.Max(minZ, Math.Min(0f, maxZ));
        float distance = (float)Math.Sqrt((nearestX * nearestX) + (nearestZ * nearestZ));
        return distance > WorldMapRenderer.WorldRadius + OceanMargin;
    }

    private static readonly object OceanTileLock = new object();

    private bool TryWriteOceanTile(string path)
    {
        lock (OceanTileLock)
        {
            if (File.Exists(path))
            {
                return true;
            }

            return TryWriteOceanTileLocked(path);
        }
    }

    private bool TryWriteOceanTileLocked(string path)
    {
        try
        {
            const int tileSize = TilePyramid.TileSize;
            var pixels = new byte[tileSize * tileSize * 4];
            MapColor ocean = MapShading.Compose(
                Heightmap.Biome.Ocean,
                -100f,
                0f,
                WorldMapRenderer.WorldRadius + (2f * OceanMargin),
                0f,
                WorldMapRenderer.PixelSize);
            for (int index = 0; index < tileSize * tileSize; index++)
            {
                ocean.WriteRgba(pixels, index * 4);
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            PngEncoder.WriteRgba(path, pixels, tileSize, tileSize, () => _stopping);
            return File.Exists(path);
        }
        catch (Exception exception)
        {
            _log.Warning(
                $"[LiveMap] shared ocean tile failed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private string TilePath(int zoom, int x, int y)
    {
        return Path.Combine(
            _cacheDirectory,
            "tiles",
            zoom.ToString(CultureInfo.InvariantCulture),
            $"{x.ToString(CultureInfo.InvariantCulture)}-{y.ToString(CultureInfo.InvariantCulture)}.png");
    }

    private string OceanTilePath()
    {
        return Path.Combine(_cacheDirectory, "tiles", "ocean.png");
    }

    private void ThrowIfStopping()
    {
        if (_stopping)
        {
            throw new OperationCanceledException();
        }
    }

    private static byte ShadeChannel(byte value, float shade)
    {
        return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value * shade)));
    }

    private readonly struct TileKey : IEquatable<TileKey>
    {
        public TileKey(int zoom, int x, int y)
        {
            Zoom = zoom;
            X = x;
            Y = y;
        }

        public int Zoom { get; }

        public int X { get; }

        public int Y { get; }

        public bool Equals(TileKey other)
        {
            return Zoom == other.Zoom && X == other.X && Y == other.Y;
        }

        public override bool Equals(object? obj)
        {
            return obj is TileKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((Zoom * 397) ^ (X * 31) ^ Y);
        }
    }

    private readonly struct CachedTile
    {
        public CachedTile(string path, long size, DateTime lastWriteTimeUtc)
        {
            Path = path;
            Size = size;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public string Path { get; }

        public long Size { get; }

        public DateTime LastWriteTimeUtc { get; }
    }
}

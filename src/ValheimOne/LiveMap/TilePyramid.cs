using System;
using System.IO;

namespace ValheimOne.LiveMap;

internal static class TilePyramid
{
    public const int TileSize = 256;

    public static int Write(
        string rootDirectory,
        byte[] baseRgba,
        int textureSize,
        int maximumZoom,
        Func<bool> shouldStop)
    {
        string tilesDirectory = Path.Combine(rootDirectory, "tiles");
        Directory.CreateDirectory(tilesDirectory);

        int tileCount = 0;
        byte[] levelPixels = baseRgba;
        int levelSize = textureSize;
        for (int zoom = maximumZoom; zoom >= 0; zoom--)
        {
            if (shouldStop())
            {
                throw new OperationCanceledException();
            }

            tileCount += WriteLevel(
                tilesDirectory,
                zoom,
                levelPixels,
                levelSize,
                shouldStop);

            if (zoom > 0)
            {
                levelPixels = Downsample(levelPixels, levelSize, shouldStop);
                levelSize /= 2;
            }
        }

        return tileCount;
    }

    private static int WriteLevel(
        string tilesDirectory,
        int zoom,
        byte[] pixels,
        int levelSize,
        Func<bool> shouldStop)
    {
        string zoomDirectory = Path.Combine(tilesDirectory, zoom.ToString());
        Directory.CreateDirectory(zoomDirectory);

        int tilesAcross = levelSize / TileSize;
        var tile = new byte[TileSize * TileSize * 4];
        for (int tileY = 0; tileY < tilesAcross; tileY++)
        {
            for (int tileX = 0; tileX < tilesAcross; tileX++)
            {
                if (shouldStop())
                {
                    throw new OperationCanceledException();
                }

                for (int row = 0; row < TileSize; row++)
                {
                    int sourceOffset = (((tileY * TileSize) + row) * levelSize + (tileX * TileSize)) * 4;
                    int destinationOffset = row * TileSize * 4;
                    Buffer.BlockCopy(pixels, sourceOffset, tile, destinationOffset, TileSize * 4);
                }

                string tilePath = Path.Combine(zoomDirectory, $"{tileX}-{tileY}.png");
                PngEncoder.WriteRgba(tilePath, tile, TileSize, TileSize, shouldStop);
            }
        }

        return tilesAcross * tilesAcross;
    }

    private static byte[] Downsample(byte[] source, int sourceSize, Func<bool> shouldStop)
    {
        int destinationSize = sourceSize / 2;
        var destination = new byte[destinationSize * destinationSize * 4];
        for (int y = 0; y < destinationSize; y++)
        {
            if (shouldStop())
            {
                throw new OperationCanceledException();
            }

            for (int x = 0; x < destinationSize; x++)
            {
                int topLeft = ((y * 2 * sourceSize) + (x * 2)) * 4;
                int topRight = topLeft + 4;
                int bottomLeft = topLeft + (sourceSize * 4);
                int bottomRight = bottomLeft + 4;
                int destinationOffset = ((y * destinationSize) + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    int total = source[topLeft + channel] + source[topRight + channel] +
                                source[bottomLeft + channel] + source[bottomRight + channel];
                    destination[destinationOffset + channel] = (byte)((total + 2) / 4);
                }
            }
        }

        return destination;
    }
}

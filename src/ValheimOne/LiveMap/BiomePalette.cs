using System;

namespace ValheimOne.LiveMap;

internal readonly struct MapColor
{
    public MapColor(float red, float green, float blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    public float Red { get; }

    public float Green { get; }

    public float Blue { get; }

    public MapColor Multiply(float amount)
    {
        return new MapColor(
            Clamp01(Red * amount),
            Clamp01(Green * amount),
            Clamp01(Blue * amount));
    }

    public static MapColor Lerp(MapColor from, MapColor to, float amount)
    {
        amount = Clamp01(amount);
        return new MapColor(
            from.Red + ((to.Red - from.Red) * amount),
            from.Green + ((to.Green - from.Green) * amount),
            from.Blue + ((to.Blue - from.Blue) * amount));
    }

    public void WriteRgba(byte[] buffer, int offset)
    {
        buffer[offset] = ToByte(Red);
        buffer[offset + 1] = ToByte(Green);
        buffer[offset + 2] = ToByte(Blue);
        buffer[offset + 3] = 255;
    }

    private static float Clamp01(float value)
    {
        return Math.Max(0f, Math.Min(1f, value));
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Round(Clamp01(value) * 255f);
    }
}

internal static class BiomePalette
{
    private static readonly MapColor Meadows = new MapColor(0.573f, 0.655f, 0.361f);
    private static readonly MapColor BlackForest = new MapColor(0.420f, 0.455f, 0.247f);
    private static readonly MapColor Swamp = new MapColor(0.639f, 0.447f, 0.345f);
    private static readonly MapColor Plains = new MapColor(0.906f, 0.671f, 0.470f);
    private static readonly MapColor Ashlands = new MapColor(0.690f, 0.192f, 0.192f);
    private static readonly MapColor Mistlands = new MapColor(0.360f, 0.220f, 0.400f);
    private static readonly MapColor MountainLow = new MapColor(0.72f, 0.76f, 0.78f);
    private static readonly MapColor MountainHigh = new MapColor(0.96f, 0.97f, 0.98f);
    private static readonly MapColor Ocean = new MapColor(0.102f, 0.165f, 0.267f);
    private static readonly MapColor Unknown = new MapColor(0.42f, 0.45f, 0.40f);

    public static MapColor Get(Heightmap.Biome biome, float height)
    {
        switch (biome)
        {
            case Heightmap.Biome.Meadows:
                return Meadows;
            case Heightmap.Biome.BlackForest:
                return BlackForest;
            case Heightmap.Biome.Swamp:
                return Swamp;
            case Heightmap.Biome.Plains:
                return Plains;
            case Heightmap.Biome.AshLands:
                return Ashlands;
            case Heightmap.Biome.Mistlands:
                return Mistlands;
            case Heightmap.Biome.Mountain:
            case Heightmap.Biome.DeepNorth:
                return MapColor.Lerp(MountainLow, MountainHigh, (height - 55f) / 100f);
            case Heightmap.Biome.Ocean:
                return Ocean;
            default:
                return Unknown;
        }
    }
}

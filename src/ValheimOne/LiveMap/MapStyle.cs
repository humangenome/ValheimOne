using System;
using System.Globalization;
using System.IO;

namespace ValheimOne.LiveMap;

internal enum MapStyle
{
    Default,
    Topo,
    Chart,
}

internal static class MapStyles
{
    public static readonly MapStyle[] NonDefaultStyles =
    {
        MapStyle.Topo,
        MapStyle.Chart,
    };

    public static bool TryParse(string? token, out MapStyle style)
    {
        switch (token)
        {
            case null:
            case "":
            case "default":
                style = MapStyle.Default;
                return true;
            case "topo":
                style = MapStyle.Topo;
                return true;
            case "chart":
                style = MapStyle.Chart;
                return true;
            default:
                style = MapStyle.Default;
                return false;
        }
    }

    public static string Token(MapStyle style)
    {
        switch (style)
        {
            case MapStyle.Default:
                return "default";
            case MapStyle.Topo:
                return "topo";
            case MapStyle.Chart:
                return "chart";
            default:
                throw new ArgumentOutOfRangeException(nameof(style));
        }
    }

    public static string TilesDirectory(string cacheDirectory, MapStyle style)
    {
        return style == MapStyle.Default
            ? Path.Combine(cacheDirectory, "tiles")
            : Path.Combine(cacheDirectory, "tiles", Token(style));
    }

    public static string TilePath(string cacheDirectory, MapStyle style, int zoom, int x, int y)
    {
        return Path.Combine(
            TilesDirectory(cacheDirectory, style),
            zoom.ToString(CultureInfo.InvariantCulture),
            $"{x.ToString(CultureInfo.InvariantCulture)}-{y.ToString(CultureInfo.InvariantCulture)}.png");
    }

    public static string OceanTilePath(string cacheDirectory, MapStyle style)
    {
        return Path.Combine(TilesDirectory(cacheDirectory, style), "ocean.png");
    }
}

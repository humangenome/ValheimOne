using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValheimOne.Infrastructure;

// Where one world's own live-map data lives: web pins, the activity heatmap, the timelapse and the activity log
// (with its console history and offline-player list). Up to 0.13.25 these files sat in one folder for the whole
// server, so a server that switched to another world showed the old world's web pins at the same coordinates and
// mixed both worlds in its timelapse and heatmap. A dedicated server now keeps them in ValheimOne/worlds/<world>/,
// named the way the map cache already names its own per-world folder.
//
// The first world started on this version gets a COPY of the old files (they stay where they were, so going back
// to an older ValheimOne loses nothing), and worlds/legacy-copied.txt records which world that was. Every later
// world starts empty. A player's own game (not a dedicated server) keeps the old single folder. Shared on purpose:
// map/ (already per world), leaderboards.json (checked against the world seed) and the item catalog cache.
internal static class WorldDataDirectory
{
    public const string WorldsFolder = "worlds";
    public const string LegacyMarkerFile = "legacy-copied.txt";

    // The dedicated server's own world name when no -world argument is given (FejdStartup).
    public const string DefaultWorldName = "Dedicated";

    internal static readonly string[] LegacyFiles =
    {
        "webpins.json",
        "heatmap.json",
        "ghosts.json",
        "console-history.json",
    };

    internal const string LegacyActivityPattern = "activity-*.jsonl";
    internal const string LegacyTimelapseFolder = "timelapse";

    private const string StagingSuffix = ".copying";

    // The folder this process should use, decided once at plugin load from the command line.
    public static string Resolve(string root, ModLogger log)
    {
        if (!GameProcess.IsDedicatedServer)
        {
            return root;
        }

        string[] args;
        try
        {
            args = Environment.GetCommandLineArgs();
        }
        catch (Exception)
        {
            args = Array.Empty<string>();
        }

        return Resolve(root, ReadWorldName(args), log);
    }

    public static string Resolve(string root, string worldName, ModLogger? log)
    {
        string folderName = SanitizeWorldName(worldName);
        string worldsRoot = Path.Combine(root, WorldsFolder);
        string target = Path.Combine(worldsRoot, folderName);
        try
        {
            Directory.CreateDirectory(worldsRoot);
            string marker = Path.Combine(worldsRoot, LegacyMarkerFile);
            if (!File.Exists(marker))
            {
                int copied = 0;
                if (!Directory.Exists(target))
                {
                    copied = CopyLegacyData(root, target);
                }

                WriteMarker(marker, folderName, copied);
                if (copied > 0)
                {
                    log?.Info(
                        $"World data: copied {copied.ToString(CultureInfo.InvariantCulture)} file(s) of map pins, " +
                        $"heatmap, timelapse and activity into {WorldsFolder}/{folderName}; the old files stay where they were.");
                }
            }

            Directory.CreateDirectory(target);
            return target;
        }
        catch (Exception exception)
        {
            log?.Warning(
                $"World data: could not prepare {WorldsFolder}/{folderName} ({exception.GetType().Name}: " +
                $"{exception.Message}); this run keeps using the shared folder.");
            return root;
        }
    }

    // The value after -world (the game reads it case-insensitively and ignores an empty one).
    public static string ReadWorldName(string[] args)
    {
        string world = DefaultWorldName;
        if (args == null)
        {
            return world;
        }

        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], "-world", StringComparison.OrdinalIgnoreCase))
            {
                string value = args[index + 1] ?? string.Empty;
                if (value.Length > 0)
                {
                    world = value;
                }

                index++;
            }
        }

        return world;
    }

    // The same folder name the map cache uses for a world (letters, digits, '-', '_', '.'; at most 80 characters).
    public static string SanitizeWorldName(string worldName)
    {
        worldName ??= string.Empty;
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

    // Copies the old shared files into a staging folder and moves the finished folder into place, so a crash halfway
    // never leaves a half-filled world folder that would be taken as done. Returns the number of files copied.
    private static int CopyLegacyData(string root, string target)
    {
        string staging = target + StagingSuffix;
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        Directory.CreateDirectory(staging);
        int copied = 0;
        foreach (string name in LegacyFiles)
        {
            string source = Path.Combine(root, name);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(staging, name), false);
                copied++;
            }
        }

        if (Directory.Exists(root))
        {
            foreach (string source in Directory.GetFiles(root, LegacyActivityPattern))
            {
                File.Copy(source, Path.Combine(staging, Path.GetFileName(source)), false);
                copied++;
            }
        }

        string timelapse = Path.Combine(root, LegacyTimelapseFolder);
        if (Directory.Exists(timelapse))
        {
            copied += CopyDirectory(timelapse, Path.Combine(staging, LegacyTimelapseFolder));
        }

        Directory.Move(staging, target);
        return copied;
    }

    private static int CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        int copied = 0;
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
            copied++;
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            copied += CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        return copied;
    }

    private static void WriteMarker(string marker, string folderName, int copied)
    {
        string text =
            "world=" + folderName + "\n" +
            "copied_files=" + copied.ToString(CultureInfo.InvariantCulture) + "\n" +
            "utc=" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\n" +
            "note=The old files in the folder above were copied, not moved, into this world's folder.\n";
        string temporary = marker + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        if (File.Exists(marker))
        {
            File.Delete(temporary);
            return;
        }

        File.Move(temporary, marker);
    }
}

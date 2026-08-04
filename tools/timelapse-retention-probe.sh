#!/usr/bin/env bash
set -u

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
scratch_dir="$(mktemp -d)"
cleanup() {
    rm -rf -- "${scratch_dir}"
}
trap cleanup EXIT

cat > "${scratch_dir}/RetentionProbe.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>10</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="${project_root}/src/ValheimOne/LiveMap/TimelapseRetention.cs" Link="TimelapseRetention.cs" />
    <Compile Include="${project_root}/src/ValheimOne/LiveMap/TimelapseRecorder.cs" Link="TimelapseRecorder.cs" />
  </ItemGroup>
</Project>
EOF

cat > "${scratch_dir}/Shims.cs" <<'EOF'
namespace ValheimOne.Infrastructure
{
    public sealed class ModLogger
    {
        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warning(string message) { }

        public void Error(string message) { }
    }
}

namespace ValheimOne.LiveMap
{
    internal static class JsonWriter
    {
        public static string Quote(string? value) => value == null ? "null" : "\"" + value + "\"";
    }

    internal static class FogTracker
    {
        public const int Size = 512;
    }

    internal static class ActivityHeatmap
    {
        public const int GridSize = 128;
    }

    internal sealed class PlayerBaseEntry
    {
        public PlayerBaseEntry(string id, float x, float z, float radius, int pieces)
        {
            Id = id;
            X = x;
            Z = z;
            Radius = radius;
            Pieces = pieces;
        }

        public string Id { get; }

        public float X { get; }

        public float Z { get; }

        public float Radius { get; }

        public int Pieces { get; }
    }

    internal readonly struct ActivityHeatmapCell
    {
        public ActivityHeatmapCell(int x, int z, int count)
        {
            X = x;
            Z = z;
            Count = count;
        }

        public int X { get; }

        public int Z { get; }

        public int Count { get; }
    }

    internal readonly struct ActivityHeatmapHourSlice
    {
        public ActivityHeatmapHourSlice(long hourStartUnixMs, ActivityHeatmapCell[] cells)
        {
            HourStartUnixMs = hourStartUnixMs;
            Cells = cells;
        }

        public long HourStartUnixMs { get; }

        public ActivityHeatmapCell[] Cells { get; }
    }
}
EOF

cat > "${scratch_dir}/Program.cs" <<'EOF'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ValheimOne.Infrastructure;
using ValheimOne.LiveMap;

internal static class Program
{
    private const long HourMs = 60L * 60L * 1000L;
    private const long DayMs = 24L * HourMs;
    private const long NowMs = 2000000000000L;

    public static void Main()
    {
        FrameCap();
        DiskCap();
        AgeThinning();
        HardAge();
        NewestSurvives();
        Determinism();
        CapturePolicy();
        DiskEviction();
    }

    private static void FrameCap()
    {
        var frames = new List<TimelapseFrameInfo>();
        int count = TimelapseRetention.MaximumFrames + 250;
        long first = NowMs - (335L * HourMs);
        for (int index = 0; index < count; index++)
        {
            frames.Add(new TimelapseFrameInfo(first + (index * HourMs), 1024));
        }

        frames.Reverse();
        List<long> evictions = TimelapseRetention.SelectEvictions(frames, NowMs);
        List<TimelapseFrameInfo> survivors = Surviving(frames, evictions);
        Require(survivors.Count <= TimelapseRetention.MaximumFrames, "frame cap exceeded");
        RequireOldestFirst(frames, evictions);
        Console.WriteLine($"frame-cap survivors={survivors.Count} evicted={evictions.Count}");
    }

    private static void DiskCap()
    {
        var frames = new List<TimelapseFrameInfo>();
        for (int index = 0; index < 100; index++)
        {
            frames.Add(new TimelapseFrameInfo(NowMs - ((99L - index) * HourMs), 1024 * 1024));
        }

        List<long> evictions = TimelapseRetention.SelectEvictions(frames, NowMs);
        List<TimelapseFrameInfo> survivors = Surviving(frames, evictions);
        long bytes = survivors.Sum(frame => (long)frame.SizeBytes);
        Require(bytes <= TimelapseRetention.MaximumDiskBytes, "disk cap exceeded");
        RequireOldestFirst(frames, evictions);
        Console.WriteLine($"disk-cap survivors={survivors.Count} bytes={bytes} evicted={evictions.Count}");
    }

    private static void AgeThinning()
    {
        var frames = new List<TimelapseFrameInfo>();
        for (int hour = 60 * 24; hour >= 1; hour--)
        {
            frames.Add(new TimelapseFrameInfo(NowMs - (hour * HourMs), 1024));
        }

        List<long> evictions = TimelapseRetention.SelectEvictions(frames, NowMs);
        List<TimelapseFrameInfo> survivors = Surviving(frames, evictions);
        long hourlyCutoff = NowMs - (TimelapseRetention.HourlyRetentionDays * DayMs);
        int hourly = survivors.Count(frame => frame.UnixMs >= hourlyCutoff);
        int maximumDailyCount = survivors
            .Where(frame => frame.UnixMs < hourlyCutoff)
            .GroupBy(frame => FloorDay(frame.UnixMs))
            .Select(group => group.Count())
            .DefaultIfEmpty(0)
            .Max();
        Require(hourly > 300, "hourly window lost granularity");
        Require(maximumDailyCount <= 1, "older frames were not thinned daily");
        Console.WriteLine(
            $"age-thinning survivors={survivors.Count} hourly={hourly} older-daily-max={maximumDailyCount}");
    }

    private static void HardAge()
    {
        long ancient = NowMs - ((TimelapseRetention.DailyRetentionDays + 1L) * DayMs);
        var frames = new List<TimelapseFrameInfo>
        {
            new TimelapseFrameInfo(ancient, 1024),
            new TimelapseFrameInfo(NowMs, 1024),
        };
        List<long> evictions = TimelapseRetention.SelectEvictions(frames, NowMs);
        Require(evictions.Contains(ancient), "hard-age frame survived");
        Console.WriteLine($"hard-age survivors={frames.Count - evictions.Count} evicted={evictions.Count}");
    }

    private static void NewestSurvives()
    {
        var frames = new List<TimelapseFrameInfo>();
        long first = NowMs - (500L * DayMs);
        for (int index = 0; index < TimelapseRetention.MaximumFrames + 100; index++)
        {
            frames.Add(new TimelapseFrameInfo(first + (index * HourMs), 1024 * 1024));
        }

        long newest = frames.Max(frame => frame.UnixMs);
        List<long> evictions = TimelapseRetention.SelectEvictions(frames, NowMs);
        List<TimelapseFrameInfo> survivors = Surviving(frames, evictions);
        Require(survivors.Any(frame => frame.UnixMs == newest), "newest frame was evicted");
        Console.WriteLine(
            $"newest-survives survivors={survivors.Count} bytes={survivors.Sum(frame => (long)frame.SizeBytes)}");
    }

    private static void Determinism()
    {
        var frames = new List<TimelapseFrameInfo>();
        for (int index = 0; index < 1400; index++)
        {
            frames.Add(new TimelapseFrameInfo(NowMs - (index * HourMs), 40000));
        }

        frames = frames.OrderBy(frame => (frame.UnixMs * 17L) % 101L).ToList();
        List<long> first = TimelapseRetention.SelectEvictions(frames, NowMs);
        List<long> second = TimelapseRetention.SelectEvictions(frames, NowMs);
        Require(first.SequenceEqual(second), "eviction selection is not deterministic");
        Console.WriteLine($"determinism evictions={first.Count}");
    }

    private static void CapturePolicy()
    {
        long last = NowMs;
        Require(
            !TimelapseRetention.ShouldCapture(last, last + (4L * 60L * 1000L), 1, 100, 10),
            "five-minute hard floor was bypassed");
        Require(
            !TimelapseRetention.ShouldCapture(last, last + HourMs, 60, 0, 10),
            "static world captured a frame");
        Require(
            TimelapseRetention.ShouldCapture(last, last + HourMs, 60, 1, 10),
            "positive interval change did not capture");
        Require(
            TimelapseRetention.ShouldCapture(last, last + (5L * 60L * 1000L), 60, 10, 10),
            "minimum-change threshold did not capture after the hard floor");
        Console.WriteLine("capture-policy floorMinutes=5 static=false intervalPositive=true");
    }

    private static void DiskEviction()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "valheimone-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DiskEvictionFrameCap(Path.Combine(root, "frame-cap"));
            DiskEvictionDiskCap(Path.Combine(root, "disk-cap"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void DiskEvictionFrameCap(string dataDirectory)
    {
        string frameDirectory = Path.Combine(dataDirectory, "timelapse");
        Directory.CreateDirectory(frameDirectory);
        int seeded = TimelapseRetention.MaximumFrames + 17;
        long firstUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() -
            (seeded * 5L * 60L * 1000L);
        var timestamps = new List<long>(seeded);
        for (int index = 0; index < seeded; index++)
        {
            long unixMs = firstUnixMs + (index * 5L * 60L * 1000L);
            timestamps.Add(unixMs);
            WriteValidFrame(frameDirectory, unixMs, large: false);
        }

        string[] filesBefore = Directory.GetFiles(frameDirectory, "*.vof");
        using (var recorder = new TimelapseRecorder(dataDirectory, new ModLogger()))
        {
            Require(
                recorder.ListFrames().Length <= TimelapseRetention.MaximumFrames,
                "recorder index exceeded frame cap after disk eviction");
        }

        string[] filesAfter = Directory.GetFiles(frameDirectory, "*.vof");
        int deleted = filesBefore.Length - filesAfter.Length;
        Require(filesBefore.Length == seeded, "frame-cap seed count did not reach disk");
        Require(deleted > 0, "frame cap did not delete any files");
        Require(
            filesAfter.Length <= TimelapseRetention.MaximumFrames,
            "on-disk frame count exceeded frame cap");
        bool newestSurvived = File.Exists(FramePath(frameDirectory, timestamps[^1]));
        bool oldestFirst = RequireContiguousNewestSuffix(frameDirectory, timestamps, deleted);
        Require(newestSurvived, "newest frame did not survive frame-cap eviction");
        Require(oldestFirst, "frame-cap eviction was not oldest-first");
        Console.WriteLine(
            $"disk-eviction frame-cap: seeded={seeded} filesBefore={filesBefore.Length} " +
            $"filesAfter={filesAfter.Length} deleted={deleted} " +
            $"newestSurvived={newestSurvived.ToString().ToLowerInvariant()} " +
            $"oldestFirst={oldestFirst.ToString().ToLowerInvariant()}");
    }

    private static void DiskEvictionDiskCap(string dataDirectory)
    {
        string frameDirectory = Path.Combine(dataDirectory, "timelapse");
        Directory.CreateDirectory(frameDirectory);
        long firstUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (7L * DayMs);
        long bytesBefore = 0L;
        var timestamps = new List<long>();
        while (bytesBefore <= TimelapseRetention.MaximumDiskBytes + (1024L * 1024L))
        {
            Require(
                timestamps.Count < TimelapseRetention.MaximumFrames,
                "disk-cap seed unexpectedly reached the frame cap");
            long unixMs = firstUnixMs + (timestamps.Count * 5L * 60L * 1000L);
            timestamps.Add(unixMs);
            string path = WriteValidFrame(frameDirectory, unixMs, large: true);
            bytesBefore += new FileInfo(path).Length;
        }

        int filesBefore = Directory.GetFiles(frameDirectory, "*.vof").Length;
        Require(
            bytesBefore > TimelapseRetention.MaximumDiskBytes,
            "disk-cap seed did not exceed the byte limit");
        using (var recorder = new TimelapseRecorder(dataDirectory, new ModLogger()))
        {
            Require(
                recorder.TotalBytes <= TimelapseRetention.MaximumDiskBytes,
                "recorder index exceeded disk cap after eviction");
        }

        string[] filesAfter = Directory.GetFiles(frameDirectory, "*.vof");
        long bytesAfter = filesAfter.Sum(path => new FileInfo(path).Length);
        int deleted = filesBefore - filesAfter.Length;
        Require(deleted > 0, "disk cap did not delete any files");
        Require(
            bytesAfter <= TimelapseRetention.MaximumDiskBytes,
            "on-disk bytes exceeded disk cap");
        bool newestSurvived = File.Exists(FramePath(frameDirectory, timestamps[^1]));
        bool oldestFirst = RequireContiguousNewestSuffix(frameDirectory, timestamps, deleted);
        Require(newestSurvived, "newest frame did not survive disk-cap eviction");
        Require(oldestFirst, "disk-cap eviction was not oldest-first");
        Console.WriteLine(
            $"disk-eviction disk-cap: seededBytes={bytesBefore} bytesBefore={bytesBefore} " +
            $"bytesAfter={bytesAfter} limit={TimelapseRetention.MaximumDiskBytes} " +
            $"deleted={deleted}");
    }

    private static string WriteValidFrame(string directory, long unixMs, bool large)
    {
        byte[] payload = BuildFramePayload(large);
        string path = FramePath(directory, unixMs);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write(new byte[] { (byte)'V', (byte)'O', (byte)'T', (byte)'L' });
            writer.Write((ushort)1);
            writer.Write(unixMs);
            writer.Write(1);
            writer.Write(large ? FogTracker.Size * FogTracker.Size : 0);
            writer.Write(large ? 512 : 0);
            writer.Write(large ? 4096 : 0);
            writer.Write(large ? 4096 : 0);
            writer.Write(large ? 4096 : 0);
            writer.Write(0);
            writer.Write(large ? ActivityHeatmap.GridSize * ActivityHeatmap.GridSize : 0);
            writer.Write(payload.Length);
            writer.Write(payload);
        }

        return path;
    }

    private static byte[] BuildFramePayload(bool large)
    {
        using var uncompressed = new MemoryStream();
        using (var writer = new BinaryWriter(uncompressed, Encoding.UTF8, leaveOpen: true))
        {
            int fogBytes = (FogTracker.Size * FogTracker.Size) / 8;
            writer.Write(large
                ? Enumerable.Repeat((byte)0xff, fogBytes).ToArray()
                : new byte[fogBytes]);
            int baseCount = large ? 512 : 0;
            writer.Write(baseCount);
            for (int index = 0; index < baseCount; index++)
            {
                writer.Write((float)index);
                writer.Write((float)-index);
                writer.Write(1f);
                writer.Write(index);
            }

            WriteValidPoints(writer, large ? 4096 : 0);
            WriteValidPoints(writer, large ? 4096 : 0);
            WriteValidPoints(writer, large ? 4096 : 0);
            int movementCount = large ? ActivityHeatmap.GridSize * ActivityHeatmap.GridSize : 0;
            writer.Write(movementCount);
            for (int index = 0; index < movementCount; index++)
            {
                writer.Write(checked((ushort)index));
                writer.Write(index + 1);
            }

            writer.Write(0);
            writer.Write(0);
        }

        uncompressed.Position = 0L;
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(
                   compressed,
                   large ? CompressionLevel.NoCompression : CompressionLevel.Optimal,
                   leaveOpen: true))
        {
            uncompressed.CopyTo(deflate);
        }

        return compressed.ToArray();
    }

    private static void WriteValidPoints(BinaryWriter writer, int count)
    {
        writer.Write(count);
        for (int index = 0; index < count; index++)
        {
            writer.Write((float)index);
            writer.Write((float)-index);
        }
    }

    private static bool RequireContiguousNewestSuffix(
        string frameDirectory,
        List<long> timestamps,
        int deleted)
    {
        for (int index = 0; index < timestamps.Count; index++)
        {
            bool exists = File.Exists(FramePath(frameDirectory, timestamps[index]));
            if (index < deleted ? exists : !exists)
            {
                return false;
            }
        }

        return true;
    }

    private static string FramePath(string directory, long unixMs)
    {
        return Path.Combine(directory, unixMs + ".vof");
    }

    private static List<TimelapseFrameInfo> Surviving(
        List<TimelapseFrameInfo> frames,
        List<long> evictions)
    {
        var evicted = new HashSet<long>(evictions);
        return frames.Where(frame => !evicted.Contains(frame.UnixMs)).ToList();
    }

    private static void RequireOldestFirst(
        List<TimelapseFrameInfo> frames,
        List<long> evictions)
    {
        List<TimelapseFrameInfo> survivors = Surviving(frames, evictions);
        if (evictions.Count == 0 || survivors.Count == 0)
        {
            return;
        }

        Require(
            evictions.Max() < survivors.Min(frame => frame.UnixMs),
            "an eviction was newer than a survivor");
    }

    private static long FloorDay(long unixMs)
    {
        long day = unixMs / DayMs;
        return unixMs < 0L && unixMs % DayMs != 0L ? day - 1L : day;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
EOF

if dotnet run --project "${scratch_dir}/RetentionProbe.csproj" --configuration Release; then
    echo "RETENTION PASS"
else
    echo "RETENTION FAIL" >&2
    exit 1
fi

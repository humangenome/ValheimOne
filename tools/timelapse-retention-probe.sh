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
  </ItemGroup>
</Project>
EOF

cat > "${scratch_dir}/Program.cs" <<'EOF'
using System;
using System.Collections.Generic;
using System.Linq;
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

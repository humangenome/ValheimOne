using System;
using System.Collections.Generic;
using System.Globalization;

namespace ValheimOne.LiveMap;

internal readonly struct TimelapseFrameInfo
{
    public TimelapseFrameInfo(long unixMs, int sizeBytes)
    {
        UnixMs = unixMs;
        SizeBytes = sizeBytes;
    }

    public long UnixMs { get; }

    public int SizeBytes { get; }
}

internal static class TimelapseRetention
{
    public const int HourlyRetentionDays = 14;
    public const int DailyRetentionDays = 365;
    public const int MaximumFrames = 1024;
    public const int MaximumDiskBytes = 24 * 1024 * 1024;
    public const int MinimumFrameIntervalMinutes = 5;

    private const long DayMilliseconds = 24L * 60L * 60L * 1000L;

    public static List<long> SelectEvictions(
        IReadOnlyList<TimelapseFrameInfo> frames,
        long nowUnixMs)
    {
        if (frames == null)
        {
            throw new ArgumentNullException(nameof(frames));
        }

        var survivors = new List<TimelapseFrameInfo>(frames.Count);
        for (int index = 0; index < frames.Count; index++)
        {
            survivors.Add(frames[index]);
        }

        survivors.Sort(CompareFrames);
        var evictions = new HashSet<long>();
        if (survivors.Count <= 1)
        {
            return new List<long>();
        }

        long newestUnixMs = survivors[survivors.Count - 1].UnixMs;
        long dailyCutoffUnixMs = SaturatingSubtract(
            nowUnixMs,
            DailyRetentionDays * DayMilliseconds);
        for (int index = survivors.Count - 1; index >= 0; index--)
        {
            TimelapseFrameInfo frame = survivors[index];
            if (frame.UnixMs != newestUnixMs && frame.UnixMs < dailyCutoffUnixMs)
            {
                evictions.Add(frame.UnixMs);
                survivors.RemoveAt(index);
            }
        }

        long hourlyCutoffUnixMs = SaturatingSubtract(
            nowUnixMs,
            HourlyRetentionDays * DayMilliseconds);
        var newestByUtcDay = new Dictionary<long, long>();
        for (int index = survivors.Count - 1; index >= 0; index--)
        {
            TimelapseFrameInfo frame = survivors[index];
            if (frame.UnixMs >= hourlyCutoffUnixMs)
            {
                continue;
            }

            long utcDay = FloorToUtcDay(frame.UnixMs);
            if (!newestByUtcDay.ContainsKey(utcDay))
            {
                // Retain the newest frame in each UTC day so the daily point reflects
                // the most complete state reached during that day.
                newestByUtcDay.Add(utcDay, frame.UnixMs);
                continue;
            }

            if (frame.UnixMs != newestUnixMs)
            {
                evictions.Add(frame.UnixMs);
                survivors.RemoveAt(index);
            }
        }

        while (survivors.Count > MaximumFrames && survivors.Count > 1)
        {
            EvictOldest(survivors, evictions, newestUnixMs);
        }

        long survivingBytes = TotalBytes(survivors);
        while (survivingBytes > MaximumDiskBytes && survivors.Count > 1)
        {
            TimelapseFrameInfo removed = EvictOldest(
                survivors,
                evictions,
                newestUnixMs);
            survivingBytes -= Math.Max(0, removed.SizeBytes);
        }

        var result = new List<long>(evictions);
        result.Sort();
        return result;
    }

    public static bool ShouldCapture(
        long lastFrameUnixMs,
        long nowUnixMs,
        int captureIntervalMinutes,
        int changeScore,
        int minimumChangeScore)
    {
        if (changeScore <= 0)
        {
            return false;
        }

        if (lastFrameUnixMs <= 0L)
        {
            return true;
        }

        long elapsedMilliseconds = nowUnixMs - lastFrameUnixMs;
        long minimumIntervalMilliseconds =
            MinimumFrameIntervalMinutes * 60L * 1000L;
        if (elapsedMilliseconds < minimumIntervalMilliseconds)
        {
            return false;
        }

        int effectiveIntervalMinutes = Math.Max(
            MinimumFrameIntervalMinutes,
            captureIntervalMinutes);
        long captureIntervalMilliseconds = effectiveIntervalMinutes * 60L * 1000L;
        if (elapsedMilliseconds >= captureIntervalMilliseconds)
        {
            // Once the configured interval elapses, any real aggregate change earns a frame.
            return true;
        }

        return changeScore >= Math.Max(1, minimumChangeScore);
    }

    private static TimelapseFrameInfo EvictOldest(
        List<TimelapseFrameInfo> survivors,
        HashSet<long> evictions,
        long newestUnixMs)
    {
        for (int index = 0; index < survivors.Count; index++)
        {
            TimelapseFrameInfo frame = survivors[index];
            if (frame.UnixMs == newestUnixMs)
            {
                continue;
            }

            survivors.RemoveAt(index);
            evictions.Add(frame.UnixMs);
            return frame;
        }

        throw new InvalidOperationException(
            "Timelapse retention could not locate an evictable frame.");
    }

    private static long TotalBytes(List<TimelapseFrameInfo> frames)
    {
        long total = 0L;
        for (int index = 0; index < frames.Count; index++)
        {
            total += Math.Max(0, frames[index].SizeBytes);
        }

        return total;
    }

    private static int CompareFrames(TimelapseFrameInfo left, TimelapseFrameInfo right)
    {
        int timestamp = left.UnixMs.CompareTo(right.UnixMs);
        return timestamp != 0 ? timestamp : left.SizeBytes.CompareTo(right.SizeBytes);
    }

    private static long FloorToUtcDay(long unixMs)
    {
        long day = unixMs / DayMilliseconds;
        return unixMs < 0L && unixMs % DayMilliseconds != 0L ? day - 1L : day;
    }

    private static long SaturatingSubtract(long value, long amount)
    {
        return value < long.MinValue + amount ? long.MinValue : value - amount;
    }
}

using System;
using System.Diagnostics;
using UnityEngine;

namespace ValheimOne.Infrastructure;

// The server process's own numbers: when it started (the boot every feed and history entry is tied
// to) and its working set (all the memory it holds, not only the managed heap). Main thread only.
internal static class ProcessInfo
{
    private const float WorkingSetReadSeconds = 60f;

    private static readonly Lazy<long> Boot = new Lazy<long>(ReadBootUnixMs);
    private static Process? _self;
    private static float _nextWorkingSetRead = float.MinValue;
    private static long _workingSetBytes;

    public static long BootUnixMs => Boot.Value;

    // Read at most once a minute; 0 when the system does not report it.
    public static long WorkingSetBytes()
    {
        float now = Time.realtimeSinceStartup;
        if (now >= _nextWorkingSetRead)
        {
            _nextWorkingSetRead = now + WorkingSetReadSeconds;
            try
            {
                _self ??= Process.GetCurrentProcess();
                _self.Refresh();
                _workingSetBytes = Math.Max(0L, _self.WorkingSet64);
            }
            catch (Exception)
            {
                _workingSetBytes = 0L;
            }
        }

        return _workingSetBytes;
    }

    private static long ReadBootUnixMs()
    {
        try
        {
            DateTime started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            long unixMs = new DateTimeOffset(started, TimeSpan.Zero).ToUnixTimeMilliseconds();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (unixMs > 0L && unixMs <= now)
            {
                return unixMs;
            }
        }
        catch (Exception)
        {
            // Fall back to the first time anyone asked, which is plugin start.
        }

        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}

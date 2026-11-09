using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace ValheimOne.LiveMap;

// One pass over every object the world holds, a slice per frame on the main thread, each slice
// stopped by the clock. The entity layer and the object census both read the world through it, so
// a world of millions of objects costs the same small slice of each frame as an empty one; only
// the number of frames grows.
//
// Objects live in the game's sector table (one list per 64 m zone) and, for portals in zones that
// have not loaded yet, in separate portal lists. The walk reads every sector in order, then every
// portal list. Lists are read by index, never enumerated, so a list that changes between two
// slices is safe: an object that moves or appears during a pass may be missed or read twice.
internal sealed class ZdoWorldWalker
{
    public const double DefaultBudgetMilliseconds = 2d;

    // A frame longer than this means the server is already behind; the walk waits for a better one,
    // but never more than MaximumSkippedFrames in a row, so a busy server still finishes.
    public const float SlowFrameSeconds = 0.1f;
    public const int MaximumSkippedFrames = 10;

    private const int ObjectsPerClockRead = 256;
    private const int EmptySectorsPerClockRead = 1024;
    // Each portal is looked up in its sector's list, so the clock is read more often there.
    private const int PortalsPerClockRead = 8;
    private const int MaximumObjectsPerSlice = 100000;

    private static readonly double TicksPerMillisecond = Stopwatch.Frequency / 1000d;

    private readonly Stopwatch _clock = new Stopwatch();
    private readonly List<ZDO> _portalObjects = new List<ZDO>();
    private int _sectorIndex;
    private int _objectIndex;
    private int _portalIndex;
    private bool _portalPhase;
    private int _skippedInRow;

    public bool Active { get; private set; }

    public int SectorCount { get; private set; }

    public int NonEmptySectors { get; private set; }

    public long ObjectsRead { get; private set; }

    public int Slices { get; private set; }

    public int SkippedFrames { get; private set; }

    public double BusyMilliseconds { get; private set; }

    public double WorstSliceMilliseconds { get; private set; }

    public long StartedUnixMs { get; private set; }

    // 0..100 by sectors read (the portal lists are a small tail).
    public int Progress =>
        !Active ? 100 :
        SectorCount <= 0 ? 0 :
        (int)Math.Min(99L, (long)Math.Min(_sectorIndex, SectorCount) * 100L / SectorCount);

    public void Begin()
    {
        _sectorIndex = 0;
        _objectIndex = 0;
        _portalIndex = 0;
        _portalPhase = false;
        _portalObjects.Clear();
        _skippedInRow = 0;
        SectorCount = 0;
        NonEmptySectors = 0;
        ObjectsRead = 0L;
        Slices = 0;
        SkippedFrames = 0;
        BusyMilliseconds = 0d;
        WorstSliceMilliseconds = 0d;
        StartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Active = true;
    }

    public void Abandon()
    {
        Active = false;
        _portalObjects.Clear();
    }

    // Reads objects until the budget is spent or the pass ends. True when the pass is complete.
    // Throws only when the game's object tables cannot be read; the caller ends its pass then.
    public bool Step(ZDOMan manager, Action<ZDO> visit, double budgetMilliseconds)
    {
        if (!Active)
        {
            return true;
        }

        if (Time.unscaledDeltaTime > SlowFrameSeconds && _skippedInRow < MaximumSkippedFrames)
        {
            _skippedInRow++;
            SkippedFrames++;
            return false;
        }

        _skippedInRow = 0;
        List<ZDO>[] sectors = GameAccess.GetZdoSectors(manager) ??
                              throw new InvalidOperationException("the game's object sector table is unreadable");
        SectorCount = sectors.Length;
        long budgetTicks = (long)(Math.Max(0.05d, budgetMilliseconds) * TicksPerMillisecond);
        int readThisSlice = 0;
        int emptyThisSlice = 0;
        _clock.Restart();
        try
        {
            while (!_portalPhase)
            {
                if (_sectorIndex >= sectors.Length)
                {
                    StartPortalPhase(manager);
                    break;
                }

                List<ZDO>? sector = sectors[_sectorIndex];
                if (sector == null || _objectIndex >= sector.Count)
                {
                    if (sector != null && sector.Count > 0 && _objectIndex > 0)
                    {
                        NonEmptySectors++;
                    }

                    _sectorIndex++;
                    _objectIndex = 0;
                    if (++emptyThisSlice % EmptySectorsPerClockRead == 0 &&
                        _clock.ElapsedTicks >= budgetTicks)
                    {
                        return false;
                    }

                    continue;
                }

                while (_objectIndex < sector.Count)
                {
                    ZDO? zdo = sector[_objectIndex++];
                    if (zdo != null && zdo.IsValid())
                    {
                        visit(zdo);
                    }

                    ObjectsRead++;
                    readThisSlice++;
                    if (readThisSlice % ObjectsPerClockRead == 0 &&
                        (_clock.ElapsedTicks >= budgetTicks || readThisSlice >= MaximumObjectsPerSlice))
                    {
                        return false;
                    }
                }
            }

            // A portal built in this session sits in its sector and in the portal lists; one that
            // came from the save sits only in the portal lists. Each is read once.
            while (_portalIndex < _portalObjects.Count)
            {
                ZDO zdo = _portalObjects[_portalIndex++];
                if (zdo != null && zdo.IsValid() && !InSectorTable(sectors, zdo))
                {
                    visit(zdo);
                    ObjectsRead++;
                }

                if (++readThisSlice % PortalsPerClockRead == 0 &&
                    _clock.ElapsedTicks >= budgetTicks)
                {
                    return false;
                }
            }

            Active = false;
            _portalObjects.Clear();
            return true;
        }
        finally
        {
            _clock.Stop();
            double elapsed = _clock.ElapsedTicks / TicksPerMillisecond;
            Slices++;
            BusyMilliseconds += elapsed;
            if (elapsed > WorstSliceMilliseconds)
            {
                WorstSliceMilliseconds = elapsed;
            }
        }
    }

    private static bool InSectorTable(List<ZDO>[] sectors, ZDO zdo)
    {
        uint sector = zdo.GetSectorIndex().Sector;
        List<ZDO>? list = sector < (uint)sectors.Length ? sectors[sector] : null;
        return list != null && list.Contains(zdo);
    }

    private void StartPortalPhase(ZDOMan manager)
    {
        _portalPhase = true;
        _portalIndex = 0;
        _portalObjects.Clear();
        IDictionary? portalLists = GameAccess.GetPortalObjectLists(manager);
        if (portalLists == null)
        {
            return;
        }

        foreach (object? value in portalLists.Values)
        {
            if (value is List<ZDO> list)
            {
                _portalObjects.AddRange(list);
            }
        }
    }
}

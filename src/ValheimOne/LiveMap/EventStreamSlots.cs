using System.Threading;

namespace ValheimOne.LiveMap;

// Open live event streams, counted per view. Each stream holds a thread for as long as the
// browser keeps it open, so the total stays small, and each view has its own ceiling: before
// 0.13.26 one shared limit of 8 meant eight anonymous public-map tabs could take every slot
// and push the owner's own Admin map back to polling every endpoint every 2 seconds.
internal sealed class EventStreamSlots
{
    public const int AdminLimit = 6;
    public const int SharedLimit = 3;
    public const int PublicLimit = 2;

    private int _admin;
    private int _shared;
    private int _public;

    public static int LimitFor(EventStreamView view)
    {
        switch (view)
        {
            case EventStreamView.Admin:
                return AdminLimit;
            case EventStreamView.Shared:
                return SharedLimit;
            default:
                return PublicLimit;
        }
    }

    public int Count(EventStreamView view)
    {
        switch (view)
        {
            case EventStreamView.Admin:
                return Volatile.Read(ref _admin);
            case EventStreamView.Shared:
                return Volatile.Read(ref _shared);
            default:
                return Volatile.Read(ref _public);
        }
    }

    // True and one slot taken when this view is under its ceiling; every true needs one Release.
    public bool TryAcquire(EventStreamView view)
    {
        int limit = LimitFor(view);
        ref int counter = ref Counter(view);
        while (true)
        {
            int current = Volatile.Read(ref counter);
            if (current >= limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void Release(EventStreamView view)
    {
        ref int counter = ref Counter(view);
        while (true)
        {
            int current = Volatile.Read(ref counter);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current)
            {
                return;
            }
        }
    }

    private ref int Counter(EventStreamView view)
    {
        switch (view)
        {
            case EventStreamView.Admin:
                return ref _admin;
            case EventStreamView.Shared:
                return ref _shared;
            default:
                return ref _public;
        }
    }
}

internal enum EventStreamView
{
    Admin,
    Shared,
    Public,
}

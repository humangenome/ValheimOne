using System;
using System.Collections.Generic;

namespace ValheimOne.Configuration;

// Which skipped synced values were already logged. The server sends its whole config again after every change, so
// without this a value the player's build cannot read would be logged again at every push. Bounded, so a server that
// keeps sending new unreadable values cannot grow it without end; past the bound only the count in the summary line
// still says how many were skipped.
internal sealed class OverlaySkipLog
{
    internal const int MaxRemembered = 256;

    private readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

    // The descriptions in skipped that have not been logged before, in their order.
    public List<string> TakeNew(IEnumerable<string> skipped)
    {
        var fresh = new List<string>();
        foreach (string description in skipped)
        {
            if (_logged.Count >= MaxRemembered)
            {
                break;
            }

            if (_logged.Add(description))
            {
                fresh.Add(description);
            }
        }

        return fresh;
    }
}

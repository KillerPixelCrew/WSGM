using System.Threading;

namespace WSGM.Shell;

/// <summary>
///     Revision bookkeeping for one data flow's default-endpoint writes:
///     rapid selections each take a revision, only the newest may publish UI
///     state, and the flow counts as pending until that newest revision
///     completes. Pure, so the latest-wins rule is testable without Core Audio;
///     the writes themselves are additionally serialized by
///     <see cref="AudioEndpointSelection" />'s gate.
/// </summary>
internal struct EndpointSelectionTracker
{
    private int _requested;
    private int _completed;

    /// <summary>Claims the next revision for a new selection.</summary>
    /// <returns>The revision to carry through this selection's write and completion.</returns>
    internal int Begin()
    {
        return Interlocked.Increment(ref _requested);
    }

    /// <summary>Whether this revision is still the newest selection.</summary>
    /// <param name="revision">Revision returned by <see cref="Begin" /> for the write being checked.</param>
    /// <returns>True if no later selection has been requested; completion does not retire the revision.</returns>
    internal bool IsCurrent(int revision)
    {
        return revision == Volatile.Read(ref _requested);
    }

    /// <summary>
    ///     Whether a selection is still in flight — the refresh path must
    ///     not overwrite the user's choice with a stale default meanwhile.
    /// </summary>
    internal bool Pending =>
        Volatile.Read(ref _requested) != Volatile.Read(ref _completed);

    /// <summary>
    ///     Records this revision's write as finished. A stale revision is
    ///     ignored: the newer selection it lost to is still pending.
    /// </summary>
    /// <param name="revision">Revision whose write settled; an older revision cannot clear a newer pending selection.</param>
    internal void Complete(int revision)
    {
        if (IsCurrent(revision))
        {
            Volatile.Write(ref _completed, revision);
        }
    }
}

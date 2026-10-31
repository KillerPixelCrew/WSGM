using System.Threading;

namespace WSGM.Core;

/// <summary>Cross-bootstrap marker used by one-shot exit sources before lifetime shutdown.</summary>
internal static class ApplicationShutdownRequest
{
    private static int _reason;
    private static int _sessionEnding;

    /// <summary>Gets the highest-priority reason requested so far: uninstall, update, session end, then normal.</summary>
    internal static ApplicationShutdownReason Current => (ApplicationShutdownReason)Volatile.Read(ref _reason);

    /// <summary>Gets whether any caller reported Windows session termination, independently of reason priority.</summary>
    internal static bool SessionEnding => Volatile.Read(ref _sessionEnding) != 0;

    /// <summary>Gets the reason cleanup runs under: session end whenever Windows is ending the session, otherwise <see cref="Current" />.</summary>
    internal static ApplicationShutdownReason Effective =>
        SessionEnding ? ApplicationShutdownReason.SessionEnd : Current;

    /// <summary>Records an exit reason without downgrading a stronger existing request.</summary>
    /// <param name="reason">Requested shutdown policy; session end also sets the independent session-ending marker.</param>
    internal static void Request(ApplicationShutdownReason reason)
    {
        if (reason is ApplicationShutdownReason.SessionEnd)
        {
            Interlocked.Exchange(ref _sessionEnding, 1);
        }

        while (true)
        {
            var current = Volatile.Read(ref _reason);
            var currentReason = (ApplicationShutdownReason)current;
            if (PriorityFor(currentReason) >= PriorityFor(reason)
                || Interlocked.CompareExchange(ref _reason, (int)reason, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Clears process-wide exit markers for isolated policy checks; never call during a live shutdown.</summary>
    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _reason, (int)ApplicationShutdownReason.Normal);
        Interlocked.Exchange(ref _sessionEnding, 0);
    }

    private static int PriorityFor(ApplicationShutdownReason reason)
    {
        return reason switch
        {
            ApplicationShutdownReason.Uninstall => 3,
            ApplicationShutdownReason.Update => 2,
            ApplicationShutdownReason.SessionEnd => 1,
            _ => 0
        };
    }
}

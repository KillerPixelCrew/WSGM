using System;

namespace WSGM.Shell;

/// <summary>
///     Session-owned, consumer-aware running-application monitor. It polls only the event-maintained
///     bounded snapshot while observed and publishes global/unknown immediately on exit or failure.
/// </summary>
internal interface IRunningApplicationTargetSource
{
    /// <summary>The latest published immutable identity snapshot; reading it does not query Steam.</summary>
    RunningApplicationTargetSnapshot Current { get; }

    /// <summary>Raised on the publishing thread when projected snapshot content changes; subscribers must not block.</summary>
    event Action<RunningApplicationTargetSnapshot>? Changed;

    /// <summary>Keeps Steam observation active while at least one lease is held and Steam observation is enabled.</summary>
    /// <returns>An independent lease owned by the caller; dispose it when the consumer stops observing.</returns>
    IDisposable AcquireObservation();
}

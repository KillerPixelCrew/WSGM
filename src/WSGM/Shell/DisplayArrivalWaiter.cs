using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Reads which monitors the adapter can currently see.</summary>
internal interface IDisplayPresence
{
    /// <summary>Observes every monitor. May throw when the driver is mid-change.</summary>
    /// <returns>The current observation.</returns>
    DisplayArrangement Observe();
}

/// <summary>Tells a waiter that the display set may have changed.</summary>
internal interface IDisplayChangeSignal
{
    /// <summary>Waits for the next hint, the backstop delay, or cancellation.</summary>
    /// <param name="backstop">How long to wait when no hint arrives.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes on a hint or after the backstop.</returns>
    Task WaitForChangeAsync(TimeSpan backstop, CancellationToken cancellationToken);
}

/// <summary>Waits until every requested monitor is connected and the picture has stopped moving.
///
/// The reference setup puts a TV behind an HDMI switch, so the target does not exist in Windows at
/// all until the switch selects this PC, and how long that takes is up to a person and a piece of
/// consumer hardware. There is therefore no deadline here, only cancellation: the splash offers
/// Cancel and the user decides when to give up. A timeout would only ever fire on the honest case.
///
/// Arrival is not a single event. A monitor coming up behind a switch enumerates, disappears and
/// re-enumerates while the sink negotiates, so the waiter requires two identical observations a
/// settle apart before it reports the display present. The change hint is an optimisation; the
/// backstop poll is what makes the wait correct when no hint is delivered.</summary>
internal sealed class DisplayArrivalWaiter(
    IDisplayPresence presence,
    IDisplayChangeSignal signal,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    /// <summary>How long the observation must hold still before it is believed.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait for a hint before looking anyway. Not a deadline: the wait
    /// continues afterwards. It exists because a display can appear without any broadcast
    /// reaching this process.</summary>
    internal static readonly TimeSpan Backstop = TimeSpan.FromSeconds(5);

    /// <summary>Waits until every target is connected and two observations agree.</summary>
    /// <param name="targets">Monitors that must be present. An empty list returns at once.</param>
    /// <param name="cancellationToken">The only way this call ends other than success.</param>
    /// <returns>The settled observation.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    internal async Task<DisplayArrangement> WaitAsync(
        IReadOnlyList<DisplayTargetIdentity> targets, CancellationToken cancellationToken)
    {
        string? stable = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisplayArrangement? observed = TryObserve();
            if (observed is not null && Present(observed, targets))
            {
                if (stable == observed.Fingerprint) { return observed; }
                // First sighting, or the topology moved since the last one. Look again after the
                // settle rather than acting on a monitor that is still negotiating.
                stable = observed.Fingerprint;
                await delay(Settle, cancellationToken).ConfigureAwait(false);
                continue;
            }
            stable = null;
            await signal.WaitForChangeAsync(Backstop, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether every requested monitor is connected in this observation.</summary>
    /// <param name="arrangement">An observation.</param>
    /// <param name="targets">Monitors that must be present.</param>
    /// <returns>True when none are missing.</returns>
    internal static bool Present(
        DisplayArrangement arrangement, IReadOnlyList<DisplayTargetIdentity> targets) =>
        targets.All(target => arrangement.Targets.Any(
            observed => observed.Available && observed.Target.Matches(target)));

    /// <summary>Which of the requested monitors this observation cannot see.</summary>
    /// <param name="arrangement">An observation.</param>
    /// <param name="targets">Monitors that must be present.</param>
    /// <returns>The missing monitors, in the order they were requested.</returns>
    internal static IReadOnlyList<DisplayTargetIdentity> Missing(
        DisplayArrangement arrangement, IReadOnlyList<DisplayTargetIdentity> targets) =>
        [.. targets.Where(target => !arrangement.Targets.Any(
            observed => observed.Available && observed.Target.Matches(target)))];

    /// <summary>A query that throws is a driver mid-change, which is the state this waiter exists
    /// to sit through. It counts as "not settled", never as an error.</summary>
    private DisplayArrangement? TryObserve()
    {
        try { return presence.Observe(); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}

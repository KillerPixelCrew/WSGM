using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Reads which monitors the adapter can currently see.</summary>
internal interface IDisplayPresence
{
    /// <summary>Observes every monitor. May throw <see cref="Win32Exception" /> when the driver is mid-change.</summary>
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

/// <summary>
///     Waits without a deadline for requested monitors and two matching topology fingerprints 500 ms apart.
/// </summary>
/// <param name="presence">Synchronous topology reader; Win32 read failures count as unsettled observations.</param>
/// <param name="signal">Change hints with a five-second polling backstop when no hint arrives.</param>
/// <param name="delay">Cancellable delay used between candidate stable observations.</param>
/// <remarks>Call off the UI thread: observations are synchronous. Fingerprints exclude HDR, scaling and rotation.</remarks>
internal sealed class DisplayArrivalWaiter(
    IDisplayPresence presence,
    IDisplayChangeSignal signal,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    /// <summary>How long the observation must hold still before it is believed.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     How long to wait for a hint before looking anyway. Not a deadline: the wait
    ///     continues afterwards. It exists because a display can appear without any broadcast
    ///     reaching this process.
    /// </summary>
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(5);

    /// <summary>Waits until every target is connected and two observations agree.</summary>
    /// <param name="targets">Monitors that must be available. Even an empty list requires two stable observations.</param>
    /// <param name="cancellationToken">Ends the unbounded wait; unrelated reader or signal failures still propagate.</param>
    /// <returns>The settled observation.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    internal async Task<DisplayArrangement> WaitAsync(
        IReadOnlyList<DisplayTargetIdentity> targets, CancellationToken cancellationToken)
    {
        string? stable = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observed = TryObserve();
            if (observed is not null && Present(observed, targets))
            {
                if (stable == observed.Fingerprint)
                {
                    return observed;
                }

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
        DisplayArrangement arrangement, IReadOnlyList<DisplayTargetIdentity> targets)
    {
        return targets.All(target =>
            arrangement.Targets.Any(observed => observed.Available && observed.Target.Matches(target)));
    }

    /// <summary>Which of the requested monitors this observation cannot see.</summary>
    /// <param name="arrangement">An observation.</param>
    /// <param name="targets">Monitors that must be present.</param>
    /// <returns>The missing monitors, in the order they were requested.</returns>
    internal static IReadOnlyList<DisplayTargetIdentity> Missing(
        DisplayArrangement arrangement, IReadOnlyList<DisplayTargetIdentity> targets)
    {
        return
        [
            .. targets.Where(target =>
                !arrangement.Targets.Any(observed => observed.Available && observed.Target.Matches(target)))
        ];
    }

    /// <summary>
    ///     Converts a Win32 read failure into an unsettled observation; other failures propagate.
    /// </summary>
    private DisplayArrangement? TryObserve()
    {
        try
        {
            return presence.Observe();
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}

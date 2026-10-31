using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>Tells a waiter that the display set may have changed.</summary>
internal interface IDisplayChangeSignal
{
    /// <summary>Waits for the next hint, the backstop delay, or cancellation.</summary>
    /// <param name="backstop">How long to wait when no hint arrives.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes on a hint or after the backstop.</returns>
    Task WaitForChangeAsync(TimeSpan backstop, CancellationToken cancellationToken);
}

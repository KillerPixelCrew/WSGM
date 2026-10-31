using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Turns the hidden top-level window's display notifications into an awaitable hint.
///     The hint only ever shortens a wait. If the window could not be created, or Windows sends
///     nothing, every wait falls back to the backstop delay and the waiter still finds the display on
///     its next look.
/// </summary>
internal static class ShellDisplayChangeSignal
{
    /// <summary>Waits for the next hint, the backstop delay, or cancellation.</summary>
    /// <param name="window">
    ///     Borrowed display-notification window; null uses only the backstop delay. Each wait revokes its
    ///     own event handler.
    /// </param>
    /// <param name="backstop">How long to wait when no hint arrives.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes on a hint or after the backstop.</returns>
    internal static async Task WaitForChangeAsync(
        DisplayChangeWindow? window,
        TimeSpan backstop,
        CancellationToken cancellationToken)
    {
        if (window is null)
        {
            await Task.Delay(backstop, cancellationToken).ConfigureAwait(false);
            return;
        }

        TaskCompletionSource signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The backstop delay ends with the wait, so a hint does not leave a timer running behind it.
        using var backstopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.DisplaysChanged += OnChanged;
        try
        {
            await Task.WhenAny(signalled.Task, Task.Delay(backstop, backstopCancellation.Token))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            window.DisplaysChanged -= OnChanged;
            backstopCancellation.Cancel();
        }

        return;

        void OnChanged()
        {
            signalled.TrySetResult();
        }
    }
}

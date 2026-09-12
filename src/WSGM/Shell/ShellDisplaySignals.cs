using System;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Reads the live display topology.</summary>
internal sealed class ShellDisplayPresence : IDisplayPresence
{
    /// <inheritdoc />
    public DisplayArrangement Observe() => DisplayLayouts.Observe();
}

/// <summary>Turns the hidden top-level window's display notifications into an awaitable hint.
///
/// The hint only ever shortens a wait. If the window could not be created, or Windows sends
/// nothing, every wait falls back to the backstop delay and the waiter still finds the display on
/// its next look.</summary>
internal sealed class ShellDisplayChangeSignal(DisplayChangeWindow? window) : IDisplayChangeSignal
{
    /// <inheritdoc />
    public async Task WaitForChangeAsync(TimeSpan backstop, CancellationToken cancellationToken)
    {
        if (window is null)
        {
            await Task.Delay(backstop, cancellationToken).ConfigureAwait(false);
            return;
        }
        TaskCompletionSource signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() => signalled.TrySetResult();
        window.DisplaysChanged += OnChanged;
        try
        {
            await Task.WhenAny(signalled.Task, Task.Delay(backstop, cancellationToken))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            window.DisplaysChanged -= OnChanged;
        }
    }
}

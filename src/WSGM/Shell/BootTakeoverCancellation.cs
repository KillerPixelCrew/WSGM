using System;
using System.Threading;

namespace WSGM.Shell;

/// <summary>Coordinates the splash's desktop recovery request with the service-boot
/// takeover running on a worker thread. A request accepted while active is sticky;
/// after completion, the caller must use the ordinary desktop transition instead.</summary>
internal sealed class BootTakeoverCancellation : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private BootTakeoverState _state;

    /// <summary>Cancellation token observed at every reversible takeover boundary.</summary>
    internal CancellationToken Token => _source.Token;

    /// <summary>Whether the splash requested desktop mode while this takeover was active.</summary>
    internal bool DesktopRequested
    {
        get
        {
            lock (_gate)
            {
                return _state == BootTakeoverState.DesktopRequested;
            }
        }
    }

    /// <summary>Requests cancellation of the active takeover.</summary>
    /// <returns>True when this coordinator accepted the request; false when the
    /// takeover had already completed and the ordinary desktop transition owns it.</returns>
    internal bool RequestDesktop()
    {
        lock (_gate)
        {
            if (_state != BootTakeoverState.Active)
            {
                return false;
            }
            _state = BootTakeoverState.DesktopRequested;
            _source.Cancel();
            return true;
        }
    }

    /// <summary>Closes the cancellation window without overwriting an accepted request.</summary>
    internal void Complete()
    {
        lock (_gate)
        {
            if (_state == BootTakeoverState.Active)
            {
                _state = BootTakeoverState.Completed;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _source.Dispose();

    private enum BootTakeoverState
    {
        Active,
        DesktopRequested,
        Completed,
    }
}

using System;
using System.Threading;

namespace WSGM.PackagedLaunch;

/// <summary>Coordinates publication and retirement of one foreground pump.</summary>
public sealed class ForegroundPumpLifetime
{
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _stopping;
    private nint _window;

    /// <summary>Whether creation admission is closed.</summary>
    public bool Stopping
    {
        get
        {
            lock (_gate)
            {
                return _stopping;
            }
        }
    }

    /// <summary>The pump's published window.</summary>
    public nint Window
    {
        get
        {
            lock (_gate)
            {
                return _window;
            }
        }
    }

    /// <summary>Waits for creation or exit, or until the startup bound expires.</summary>
    /// <param name="timeout">The startup bound.</param>
    /// <remarks>A return does not establish success; inspect <see cref="Window" /> afterwards.</remarks>
    public void WaitReady(TimeSpan timeout)
    {
        _ready.Wait(timeout);
    }

    /// <summary>Signals creation or exit; safe after a failed join.</summary>
    public void SignalReady()
    {
        _ready.Set();
    }

    /// <summary>Publishes an owned window or retires a late creation.</summary>
    /// <param name="window">The created window.</param>
    /// <param name="destroy">Destroys it on the owning pump.</param>
    /// <returns>Whether the window was published.</returns>
    public bool Publish(nint window, Action<nint> destroy)
    {
        lock (_gate)
        {
            if (!_stopping)
            {
                _window = window;
                return true;
            }
        }

        destroy(window);
        return false;
    }

    /// <summary>Closes admission and returns the existing window for a close request.</summary>
    /// <returns>The published window, or zero while creation is incomplete.</returns>
    public nint RequestStop()
    {
        lock (_gate)
        {
            _stopping = true;
            return _window;
        }
    }

    /// <summary>Clears the window after owner cleanup.</summary>
    public void Retired()
    {
        lock (_gate)
        {
            _window = 0;
        }
    }

    /// <summary>Releases the signal only after a confirmed join.</summary>
    /// <param name="joined">Whether the pump is known to have exited.</param>
    /// <remarks>Call once after the join attempt; a failed join retains the signal for the surviving pump.</remarks>
    public void CompleteJoin(bool joined)
    {
        if (joined)
        {
            _ready.Dispose();
        }
    }
}

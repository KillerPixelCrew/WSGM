using System;

namespace WSGM.Core;

/// <summary>The controller's one clock: when a frame was last presented, and which window was last judged.</summary>
/// <remarks>
///     The gap since the last present is the stall signal. It is measured rather than read off one
///     sample because the telemetry itself disappears mid-stall: a long stall is first a window whose
///     age keeps growing and then no window at all. Single-threaded, like the controller that owns it.
/// </remarks>
internal sealed class AutoTdpPresentClock
{
    private double _elapsedMs;
    private bool _hasElapsed;
    private bool _hasWindow;
    private double _lastPresentElapsedMs;
    private uint _lastWindowEnd;
    private uint _lastWindowStart;

    /// <summary>Time since the last frame was presented, as of the last <see cref="Advance" />.</summary>
    internal double GapMs => _elapsedMs - _lastPresentElapsedMs;

    /// <summary>Forgets the clock and the last judged window, so the next tick starts both afresh.</summary>
    internal void Restart()
    {
        _hasWindow = false;
        _hasElapsed = false;
    }

    /// <summary>Forgets the last judged window, so the next one counts as fresh.</summary>
    internal void ForgetWindow()
    {
        _hasWindow = false;
    }

    /// <summary>Moves the clock to this tick and notes when its window says a frame was last presented.</summary>
    /// <param name="observation">The tick.</param>
    internal void Advance(AutoTdpObservation observation)
    {
        if (!_hasElapsed)
        {
            _hasElapsed = true;
            _lastPresentElapsedMs = observation.ElapsedMs;
        }

        _elapsedMs = observation.ElapsedMs;
        if (observation.Window is { } window)
        {
            // When the last frame was presented, on the caller's clock. Taken from every read, not
            // only a fresh one, because a window whose age keeps growing is the stall itself.
            _lastPresentElapsedMs = Math.Max(_lastPresentElapsedMs, observation.ElapsedMs - window.AgeMs);
        }
    }

    /// <summary>Returns the window only when it is one the controller has not judged.</summary>
    /// <param name="observation">The tick.</param>
    /// <returns>The fresh window, or null for a repeated read or no renderer.</returns>
    internal AutoTdpWindow? TakeFresh(AutoTdpObservation observation)
    {
        if (observation.Window is not { } window)
        {
            return null;
        }

        if (_hasWindow && window.StartTicks == _lastWindowStart && window.EndTicks == _lastWindowEnd)
        {
            return null;
        }

        _hasWindow = true;
        _lastWindowStart = window.StartTicks;
        _lastWindowEnd = window.EndTicks;
        return window;
    }
}

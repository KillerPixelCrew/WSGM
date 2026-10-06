using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>A screen edge from which WSGM recognizes an inward swipe.</summary>
internal enum ScreenEdge
{
    /// <summary>The bottom edge of the primary display.</summary>
    Bottom,

    /// <summary>The right edge of the primary display.</summary>
    Right,

    /// <summary>The left edge of the primary display.</summary>
    Left,

    /// <summary>The top edge of the primary display.</summary>
    Top
}

/// <summary>
///     Turns inward swipes from enabled screen edges into <see cref="Triggered" />
///     events by subscribing to the process's raw touch input (<see cref="RawTouchInput" />,
///     WM_INPUT on a message-only window, RIDEV_INPUTSINK) and running each contact through
///     <see cref="EdgeSwipeRecognizer" />.
///     Purely observational: touch-screen input is registered without suppressing
///     legacy delivery. Nothing is consumed, and no window takes part in
///     hit-testing — the foreground game keeps receiving every event untouched.
///     Contact coordinates are parsed straight from the raw HID reports and scaled
///     from the digitizer's logical range to primary-screen physical pixels (the
///     built-in panel is assumed to be the primary display, as before).
///     Fallback knowledge if raw HID parsing ever fails on a device: a hit-testable
///     strip window (layered alpha 1, NOT 0 — fully transparent layered windows are
///     click-through) whose WM_NCHITTEST returns HTCLIENT only when
///     GetMessageExtraInfo() carries MI_WP_SIGNATURE ((extra &amp; 0xFFFFFF00) ==
///     0xFF515700, i.e. touch/pen-synthesized) and HTTRANSPARENT for real mouse.
/// </summary>
internal sealed class TouchSwipeMonitor : IDisposable
{
    private const int DiagnosticBandPx = 64;
    private const int DiagnosticLimitPerMinute = 12;

    private readonly IDisposable _subscription;
    private bool _armed = true;
    private bool _bottomEnabled;
    private bool _contactWasDown;
    private int _diagnosticCount;
    private bool _diagnosticPending;
    private ulong _diagnosticWindowAt;
    private int _dispatchPending;
    private bool _disposed;
    private int _horizontalBandPx;
    private bool _leftEnabled;
    private bool _rightEnabled;
    private int _screenH;
    private int _screenW;
    private uint _startRawX;
    private uint _startRawY;
    private long _startedAt;
    private bool _topEnabled;
    private EdgeSwipeRecognizer.GestureTrace _trace;
    private bool _tracking;
    private int _verticalBandPx;

    /// <summary>Creates a monitor on the UI thread and subscribes it to the shared raw touch input.</summary>
    public TouchSwipeMonitor()
    {
        _subscription = RawTouchInput.Subscribe(OnReport);
    }

    /// <summary>Stops monitoring; the last monitor's disposal ends the raw-input registration.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
    }

    /// <summary>Raised on the Avalonia UI thread with the edge that was swiped.</summary>
    public event Action<ScreenEdge>? Triggered;

    /// <summary>Applies the enabled-edge settings.</summary>
    /// <param name="gestures">The persisted gesture configuration to observe.</param>
    public void Configure(GestureConfig gestures)
    {
        _bottomEnabled = gestures.BottomEdge;
        _rightEnabled = gestures.RightEdgeSteamQuickAccess;
        _leftEnabled = gestures.LeftEdgeSteamMenu;
        _topEnabled = gestures.TopEdge;
        _tracking = false;
        _diagnosticPending = false;
        // Re-applied on every config reload, which the shell does often, so this restated an
        // unchanged gesture set 1,162 times in one session.
        Log.Change(
            "touch.edges",
            $"Touch edge swipes configured (bottom={_bottomEnabled}, top={_topEnabled}, " +
            $"left-steam={_leftEnabled}, right-qam={_rightEnabled}, start-band={EdgeSwipeRecognizer.StartBandMm}mm, " +
            $"entry={EdgeSwipeRecognizer.EntrySlopPx}px/{EdgeSwipeRecognizer.EntryWindowMs}ms net 1:1, " +
            $"travel={EdgeSwipeRecognizer.TriggerDistancePx}px/{EdgeSwipeRecognizer.TriggerTimeMs}ms net 2:1, " +
            "second contact cancels).");
    }

    /// <summary>Resume gesture detection (overlay closed).</summary>
    public void Arm()
    {
        if (_disposed || _armed)
        {
            return;
        }

        _armed = true;
        // Reset the one-shot so every arm cycle proves whether raw reports
        // still flow — swipes reportedly die when specific apps take focus.
        RawTouchInput.LogNextArrival();
        Log.Info($"Touch edge swipes armed (foreground {RawTouchInput.DescribeForeground()}).");
    }

    /// <summary>Suspend gesture detection (overlay open).</summary>
    public void Disarm()
    {
        if (!_armed)
        {
            return;
        }

        _armed = false;
        _tracking = false;
        _diagnosticPending = false;
        Log.Info("Touch edge swipes disarmed.");
    }

    private void OnReport(TouchContactReport report)
    {
        if (_disposed)
        {
            return;
        }

        var caps = report.Digitizer;
        if (!report.TipDown)
        {
            CompleteDiagnostic(caps, "released");
            _contactWasDown = false;
            _tracking = false;
            return;
        }

        if (!_contactWasDown)
        {
            _contactWasDown = true;
            OnContactDown(caps, report.RawX, report.RawY);
        }
        else if (report.Contacts == 1)
        {
            OnContactMove(caps, report.RawX, report.RawY);
        }

        // An edge swipe is one finger, as in Android and HHD. A second contact ends it until every
        // finger lifts, so a gripping thumb cannot take over the primary slot mid-gesture.
        if (report.Contacts > 1 && (_tracking || _diagnosticPending))
        {
            _trace.Cancel("second-contact");
            _tracking = false;
            CompleteDiagnostic(caps, "cancelled");
        }
    }

    private void OnContactDown(TouchDigitizer caps, uint rawX, uint rawY)
    {
        _tracking = false;
        _diagnosticPending = false;
        if (!_armed)
        {
            return;
        }

        _screenW = NativeMethods.GetSystemMetrics(0);
        _screenH = NativeMethods.GetSystemMetrics(1);
        var (x, y) = ScaleToScreen(caps, rawX, rawY);
        _horizontalBandPx = EdgeSwipeRecognizer.StartBandPx(caps.XSpanMm, _screenW);
        _verticalBandPx = EdgeSwipeRecognizer.StartBandPx(caps.YSpanMm, _screenH);

        // TickCount64 advances in 15.6 ms steps, too coarse for the entry window.
        _startedAt = Stopwatch.GetTimestamp();
        _startRawX = rawX;
        _startRawY = rawY;
        _trace = new EdgeSwipeRecognizer.GestureTrace(x, y, _screenW, _screenH, _horizontalBandPx, _verticalBandPx,
            _bottomEnabled, _rightEnabled, _leftEnabled, _topEnabled);
        _tracking = _trace.HasCandidates;
        // Verbose calibration includes the old title-bar region so rejected starts are visible.
        // One summary per contact, capped before formatting, never one line per HID sample.
        _diagnosticPending = Log.MinimumLevel <= LogLevel.Debug &&
                             ((_bottomEnabled && y >= _screenH - DiagnosticBandPx) ||
                              (_rightEnabled && x >= _screenW - DiagnosticBandPx) ||
                              (_leftEnabled && x < DiagnosticBandPx) ||
                              (_topEnabled && y < DiagnosticBandPx));
    }

    private void OnContactMove(TouchDigitizer caps, uint rawX, uint rawY)
    {
        if (!_tracking && !_diagnosticPending)
        {
            return;
        }

        if (!_armed)
        {
            _tracking = false;
            _diagnosticPending = false;
            return;
        }

        var (x, y) = ScaleToScreen(caps, rawX, rawY);
        var triggeredEdge = _trace.Move(x, y, ElapsedMs());
        _tracking = _trace.HasCandidates;
        if (triggeredEdge is null)
        {
            return;
        }

        _tracking = false;
        CompleteDiagnostic(caps, "triggered");
        if (Interlocked.Exchange(ref _dispatchPending, 1) != 0)
        {
            return;
        }

        var edge = triggeredEdge.Value;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _dispatchPending, 0);
            // Opening a surface can disarm the monitor after recognition but before this post runs.
            if (_disposed || !_armed)
            {
                return;
            }

            Log.Info($"{edge} touch edge swipe triggered.");
            Triggered?.Invoke(edge);
        });
    }

    private void CompleteDiagnostic(TouchDigitizer caps, string outcome)
    {
        if (!_diagnosticPending)
        {
            return;
        }

        _diagnosticPending = false;
        var now = (ulong)Environment.TickCount64;
        if (now - _diagnosticWindowAt >= 60_000)
        {
            _diagnosticWindowAt = now;
            _diagnosticCount = 0;
        }

        if (_diagnosticCount >= DiagnosticLimitPerMinute || Log.MinimumLevel > LogLevel.Debug)
        {
            return;
        }

        _diagnosticCount++;
        Log.Debug($"Touch edge trace: {outcome}, decision={_trace.Decision}, " +
                  $"first-raw={_startRawX},{_startRawY}, ranges={caps.XMin}..{caps.XMax}/{caps.YMin}..{caps.YMax}, " +
                  $"first-px={_trace.StartX},{_trace.StartY}, last-px={_trace.LastX},{_trace.LastY}, " +
                  $"screen={_screenW}x{_screenH}, start-band-x/y={_horizontalBandPx}/{_verticalBandPx}px, " +
                  $"elapsed={ElapsedMs()}ms, dwell-2px={_trace.FirstMovementMs?.ToString() ?? "none"}ms, " +
                  $"entry-{EdgeSwipeRecognizer.EntrySlopPx}px={_trace.EntryMs?.ToString() ?? "none"}ms, " +
                  $"path-x/y={_trace.HorizontalTravel}/{_trace.VerticalTravel}px.");
    }

    private ulong ElapsedMs()
    {
        return (ulong)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
    }

    private (int X, int Y) ScaleToScreen(TouchDigitizer caps, uint rawX, uint rawY)
    {
        return (EdgeSwipeRecognizer.ScaleToScreen(rawX, caps.XMin, caps.XMax, _screenW),
            EdgeSwipeRecognizer.ScaleToScreen(rawY, caps.YMin, caps.YMax, _screenH));
    }
}

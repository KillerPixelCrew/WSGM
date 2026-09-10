using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
/// Sends the machine back to sleep after a wake nothing accounts for, while the mode is switched on.
/// </summary>
/// <remarks>
/// The Modern Standby enhancement from #27, in the shape the Winhanced investigation found: WSGM
/// changes no power settings and arms no wake sources, so nothing global is left altered and there
/// is nothing to restore if this process dies. The machine wakes normally; this only decides
/// whether to put it back.
/// <para>
/// <see cref="ModernStandbyPolicy"/> owns every rule. This type owns the subscriptions, the timer
/// and the attempt count, and marshals its own state onto the dispatcher.
/// </para>
/// </remarks>
internal sealed class ModernStandbyGuard : IDisposable
{
    private readonly MessageWindow _messages;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _unattendedResume;
    private readonly Func<TimeSpan> _sinceWake;
    private readonly Func<TimeSpan> _sinceUserInput;
    private readonly Func<CancellationToken, Task> _suspend;
    private readonly TimeSpan _grace;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();

    // Lit until the console display tells us otherwise. A guard that assumed darkness before any
    // notification arrived could suspend a machine on its very first tick.
    private bool _displayOn = true;
    private int _attempts;
    private bool _suspending;
    private bool _disposed;

    /// <summary>Creates the guard and subscribes it to resume and display notifications.</summary>
    /// <param name="messages">The session's message window; not owned or disposed here.</param>
    /// <param name="enabled">Reads the current user setting on every look.</param>
    /// <param name="unattendedResume">Whether Windows attributes the last resume to the machine.</param>
    /// <param name="sinceWake">How long ago the machine woke.</param>
    /// <param name="sinceUserInput">How long ago Windows recorded user input.</param>
    /// <param name="suspend">Suspends the machine.</param>
    /// <param name="grace">How long an unexplained wake is allowed to settle.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    internal ModernStandbyGuard(
        MessageWindow messages,
        Func<bool> enabled,
        Func<bool>? unattendedResume = null,
        Func<TimeSpan>? sinceWake = null,
        Func<TimeSpan>? sinceUserInput = null,
        Func<CancellationToken, Task>? suspend = null,
        TimeSpan? grace = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(enabled);
        _messages = messages;
        _enabled = enabled;
        _unattendedResume = unattendedResume ?? ModernStandby.WasLastResumeUnattended;
        _sinceWake = sinceWake ?? (() => ModernStandby.ReadStandbyTiming().SinceWake);
        _sinceUserInput = sinceUserInput ?? ReadSinceUserInput;
        _suspend = suspend ?? (token => WindowsPower.SuspendAsync(hibernate: false, token));
        _grace = grace ?? ModernStandbyPolicy.DefaultGrace;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Evaluate();
        _messages.SystemResumed += OnSystemResumed;
        _messages.DisplayStateChanged += OnDisplayStateChanged;
    }

    /// <summary>The reason the most recent look reached, for diagnostics and tests.</summary>
    internal ModernStandbyOutcome LastOutcome { get; private set; } = ModernStandbyOutcome.Disabled;

    /// <summary>How many times the current wake has been slept through.</summary>
    internal int Attempts => _attempts;

    private void OnSystemResumed()
    {
        // SystemResumed fires for both PBT codes and can arrive twice for one resume, so the count
        // is reset from the wake itself rather than incremented per notification.
        _attempts = 0;
        Evaluate();
    }

    private void OnDisplayStateChanged(int state, DisplayStateSource source)
    {
        if (source == DisplayStateSource.LegacyMonitor)
        {
            // The legacy monitor notification describes the whole console, and Windows sends it
            // alongside the console state on some builds. The console value is the authority.
            return;
        }

        _displayOn = !DisplayMuteDecider.IsDisplayOff(state);
        if (!_displayOn)
        {
            // The screen going dark is the transition that can turn a refusal into a decision.
            Evaluate();
        }
    }

    private void Evaluate()
    {
        if (_disposed || _suspending)
        {
            return;
        }

        ModernStandbyDecision decision;
        try
        {
            decision = ModernStandbyPolicy.Decide(
                _enabled(),
                _unattendedResume(),
                _displayOn,
                _sinceWake(),
                _sinceUserInput(),
                _grace,
                _attempts);
        }
        catch (Exception ex)
        {
            // A guard that cannot read the machine leaves it awake. Staying on is recoverable by
            // the user; suspending on a state WSGM could not establish is not.
            _timer.Stop();
            LastOutcome = ModernStandbyOutcome.Disabled;
            Log.Warn($"Modern Standby guard: could not read wake state, leaving the machine awake: {ex.Message}");
            return;
        }

        LastOutcome = decision.Outcome;
        if (decision.ShouldKeepWatching)
        {
            _timer.Start();
            return;
        }

        _timer.Stop();
        if (!decision.ShouldResuspend)
        {
            return;
        }

        _attempts++;
        _suspending = true;
        Log.Change(
            "power.modern-standby.resuspend",
            $"Modern Standby guard: unexplained wake {_sinceWake().TotalSeconds:F0}s ago with the "
                + $"display off and no input; suspending again (attempt {_attempts} of "
                + $"{ModernStandbyPolicy.MaximumAttemptsPerWake}).");
        _ = SuspendAsync();
    }

    private async Task SuspendAsync()
    {
        try
        {
            await _suspend(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The session is going away; the machine's power state is no longer this guard's business.
        }
        catch (Exception ex)
        {
            Log.Warn($"Modern Standby guard: the suspend request failed: {ex.Message}");
        }
        finally
        {
            _suspending = false;
        }
    }

    private static TimeSpan ReadSinceUserInput()
    {
        NativeMethods.LastInputInfo info = new() { CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            // Unreadable input time reads as "somebody just touched it", which refuses the suspend.
            return TimeSpan.Zero;
        }

        // Both are 32-bit tick counts that wrap every 49.7 days; the unchecked subtraction is
        // correct across the wrap and the cast keeps it unsigned.
        uint elapsed = unchecked((uint)Environment.TickCount - info.DwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _messages.SystemResumed -= OnSystemResumed;
        _messages.DisplayStateChanged -= OnDisplayStateChanged;
        _timer.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

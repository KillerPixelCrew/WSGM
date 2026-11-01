using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Sends the machine back to sleep after a wake nothing accounts for, while the mode is switched on.
/// </summary>
/// <remarks>
///     The Modern Standby enhancement from #27, in the shape the Winhanced investigation found: WSGM
///     changes no power settings and arms no wake sources, so nothing global is left altered and there
///     is nothing to restore if this process dies. The machine wakes normally; this only decides
///     whether to put it back.
///     <para>
///         <see cref="ModernStandbyPolicy" /> owns every rule. This type owns the subscriptions, the timer
///         and the attempt count, and marshals its own state onto the dispatcher.
///     </para>
/// </remarks>
internal sealed class ModernStandbyGuard : IDisposable
{
    private readonly Func<bool> _enabled;
    private readonly Func<TimeSpan> _lastInputAge;
    private readonly Func<bool> _lastResumeUnattended;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly MessageWindow _messages;
    private readonly Func<TimeSpan> _sinceWake;
    private readonly Func<CancellationToken, Task> _suspend;
    private readonly DispatcherTimer _timer;

    // Lit until this session's display says otherwise. A guard that assumed darkness before any
    // notification arrived could suspend a machine on its very first tick.
    private bool _displayOn = true;
    private bool _disposed;
    private bool _suspending;

    /// <summary>Creates the guard and subscribes it to resume and display notifications.</summary>
    /// <param name="messages">The session's message window; not owned or disposed here.</param>
    /// <param name="enabled">Reads the current user setting on every look.</param>
    internal ModernStandbyGuard(MessageWindow messages, Func<bool> enabled)
        : this(messages, enabled, () => ModernStandby.ReadStandbyTiming().SinceWake,
            ModernStandby.WasLastResumeUnattended, LastInput.Age,
            token => WindowsPower.SuspendAsync(false, token))
    {
    }

    internal ModernStandbyGuard(MessageWindow messages, Func<bool> enabled,
        Func<TimeSpan> sinceWake, Func<bool> lastResumeUnattended,
        Func<TimeSpan> lastInputAge, Func<CancellationToken, Task> suspend)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(enabled);
        _messages = messages;
        _enabled = enabled;
        _sinceWake = sinceWake ?? throw new ArgumentNullException(nameof(sinceWake));
        _lastResumeUnattended = lastResumeUnattended ?? throw new ArgumentNullException(nameof(lastResumeUnattended));
        _lastInputAge = lastInputAge ?? throw new ArgumentNullException(nameof(lastInputAge));
        _suspend = suspend ?? throw new ArgumentNullException(nameof(suspend));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Evaluate();
        _messages.SystemResumed += OnSystemResumed;
        _messages.DisplayStateChanged += OnDisplayStateChanged;
        // The guard reads the display itself; it cannot rely on another feature having asked.
        _messages.RegisterDisplayStateNotifications();
    }

    /// <summary>How many times unattended wakes have been slept through since a person last woke it.</summary>
    private int Attempts { get; set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _messages.SystemResumed -= OnSystemResumed;
        _messages.DisplayStateChanged -= OnDisplayStateChanged;
        _messages.DeregisterDisplayStateNotifications();
        _timer.Stop();
        _lifetime.Cancel();
    }

    private void OnSystemResumed()
    {
        // A resume ends any suspend this guard asked for, even before that request's continuation runs.
        _suspending = false;
        Evaluate();
    }

    private void OnDisplayStateChanged(int state, DisplayStateSource source)
    {
        var off = DisplayPowerSignal.IsDisplayOff(state);
        if (off && !DisplayPowerSignal.MayReportDark(source))
        {
            // Only this session's display may report darkness; the other sources only report it lit.
            return;
        }

        _displayOn = !off;
        if (off)
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

        if (!_enabled())
        {
            _timer.Stop();
            return;
        }

        ModernStandbyDecision decision;
        TimeSpan sinceWake;
        try
        {
            sinceWake = _sinceWake();
            decision = ModernStandbyPolicy.Decide(
                true,
                _lastResumeUnattended(),
                _displayOn,
                sinceWake,
                _lastInputAge(),
                ModernStandbyPolicy.DefaultGrace,
                Attempts);
        }
        catch (Exception ex)
        {
            // A guard that cannot read the machine leaves it awake. Staying on is recoverable by
            // the user; suspending on a state WSGM could not establish is not.
            _timer.Stop();
            Log.Warn($"Modern Standby guard: could not read wake state, leaving the machine awake: {ex.Message}");
            return;
        }

        if (decision.Outcome is ModernStandbyOutcome.UserWoke)
        {
            // Only a person waking the machine starts a new count. The guard's own wakes and other
            // unattended ones keep counting, so MaximumAttemptsPerWake bounds a wake loop.
            Attempts = 0;
        }

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

        Attempts++;
        _suspending = true;
        Log.Change(
            "power.modern-standby.resuspend",
            $"Modern Standby guard: unexplained wake {sinceWake.TotalSeconds:F0}s ago with the "
            + $"display off and no input; suspending again (attempt {Attempts} of "
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
}

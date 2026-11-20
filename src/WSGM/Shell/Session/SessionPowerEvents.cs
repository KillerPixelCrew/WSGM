using System;
using System.Diagnostics;
using System.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Turns the message window's lock, sleep and power-source notifications into the session's
///     responses: the device cycle follows the session it belongs to, the Steam transport holds across
///     a sleep and reconnects after it, and a person waking the machine runs the wake actions.
/// </summary>
/// <param name="state">The session's live state.</param>
/// <param name="devicePower">The queue the device cycle's edges go through.</param>
/// <param name="steamUi">The Steam transport the sleep holds and releases.</param>
/// <param name="performance">The performance answers a sleep invalidates.</param>
/// <param name="integrations">The device a power-source change reaches.</param>
/// <param name="setPollingParked">Parks session fallback polls while either sleep signal is active.</param>
/// <param name="queueWakeActions">Queues the desktop wake action list.</param>
internal sealed class SessionPowerEvents(
    SessionState state,
    DevicePowerQueue devicePower,
    SteamUiServices steamUi,
    PerformanceServices performance,
    IntegrationServices integrations,
    Action<bool> setPollingParked,
    Action queueWakeActions)
{
    /// <summary>How close to a resume a suspend has to be before it is treated as its stale partner.</summary>
    private static readonly TimeSpan SpuriousSuspendWindow = TimeSpan.FromSeconds(2);

    /// <summary>Enough Stopwatch ticks that a session which never resumed reads as long awake.</summary>
    private static readonly long LongAgo = Stopwatch.Frequency * 3600;

    /// <summary>When the system last told this process it had resumed, on the monotonic clock.</summary>
    private long _systemResumeTimestamp = Stopwatch.GetTimestamp() - LongAgo;

    /// <summary>The same moment on the wall clock, which a sleep does not stop.</summary>
    private long _systemResumeWallTicks = DateTimeOffset.UtcNow.AddHours(-1).UtcTicks;

    /// <summary>One screensaver dismissal per physical wake, whichever signal arrives first.</summary>
    private readonly PhysicalWakePolicy _screensaverWake = new();

    /// <summary>Wake actions wait for an attended resume or a display that lights after going dark.</summary>
    private readonly PhysicalWakePolicy _desktopWake = new();

    /// <summary>
    ///     Follows the message window's notifications. The device cycle follows the session it belongs to:
    ///     without these the Claw's controller, motion, OEM and suppressor services stayed live across a
    ///     lock and a system sleep, and the fresh cycle generation the resume contract requires was never
    ///     established afterwards.
    /// </summary>
    /// <param name="messageWindow">The process's message window.</param>
    internal void Attach(MessageWindow messageWindow)
    {
        messageWindow.SessionLocked += OnSessionLocked;
        messageWindow.SessionUnlocked += OnSessionUnlocked;
        messageWindow.SystemSuspending += OnSystemSuspending;
        messageWindow.SystemResumed += OnSystemResumed;
        // Power preset assignments and per-source capability availability follow AC and battery.
        messageWindow.PowerSourceChanged += OnPowerSourceChanged;
        // A display that went dark and lit again is a wake even without a system sleep.
        messageWindow.DisplayStateChanged += OnDisplayStateChanged;
        messageWindow.RegisterDisplayStateNotifications();
    }

    /// <summary>Stops following the message window, as it is disposed.</summary>
    /// <param name="messageWindow">The process's message window.</param>
    internal void Detach(MessageWindow messageWindow)
    {
        messageWindow.SessionLocked -= OnSessionLocked;
        messageWindow.SessionUnlocked -= OnSessionUnlocked;
        messageWindow.SystemSuspending -= OnSystemSuspending;
        messageWindow.SystemResumed -= OnSystemResumed;
        messageWindow.PowerSourceChanged -= OnPowerSourceChanged;
        messageWindow.DisplayStateChanged -= OnDisplayStateChanged;
        messageWindow.DeregisterDisplayStateNotifications();
    }

    private bool _displaySleeping;
    private bool _systemSuspended;
    private bool _pollingParked;

    private void UpdatePolling()
    {
        var parked = _displaySleeping || _systemSuspended;
        if (_pollingParked == parked || state.ShutdownRequested)
        {
            return;
        }

        _pollingParked = parked;
        setPollingParked(parked);
        Log.Info($"Session background polling {(parked ? "parked" : "resumed")}.");
    }

    private void OnSessionLocked()
    {
        devicePower.Queue(true, "session locked");
    }

    private void OnSessionUnlocked()
    {
        devicePower.Queue(false, "session unlocked");
        steamUi.RequestHealthCheck();
    }

    private void OnSystemSuspending()
    {
        var monotonic = Stopwatch.GetElapsedTime(Interlocked.Read(ref _systemResumeTimestamp));
        var wall = DateTimeOffset.UtcNow
                   - new DateTimeOffset(Interlocked.Read(ref _systemResumeWallTicks), TimeSpan.Zero);
        if (IsStaleSuspend(monotonic, wall))
        {
            Log.Info(
                "Device cycle suspend skipped (system suspending): the system resumed "
                + $"{monotonic.TotalMilliseconds:F0} ms (monotonic) / {wall.TotalMilliseconds:F0} ms (wall) "
                + "ago, so this suspend belongs to a standby window that has already ended.");
            return;
        }

        // One line per sleep: both clocks are suspect across a hibernation, and the next wake has to
        // be readable from the log alone.
        Log.Info(
            $"Device cycle suspend accepted (system suspending): monotonic={monotonic.TotalMilliseconds:F0} ms, "
            + $"wall={wall.TotalMilliseconds:F0} ms since the last resume.");
        _screensaverWake.NoteSuspendAccepted();
        _desktopWake.NoteSuspendAccepted();
        _systemSuspended = true;
        UpdatePolling();
        steamUi.HoldForSuspend();
        devicePower.Queue(true, "system suspending", true);
    }

    private void OnSystemResumed()
    {
        Interlocked.Exchange(ref _systemResumeTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _systemResumeWallTicks, DateTimeOffset.UtcNow.UtcTicks);
        // Every resume, including the second of one modern standby wake: the settle restarts with it.
        _systemSuspended = false;
        UpdatePolling();
        steamUi.ReleaseAfterResume();
        devicePower.Queue(false, "system resumed", true);
        var unattended = ResumeWasUnattended();
        if (_desktopWake.NoteResume(unattended))
        {
            // A wake timer or maintenance wake has nobody in front of the screen; the wake list
            // (a TV, an HDMI switch) runs when a person wakes the machine, which sends another resume.
            Log.Info("Desktop wake actions signalled: system resumed.");
            queueWakeActions();
        }

        performance.RepairAfterResume();
        if (_screensaverWake.NoteResume(unattended))
        {
            steamUi.RequestScreensaverDismissal("system resumed", restoreGame: !unattended);
        }
    }

    private void OnDisplayStateChanged(int displayState, DisplayStateSource source)
    {
        if (!DisplayPowerSignal.IsDisplayOff(displayState))
        {
            _displaySleeping = false;
            UpdatePolling();
            steamUi.SetDisplaySleeping(false);
            devicePower.QueueDisplaySleep(sleeping: false, PowerActions.UsesModernStandby);
            performance.RecheckExtendedVrrWhenDisplayLit();
            if (_desktopWake.NoteDisplayOn())
            {
                // A classic desktop can wake its display before any system-resume or unlock event.
                // The lock suspended common plugins too. Resume that lane before the action list
                // joins its tail, so IR is running even while the Windows lock screen is showing.
                devicePower.Queue(false, "display lit");
                Log.Info("Desktop wake actions signalled: display lit.");
                queueWakeActions();
            }

            if (_screensaverWake.NoteDisplayOn())
            {
                steamUi.RequestScreensaverDismissal("display lit");
            }

            return;
        }

        // Only the session's own display may say the screen went dark.
        if (DisplayPowerSignal.MayReportDark(source))
        {
            // Modern Standby can freeze managed work before a system-suspend notification.
            // Return software-driven fans to firmware now; maintenance resumes keep this hold.
            steamUi.CaptureGameBeforeSleep();
            if (PowerActions.UsesModernStandby)
            {
                _displaySleeping = true;
                UpdatePolling();
                steamUi.SetDisplaySleeping(true);
            }

            devicePower.QueueDisplaySleep(sleeping: true, PowerActions.UsesModernStandby);
            _screensaverWake.NoteDisplayOff();
            _desktopWake.NoteDisplayOff();
        }
    }

    private void OnPowerSourceChanged()
    {
        if (!state.ShutdownRequested)
        {
            integrations.Device?.OnPowerSourceChanged();
        }
    }

    private static bool ResumeWasUnattended()
    {
        try
        {
            return ModernStandby.WasLastResumeUnattended();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Whether a suspend notification is the stale half of a wake that already happened.</summary>
    /// <param name="monotonic">Time since the last resume on the monotonic clock.</param>
    /// <param name="wall">Time since the last resume on the wall clock.</param>
    /// <returns><see langword="true" /> when the suspend must not be acted on.</returns>
    /// <remarks>
    ///     One modern standby wake delivers a resume, a suspend and a second resume within a few hundred
    ///     milliseconds; acting on the suspend in the middle tears the controller down on an awake
    ///     machine (docs\device-integration.md, "A modern standby wake must not quiesce the device").
    ///     A hibernation resume moves both clocks, forward or back, so a negative gap is discarded and
    ///     the shorter remaining gap decides.
    /// </remarks>
    internal static bool IsStaleSuspend(TimeSpan monotonic, TimeSpan wall)
    {
        TimeSpan? gap = null;
        foreach (var candidate in (ReadOnlySpan<TimeSpan>)[monotonic, wall])
        {
            if (candidate >= TimeSpan.Zero && (gap is null || candidate < gap))
            {
                gap = candidate;
            }
        }

        return gap < SpuriousSuspendWindow;
    }
}

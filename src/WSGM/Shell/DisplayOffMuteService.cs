using System;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Mutes system audio only while the screen is off and Steam is downloading.
/// It restores immediately when the screen comes back, or ten seconds after the last
/// active download stops.
///
/// <para>The problem this solves: keep-awake deliberately lets the display time out
/// while downloads continue (that is the whole point — Wi-Fi and Steam keep running on
/// a Modern-Standby handheld), but Steam plays a notification sound every time a
/// download finishes, into a dark room.</para>
///
/// <para>The signal is <c>GUID_SESSION_DISPLAY_STATUS</c> via
/// <see cref="MessageWindow.RegisterDisplayStateNotifications"/> — Microsoft documents
/// that setting as the one interactive user-mode apps must use (the console variant is
/// for services). It does fire when the Claw's screen times out under Modern Standby
/// (device-verified 2026-08-13); the <c>Display state:</c> log lines are what proves it.
/// </para>
///
/// <para><b>Coming back must not depend on one notification arriving, nor on the audio
/// endpoint being readable the instant it does</b> (reported 2026-08-19: a mute applied at
/// screen-off while downloading never came back). The wake side therefore listens on
/// everything Windows offers — there is no API to <i>query</i> display power state from
/// user mode, so notifications are the only mechanism — and every one of these restores:
/// </para>
/// <list type="bullet">
/// <item><description><c>GUID_SESSION_DISPLAY_STATUS</c>, the documented primary and the
/// only source allowed to report the screen going DARK.</description></item>
/// <item><description><c>GUID_CONSOLE_DISPLAY_STATE</c> and the superseded
/// <c>GUID_MONITOR_POWER_ON</c>, registered as redundant wake sources.</description></item>
/// <item><description>Session unlock (<c>WM_WTSSESSION_CHANGE</c>), for a wake that ends
/// at a sign-in prompt.</description></item>
/// <item><description><c>GetLastInputInfo</c> advancing past the baseline taken at mute
/// time. <b>This one has a known blind spot</b>: it tracks keyboard/mouse/touch, not
/// gamepads and not the power button, so a user who wakes with the power button and then
/// navigates only with a controller is not covered by it — the notifications above are.
/// </description></item>
/// </list>
///
/// <para>Two further rules keep a restore from being lost once it is triggered:</para>
/// <list type="number">
/// <item><description>The "we muted this" claim is cleared only after the unmute is
/// <i>confirmed</i>. The default endpoint is re-enumerated when the display wakes, so a
/// read or a toggle can fail on the first attempt; clearing the claim before attempting
/// anything stranded the mute permanently with nothing left to retry.</description></item>
/// <item><description>A failed attempt is retried on a short timer that runs only while
/// the claim is outstanding.</description></item>
/// </list>
///
/// <para>Only a mute WSGM itself applied is undone. If the user had already muted the
/// device before the screen went off, the screen coming back leaves it muted — the
/// alternative would silently unmute someone who muted on purpose.</para></summary>
public sealed class DisplayOffMuteService : IDisposable
{
    // Long enough to stay invisible next to a screen-off period measured in hours,
    // short enough that a user who wakes the device is not left in silence.
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(2);

    private readonly MessageWindow _window;
    private DispatcherTimer? _recovery;
    private DispatcherTimer? _downloadCompletionRestore;
    private bool _enabled;
    private bool _displayOff;
    private bool _downloadActive;
    private bool _mutedByUs;
    private bool _restorePending;
    private bool _subscribed;
    private bool _inputRecoveryLogged;
    private uint _inputBaseline;

    /// <summary>Creates the service over the process message window. Nothing is
    /// registered until <see cref="ApplyConfig"/> enables it.</summary>
    /// <param name="window">The process-wide message-only window.</param>
    public DisplayOffMuteService(MessageWindow window)
    {
        _window = window;
        // A WSGM that exits while the screen is dark would otherwise leave the device
        // muted with nothing left to unmute it — the display-on event needs this
        // process. Covers a normal exit and the installer's update handshake; a hard
        // kill cannot be covered, and the user's volume keys remain the way out.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
    }

    /// <summary>Turns the feature on or off, matching a reloaded configuration. Turning
    /// it off while the screen is dark restores the volume immediately, so a user cannot
    /// be left muted by a feature they just disabled.</summary>
    /// <param name="enabled">Whether muting on display-off is wanted.</param>
    public void ApplyConfig(bool enabled)
    {
        if (enabled == _enabled)
        {
            return;
        }
        _enabled = enabled;
        if (enabled)
        {
            if (!_subscribed)
            {
                _window.DisplayStateChanged += OnDisplayStateChanged;
                _window.SessionUnlocked += OnSessionUnlocked;
                _subscribed = true;
            }
            _window.RegisterDisplayStateNotifications();
            Log.Info("Mute on display off: enabled for active Steam downloads.");
            ReconcileMuteState();
            return;
        }
        // Notifications were not observed while disabled, so retaining a previous
        // dark value would let a later re-enable mute a display that has since woken.
        _displayOff = false;
        _window.DeregisterDisplayStateNotifications();
        StopDownloadCompletionRestore();
        Restore();
        Log.Info("Mute on display off: disabled.");
    }

    /// <summary>Applies the last usable activity answer from the shared Steam download
    /// poller. A false transition while the screen remains dark starts the ten-second
    /// restore grace; activity resuming cancels it.</summary>
    /// <param name="active">Whether Steam reports an active download.</param>
    public void SetDownloadActive(bool active)
    {
        if (active == _downloadActive)
        {
            return;
        }
        _downloadActive = active;
        Log.Info($"Mute on display off: Steam downloads {(active ? "active" : "inactive")}.");
        ReconcileMuteState();
    }

    private void OnDisplayStateChanged(int state, DisplayStateSource source)
    {
        // MONITOR_DISPLAY_STATE: 0 = off, 1 = on, 2 = dimmed. Dimmed is still lit and
        // still in front of the user, so it is deliberately not treated as off.
        var name = state switch
        {
            DisplayMuteDecider.DisplayOff => "off",
            DisplayMuteDecider.DisplayOn => "on",
            DisplayMuteDecider.DisplayDimmed => "dimmed",
            _ => $"unknown ({state})",
        };
        // The source is part of the line on purpose: when a wake is missed, which of the
        // three settings did and did not speak is the only thing that identifies it.
        Log.Info($"Display state: {name} (via {source}).");
        if (!_enabled)
        {
            return;
        }
        if (!DisplayMuteDecider.IsDisplayOff(state))
        {
            _displayOff = false;
            ReconcileMuteState();
            return;
        }
        if (DisplayMuteDecider.MayReportDark(source))
        {
            _displayOff = true;
            if (!_downloadActive)
            {
                Log.Info("Mute on display off: screen dark without an active Steam "
                    + "download, leaving audio unchanged.");
            }
            ReconcileMuteState();
        }
    }

    private void OnSessionUnlocked()
    {
        if (!_enabled)
        {
            return;
        }
        _displayOff = false;
        if (_mutedByUs)
        {
            Log.Info("Session unlocked while muted — restoring audio.");
        }
        ReconcileMuteState();
    }

    private void ReconcileMuteState()
    {
        var action = DisplayMuteDecider.Reconcile(
            _enabled,
            _displayOff,
            _downloadActive,
            _mutedByUs);
        if (action != DisplayMuteAction.DelayRestore)
        {
            StopDownloadCompletionRestore();
        }
        if (_enabled && _displayOff && _downloadActive)
        {
            // A new download that begins during a failed or delayed restore owns the
            // dark-screen condition again. Keep the mute and cancel recovery until
            // the condition becomes false once more.
            _restorePending = false;
        }
        switch (action)
        {
            case DisplayMuteAction.Mute:
                Mute();
                break;
            case DisplayMuteAction.Restore:
                Restore();
                break;
            case DisplayMuteAction.DelayRestore:
                ScheduleDownloadCompletionRestore();
                break;
        }
    }

    private void ScheduleDownloadCompletionRestore()
    {
        if (_downloadCompletionRestore is null)
        {
            _downloadCompletionRestore = new DispatcherTimer
            {
                Interval = DisplayMuteDecider.DownloadCompletionRestoreDelay,
            };
            _downloadCompletionRestore.Tick += (_, _) => OnDownloadCompletionRestore();
        }
        if (_downloadCompletionRestore.IsEnabled)
        {
            return;
        }
        Log.Info("Mute on display off: downloads inactive, waiting 10 s before unmute.");
        _downloadCompletionRestore.Start();
    }

    private void StopDownloadCompletionRestore()
        => _downloadCompletionRestore?.Stop();

    private void OnDownloadCompletionRestore()
    {
        StopDownloadCompletionRestore();
        if (DisplayMuteDecider.Reconcile(
                _enabled,
                _displayOff,
                _downloadActive,
                _mutedByUs) != DisplayMuteAction.DelayRestore)
        {
            return;
        }
        Log.Info("Mute on display off: downloads remained inactive for 10 s, restoring audio.");
        Restore();
    }

    private void Mute()
    {
        if (_mutedByUs)
        {
            return;
        }
        // Entering the complete dark+download condition cancels a restore that never
        // completed: the mute is wanted again until either condition becomes false.
        _restorePending = false;
        if (!TryReadMuted(out var muted))
        {
            return;
        }
        if (muted)
        {
            // Already muted by the user — leave it, and remember that we did not do
            // it so the screen coming back does not unmute them.
            Log.Info("Mute on display off: already muted, leaving it alone.");
            return;
        }
        if (SetMuted(true))
        {
            _mutedByUs = true;
            _inputRecoveryLogged = false;
            _inputBaseline = ReadLastInputTick();
            Log.Info("Mute on display off: muted.");
            SyncRecoveryTimer();
        }
    }

    /// <summary>Asks for the mute this service applied to be undone. The claim survives a
    /// failed attempt so the recovery timer can retry it; it is dropped only once the
    /// device is confirmed to be unmuted.</summary>
    private void Restore()
    {
        if (!_mutedByUs)
        {
            return;
        }
        if (!TryReadMuted(out var muted))
        {
            // The default endpoint is re-enumerated when the display wakes, so an
            // unreadable read here is expected to be transient. Keep the claim.
            _restorePending = true;
            SyncRecoveryTimer();
            return;
        }
        if (!muted)
        {
            // The user unmuted while the screen was off; nothing to restore.
            _mutedByUs = false;
            _restorePending = false;
            SyncRecoveryTimer();
            return;
        }
        if (!SetMuted(false))
        {
            _restorePending = true;
            SyncRecoveryTimer();
            return;
        }
        _mutedByUs = false;
        _restorePending = false;
        Log.Info("Mute on display off: unmuted.");
        SyncRecoveryTimer();
    }

    private void SyncRecoveryTimer()
    {
        // ProcessExit runs off the UI thread; there is no next tick to schedule there.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return;
        }
        if (!_mutedByUs)
        {
            _recovery?.Stop();
            return;
        }
        if (_recovery is null)
        {
            // Avalonia's 3-arg DispatcherTimer ctor auto-starts; this one must only run
            // while a mute of ours is outstanding, and IsEnabled is consulted below.
            _recovery = new DispatcherTimer { Interval = RecoveryInterval };
            _recovery.Tick += (_, _) => OnRecoveryTick();
        }
        if (!_recovery.IsEnabled)
        {
            _recovery.Start();
        }
    }

    private void OnRecoveryTick()
    {
        if (!_mutedByUs)
        {
            _recovery?.Stop();
            return;
        }
        if (_restorePending)
        {
            Restore();
            return;
        }
        // No display-on notification arrived, but the user is typing/tapping — that only
        // happens at a lit screen, so treat it as the screen being back.
        if (!DisplayMuteDecider.HasInputSince(_inputBaseline, ReadLastInputTick()))
        {
            return;
        }
        if (!_inputRecoveryLogged)
        {
            _inputRecoveryLogged = true;
            Log.Info("Mute on display off: user input while muted, restoring without a "
                + "display-on notification.");
        }
        _displayOff = false;
        StopDownloadCompletionRestore();
        Restore();
    }

    private static uint ReadLastInputTick()
    {
        var info = new NativeMethods.LastInputInfo { CbSize = 8 };
        return NativeMethods.GetLastInputInfo(ref info) ? info.DwTime : 0;
    }

    private static bool TryReadMuted(out bool muted)
    {
        muted = false;
        try
        {
            var hr = CoreAudio.GetVolume(out _, out var value);
            if (hr < 0)
            {
                Log.Warn($"Mute on display off: reading the volume failed (0x{hr:X8}).");
                return false;
            }
            muted = value != 0;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Mute on display off: volume state unavailable ({ex.Message}).");
            return false;
        }
    }

    private static bool SetMuted(bool muted)
    {
        try
        {
            var hr = CoreAudio.SetMuted(muted);
            if (hr < 0)
            {
                Log.Warn($"Mute on display off: setting muted={muted} failed (0x{hr:X8}).");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Mute on display off: volume state unavailable ({ex.Message}).");
            return false;
        }
    }

    /// <summary>Unsubscribes and restores any mute this service applied.</summary>
    public void Dispose()
    {
        if (_subscribed)
        {
            _window.DisplayStateChanged -= OnDisplayStateChanged;
            _window.SessionUnlocked -= OnSessionUnlocked;
            _subscribed = false;
        }
        _window.DeregisterDisplayStateNotifications();
        _displayOff = false;
        StopDownloadCompletionRestore();
        Restore();
        _recovery?.Stop();
        _enabled = false;
    }
}

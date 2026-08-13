using System;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Mutes system audio while the screen is off and unmutes it when the screen
/// comes back.
///
/// <para>The problem this solves: keep-awake deliberately lets the display time out
/// while downloads continue (that is the whole point — Wi-Fi and Steam keep running on
/// a Modern-Standby handheld), but Steam plays a notification sound every time a
/// download finishes, into a dark room.</para>
///
/// <para>The signal is <c>GUID_SESSION_DISPLAY_STATUS</c> via
/// <see cref="MessageWindow.RegisterDisplayStateNotifications"/> — Microsoft documents
/// that setting as the one interactive user-mode apps must use (the console variant is
/// for services). <b>Whether it actually fires when the Claw's screen times out under
/// Modern Standby is device-verification pending</b>; the
/// <c>Display state:</c> log lines are what proves it.</para>
///
/// <para>Only a mute WSGM itself applied is undone. If the user had already muted the
/// device before the screen went off, the screen coming back leaves it muted — the
/// alternative would silently unmute someone who muted on purpose.</para></summary>
public sealed class DisplayOffMuteService : IDisposable
{
    private readonly MessageWindow _window;
    private bool _enabled;
    private bool _mutedByUs;
    private bool _subscribed;

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
                _subscribed = true;
            }
            _window.RegisterDisplayStateNotifications();
            Log.Info("Mute on display off: enabled.");
            return;
        }
        _window.DeregisterDisplayStateNotifications();
        Restore();
        Log.Info("Mute on display off: disabled.");
    }

    private void OnDisplayStateChanged(int state)
    {
        // MONITOR_DISPLAY_STATE: 0 = off, 1 = on, 2 = dimmed. Dimmed is still lit and
        // still in front of the user, so it is deliberately not treated as off.
        var name = state switch { 0 => "off", 1 => "on", 2 => "dimmed", _ => $"unknown ({state})" };
        Log.Info($"Display state: {name}.");
        if (!_enabled)
        {
            return;
        }
        if (state == 0)
        {
            Mute();
        }
        else if (state == 1)
        {
            Restore();
        }
    }

    private void Mute()
    {
        if (_mutedByUs)
        {
            return;
        }
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
        if (Toggle())
        {
            _mutedByUs = true;
            Log.Info("Mute on display off: muted.");
        }
    }

    private void Restore()
    {
        if (!_mutedByUs)
        {
            return;
        }
        _mutedByUs = false;
        if (!TryReadMuted(out var muted) || !muted)
        {
            // The user unmuted while the screen was off; nothing to restore.
            return;
        }
        if (Toggle())
        {
            Log.Info("Mute on display off: unmuted.");
        }
    }

    private static bool TryReadMuted(out bool muted)
    {
        muted = false;
        try
        {
            var hr = NativeVolumeControl.GetVolume(out _, out var value);
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
            Log.Warn($"Mute on display off: volume helper unavailable ({ex.Message}).");
            return false;
        }
    }

    // The native helper exposes a mute TOGGLE (the APPCOMMAND the volume keys send),
    // not an absolute set; every caller here checks the current state first.
    private static bool Toggle()
    {
        try
        {
            const int appCommandVolumeMute = 8;
            var hr = NativeVolumeControl.ApplyCommand(appCommandVolumeMute, out _, out _);
            if (hr < 0)
            {
                Log.Warn($"Mute on display off: toggling mute failed (0x{hr:X8}).");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Mute on display off: volume helper unavailable ({ex.Message}).");
            return false;
        }
    }

    /// <summary>Unsubscribes and restores any mute this service applied.</summary>
    public void Dispose()
    {
        if (_subscribed)
        {
            _window.DisplayStateChanged -= OnDisplayStateChanged;
            _subscribed = false;
        }
        _window.DeregisterDisplayStateNotifications();
        Restore();
        _enabled = false;
    }
}

using System;

namespace WSGM.Core;

/// <summary>Summons the Windows touch keyboard.
///
/// Game-mode sessions have no taskbar to summon it from, and any text entry
/// there — a Wi-Fi password, a Bluetooth PIN, an executable path in Settings —
/// is unreachable on a keyboard-less handheld without it.</summary>
public static class TouchKeyboard
{
    private static bool _missingLogged;

    /// <summary>Asks Windows to show its touch keyboard.
    ///
    /// Goes through the helper's ITipInvocation call, NOT by starting
    /// TabTip.exe: on Windows 11 that process is already running, so a second
    /// launch exits immediately and nothing appears.
    ///
    /// Only usable on the DESKTOP. Windows renders the touch keyboard from
    /// TextInputHost, part of the same immersive-shell AppX family as
    /// `ms-settings`, so it cannot come up with no Explorer in the session.
    /// Game-mode surfaces must draw their own — see
    /// <see cref="Controls.OnScreenKeyboard"/>, which the radio panel uses.</summary>
    public static void Show()
    {
        try
        {
            var status = Interop.NativeRadio.ShowTouchKeyboard();
            if (status != Interop.NativeRadio.Ok)
            {
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    Log.Warn($"Touch keyboard could not be shown: {Interop.NativeRadio.LastError()}");
                }
                return;
            }
            Log.Info("Touch keyboard: shown.");
        }
        catch (Exception ex)
        {
            if (!_missingLogged)
            {
                _missingLogged = true;
                Log.Warn($"Touch keyboard helper unavailable: {ex.Message}");
            }
        }
    }
}

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

    /// <summary>Shows the touch keyboard.
    ///
    /// Goes through the radio helper's ITipInvocation call, NOT by starting
    /// TabTip.exe. Launching the executable is the obvious approach and does
    /// nothing on Windows 11: the process is already running, so the second
    /// launch exits immediately and no keyboard appears. Falling back to
    /// osk.exe is deliberately not done either — that is the legacy
    /// accessibility keyboard, which is never right on a touch handheld.</summary>
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

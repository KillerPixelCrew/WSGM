using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Summons the Windows touch keyboard.
///
/// Game-mode sessions have no taskbar to summon it from, and any text entry
/// there — a Wi-Fi password, a Bluetooth PIN, an executable path in Settings —
/// is unreachable on a keyboard-less handheld without it.</summary>
public static class TouchKeyboard
{
    private static bool _missingLogged;

    /// <summary>Shows the touch keyboard, if this machine has its host.
    ///
    /// TabTip only: the osk.exe fallback brings up the legacy accessibility
    /// keyboard, which is never the right thing on a touch handheld.</summary>
    public static void Show()
    {
        var tabTip = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            @"microsoft shared\ink\TabTip.exe");
        if (!File.Exists(tabTip))
        {
            if (!_missingLogged)
            {
                _missingLogged = true;
                Log.Warn($"Touch keyboard host not found: {tabTip}");
            }
            return;
        }
        // Logged on both paths: whether TabTip actually renders over a game-mode
        // surface with no shell running is not something this machine can prove,
        // so the device log is the only evidence available.
        Log.Info("Touch keyboard: launching TabTip.");
        AppLauncher.Open(tabTip);
    }
}

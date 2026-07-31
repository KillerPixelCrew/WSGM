using System;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Controls Windows' device-posture signal so the Windows touch keyboard
/// stays out of game mode (Steam Big Picture has its own keyboard).
///
/// ConvertibleSlateMode (HKLM, instant effect — CSRSS/TabTip watch it):
///   1 = laptop, "physical keyboard present"  → Windows never auto-shows the OSK
///   0 = slate,  "no physical keyboard"       → OSK auto-shows on text fields
/// NOTE the polarity: game mode wants LAPTOP (1). Firmware/Windows recomputes the
/// value at boot, so it is reapplied at every shell start. The HKLM write needs
/// admin; the per-user TouchKeyboardTapInvoke value (0=never, 1=when no keyboard)
/// is written as well so unelevated setups still get most of the effect.</summary>
public static class SlateMode
{
    private const string PriorityControlKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string TabletTipKey = @"HKEY_CURRENT_USER\Software\Microsoft\TabletTip\1.7";

    private static int? _originalSlateMode;

    /// <summary>Game mode: pretend a keyboard is attached → no Windows auto-OSK.</summary>
    public static void ApplyGameMode() => Apply(slateMode: 1, tapInvoke: 0);

    /// <summary>Desktop mode: slate posture → tap a text field, keyboard appears.</summary>
    public static void ApplyDesktopMode() => Apply(slateMode: 0, tapInvoke: 1);

    /// <summary>Puts back whatever the firmware had set at boot (clean exits only —
    /// after a crash the next boot recomputes the value anyway).</summary>
    public static void RestoreOriginal()
    {
        if (_originalSlateMode is int original)
        {
            Apply(original, tapInvoke: 1, restoring: true);
        }
    }

    private static void Apply(int slateMode, int tapInvoke, bool restoring = false)
    {
        try
        {
            if (!restoring && _originalSlateMode is null)
            {
                _originalSlateMode = Registry.GetValue(PriorityControlKey, "ConvertibleSlateMode", null) as int?;
            }
            Registry.SetValue(PriorityControlKey, "ConvertibleSlateMode", slateMode, RegistryValueKind.DWord);
            Log.Info($"ConvertibleSlateMode = {slateMode} ({(slateMode == 1 ? "laptop, auto-OSK off" : "slate, auto-OSK on")}).");
        }
        catch (Exception ex)
        {
            Log.Warn($"ConvertibleSlateMode write failed (needs admin): {ex.Message}");
        }

        try
        {
            Registry.SetValue(TabletTipKey, "TouchKeyboardTapInvoke", tapInvoke, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Warn($"TouchKeyboardTapInvoke write failed: {ex.Message}");
        }
    }
}

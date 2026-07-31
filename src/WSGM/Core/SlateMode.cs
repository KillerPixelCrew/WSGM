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
/// is written as well so unelevated setups still get most of the effect.
///
/// The pre-WSGM values (including "value absent") are snapshotted into the config
/// BEFORE the first write — capturing later would record WSGM's own value as the
/// firmware original — and put back exactly by RestoreOriginal.</summary>
public static class SlateMode
{
    private const string PriorityControlKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string PriorityControlSubKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string TabletTipKey = @"HKEY_CURRENT_USER\Software\Microsoft\TabletTip\1.7";
    private const string TabletTipSubKey = @"Software\Microsoft\TabletTip\1.7";

    /// <summary>Game mode: pretend a keyboard is attached → no Windows auto-OSK.</summary>
    public static void ApplyGameMode(AppConfig? config = null) => Apply(slateMode: 1, tapInvoke: 0, config);

    /// <summary>Desktop mode: slate posture → tap a text field, keyboard appears.
    /// Recovery paths pass no config: the values are written but nothing is
    /// captured (a blank config loaded mid-panic must never clobber snapshots).</summary>
    public static void ApplyDesktopMode(AppConfig? config = null) => Apply(slateMode: 0, tapInvoke: 1, config);

    /// <summary>Puts back exactly what the machine had before WSGM's first write —
    /// the values captured in the config, with "absent" restored as a delete (clean
    /// exits only; after a crash the next boot recomputes ConvertibleSlateMode).</summary>
    public static void RestoreOriginal()
    {
        try
        {
            var config = ConfigStore.Load();
            if (!config.SlateModeSnapshotCaptured)
            {
                return;
            }
            RestoreValue(Registry.LocalMachine, PriorityControlSubKey, "ConvertibleSlateMode", config.PreviousSlateMode);
            RestoreValue(Registry.CurrentUser, TabletTipSubKey, "TouchKeyboardTapInvoke", config.PreviousTouchKeyboardTapInvoke);
            Log.Info($"Slate mode restored (ConvertibleSlateMode={Describe(config.PreviousSlateMode)}, " +
                     $"TouchKeyboardTapInvoke={Describe(config.PreviousTouchKeyboardTapInvoke)}).");
            config.SlateModeSnapshotCaptured = false;
            config.PreviousSlateMode = -1;
            config.PreviousTouchKeyboardTapInvoke = -1;
            ConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            Log.Warn($"Slate mode restore failed: {ex.Message}");
        }
    }

    private static void Apply(int slateMode, int tapInvoke, AppConfig? config)
    {
        if (config is not null && !config.SlateModeSnapshotCaptured)
        {
            try
            {
                config.PreviousSlateMode = Registry.GetValue(PriorityControlKey, "ConvertibleSlateMode", null) as int? ?? -1;
                config.PreviousTouchKeyboardTapInvoke = Registry.GetValue(TabletTipKey, "TouchKeyboardTapInvoke", null) as int? ?? -1;
                config.SlateModeSnapshotCaptured = true;
                ConfigStore.Save(config);
                Log.Info($"Slate mode snapshot captured (ConvertibleSlateMode={Describe(config.PreviousSlateMode)}, " +
                         $"TouchKeyboardTapInvoke={Describe(config.PreviousTouchKeyboardTapInvoke)}).");
            }
            catch (Exception ex)
            {
                Log.Warn($"Slate mode snapshot not captured: {ex.Message}");
            }
        }

        try
        {
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

    /// <summary>Writes the captured value back; a captured "absent" (-1) deletes the
    /// value so WSGM's write does not linger where nothing existed before.</summary>
    private static void RestoreValue(RegistryKey hive, string subKey, string valueName, int previous)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return;
            }
            if (previous < 0)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(valueName, previous, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{valueName} restore failed: {ex.Message}");
        }
    }

    private static string Describe(int value) => value < 0 ? "absent" : value.ToString();
}

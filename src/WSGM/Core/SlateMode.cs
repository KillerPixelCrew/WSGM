using System;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Controls Windows' device-posture signal so the Windows touch keyboard
/// stays out of game mode (Steam Big Picture has its own keyboard).
///
/// ConvertibleSlateMode (HKLM, instant effect — CSRSS/TabTip watch it):
///   1 = laptop, "physical keyboard present"  → Windows never auto-shows the OSK
///   0 = slate,  "no physical keyboard"       → OSK auto-shows on text fields
/// NOTE the polarity: game mode wants LAPTOP (1). WSGM only changes this value
/// when it already existed before WSGM first ran — ordinary PCs without the
/// device-posture value must not gain one. Firmware/Windows recomputes the value
/// at boot, so an eligible device reapplies it at every shell start. The HKLM
/// write needs admin; the per-user TouchKeyboardTapInvoke value (0=never,
/// 1=when no keyboard) is written as well so unelevated setups still get most of
/// the effect.
///
/// The pre-WSGM values are snapshotted into the config BEFORE the first write —
/// capturing later would record WSGM's own value as the firmware original. An
/// absent ConvertibleSlateMode remains untouched; TouchKeyboardTapInvoke is
/// restored exactly by <see cref="RestoreOriginal"/>.</summary>
public static class SlateMode
{
    private const string PriorityControlKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string PriorityControlSubKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string TabletTipKey = @"HKEY_CURRENT_USER\Software\Microsoft\TabletTip\1.7";
    private const string TabletTipSubKey = @"Software\Microsoft\TabletTip\1.7";

    /// <summary>Game mode: pretend a keyboard is attached → no Windows auto-OSK.</summary>
    public static void ApplyGameMode(AppConfig? config = null) => Apply(slateMode: 1, tapInvoke: 0, config);

    /// <summary>Desktop mode: slate posture → tap a text field, keyboard appears.
    /// Without a config, ConvertibleSlateMode is changed only when it currently
    /// exists; no snapshot is captured (a blank config loaded mid-panic must never
    /// clobber snapshots).</summary>
    public static void ApplyDesktopMode(AppConfig? config = null) => Apply(slateMode: 0, tapInvoke: 1, config);

    /// <summary>Puts back the device-posture value only when WSGM changed it, and
    /// restores the captured touch-keyboard preference. A legacy WSGM snapshot
    /// without the write marker is cleaned up once to undo the old behavior that
    /// created ConvertibleSlateMode on PCs where it was originally absent.</summary>
    public static void RestoreOriginal()
    {
        try
        {
            var config = ConfigStore.Load();
            if (!config.SlateModeSnapshotCaptured)
            {
                return;
            }
            if (ShouldRestoreConvertibleSlateMode(config.ConvertibleSlateModeModifiedByWsgm))
            {
                RestoreValue(Registry.LocalMachine, PriorityControlSubKey, "ConvertibleSlateMode", config.PreviousSlateMode);
            }
            RestoreValue(Registry.CurrentUser, TabletTipSubKey, "TouchKeyboardTapInvoke", config.PreviousTouchKeyboardTapInvoke);
            var convertibleResult = ShouldRestoreConvertibleSlateMode(config.ConvertibleSlateModeModifiedByWsgm)
                ? Describe(config.PreviousSlateMode)
                : "unchanged (originally absent)";
            Log.Info($"Slate mode restored (ConvertibleSlateMode={convertibleResult}, " +
                     $"TouchKeyboardTapInvoke={Describe(config.PreviousTouchKeyboardTapInvoke)}).");
            config.SlateModeSnapshotCaptured = false;
            config.PreviousSlateMode = -1;
            config.PreviousTouchKeyboardTapInvoke = -1;
            config.ConvertibleSlateModeModifiedByWsgm = null;
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
                // False is persisted before any write. A missing value means this
                // config came from WSGM <= 0.3.2, which may have created the key.
                config.ConvertibleSlateModeModifiedByWsgm = false;
                config.SlateModeSnapshotCaptured = true;
                ConfigStore.Save(config);
                Log.Info($"Slate mode snapshot captured (ConvertibleSlateMode={Describe(config.PreviousSlateMode)}, " +
                         $"TouchKeyboardTapInvoke={Describe(config.PreviousTouchKeyboardTapInvoke)}).");
            }
            catch (Exception ex)
            {
                config.SlateModeSnapshotCaptured = false;
                config.PreviousSlateMode = -1;
                config.PreviousTouchKeyboardTapInvoke = -1;
                config.ConvertibleSlateModeModifiedByWsgm = null;
                Log.Warn($"Slate mode snapshot not captured: {ex.Message}");
            }
        }

        RemoveLegacyAbsentConvertibleSlateMode(config);
        if (ShouldOverrideConvertibleSlateMode(config))
        {
            if (TryMarkConvertibleSlateModeModified(config))
            {
                try
                {
                    Registry.SetValue(PriorityControlKey, "ConvertibleSlateMode", slateMode, RegistryValueKind.DWord);
                    Log.Info($"ConvertibleSlateMode = {slateMode} ({(slateMode == 1 ? "laptop, auto-OSK off" : "slate, auto-OSK on")}).");
                }
                catch (Exception ex)
                {
                    if (config is not null)
                    {
                        config.ConvertibleSlateModeModifiedByWsgm = false;
                        TrySaveConvertibleSlateModeMarker(config);
                    }
                    Log.Warn($"ConvertibleSlateMode write failed (needs admin): {ex.Message}");
                }
            }
        }
        else
        {
            Log.Info(config is null
                ? "ConvertibleSlateMode is absent — leaving it unchanged."
                : "ConvertibleSlateMode was not captured as an existing value — leaving it unchanged.");
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

    /// <summary>Returns whether a captured, pre-existing posture signal permits a
    /// mode transition to change ConvertibleSlateMode.</summary>
    internal static bool ShouldOverrideConvertibleSlateMode(bool snapshotCaptured, int previousSlateMode)
        => snapshotCaptured && previousSlateMode >= 0;

    /// <summary>Returns whether a restore must touch ConvertibleSlateMode. A null
    /// marker denotes a config written by a release before the non-creation policy,
    /// so restoring it once removes a possible legacy override.</summary>
    internal static bool ShouldRestoreConvertibleSlateMode(bool? modifiedByWsgm)
        => modifiedByWsgm is not false;

    private static bool ShouldOverrideConvertibleSlateMode(AppConfig? config)
    {
        if (config is not null)
        {
            return ShouldOverrideConvertibleSlateMode(config.SlateModeSnapshotCaptured, config.PreviousSlateMode);
        }

        return TryReadConvertibleSlateMode(out var exists) && exists;
    }

    private static void RemoveLegacyAbsentConvertibleSlateMode(AppConfig? config)
    {
        if (config is null || config.SlateModeSnapshotCaptured == false ||
            config.PreviousSlateMode >= 0 || config.ConvertibleSlateModeModifiedByWsgm is not null)
        {
            return;
        }

        if (!TryReadConvertibleSlateMode(out var exists))
        {
            return;
        }
        if (!exists || RestoreValue(Registry.LocalMachine, PriorityControlSubKey, "ConvertibleSlateMode", previous: -1))
        {
            config.ConvertibleSlateModeModifiedByWsgm = false;
            TrySaveConvertibleSlateModeMarker(config);
            Log.Info(exists
                ? "Removed legacy ConvertibleSlateMode override that was originally absent."
                : "Legacy ConvertibleSlateMode snapshot confirmed absent; no registry value was touched.");
        }
    }

    private static bool TryReadConvertibleSlateMode(out bool exists)
    {
        try
        {
            exists = Registry.GetValue(PriorityControlKey, "ConvertibleSlateMode", null) is int;
            return true;
        }
        catch (Exception ex)
        {
            exists = false;
            Log.Warn($"ConvertibleSlateMode read failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryMarkConvertibleSlateModeModified(AppConfig? config)
    {
        if (config is null || config.ConvertibleSlateModeModifiedByWsgm == true)
        {
            return true;
        }

        config.ConvertibleSlateModeModifiedByWsgm = true;
        if (TrySaveConvertibleSlateModeMarker(config))
        {
            return true;
        }

        config.ConvertibleSlateModeModifiedByWsgm = false;
        return false;
    }

    private static bool TrySaveConvertibleSlateModeMarker(AppConfig config)
    {
        try
        {
            ConfigStore.Save(config);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"ConvertibleSlateMode marker was not saved: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes a captured value back; a captured "absent" (-1) deletes the
    /// value. This is used only for legacy cleanup because current WSGM versions
    /// never create an absent ConvertibleSlateMode value.</summary>
    private static bool RestoreValue(RegistryKey hive, string subKey, string valueName, int previous)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return previous < 0;
            }
            if (previous < 0)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(valueName, previous, RegistryValueKind.DWord);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"{valueName} restore failed: {ex.Message}");
            return false;
        }
    }

    private static string Describe(int value) => value < 0 ? "absent" : value.ToString();
}

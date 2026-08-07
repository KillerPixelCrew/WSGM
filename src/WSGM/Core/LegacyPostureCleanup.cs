using System;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Restores device-posture and touch-keyboard values captured by older
/// WSGM builds. Current builds never capture or apply either policy.</summary>
public static class LegacyPostureCleanup
{
    private const string PriorityControlSubKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string TabletTipSubKey = @"Software\Microsoft\TabletTip\1.7";

    /// <summary>Restores and clears a legacy snapshot. Failed values remain
    /// pending so a later elevated shell can retry an HKLM restoration.</summary>
    public static void Restore()
    {
        try
        {
            var config = ConfigStore.Load();
            if (!config.SlateModeSnapshotCaptured)
            {
                return;
            }

            var restoreConvertible = config.ConvertibleSlateModeModifiedByWsgm is not false;
            var convertibleRestored = !restoreConvertible || RestoreValue(
                Registry.LocalMachine,
                PriorityControlSubKey,
                "ConvertibleSlateMode",
                config.PreviousSlateMode);
            var touchKeyboardRestored = RestoreValue(
                Registry.CurrentUser,
                TabletTipSubKey,
                "TouchKeyboardTapInvoke",
                config.PreviousTouchKeyboardTapInvoke);
            if (!convertibleRestored || !touchKeyboardRestored)
            {
                return;
            }

            config.SlateModeSnapshotCaptured = false;
            config.PreviousSlateMode = -1;
            config.PreviousTouchKeyboardTapInvoke = -1;
            config.ConvertibleSlateModeModifiedByWsgm = null;
            ConfigStore.Save(config);
            Log.Info("Restored legacy posture/keyboard values; Windows now owns both policies.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Legacy posture cleanup failed: {ex.Message}");
        }
    }

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
            Log.Warn($"{valueName} legacy restore failed: {ex.Message}");
            return false;
        }
    }
}

using System;
using Avalonia.Controls;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private DeviceSettingRow? _lockOnWakeRow;
    private bool _refreshingWindowsPolicies;
    private DeviceSettingRow? _uacRow;

    /// <summary>Raised with true to turn the machine's UAC consent prompts off, false to restore them.</summary>
    internal event Action<bool>? UacPromptsRequested;

    /// <summary>Raised with true to skip the sign-in after standby, false to restore it.</summary>
    internal event Action<bool>? LockOnWakeRequested;

    private void InitializeWindowsPolicies()
    {
        _uacRow = AddWindowsPolicyRow(UacPolicyHost, "system.uac-prompts", "Never show UAC prompts on this PC",
            "Applies to every administrator account. Enable only on a personal device you trust.",
            disable => UacPromptsRequested?.Invoke(disable));
        _lockOnWakeRow = AddWindowsPolicyRow(LockOnWakePolicyHost, "system.lock-on-wake",
            "No lock screen after standby",
            "Keep a personal handheld ready after it wakes. Anyone with the device can use the current session.",
            disable => LockOnWakeRequested?.Invoke(disable));
    }

    /// <summary>Shows both machine policies as Windows reports them and re-enables their switches.</summary>
    /// <param name="uacPromptsDisabled">Whether UAC consent prompts are off.</param>
    /// <param name="lockOnWakeDisabled">Whether Windows skips the sign-in after standby.</param>
    /// <param name="editable">False on a preview surface, which must not change the machine.</param>
    internal void RefreshWindowsPolicies(bool uacPromptsDisabled, bool lockOnWakeDisabled, bool editable)
    {
        _refreshingWindowsPolicies = true;
        try
        {
            Show(_uacRow, uacPromptsDisabled);
            Show(_lockOnWakeRow, lockOnWakeDisabled);
        }
        finally
        {
            _refreshingWindowsPolicies = false;
        }

        void Show(DeviceSettingRow? row, bool disabled)
        {
            if (row is null)
            {
                return;
            }

            // Set directly rather than through the row's refresh, which leaves a focused editor alone: a
            // declined elevation must put the pressed switch back.
            ((ToggleSwitch)row.Editor).IsChecked = disabled;
            row.Editor.IsEnabled = editable;
        }
    }

    private DeviceSettingRow AddWindowsPolicyRow(Panel host, string key, string title, string description,
        Action<bool> requested)
    {
        // Disabled until Windows has been read, and again while a change runs, so a second
        // press cannot queue a second elevation prompt.
        DeviceSettingRow? row = null;
        row = DeviceControlRows.Toggle(key, title, description, false, false, disable =>
        {
            if (_refreshingWindowsPolicies)
            {
                return;
            }

            row!.Editor.IsEnabled = false;
            requested(disable);
        });
        host.Children.Add(row);
        return row;
    }
}

using System;
using Avalonia.Controls;

namespace WSGM.Overlay;

/// <summary>
///     The two machine policy switches the sheet offers: UAC consent prompts on the System page and
///     the sign-in after standby on the Wake page.
/// </summary>
/// <remarks>
///     The switches report intent only; the controller runs the elevated change and reads Windows
///     back through <see cref="Refresh" />. Each switch is disabled until Windows has been read, and
///     again while a change runs, so a second press cannot queue a second elevation prompt.
/// </remarks>
internal sealed class WindowsPolicyRows
{
    private readonly DeviceSettingRow _lockOnWake;
    private readonly DeviceSettingRow _uac;
    private bool _refreshing;

    /// <summary>Adds the switches to their pages.</summary>
    /// <param name="uacHost">The System page panel that receives the UAC switch.</param>
    /// <param name="lockOnWakeHost">The Wake page panel that receives the sign-in switch.</param>
    internal WindowsPolicyRows(Panel uacHost, Panel lockOnWakeHost)
    {
        _uac = AddRow(uacHost, "system.uac-prompts", "Never show UAC prompts on this PC",
            "Applies to every administrator account. Enable only on a personal device you trust.",
            disable => UacPromptsRequested?.Invoke(disable));
        _lockOnWake = AddRow(lockOnWakeHost, "system.lock-on-wake", "No lock screen after standby",
            "Keep a personal handheld ready after it wakes. Anyone with the device can use the current session.",
            disable => LockOnWakeRequested?.Invoke(disable));
    }

    /// <summary>Raised with true to turn the machine's UAC consent prompts off, false to restore them.</summary>
    internal event Action<bool>? UacPromptsRequested;

    /// <summary>Raised with true to skip the sign-in after standby, false to restore it.</summary>
    internal event Action<bool>? LockOnWakeRequested;

    /// <summary>Shows both machine policies as Windows reports them and re-enables their switches.</summary>
    /// <param name="uacPromptsDisabled">Whether UAC consent prompts are off.</param>
    /// <param name="lockOnWakeDisabled">Whether Windows skips the sign-in after standby.</param>
    /// <param name="editable">False on a preview surface, which must not change the machine.</param>
    internal void Refresh(bool uacPromptsDisabled, bool lockOnWakeDisabled, bool editable)
    {
        _refreshing = true;
        try
        {
            Show(_uac, uacPromptsDisabled);
            Show(_lockOnWake, lockOnWakeDisabled);
        }
        finally
        {
            _refreshing = false;
        }

        void Show(DeviceSettingRow row, bool disabled)
        {
            // Set directly rather than through the row's refresh, which leaves a focused editor alone: a
            // declined elevation must put the pressed switch back.
            ((ToggleSwitch)row.Editor).IsChecked = disabled;
            row.Editor.IsEnabled = editable;
        }
    }

    private DeviceSettingRow AddRow(Panel host, string key, string title, string description, Action<bool> requested)
    {
        DeviceSettingRow? row = null;
        row = DeviceControlRows.Toggle(key, title, description, false, false, disable =>
        {
            if (_refreshing)
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

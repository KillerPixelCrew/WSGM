using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private ToggleSwitch? _lockOnWakeToggle;
    private bool _refreshingWindowsPolicies;
    private ToggleSwitch? _uacToggle;

    /// <summary>Raised with true to turn the machine's UAC consent prompts off, false to restore them.</summary>
    internal event Action<bool>? UacPromptsRequested;

    /// <summary>Raised with true to skip the sign-in after standby, false to restore it.</summary>
    internal event Action<bool>? LockOnWakeRequested;

    private void InitializeWindowsPolicies()
    {
        _uacToggle = AddWindowsPolicyRow(UacPolicyHost, "Never show UAC prompts on this PC",
            "Applies to every administrator account. Enable only on a personal device you trust.",
            disable => UacPromptsRequested?.Invoke(disable));
        _lockOnWakeToggle = AddWindowsPolicyRow(LockOnWakePolicyHost, "No lock screen after standby",
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
            Show(_uacToggle, uacPromptsDisabled);
            Show(_lockOnWakeToggle, lockOnWakeDisabled);
        }
        finally
        {
            _refreshingWindowsPolicies = false;
        }

        void Show(ToggleSwitch? toggle, bool disabled)
        {
            if (toggle is null)
            {
                return;
            }

            toggle.IsChecked = disabled;
            toggle.IsEnabled = editable;
        }
    }

    private ToggleSwitch AddWindowsPolicyRow(Panel host, string title, string description, Action<bool> requested)
    {
        // Disabled until Windows has been read, and again while a change runs, so a second
        // press cannot queue a second elevation prompt.
        var toggle = new ToggleSwitch
        {
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            OffContent = null,
            OnContent = null
        };
        AutomationProperties.SetName(toggle, title);
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (_refreshingWindowsPolicies)
            {
                return;
            }

            toggle.IsEnabled = false;
            requested(toggle.IsChecked == true);
        };
        host.Children.Add(new FASettingsExpanderItem
        {
            Content = title, Description = description, Footer = toggle,
            HorizontalAlignment = HorizontalAlignment.Stretch, Focusable = false
        });
        return toggle;
    }
}

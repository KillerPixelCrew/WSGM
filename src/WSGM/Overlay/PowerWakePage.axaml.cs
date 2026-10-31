using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Overlay;

/// <summary>Power > Wake: Keep awake, what holds a wake lock and the sign-in after standby.</summary>
/// <remarks>
///     <see cref="OverlayPowerEditors" /> fills <see cref="KeepAwakeHost" /> and
///     <see cref="WindowsPolicyRows" /> fills <see cref="LockOnWakePolicyHost" />.
/// </remarks>
public sealed partial class PowerWakePage : StackPanel
{
    /// <summary>Builds the page.</summary>
    public PowerWakePage()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user opens the list of wake lock holders.</summary>
    internal event Action? WakeLocksRequested;

    private void OnWakeLocks(object? sender, RoutedEventArgs e)
    {
        WakeLocksRequested?.Invoke();
    }
}

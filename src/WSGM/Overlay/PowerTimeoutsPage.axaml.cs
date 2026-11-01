using Avalonia.Controls;

namespace WSGM.Overlay;

/// <summary>Power > Idle timeouts: screen off, standby and hibernate, on battery and plugged in.</summary>
/// <remarks><see cref="OverlayPowerEditors" /> fills <see cref="PowerTimeoutEditors" /> with one row per timeout.</remarks>
public sealed partial class PowerTimeoutsPage : StackPanel
{
    /// <summary>Builds the page.</summary>
    public PowerTimeoutsPage()
    {
        InitializeComponent();
    }
}

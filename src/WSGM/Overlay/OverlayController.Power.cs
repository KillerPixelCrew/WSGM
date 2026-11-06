using WSGM.Core;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    /// <summary>
    ///     Opens the power menu on the current overlay, or from the desktop without a Steam lease.
    ///     Hardware-button capture is owned by the session's input integration.
    /// </summary>
    public void ShowPowerMenu()
    {
        if (_disposed)
        {
            return;
        }

        var standalone = Sheet is null;
        ShowOverlayCore(!ExplorerControl.IsDesktopShellRunning());
        _powerMenuOnly |= standalone;
        Sheet?.ShowPowerMenu();
    }

    /// <summary>Handles a repeated power-menu request as cancellation.</summary>
    public void TogglePowerMenu()
    {
        if (PowerMenuOpen)
        {
            Sheet?.Surfaces.CloseActive();
            return;
        }

        ShowPowerMenu();
    }
}

using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private void RefreshPerformancePanel()
    {
        RefreshPerformancePanel(_performanceSource?.Snapshot());
    }

    /// <summary>Draws the shared performance rows on Tools when there is no Device destination to hold them.</summary>
    /// <param name="snapshot">The performance snapshot, or null without a performance source.</param>
    /// <remarks>
    ///     With Device, the Power page and the Overview carry the rows through their own description; without it
    ///     they live on Tools, with the per-application toggle the Overview would otherwise head. Quick access
    ///     draws its pinned copy in the same pass.
    /// </remarks>
    private void RefreshPerformancePanel(PerformanceOverlaySnapshot? snapshot)
    {
        if (_closed)
        {
            return;
        }

        _profileScope.Refresh();

        // The first render runs when the window opens.
        if (!_opened)
        {
            return;
        }

        var onTools = snapshot is { Visible: true } && !_navigation.IsVisible(OverlayDestination.Device);
        // The Tools root offers Performance only while the rows live there, since a tile leading to an empty
        // page is a dead end.
        SystemPerformanceTile.IsVisible = onTools;
        _deviceRenderer.Reconcile(PanelSystemPerformance,
            onTools ? [DevicePages.Performance(snapshot!, true, false)] : [], false);
    }
}

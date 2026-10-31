using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private void RefreshPerformancePanel()
    {
        RefreshPerformancePanel(_performanceSource?.Snapshot());
    }

    /// <summary>Follows a performance change in the header profile switch and on Tools > Performance.</summary>
    /// <param name="snapshot">The performance snapshot, or null without a performance source.</param>
    /// <remarks>Quick access draws its pinned copy in the same pass.</remarks>
    private void RefreshPerformancePanel(PerformanceOverlaySnapshot? snapshot)
    {
        if (_closed)
        {
            return;
        }

        _profileScope.Refresh();

        // The first render runs when the window opens.
        if (_opened)
        {
            PanelSystemPerformance.Show(snapshot, _navigation.IsVisible(OverlayDestination.Device));
        }
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The taskbar's Safe Eject panel: the removable devices, each with an
/// Eject action revealed on selection.
///
/// A real window rather than a taskbar flyout for the same reasons as the radio
/// panel: a flyout cannot hold a device list, and
/// <see cref="Input.GamepadNavigation"/> has no popup awareness, so a list
/// inside one would not be reachable with a controller at all.</summary>
public partial class EjectWindow : Window
{
    private const double BaseWidth = 500;
    private const double BaseHeight = 420;
    private readonly RemovableDriveManager _drives;
    private readonly double _uiScale;

    /// <summary>Creates the panel.</summary>
    /// <param name="drives">The manager backing the list. Not owned: the
    /// taskbar's status object outlives this window.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI.</param>
    public EjectWindow(RemovableDriveManager drives, double uiScale = 1.0)
    {
        _drives = drives;
        _uiScale = uiScale;
        InitializeComponent();
        DataContext = drives;
        Opened += (_, _) => _drives.Refresh();
        TaskbarPanel.WirePanelBehaviour(this, ListScroller);
    }

    /// <summary>Places the panel just above the right-hand status section of the
    /// taskbar and scales it back to the user's normal desktop DPI (same
    /// mechanism as the audio panel).</summary>
    /// <param name="taskbarTop">The bar's top edge in physical screen pixels, or
    /// 0 when it is not on screen.</param>
    internal void DockAboveTaskbar(int taskbarTop = 0) => TaskbarPanel.DockAboveTaskbar(
        this, RootScale, _uiScale, BaseWidth, BaseHeight, taskbarTop, "Eject");

    /// <summary>Selecting a row reveals its Eject action. It never ejects on its
    /// own: a stray tap must not pull a mounted game library.</summary>
    private void OnDriveClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not RemovableDriveEntry entry)
        {
            return;
        }
        foreach (var other in _drives.Drives)
        {
            other.Expanded = ReferenceEquals(other, entry) && !entry.Expanded;
        }
    }

    private async void OnDriveEject(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RemovableDriveEntry entry)
        {
            await _drives.EjectAsync(entry);
        }
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => _drives.Refresh();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}

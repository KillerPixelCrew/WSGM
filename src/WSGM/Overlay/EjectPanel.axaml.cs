using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>In-window eject controls backed by the existing overlay actions.</summary>
public partial class EjectPanel : UserControl
{
    private readonly RemovableDriveManager _drives;

    /// <summary>Creates the panel.</summary>
    /// <param name="drives">
    ///     The manager backing the list. Not owned: the
    ///     sheet's status object outlives this window.
    /// </param>
    public EjectPanel(RemovableDriveManager drives)
    {
        _drives = drives;
        InitializeComponent();
        DataContext = drives;
        AttachedToVisualTree += (_, _) => _drives.Refresh();
        StatusPanel.WirePanelBehaviour(ListScroller);
    }


    /// <summary>
    ///     Selecting a row reveals its Eject action. It never ejects on its
    ///     own: a stray tap must not pull a mounted game library.
    /// </summary>
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

    private void OnDriveEject(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RemovableDriveEntry entry)
        {
            // EjectAsync contains the user-visible error boundary. Detach only
            // after that boundary so no event-handler exception can reach Avalonia.
            Log.Observe(_drives.EjectAsync(entry), $"eject {entry.Name}");
        }
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        _drives.Refresh();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }

    /// <summary>Requests closure of this in-window surface.</summary>
    public event Action? CloseRequested;
}

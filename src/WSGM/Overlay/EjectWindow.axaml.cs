using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Core;
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

    /// <summary>Design-time constructor. Avalonia's XAML loader needs it.</summary>
    public EjectWindow()
        : this(new RemovableDriveManager())
    {
    }

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
        // Controller navigation moves focus with Focus(Directional), which does
        // NOT raise RequestBringIntoView — a row below the fold would take focus
        // invisibly. Ask for it explicitly (taskbar-strip discipline).
        ListScroller.AddHandler(GotFocusEvent, OnRowGotFocus, RoutingStrategies.Bubble);
        // Same touch-promotion defense as the other panels (invariant 3): the
        // controller owns the matching 150 ms deferred close.
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                Close();
            }
        };
    }

    /// <summary>Places the panel just above the right-hand status section of the
    /// taskbar and scales it back to the user's normal desktop DPI (same
    /// mechanism as the audio panel).</summary>
    /// <param name="taskbarTop">The bar's top edge in physical screen pixels, or
    /// 0 when it is not on screen.</param>
    internal void DockAboveTaskbar(int taskbarTop = 0)
    {
        var factor = Math.Clamp(_uiScale / DesktopScaling, 1.0, 3.0);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            Log.Info($"Eject panel UI scale {factor:0.##}x "
                + $"(desktop DPI over current {DesktopScaling:0.##}).");
            RootScale.LayoutTransform = new Avalonia.Media.ScaleTransform(factor, factor);
        }

        var screen = Screens.Primary ?? (Screens.ScreenCount > 0 ? Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }
        var area = screen.Bounds;
        var bottom = taskbarTop > 0 ? taskbarTop : area.Y + area.Height;
        Width = Math.Min(BaseWidth * factor, area.Width / DesktopScaling - 12);
        Height = Math.Min(BaseHeight * factor, (bottom - area.Y) / DesktopScaling - 8);
        UpdateLayout();

        // Window scaling, not screen.Scaling — the screens cache is stale after
        // a runtime display-scale flip (see OverlayWindow.DockToRightEdge).
        var scale = DesktopScaling;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        var gap = (int)Math.Round(2 * scale);
        var margin = (int)Math.Round(6 * scale);
        var x = area.X + area.Width - width - margin;
        var y = Math.Max(area.Y, bottom - height - gap);
        Position = new PixelPoint(x, y);
    }

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

    /// <summary>Scrolls a newly focused row (or its Eject button) into the
    /// viewport. A no-op when it is already fully visible.</summary>
    private void OnRowGotFocus(object? sender, Avalonia.Input.FocusChangedEventArgs e)
    {
        if (e.Source is Control control && control is not ScrollViewer)
        {
            control.BringIntoView();
        }
    }

    private static IntPtr WndProcHook(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is Interop.NativeMethods.WmMouseMove
                or Interop.NativeMethods.WmLButtonDown
                or Interop.NativeMethods.WmLButtonUp)
        {
            var extra = (uint)Interop.NativeMethods.GetMessageExtraInfo();
            if ((extra & Interop.NativeMethods.MiWpSignatureMask)
                == Interop.NativeMethods.MiWpSignature)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }
}

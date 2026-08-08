using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The game-mode taskbar: a full-width, bottom-docked three-zone bar —
/// WSGM home button, centered app tiles (and, when the tray host is live, tray
/// icons), and the system status cluster (Wi-Fi/Bluetooth/battery/clock). Shares
/// the overlay's focus-taking model: safe only because the Steam Input lease keeps
/// the pad readable while a WSGM window is foreground.</summary>
public partial class TaskbarWindow : Window
{
    private DispatcherTimer? _slideTimer;
    private PixelPoint _slideStart;
    private PixelPoint _slideEnd;
    private DateTime _slideStartedUtc;

    /// <summary>Raised when the user picks an application tile.</summary>
    public event Action<TaskbarEntry>? WindowPicked;

    /// <summary>Raised when the user activates a tray icon. Arguments: the entry,
    /// whether this is a context-menu (right-click) activation, and the screen
    /// pixel position the app should anchor any menu to.</summary>
    public event Action<TrayIconEntry, bool, PixelPoint>? TrayIconActivated;

    /// <summary>Raised when the taskbar is dismissed without another action.</summary>
    public event Action? Dismissed;

    /// <summary>Raised when the WSGM home button is pressed: the controller opens
    /// quick access through its existing taskbar-to-overlay handover (restore
    /// target inherited, shared lease kept alive).</summary>
    public event Action? HomeRequested;

    /// <summary>The control gamepad navigation should land on when the bar opens:
    /// the first application tile — explicitly, because the window's visual-tree
    /// order now puts the home button first (falls back to the first visible
    /// button when no tiles exist).</summary>
    internal InputElement? DefaultFocusTarget => FindFirstTile();

    private readonly double _uiScale;

    /// <summary>The factor RootScale currently applies (1.0 = no transform). The
    /// bar's inner layout happens in pre-transform units, so every budget derived
    /// from the window's width has to divide by this first.</summary>
    private double _contentScale = 1.0;

    /// <summary>Share of the bar's inner width the tray strip may claim before it
    /// starts scrolling. The tray is the only right-zone content whose length WSGM
    /// does not control (the Shell_TrayWnd host takes whatever apps register), so
    /// it is the part that gets a budget; the Wi-Fi/Bluetooth/battery/clock
    /// controls after it are fixed-size and always keep their space.</summary>
    private const double TrayWidthFraction = 0.30;

    /// <summary>Floor for the tray budget: one 36 px tray tile plus its 2 px
    /// spacing and 2 px focus border, so a single icon is never clipped even on an
    /// absurdly narrow bar.</summary>
    private const double TrayMinWidth = 40;

    /// <summary>Horizontal padding the bar's Border adds inside the window (8 left
    /// + 8 right — keep in sync with Padding="8,2" in the XAML).</summary>
    private const double BarHorizontalPadding = 16;

    /// <summary>The widest the tray strip may become before it scrolls, so that
    /// the fixed status controls behind it always fit. Pure: the width budget is
    /// unit-tested against this method rather than against a live window.</summary>
    /// <param name="windowWidth">The bar window's logical (DIP) width.</param>
    /// <param name="contentScale">The factor RootScale applies to the bar's
    /// content (see <see cref="ApplyTouchScale"/>); 1.0 when untransformed.</param>
    /// <returns>A MaxWidth in the bar's pre-transform layout units.</returns>
    internal static double ComputeTrayMaxWidth(double windowWidth, double contentScale)
    {
        if (!double.IsFinite(windowWidth) || !double.IsFinite(contentScale) || contentScale <= 0)
        {
            return TrayMinWidth;
        }
        var inner = windowWidth / contentScale - BarHorizontalPadding;
        return Math.Max(TrayMinWidth, inner * TrayWidthFraction);
    }

    /// <summary>Creates the taskbar window bound to the supplied state.</summary>
    /// <param name="viewModel">The tile collection driving the bar.</param>
    /// <param name="status">The live clock/battery/radio status the right zone binds.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI (e.g. 1.5
    /// for a 150% desktop; see DisplayScale.GetUiScalePercent).</param>
    public TaskbarWindow(TaskbarViewModel viewModel, SystemStatus status, double uiScale = 1.0)
    {
        _uiScale = uiScale;
        InitializeComponent();
        DataContext = viewModel;
        // The right zone binds a different object than the window (compiled
        // bindings: x:DataType="sh:SystemStatus" on the StatusZone subtree).
        StatusZone.DataContext = status;
        // Tap-outside dismissal must ignore taps on the status flyouts, which
        // pop above the bar's rectangle (see OverlayController.OnTappedAt).
        TrackStatusFlyout(WifiButton);
        TrackStatusFlyout(BluetoothButton);
        // Controller navigation moves focus with InputElement.Focus(Directional),
        // which does NOT raise RequestBringIntoView on its own — a tile scrolled
        // out of the strip would take focus invisibly. Ask for it explicitly:
        // Control.BringIntoView() raises Control.RequestBringIntoViewEvent, which
        // the ScrollViewer's ScrollContentPresenter handles by scrolling. Arrow
        // keys are safe to leave to the ScrollViewer's own handler because
        // GamepadNavigation marks them handled from a TUNNEL handler on the
        // window, i.e. before they ever bubble into the scroll viewer.
        // The tray strip is bounded and scrolls the same way, so it needs the
        // same treatment — a tray icon scrolled out of its viewport would
        // otherwise take controller focus invisibly.
        TileScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        TrayScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        // Budget the tray against the XAML's declared width right away, so the
        // strip is bounded even on the path where DockToBottomEdge bails out (no
        // primary screen); the dock recomputes it against the real display width.
        TrayScroller.MaxWidth = ComputeTrayMaxWidth(Width, _contentScale);
        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closed += (_, _) => StopSlide();
        // SizeToContent height: the bar's height can change as tiles appear —
        // keep it flush on the bottom edge (skip during the slide-in, which owns
        // Position until it finishes).
        SizeChanged += (_, _) =>
        {
            if (_slideTimer is null && IsVisible)
            {
                SnapToBottomEdge();
            }
        };

        // Same touch-promotion defense as OverlayWindow:
        // Avalonia never handles raw touch, DefWindowProc promotes the tap into a
        // synthesized mouse click delivered late; eat it here, and let the
        // controller's deferred Close() keep this window alive to do so.
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);
    }

    private int _openStatusFlyouts;

    /// <summary>Whether a Wi-Fi/Bluetooth status flyout is currently open — their
    /// popups sit above the bar, outside the tap-outside hit rectangle.</summary>
    internal bool IsStatusFlyoutOpen => _openStatusFlyouts > 0;

    private void TrackStatusFlyout(Button button)
    {
        if (button.Flyout is not { } flyout)
        {
            return;
        }
        flyout.Opened += (_, _) => _openStatusFlyouts++;
        flyout.Closed += (_, _) => _openStatusFlyouts = Math.Max(0, _openStatusFlyouts - 1);
    }

    /// <summary>Scrolls a newly focused tile into its strip's viewport (app tiles
    /// and tray icons share this handler). Bubbles from the tile buttons; the
    /// scroll viewers themselves are not focusable, and the call is a no-op when
    /// the tile is already fully visible.</summary>
    private void OnStripGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control && control is not ScrollViewer)
        {
            control.BringIntoView();
        }
    }

    private static IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is Interop.NativeMethods.WmMouseMove
                or Interop.NativeMethods.WmLButtonDown
                or Interop.NativeMethods.WmLButtonUp)
        {
            var extra = (uint)Interop.NativeMethods.GetMessageExtraInfo();
            if ((extra & Interop.NativeMethods.MiWpSignatureMask) == Interop.NativeMethods.MiWpSignature)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyTouchScale();
        DockToBottomEdge();
        FindFirstTile()?.Focus(NavigationMethod.Directional);
    }

    /// <summary>Game mode forces every display to 100% scaling, so Avalonia
    /// renders 1 DIP = 1 physical pixel and a DIP-sized bar shrinks to
    /// millimeters on dense handheld panels (device-reported: 1200p Claw).
    /// Render at the desktop's DPI instead: divide the desktop scale factor by
    /// what Avalonia already applies, so the bar has the size it would have on
    /// the user's normal desktop in every mode.</summary>
    private void ApplyTouchScale()
    {
        var screen = Screens?.Primary;
        if (screen is null)
        {
            return;
        }
        // Window scaling, not screen.Scaling — the screens cache is stale after a
        // runtime display-scale flip (see OverlayWindow.DockToRightEdge).
        var factor = Math.Clamp(_uiScale / DesktopScaling, 1.0, 3.0);
        if (Math.Abs(factor - 1.0) < 0.01)
        {
            return;
        }
        Log.Info($"Taskbar UI scale {factor:0.##}x (desktop DPI over current {DesktopScaling:0.##}).");
        _contentScale = factor;
        RootScale.LayoutTransform = new Avalonia.Media.ScaleTransform(factor, factor);
        // Sizes must be final before the dock computes the slide positions.
        UpdateLayout();
    }

    private InputElement? FindFirstTile()
    {
        // The first APP TILE, not the window's first button (that is the home
        // button since the three-zone rebuild).
        foreach (var descendant in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(AppTiles))
        {
            if (descendant is Button { IsEffectivelyVisible: true } button)
            {
                return button;
            }
        }
        // No tiles: fall back to the window-wide walk (lands on the home button).
        foreach (var descendant in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this))
        {
            if (descendant is Button { IsEffectivelyVisible: true } button)
            {
                return button;
            }
        }
        return null;
    }

    private PixelPoint ComputeDockedPosition()
    {
        var screen = Screens?.Primary;
        if (screen is null)
        {
            return Position;
        }
        var bounds = screen.Bounds;
        var heightPx = (int)Math.Ceiling(Height * DesktopScaling);
        return new PixelPoint(bounds.X, bounds.Y + bounds.Height - heightPx);
    }

    private void SnapToBottomEdge() => Position = ComputeDockedPosition();

    /// <summary>Spans the bar across the primary display's bottom edge and slides
    /// it up from below the screen (mirror of the overlay's right-edge slide-in).</summary>
    private void DockToBottomEdge()
    {
        var screen = Screens?.Primary;
        if (screen is null)
        {
            return;
        }

        // Full width: the window spans the display; only the height is
        // content-sized (SizeToContent="Height"). Window scaling, not
        // screen.Scaling — the screens cache is stale after a runtime
        // display-scale flip (see OverlayWindow.DockToRightEdge).
        Width = screen.Bounds.Width / DesktopScaling;
        // Bound the tray strip against the bar that was just sized (ApplyTouchScale
        // ran first, so _contentScale is final): everything to its right is
        // fixed-size and must keep its space no matter how many icons register.
        TrayScroller.MaxWidth = ComputeTrayMaxWidth(Width, _contentScale);
        // The height must be final before the dock computes the slide positions.
        UpdateLayout();

        _slideEnd = ComputeDockedPosition();
        _slideStart = new PixelPoint(_slideEnd.X, screen.Bounds.Y + screen.Bounds.Height);
        Position = _slideStart;

        StopSlide();
        _slideStartedUtc = DateTime.UtcNow;
        // Parameterless ctor + explicit Start: the 3-arg ctor auto-starts.
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slideTimer.Tick += OnSlideTick;
        _slideTimer.Start();
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        const double durationMs = 180;
        var progress = Math.Clamp((DateTime.UtcNow - _slideStartedUtc).TotalMilliseconds / durationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        Position = new PixelPoint(
            _slideEnd.X,
            (int)Math.Round(_slideStart.Y + (_slideEnd.Y - _slideStart.Y) * eased));

        if (progress >= 1)
        {
            StopSlide();
        }
    }

    private void StopSlide()
    {
        if (_slideTimer is null)
        {
            return;
        }
        _slideTimer.Stop();
        _slideTimer.Tick -= OnSlideTick;
        _slideTimer = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismissed?.Invoke();
        }
    }

    private void OnHome(object? sender, RoutedEventArgs e) => HomeRequested?.Invoke();

    private void OnPickWindow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TaskbarEntry entry)
        {
            WindowPicked?.Invoke(entry);
        }
    }

    /// <summary>Tap / A-button / left click → the icon's primary activation.</summary>
    private void OnTrayClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TrayIconEntry entry && sender is Control control)
        {
            TrayIconActivated?.Invoke(entry, false, AnchorAbove(control));
        }
    }

    /// <summary>Right mouse button → the icon's context menu (many tray apps only
    /// respond to this). Button.Click never fires for the right button, so this
    /// rides PointerReleased.</summary>
    private void OnTrayPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right
            && (sender as Control)?.DataContext is TrayIconEntry entry
            && sender is Control control)
        {
            TrayIconActivated?.Invoke(entry, true, AnchorAbove(control));
        }
    }

    /// <summary>Screen position just above the tile's center — where the app
    /// should anchor a popup menu (v4 coordinate protocol).</summary>
    private static PixelPoint AnchorAbove(Control control)
    {
        var point = control.PointToScreen(new Point(control.Bounds.Width / 2, 0));
        return new PixelPoint(point.X, point.Y);
    }

    /// <summary>Gamepad secondary action (X): the context menu of the focused
    /// tray tile. No-op (logged — this is remote-diagnosis territory) when the
    /// focused element isn't a tray icon.</summary>
    internal void RequestTrayContextMenu(InputElement? focused)
    {
        if (focused is Control { DataContext: TrayIconEntry entry } control)
        {
            Log.Info($"Gamepad X: tray context menu for '{entry.Tip}'.");
            TrayIconActivated?.Invoke(entry, true, AnchorAbove(control));
        }
        else
        {
            Log.Info($"Gamepad X: focused element is not a tray icon ({focused?.GetType().Name ?? "none"}, ctx {(focused as Control)?.DataContext?.GetType().Name ?? "-"}).");
        }
    }
}

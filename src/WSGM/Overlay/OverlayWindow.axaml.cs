using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>The fullscreen, controller-friendly overlay window.</summary>
public partial class OverlayWindow : Window
{
    private bool _confirmRestart;
    private bool _confirmShutdown;
    private DispatcherTimer? _confirmResetTimer;
    private DispatcherTimer? _slideTimer;
    private PixelPoint _slideStart;
    private PixelPoint _slideEnd;
    private DateTime _slideStartedUtc;

    /// <summary>Raised when the user requests to start or focus the home application.</summary>
    public event Action? HomeAppRequested;

    /// <summary>Raised when the user requests a desktop/game-mode transition.</summary>
    public event Action? DesktopRequested;

    /// <summary>Raised when the user requests the Settings window.</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when the user requests to leave Steam Big Picture mode.</summary>
    public event Action? ExitBigPictureRequested;

    /// <summary>Raised after the user confirms closing the home application.</summary>
    public event Action? CloseLauncherRequested;

    /// <summary>Raised when the user requests Task Manager.</summary>
    public event Action? TaskManagerRequested;

    /// <summary>Raised when the overlay is dismissed without another action.</summary>
    public event Action? Dismissed;

    private bool _confirmCloseLauncher;

    /// <summary>The control gamepad navigation should land on when the panel opens
    /// or when focus tracking is lost: the ACTIVE tab's first row — HomeAppButton
    /// is invisible on the Tools/Power tabs and focusing it would fall through to
    /// the header close button.</summary>
    internal InputElement DefaultFocusTarget
    {
        get
        {
            var panel = Tabs.SelectedIndex switch
            {
                1 => (Control)PanelTools,
                2 => PanelPower,
                _ => PanelSession,
            };
            foreach (var visual in panel.GetVisualDescendants())
            {
                if (visual is Button { Focusable: true, IsEffectivelyEnabled: true } button
                    && button.IsEffectivelyVisible)
                {
                    return button;
                }
            }
            return HomeAppButton;
        }
    }

    private readonly double _uiScale;

    /// <summary>Creates the overlay window bound to the supplied state.</summary>
    /// <param name="viewModel">The state that drives labels, warnings, and the window picker.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI (e.g. 1.5
    /// for a 150% desktop; see DisplayScale.GetUiScalePercent).</param>
    public OverlayWindow(OverlayViewModel viewModel, double uiScale = 1.0)
    {
        _uiScale = uiScale;
        InitializeComponent();
        DataContext = viewModel;

        Tabs.Tabs = new List<TabStripItem>
        {
            new("Session", Icons.Play, 0),
            new("Tools", Icons.Wrench, 1),
            new("Power", Icons.Power, 2),
        };
        Tabs.SelectionChanged += OnTabSelectionChanged;
        // The panel opens on Session every time. Activated covers both the fresh
        // open (a no-op — the index is already 0) and a re-summon of a still-open
        // panel (hotkey/swipe while browsing another tab).
        Activated += (_, _) => Tabs.SelectedIndex = 0;

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closed += (_, _) => { StopSlide(); ResetConfirms(); };

        // The overlay takes focus Game-Bar-style: the game stops receiving input
        // while the panel is open. Viable because the Steam Input lease keeps the pad
        // readable even with a non-game window focused.
        //
        // Touch pass-through defense: Avalonia never marks touch raw events
        // handled, so WM_POINTER falls to DefWindowProc, which PROMOTES a tap into
        // a synthesized mouse click delivered AFTER the tap's dispatch. The
        // synthesized-message eater in WndProcHook consumes it — as long as this
        // window still exists when it arrives, which is why OverlayController
        // defers Close() by a beat. (The clean fix — consuming the raw touch
        // event — needs Avalonia's [PrivateApi] InputManager, which is stripped
        // from the published reference assemblies.)
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);
    }

    private static IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Eat touch/pen-SYNTHESIZED mouse messages (MI_WP_SIGNATURE). They are
        // promotion ghosts — the tap itself already went through the WM_POINTER
        // pipeline — and around the deferred close they would otherwise double-fire
        // or leak to the window underneath. Real mouse messages pass untouched.
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
        DockToRightEdge();
        HomeAppButton.Focus(NavigationMethod.Directional);
    }

    /// <summary>Selects the previous tab (LB), wrapping from the first to the last.</summary>
    internal void SelectPreviousTab() => Tabs.SelectPrevious();

    /// <summary>Selects the next tab (RB), wrapping from the last to the first.</summary>
    internal void SelectNextTab() => Tabs.SelectNext();

    /// <summary>One selection path for touch, mouse and the LB/RB shoulder buttons:
    /// the TabStrip owns the index, this toggles the three always-alive panels'
    /// visibility and lands controller focus on the new tab's first row (mirrors
    /// SettingsWindow — without it the next D-pad press would fall back to the
    /// window's first focusable, the close button).</summary>
    private void OnTabSelectionChanged(object? sender, TabStripSelectionChangedEventArgs e)
    {
        PanelSession.IsVisible = e.NewIndex == 0;
        PanelTools.IsVisible = e.NewIndex == 1;
        PanelPower.IsVisible = e.NewIndex == 2;

        var panel = e.NewIndex switch
        {
            0 => (Control)PanelSession,
            1 => PanelTools,
            _ => PanelPower,
        };
        FocusFirstControl(panel);
    }

    private static void FocusFirstControl(Control panel)
    {
        foreach (var visual in panel.GetVisualDescendants())
        {
            // TextBoxes are excluded for the same reason D-pad traversal skips
            // them: focusing one pops the touch keyboard.
            if (visual is InputElement { Focusable: true, IsEffectivelyEnabled: true } element
                && element is not TextBox
                && element.IsEffectivelyVisible)
            {
                element.Focus(NavigationMethod.Directional);
                return;
            }
        }
    }

    /// <summary>Fits the panel to the primary display and slides it in from the right.
    /// The window never covers the whole display, so the active game remains visible.</summary>
    private void DockToRightEdge()
    {
        var screen = Screens?.Primary;
        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        // The WINDOW'S scaling, not screen.Scaling: Avalonia's screens cache goes
        // stale when the display scale flips (game/desktop transitions) while no
        // Avalonia window exists to receive the display change — a freshly opened
        // window carries the true current DPI (device-observed: overlay kept the
        // desktop DPI after returning to game mode).
        var scaling = DesktopScaling;
        // Render at the desktop's DPI: game mode forces displays to 100%, which
        // otherwise shrinks this DIP-sized panel to millimeters on dense
        // handheld screens (device-reported). The content lays out in
        // desktop-DIP space (the factor divides the available size), the window
        // takes the scaled-up physical footprint.
        var factor = Math.Clamp(_uiScale / scaling, 1.0, 3.0);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            Core.Log.Info($"Quick access UI scale {factor:0.##}x (desktop DPI over current {scaling:0.##}).");
            RootScale.LayoutTransform = new Avalonia.Media.ScaleTransform(factor, factor);
        }
        // Keep the panel compact on high-DPI handheld displays. A 360-DIP panel
        // remains comfortably touchable without taking half the game view.
        var panelWidth = Math.Min(360d, Math.Max(320d, bounds.Width / scaling / factor * 0.30)) * factor;
        Width = panelWidth;
        Height = bounds.Height / scaling;

        var panelWidthPx = (int)Math.Ceiling(panelWidth * scaling);
        _slideEnd = new PixelPoint(bounds.X + bounds.Width - panelWidthPx, bounds.Y);
        _slideStart = new PixelPoint(bounds.X + bounds.Width, bounds.Y);
        Position = _slideStart;

        StopSlide();
        _slideStartedUtc = DateTime.UtcNow;
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slideTimer.Tick += OnSlideTick;
        _slideTimer.Start();
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        const double durationMs = 180;
        var progress = Math.Clamp((DateTime.UtcNow - _slideStartedUtc).TotalMilliseconds / durationMs, 0, 1);
        // Cubic ease-out keeps the movement quick without a sharp stop.
        var eased = 1 - Math.Pow(1 - progress, 3);
        Position = new PixelPoint(
            (int)Math.Round(_slideStart.X + (_slideEnd.X - _slideStart.X) * eased),
            _slideEnd.Y);

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

    private void OnHomeApp(object? sender, RoutedEventArgs e) => HomeAppRequested?.Invoke();
    private void OnDesktop(object? sender, RoutedEventArgs e) => DesktopRequested?.Invoke();
    private void OnSettings(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnExitBigPicture(object? sender, RoutedEventArgs e) => ExitBigPictureRequested?.Invoke();
    private void OnTaskManager(object? sender, RoutedEventArgs e) => TaskManagerRequested?.Invoke();
    private void OnClose(object? sender, RoutedEventArgs e) => Dismissed?.Invoke();

    private async void OnCopyDeelevationCommand(object? sender, RoutedEventArgs e)
    {
        var helperPath = DeelevationCommand.HelperPathForCurrentDeployment();
        if (!System.IO.File.Exists(helperPath))
        {
            DeelevationCommandTitle.Title = "De-elevation helper missing";
            Log.Warn($"Cannot copy Steam de-elevation command; helper not found: {helperPath}");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            DeelevationCommandTitle.Title = "Clipboard unavailable";
            Log.Warn("Cannot copy Steam de-elevation command; no clipboard is available.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(DeelevationCommand.SteamLaunchOptions(helperPath));
            DeelevationCommandTitle.Title = "Copied to clipboard";
            Log.Info("Copied Steam de-elevation launch-option command to clipboard.");
            await DismissAfterCopyFeedback();
        }
        catch (Exception ex)
        {
            DeelevationCommandTitle.Title = "Clipboard copy failed";
            Log.Error("Could not copy Steam de-elevation command", ex);
        }
    }

    // A copied command means the user is heading to Steam to paste it: show the
    // "Copied" confirmation briefly, then dismiss the panel (which restores Steam
    // to the foreground). Same rule as the actions that open a window.
    private static async System.Threading.Tasks.Task FeedbackDelay()
        => await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(700));

    private async System.Threading.Tasks.Task DismissAfterCopyFeedback()
    {
        await FeedbackDelay();
        Dismissed?.Invoke();
    }

    private async void OnCopySteamInputBlockCommand(object? sender, RoutedEventArgs e)
    {
        var helperPath = SteamInputLeaseCommand.HelperPathForCurrentDeployment();
        if (!System.IO.File.Exists(helperPath))
        {
            SteamInputBlockCommandTitle.Title = "Steam Input wrapper missing";
            Log.Warn($"Cannot copy Steam Input block command; wrapper not found: {helperPath}");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            SteamInputBlockCommandTitle.Title = "Clipboard unavailable";
            Log.Warn("Cannot copy Steam Input block command; no clipboard is available.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(SteamInputLeaseCommand.SteamLaunchOptions(helperPath));
            SteamInputBlockCommandTitle.Title = "Copied to clipboard";
            Log.Info("Copied Steam Input block launch-option command to clipboard.");
            await DismissAfterCopyFeedback();
        }
        catch (Exception ex)
        {
            SteamInputBlockCommandTitle.Title = "Clipboard copy failed";
            Log.Error("Could not copy Steam Input block command", ex);
        }
    }

    private void OnCloseLauncher(object? sender, RoutedEventArgs e)
    {
        if (!_confirmCloseLauncher)
        {
            _confirmCloseLauncher = true;
            // Via the view model: the title is bound to CloseLauncherText, and a
            // direct Text write here would silently be overwritten by any
            // HomeAppName-triggered re-evaluation of that binding.
            if (DataContext is OverlayViewModel vm)
            {
                vm.ConfirmingCloseLauncher = true;
            }
            ArmConfirmReset();
            return;
        }
        ResetConfirms();
        CloseLauncherRequested?.Invoke();
    }

    private void OnSleep(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        Core.PowerActions.Sleep();
    }

    private void OnRestart(object? sender, RoutedEventArgs e)
    {
        if (!_confirmRestart)
        {
            _confirmRestart = true;
            RestartButton.Title = "Really?";
            ArmConfirmReset();
            return;
        }
        Core.PowerActions.Restart();
    }

    private void OnShutdown(object? sender, RoutedEventArgs e)
    {
        if (!_confirmShutdown)
        {
            _confirmShutdown = true;
            ShutdownButton.Title = "Really?";
            ArmConfirmReset();
            return;
        }
        Core.PowerActions.Shutdown();
    }

    /// <summary>Armed "Really?" confirms revert on their own — after ~5 s and when
    /// the panel closes — so a stray second press minutes later cannot restart or
    /// shut down the device.</summary>
    private void ArmConfirmReset()
    {
        if (_confirmResetTimer is null)
        {
            // Parameterless ctor + explicit Start: Avalonia's 3-arg
            // DispatcherTimer ctor auto-starts, which silently defeats every
            // "start it if it isn't running" guard.
            _confirmResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _confirmResetTimer.Tick += (_, _) => ResetConfirms();
        }
        _confirmResetTimer.Stop();
        _confirmResetTimer.Start();
    }

    private void ResetConfirms()
    {
        _confirmResetTimer?.Stop();
        _confirmRestart = false;
        _confirmShutdown = false;
        _confirmCloseLauncher = false;
        if (DataContext is OverlayViewModel vm)
        {
            vm.ConfirmingCloseLauncher = false;
        }
        RestartButton.Title = "Restart";
        ShutdownButton.Title = "Shut down";
    }
}

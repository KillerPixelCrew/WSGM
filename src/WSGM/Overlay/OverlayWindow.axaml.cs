using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The fullscreen, controller-friendly overlay window.</summary>
public partial class OverlayWindow : Window
{
    /// <summary>Raised when a Tools sub-view is torn down so auxiliary peer windows close too.</summary>
    public event Action? SubViewClosed;
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

    /// <summary>Raised when the keep-awake row is activated (toggle the manual hold).</summary>
    public event Action? KeepAwakeToggleRequested;

    /// <summary>Raised when an idle-timeout row is activated (cycle to the next preset).</summary>
    public event Action<Core.PowerTimeoutKind>? PowerTimeoutCycleRequested;

    /// <summary>Raised when the overlay is dismissed without another action.</summary>
    public event Action? Dismissed;

    private bool _confirmCloseLauncher;

    /// <summary>Set once this window instance is gone. Post-action feedback delays
    /// outlive the window they started on, and a dismissal raised from a dead window
    /// would close whatever panel is on screen by then.</summary>
    private bool _closed;

    private Shell.SdFormatManager? _format;
    private FormatTargetEntry? _pendingTarget;

    /// <summary>Whether the in-place Format/Add-library sub-view is showing. While
    /// it is, LB/RB tab switching is suppressed and B cancels the sub-view rather
    /// than closing the overlay.</summary>
    internal bool InFormatSubView { get; private set; }

    /// <summary>Whether the Library Tabs builder sub-view is showing.
    /// Same LB/RB-suppress + B-cancels rules as the format sub-view.</summary>
    internal bool InLibraryTabsSubView { get; private set; }

    /// <summary>Whether the SD-card manager sub-view is showing.</summary>
    internal bool InCardManagerSubView { get; private set; }

    /// <summary>Whether the SteamGridDB artwork picker sub-view is showing.</summary>
    internal bool InArtworkSubView { get; private set; }

    /// <summary>Whether the launch-fix game picker sub-view is showing.</summary>
    internal bool InLaunchWrapperSubView { get; private set; }

    /// <summary>Whether the wake-lock holder list sub-view is showing. It belongs to
    /// the Power tab, so leaving it restores that panel rather than Tools.</summary>
    internal bool InWakeLockSubView { get; private set; }

    /// <summary>The launch fix waiting on the user to pick a game, and the button
    /// whose title reports the outcome.</summary>
    private (LaunchWrapperMode Mode, CardButton Button)? _pendingLaunchFix;

    /// <summary>Set while the peer keyboard owns activation so focus handoff does not
    /// look like a fresh overlay summons and discard the active workflow.</summary>
    internal bool KeyboardOwnsFocus { get; set; }

    /// <summary>Whether any in-place Tools sub-view owns the surface.</summary>
    private bool AnySubView
        => InFormatSubView || InLibraryTabsSubView || InCardManagerSubView || InArtworkSubView
           || InLaunchWrapperSubView || InWakeLockSubView;

    /// <summary>Gives the overlay the shared removable-storage format manager so
    /// its Tools sub-view can drive it. Called by the controller right after
    /// construction (the manager outlives the window).</summary>
    /// <param name="format">The controller-owned format manager.</param>
    internal void AttachFormatManager(Shell.SdFormatManager format)
    {
        _format = format;
        PanelFormat.DataContext = format;
    }

    // The library name the confirm step will format with. Held here rather than in a
    // TextBox: the row is press-to-edit (see the XAML), matching the tab editor and
    // card rename, and the peer keyboard window owns the typing.
    private string _formatName = "";

    /// <summary>Shows the name on its row so the value is visible without focusing
    /// anything, the way every other name row in the panel reads.</summary>
    private void SetFormatName(string value)
    {
        _formatName = value ?? "";
        FormatNameButton.Description = _formatName.Length > 0 ? _formatName : "(required)";
    }

    // Controller text entry for the library name goes through the peer keyboard
    // window (KeyboardService), like every other game-mode text field.
    private void OnFormatEditName(object? sender, RoutedEventArgs e)
    {
        if (!Core.KeyboardService.Request("Name (volume and Steam library)",
                _formatName, 32, SetFormatName))
        {
            // No keyboard window means no way to type on a controller; say so instead
            // of leaving a row that silently does nothing when pressed.
            Core.Log.Warn("Format: no on-screen keyboard available for the library name.");
        }
    }

    /// <summary>The control gamepad navigation should land on when the panel opens
    /// or when focus tracking is lost: the ACTIVE tab's first row — HomeAppButton
    /// is invisible on the Tools/Power tabs and focusing it would fall through to
    /// the header close button.</summary>
    internal InputElement DefaultFocusTarget
    {
        get
        {
            // The Tools sub-views live inside the Tools tab; focus lands there while
            // one is open, not on the tab's ordinary rows.
            if (AnySubView)
            {
                var host = InLibraryTabsSubView ? (Control)LibraryTabsHost
                    : InCardManagerSubView ? CardManagerHost
                    : InArtworkSubView ? ArtworkHost
                    : InLaunchWrapperSubView ? LaunchWrapperHost
                    : InWakeLockSubView ? WakeLockHost
                    : PanelFormat;
                foreach (var visual in host.GetVisualDescendants())
                {
                    if (visual is Button { Focusable: true, IsEffectivelyEnabled: true } b
                        && b.IsEffectivelyVisible)
                    {
                        return b;
                    }
                }
            }
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

    // The tab the user last selected, restored on the next open. Static because the
    // overlay window is recreated per open; deliberately not persisted to config —
    // a tab switch must not cost a disk write.
    private static int _lastSelectedTab;

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
        // The panel reopens on the tab the user last had selected (static: the
        // window is recreated per open). Activated covers both the fresh open and a
        // re-summon of a still-open panel (hotkey/swipe while browsing another tab).
        // Any open sub-view is torn down with it.
        Activated += (_, _) =>
        {
            if (KeyboardOwnsFocus)
            {
                return;
            }
            LeaveFormatSubView();
            LeaveLibraryTabsSubView();
            LeaveCardManagerSubView();
            LeaveArtworkSubView();
            LeaveLaunchWrapperSubView();
            LeaveWakeLockSubView();
            Tabs.SelectedIndex = _lastSelectedTab;
        };

        LibraryTabsHost.CloseRequested += LeaveLibraryTabsSubView;
        CardManagerHost.CloseRequested += LeaveCardManagerSubView;
        CardManagerHost.FormatRequested += OnFormatFromCardManager;
        ArtworkHost.CloseRequested += LeaveArtworkSubView;
        LaunchWrapperHost.CloseRequested += LeaveLaunchWrapperSubView;
        LaunchWrapperHost.Picked += OnLaunchFixGamePicked;
        LaunchWrapperHost.CustomPicked += OnCustomLaunchGamePicked;
        WakeLockHost.CloseRequested += LeaveWakeLockSubView;
        InitializeLaunchFixLabels(viewModel);

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closed += (_, _) => { _closed = true; StopSlide(); ResetConfirms(); };

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
        MaybeAutoSyncTabs();
    }

    /// <summary>Selects the previous tab (LB), wrapping from the first to the
    /// last. Suppressed while the Format sub-view owns the surface — a bumper
    /// press must not switch tabs out from under it.</summary>
    internal void SelectPreviousTab()
    {
        if (!AnySubView)
        {
            Tabs.SelectPrevious();
        }
    }

    /// <summary>Selects the next tab (RB). Suppressed while a Tools sub-view is open.</summary>
    internal void SelectNextTab()
    {
        if (!AnySubView)
        {
            Tabs.SelectNext();
        }
    }

    /// <summary>The controller's Back/B action consults this first: when the
    /// Format sub-view is open, Back returns to the Tools list instead of
    /// closing the whole overlay. A format already running keeps running — only
    /// the view resets. Returns true when it handled the press.</summary>
    internal bool TryCancelSubView()
    {
        if (InLibraryTabsSubView)
        {
            // The builder handles Back internally (popping a level); at its root it
            // raises CloseRequested, which leaves the sub-view.
            return LibraryTabsHost.Back();
        }
        if (InCardManagerSubView)
        {
            return CardManagerHost.Back();
        }
        if (InArtworkSubView)
        {
            return ArtworkHost.Back();
        }
        if (InLaunchWrapperSubView)
        {
            return LaunchWrapperHost.Back();
        }
        if (InWakeLockSubView)
        {
            return WakeLockHost.Back();
        }
        if (!InFormatSubView)
        {
            return false;
        }
        LeaveFormatSubViewToOrigin();
        return true;
    }

    /// <summary>One selection path for touch, mouse and the LB/RB shoulder buttons:
    /// the TabStrip owns the index, this toggles the three always-alive panels'
    /// visibility and lands controller focus on the new tab's first row (mirrors
    /// SettingsWindow — without it the next D-pad press would fall back to the
    /// window's first focusable, the close button).</summary>
    private void OnTabSelectionChanged(object? sender, TabStripSelectionChangedEventArgs e)
    {
        // Switching tabs (touch or click can still do it) leaves any open
        // sub-view; the format run, if one is going, continues in the manager.
        if (InFormatSubView)
        {
            LeaveFormatSubView();
        }
        if (InLibraryTabsSubView)
        {
            LeaveLibraryTabsSubView();
        }
        if (InCardManagerSubView)
        {
            LeaveCardManagerSubView();
        }
        if (InArtworkSubView)
        {
            LeaveArtworkSubView();
        }
        if (InLaunchWrapperSubView)
        {
            LeaveLaunchWrapperSubView();
        }
        if (InWakeLockSubView)
        {
            LeaveWakeLockSubView();
        }
        _lastSelectedTab = e.NewIndex;
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

    // ---- Per-game launch fixes ----

    /// <summary>Re-labels the launch-fix rows for the current CEF state. Called when
    /// a config reload flips live configuration on or off under an open panel.</summary>
    internal void RefreshLaunchFixLabels()
    {
        if (DataContext is OverlayViewModel viewModel)
        {
            InitializeLaunchFixLabels(viewModel);
        }
    }

    // Set from the view model, because the same buttons do two different things:
    // with CEF on they configure the game in the running Steam client, with CEF off
    // they fall back to copying the command for the user to paste.
    private void InitializeLaunchFixLabels(OverlayViewModel viewModel)
    {
        var live = viewModel.ConfigureLaunchOptionsLive;
        DeelevateFixButton.Title = live ? "Fix: run without admin" : "Copy de-elevation command";
        DeelevateFixButton.Description = live
            ? "For games that refuse to start under elevated Steam"
            : "Paste into a game's Steam launch options";
        InputLeaseFixButton.Title = live ? "Fix: give the game the controller" : "Copy Steam Input block command";
        InputLeaseFixButton.Description = "For games that read the controller themselves";
        BothFixesButton.Title = live ? "Fix: both of the above" : "Copy combined command";
        BothFixesButton.Description = "No admin, and the game owns the controller";
        RemoveFixesButton.Title = "Restore original launch action";
        RemoveFixesButton.Description = "Remove WSGM changes and restore the original";
    }

    private void OnApplyDeelevation(object? sender, RoutedEventArgs e)
        => StartLaunchFix(LaunchWrapperMode.Deelevate, DeelevateFixButton);

    private void OnApplySteamInputBlock(object? sender, RoutedEventArgs e)
        => StartLaunchFix(LaunchWrapperMode.InputLease, InputLeaseFixButton);

    private void OnApplyBothWrappers(object? sender, RoutedEventArgs e)
        => StartLaunchFix(LaunchWrapperMode.Both, BothFixesButton);

    private void OnRemoveLaunchWrappers(object? sender, RoutedEventArgs e)
        => StartLaunchFix(LaunchWrapperMode.None, RemoveFixesButton);

    private async void OnPickCustomLaunchAction(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a custom launch action",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Launch actions")
                {
                    Patterns = ["*.exe", "*.cmd", "*.bat", "*.ps1"],
                },
            ],
        });
        if (_closed || files.Count == 0 || !files[0].Path.IsFile)
        {
            return;
        }
        var path = files[0].Path.LocalPath;
        if (!SteamCustomLaunchCommand.IsSupported(path))
        {
            CustomLaunchButton.Title = "Unsupported file type";
            return;
        }
        LaunchWrapperHost.OpenCustom(path);
        EnterLaunchWrapperSubView();
    }

    private void OnCustomLaunchGamePicked(
        string path, string arguments, SteamCollections.AppInfo game)
        => _ = ApplyCustomLaunchToAsync(path, arguments, game, CustomLaunchButton);

    private async System.Threading.Tasks.Task ApplyCustomLaunchToAsync(
        string path, string arguments, SteamCollections.AppInfo game, CardButton button)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                button.Title = "File is no longer available";
                return;
            }
            var details = await SteamLaunchConfig.ReadAsync(game.AppId);
            if (details is null)
            {
                button.Title = "Steam didn't answer";
                return;
            }
            var existing = await LibraryTabManager.FindLaunchWrapperAsync(game.AppId);
            var originals = existing is null
                ? (details.ShortcutTarget,
                    game.Shortcut ? details.ShortcutArguments : details.LaunchOptions,
                    details.ShortcutStartDir)
                : (existing.OriginalTarget, existing.OriginalLaunchOptions, existing.OriginalStartDir);
            var snapshot = existing ?? new LaunchWrapperConfig
            {
                AppId = game.AppId,
                IsShortcut = game.Shortcut,
                OriginalTarget = originals.Item1,
                OriginalLaunchOptions = originals.Item2,
                OriginalStartDir = originals.Item3,
            };
            snapshot.Kind = LaunchConfigurationKind.CustomAction;
            snapshot.Mode = LaunchWrapperMode.None;
            snapshot.CustomActionPath = path;
            snapshot.CustomArguments = arguments;
            snapshot.Name = game.Name;
            if (existing is null)
            {
                // Persist the only restoration copy before Steam destroys a shortcut Target.
                await LibraryTabManager.RememberLaunchWrapperAsync(snapshot);
            }
            var result = await SteamLaunchConfig.ApplyCustomAsync(
                game.AppId, game.Shortcut, path, arguments);
            if (!result.Ok && existing is null)
            {
                await LibraryTabManager.ForgetLaunchWrapperAsync(game.AppId);
            }
            else if (result.Ok && existing is not null)
            {
                await LibraryTabManager.RememberLaunchWrapperAsync(snapshot);
            }
            button.Title = result.Ok ? $"Applied to {game.Name}" : result.Detail;
            if (result.Ok)
            {
                Log.Info($"Custom launch action written for {game.Name} ({game.AppId}).");
                LeaveLaunchWrapperSubView();
                await DismissAfterCopyFeedback();
            }
        }
        catch (Exception ex)
        {
            button.Title = "Couldn't configure launch action";
            Log.Error($"Could not configure custom launch action for {game.AppId}", ex);
        }
    }

    private void StartLaunchFix(LaunchWrapperMode mode, CardButton button)
    {
        // Resolve the lease route ONCE, here, before anything branches. The
        // clipboard text, the value written into Steam and the snapshot persisted
        // into config all flow from this, so deciding it in one place is what stops
        // them disagreeing about how a given game blocks Steam Input.
        mode = LaunchWrapperCommand.ForCurrentInputMode(
            mode, (DataContext as OverlayViewModel)?.InputLeaseUsesShim ?? true);
        var helperPath = LaunchWrapperCommand.HelperPathForCurrentDeployment();
        if (mode != LaunchWrapperMode.None && !System.IO.File.Exists(helperPath))
        {
            button.Title = "Launch wrapper missing";
            Log.Warn($"Cannot configure a launch fix; wrapper not found: {helperPath}");
            return;
        }

        if (DataContext is not OverlayViewModel { ConfigureLaunchOptionsLive: true })
        {
            _ = CopyLaunchCommandAsync(mode, button, helperPath);
            return;
        }
        _ = ApplyLaunchFixAsync(mode, button);
    }

    private async System.Threading.Tasks.Task CopyLaunchCommandAsync(
        LaunchWrapperMode mode, CardButton button, string helperPath)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            button.Title = "Clipboard unavailable";
            Log.Warn("Cannot copy a launch command; no clipboard is available.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(LaunchWrapperCommand.SteamLaunchOptions(helperPath, mode));
            button.Title = "Copied to clipboard";
            Log.Info($"Copied the {mode} launch-option command to clipboard.");
            await DismissAfterCopyFeedback();
        }
        catch (Exception ex)
        {
            button.Title = "Clipboard copy failed";
            Log.Error("Could not copy the launch command", ex);
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
        if (_closed)
        {
            // The panel was dismissed and re-opened while the confirmation showed:
            // the controller wires Dismissed per window instance, so this stale
            // window would close the live panel out from under the user.
            Log.Info("Launch-fix feedback dismissal skipped — its panel is already closed.");
            return;
        }
        Dismissed?.Invoke();
    }

    private async System.Threading.Tasks.Task ApplyLaunchFixAsync(
        LaunchWrapperMode mode, CardButton button)
    {
        button.Title = "Asking Steam…";
        var appId = await SteamPageBridge.GetCurrentAppIdAsync();
        if (appId <= 0)
        {
            // Nothing on screen identifies a game (the library root, or a Steam that
            // did not answer): ask which one instead of guessing.
            _pendingLaunchFix = (mode, button);
            LaunchWrapperHost.Open(mode == LaunchWrapperMode.None
                ? "Remove launch fixes"
                : "Apply launch fix");
            EnterLaunchWrapperSubView();
            return;
        }

        var games = await SafeGameLookupAsync();
        var match = games.FirstOrDefault(g => g.AppId == appId);
        await ApplyLaunchFixToAsync(
            mode, button, appId, match?.Name ?? appId.ToString(CultureInfo.InvariantCulture),
            match?.Shortcut ?? appId >= 0x80000000L);
    }

    private async System.Threading.Tasks.Task ApplyLaunchFixToAsync(
        LaunchWrapperMode mode, CardButton button, long appId, string name, bool isShortcut)
    {
        try
        {
            var current = await SteamLaunchConfig.ReadAsync(appId);
            if (current is not { } details)
            {
                button.Title = "Steam didn't answer";
                return;
            }

            LaunchConfigResult result;
            if (mode == LaunchWrapperMode.None)
            {
                var snapshot = await LibraryTabManager.FindLaunchWrapperAsync(appId);
                if (snapshot is null)
                {
                    button.Title = $"No fix applied to {name}";
                    return;
                }
                result = await SteamLaunchConfig.RestoreAsync(snapshot);
                if (result.Ok)
                {
                    await LibraryTabManager.ForgetLaunchWrapperAsync(appId);
                }
            }
            else
            {
                var existing = await LibraryTabManager.FindLaunchWrapperAsync(appId);
                // Snapshot BEFORE the write: configuring a shortcut overwrites its
                // Target, so this becomes the only record of the real program. When
                // the game is already wrapped (the user is switching modes) the
                // values on screen are WSGM's own — keep the first snapshot instead,
                // and when there is none (the command was pasted by hand, or the
                // config was reset) unwrap them rather than recording the wrapper as
                // the "original", which would make Remove restore the wrapper itself.
                var originals = SteamLaunchConfig.OriginalsFrom(isShortcut, details);
                var wrapped = SteamLaunchConfig.ModeFor(isShortcut, details) != LaunchWrapperMode.None;
                if (wrapped && existing is null && isShortcut
                    && string.IsNullOrWhiteSpace(originals.Target))
                {
                    // A wrapped shortcut whose real program cannot be recovered has
                    // no restorable state; writing WSGM's own values as the original
                    // would strand it permanently.
                    button.Title = "Can't read the original program";
                    Log.Warn($"Launch fix refused for {name} ({appId}): the shortcut is already "
                        + "wrapped and its original target could not be recovered.");
                    return;
                }
                var snapshot = existing ?? new LaunchWrapperConfig
                    {
                        AppId = appId,
                        IsShortcut = isShortcut,
                        OriginalTarget = originals.Target,
                        OriginalLaunchOptions = originals.LaunchOptions,
                        OriginalStartDir = originals.StartDir,
                    };
                snapshot.Kind = LaunchConfigurationKind.Wrapper;
                snapshot.Mode = mode;
                snapshot.CustomActionPath = "";
                snapshot.CustomArguments = "";
                snapshot.Name = name;
                await LibraryTabManager.RememberLaunchWrapperAsync(snapshot);

                result = await SteamLaunchConfig.ApplyAsync(appId, isShortcut, mode, details);
                if (!result.Ok && existing is null)
                {
                    // Nothing was changed in Steam, so leave no snapshot behind
                    // claiming otherwise — unless one was already there.
                    await LibraryTabManager.ForgetLaunchWrapperAsync(appId);
                }
            }

            button.Title = result.Ok
                ? mode == LaunchWrapperMode.None ? $"Removed from {name}" : $"Applied to {name}"
                : result.Detail;
            if (result.Ok)
            {
                Log.Info($"Launch fix {mode} written for {name} ({appId}).");
                await DismissAfterCopyFeedback();
            }
        }
        catch (Exception ex)
        {
            button.Title = "Couldn't reach Steam";
            Log.Error($"Could not configure the launch fix for {appId}", ex);
        }
    }

    private static async System.Threading.Tasks.Task<IReadOnlyList<SteamCollections.AppInfo>>
        SafeGameLookupAsync()
    {
        try { return await SteamCollections.GetGamesAsync(); }
        catch (Exception ex)
        {
            Log.Warn($"Could not list games while configuring a launch fix: {ex.Message}");
            return [];
        }
    }

    private void OnLaunchFixGamePicked(SteamCollections.AppInfo game)
    {
        if (_pendingLaunchFix is not { } pending)
        {
            LeaveLaunchWrapperSubView();
            return;
        }
        LeaveLaunchWrapperSubView();
        _ = ApplyLaunchFixToAsync(pending.Mode, pending.Button, game.AppId, game.Name, game.Shortcut);
    }

    private void OnShowWakeLockHolders(object? sender, RoutedEventArgs e)
    {
        WakeLockHost.Open();
        EnterWakeLockSubView();
    }

    private void EnterWakeLockSubView()
    {
        InWakeLockSubView = true;
        PanelPower.IsVisible = false;
        WakeLockHost.IsVisible = true;
        FocusFirstControl(WakeLockHost);
    }

    private void LeaveWakeLockSubView()
    {
        if (!InWakeLockSubView)
        {
            return;
        }
        InWakeLockSubView = false;
        SubViewClosed?.Invoke();
        WakeLockHost.IsVisible = false;
        // Unlike the Tools sub-views this one belongs to the Power tab, so it is
        // that panel that comes back.
        PanelPower.IsVisible = Tabs.SelectedIndex == 2;
        if (PanelPower.IsVisible)
        {
            FocusFirstControl(PanelPower);
        }
    }

    private void EnterLaunchWrapperSubView()
    {
        InLaunchWrapperSubView = true;
        PanelTools.IsVisible = false;
        LaunchWrapperHost.IsVisible = true;
        FocusFirstControl(LaunchWrapperHost);
    }

    private void LeaveLaunchWrapperSubView()
    {
        if (!InLaunchWrapperSubView)
        {
            return;
        }
        InLaunchWrapperSubView = false;
        _pendingLaunchFix = null;
        // Clears the "Asking Steam…" title left on whichever button opened the
        // picker. A pick re-writes it moments later with the real outcome.
        if (DataContext is OverlayViewModel viewModel)
        {
            InitializeLaunchFixLabels(viewModel);
        }
        SubViewClosed?.Invoke();
        LaunchWrapperHost.IsVisible = false;
        PanelTools.IsVisible = Tabs.SelectedIndex == 1;
        if (PanelTools.IsVisible)
        {
            FocusFirstControl(PanelTools);
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

    // ---- Format SD Card / Add Steam Library sub-view ----

    private void OnFormatSdCard(object? sender, RoutedEventArgs e)
    {
        if (_format is null)
        {
            return;
        }
        FormatHeading.Text = "Format SD Card";
        ShowFormatState(pick: true, confirm: false, progress: false);
        EnterFormatSubView();
        _format.Refresh();
    }

    private void OnFormatRefresh(object? sender, RoutedEventArgs e) => _format?.Refresh();

    private void OnFormatTargetChosen(object? sender, RoutedEventArgs e)
    {
        if (_format is null || _format.Busy
            || (sender as Control)?.DataContext is not FormatTargetEntry entry)
        {
            return;
        }
        _pendingTarget = entry;
        FormatConfirmTarget.Text = $"Erase {entry.Name}?";
        FormatConfirmDetail.Text = entry.Detail;
        SetFormatName(Shell.SdFormatManager.DefaultLabel);
        ShowFormatState(pick: false, confirm: true, progress: false);
        FocusFirstControl(FormatConfirmView);
    }

    private async void OnFormatConfirmed(object? sender, RoutedEventArgs e)
    {
        if (_format is null || _pendingTarget is null)
        {
            return;
        }
        var target = _pendingTarget;
        var name = _formatName;
        ShowFormatState(pick: false, confirm: false, progress: true);
        ScrollFormatToTop();
        await _format.FormatAsync(target, name);
    }

    private void OnFormatCancel(object? sender, RoutedEventArgs e) => LeaveFormatSubViewToOrigin();

    private Shell.LibraryTabManager? _libraryTabs;
    private Shell.LibraryTabManager LibraryTabs => _libraryTabs ??= new Shell.LibraryTabManager();

    // Debounce for the on-open auto-sync, shared across overlay instances (the
    // window is recreated per open). Auto-sync keeps card and category tabs current
    // without the user pressing the button; the button forces an immediate sync.
    private static long _lastAutoTabSyncTicks;
    private static readonly TimeSpan AutoTabSyncInterval = TimeSpan.FromMinutes(10);

    /// <summary>Opens the Library Tabs builder sub-view (the gamepad-driven
    /// custom-tab UI). Its own "Sync now" materializes the tabs.</summary>
    private void OnLibraryTabs(object? sender, RoutedEventArgs e)
    {
        LibraryTabsHost.Open(LibraryTabs);
        EnterLibraryTabsSubView();
    }

    /// <summary>Opens the SD-card library manager sub-view.</summary>
    private void OnCardManager(object? sender, RoutedEventArgs e)
    {
        CardManagerHost.ShowFormat = _format is not null
            && DataContext is OverlayViewModel { ShowSdCard: true };
        CardManagerHost.Open(LibraryTabs);
        EnterCardManagerSubView();
    }

    /// <summary>Format picked from inside the Card Manager: hand the surface over to
    /// the format panel. Both are Tools sub-views, so the old one must be left first
    /// or two would claim the surface at once.</summary>
    private void OnFormatFromCardManager()
    {
        LeaveCardManagerSubView();
        OnFormatSdCard(this, new RoutedEventArgs());
        // Set AFTER entering: OnFormatSdCard runs the ordinary enter path, and
        // LeaveFormatSubView clears this on every exit.
        _formatReturnsToCards = true;
    }

    /// <summary>Whether leaving the format panel should land back in the Card Manager
    /// rather than the Tools list, because that is where the user opened it from.</summary>
    private bool _formatReturnsToCards;

    /// <summary>Cancel/Back out of the format panel, returning to whichever surface
    /// opened it. Re-opening the Card Manager also rescans, so a card that was just
    /// formatted shows up straight away.</summary>
    private void LeaveFormatSubViewToOrigin()
    {
        var toCards = _formatReturnsToCards;
        LeaveFormatSubView();
        if (toCards)
        {
            OnCardManager(this, new RoutedEventArgs());
        }
    }

    private void EnterCardManagerSubView()
    {
        InCardManagerSubView = true;
        PanelTools.IsVisible = false;
        CardManagerHost.IsVisible = true;
        FocusFirstControl(CardManagerHost);
    }

    private void LeaveCardManagerSubView()
    {
        if (!InCardManagerSubView)
        {
            return;
        }
        InCardManagerSubView = false;
        SubViewClosed?.Invoke();
        CardManagerHost.IsVisible = false;
        PanelTools.IsVisible = Tabs.SelectedIndex == 1;
        if (PanelTools.IsVisible)
        {
            FocusFirstControl(PanelTools);
        }
    }

    private void EnterLibraryTabsSubView()
    {
        InLibraryTabsSubView = true;
        PanelTools.IsVisible = false;
        LibraryTabsHost.IsVisible = true;
        FocusFirstControl(LibraryTabsHost);
    }

    private void LeaveLibraryTabsSubView()
    {
        if (!InLibraryTabsSubView)
        {
            return;
        }
        InLibraryTabsSubView = false;
        SubViewClosed?.Invoke();
        LibraryTabsHost.IsVisible = false;
        PanelTools.IsVisible = Tabs.SelectedIndex == 1;
        if (PanelTools.IsVisible)
        {
            FocusFirstControl(PanelTools);
        }
    }

    /// <summary>Opens the SteamGridDB artwork picker sub-view.</summary>
    private void OnChangeArtwork(object? sender, RoutedEventArgs e)
    {
        ArtworkHost.Open();
        EnterArtworkSubView();
    }

    private void EnterArtworkSubView()
    {
        InArtworkSubView = true;
        PanelTools.IsVisible = false;
        ArtworkHost.IsVisible = true;
        FocusFirstControl(ArtworkHost);
    }

    private void LeaveArtworkSubView()
    {
        if (!InArtworkSubView)
        {
            return;
        }
        InArtworkSubView = false;
        SubViewClosed?.Invoke();
        ArtworkHost.Close();
        ArtworkHost.IsVisible = false;
        PanelTools.IsVisible = Tabs.SelectedIndex == 1;
        if (PanelTools.IsVisible)
        {
            FocusFirstControl(PanelTools);
        }
    }

    /// <summary>Fire-and-forget background sync when the overlay opens, throttled so
    /// it runs at most once per <see cref="AutoTabSyncInterval"/>. Best-effort — a
    /// closed Steam simply leaves the tabs for the next open.</summary>
    private void MaybeAutoSyncTabs()
    {
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastAutoTabSyncTicks)
            < AutoTabSyncInterval.Ticks)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await LibraryTabs.SyncAllDetailedAsync();
                Log.Info($"Library tabs auto-sync: {result.Summary}");
                if (result.Success)
                {
                    Interlocked.Exchange(ref _lastAutoTabSyncTicks, DateTime.UtcNow.Ticks);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Library tabs auto-sync failed: {ex.Message}");
            }
        });
    }

    private async void OnAddLibrary(object? sender, RoutedEventArgs e)
    {
        if (_format is null)
        {
            return;
        }
        // A native folder picker: for network shares / second internal drives on
        // DIY Steam machines, where the user has a pointer. Not gamepad-driven —
        // the format flow is the controller-only path.
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Choose a folder for the Steam library",
                AllowMultiple = false,
            });
        if (folders.Count == 0)
        {
            return;
        }
        var path = folders[0].Path.IsAbsoluteUri && folders[0].Path.IsFile
            ? folders[0].Path.LocalPath
            : null;
        if (string.IsNullOrEmpty(path))
        {
            Log.Warn("Add library: picked folder has no local path (a network location "
                + "without a mapped drive?).");
            return;
        }
        FormatHeading.Text = "Add Steam Library";
        ShowFormatState(pick: false, confirm: false, progress: true);
        EnterFormatSubView();
        await _format.AddLibraryAsync(path);
    }

    private void EnterFormatSubView()
    {
        InFormatSubView = true;
        PanelTools.IsVisible = false;
        PanelFormat.IsVisible = true;
        FocusFirstControl(PanelFormat);
    }

    private void LeaveFormatSubView()
    {
        if (!InFormatSubView)
        {
            return;
        }
        InFormatSubView = false;
        _pendingTarget = null;
        _formatReturnsToCards = false;
        // Close the format-name peer keyboard on the way out, matching the other
        // Leave*SubView methods; without this the keyboard can outlive its sub-view
        // and keep writing back to the now-hidden name field.
        SubViewClosed?.Invoke();
        PanelFormat.IsVisible = false;
        PanelTools.IsVisible = Tabs.SelectedIndex == 1;
        if (PanelTools.IsVisible)
        {
            FocusFirstControl(PanelTools);
        }
    }

    private void ShowFormatState(bool pick, bool confirm, bool progress)
    {
        FormatPickView.IsVisible = pick;
        FormatConfirmView.IsVisible = confirm;
        FormatProgressView.IsVisible = progress;
    }

    /// <summary>Brings a controller-focused control into the overlay viewport.
    /// Directional focus navigation does not raise this request on its own, so
    /// without it the lower keyboard rows could be focused off-screen.</summary>
    private void OnContentGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control && control is not ScrollViewer)
        {
            control.BringIntoView();
        }
    }

    /// <summary>Returns the format flow to its heading when its state changes.
    /// The confirmation keyboard can leave the scroller at its bottom, where the
    /// terminal format message would otherwise be invisible.</summary>
    private void ScrollFormatToTop() => ContentScroller.Offset = new Vector(0, 0);

    private void OnStandby(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        Core.PowerActions.Standby();
    }

    private void OnHibernate(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        Core.PowerActions.Hibernate();
    }

    // Deliberately no dismiss: the row is a toggle, and the updated description/badge
    // are the immediate feedback the user is looking at.
    private void OnKeepAwakeToggle(object? sender, RoutedEventArgs e)
        => KeepAwakeToggleRequested?.Invoke();

    /// <summary>Paints the Keep Awake row's status dot in the WakeWatch color
    /// vocabulary: green free, yellow standby-blocked, red display-pinned, grey
    /// unknown. Brushes come from the palette tokens; set from the controller's
    /// indicator poll.</summary>
    /// <param name="state">The system-wide wake-lock state.</param>
    internal void SetKeepAwakeStatus(Core.WakeLockState state)
        => KeepAwakeButton.StatusBrush = this.FindResource(state switch
        {
            Core.WakeLockState.DisplayHeld => "HcDangerBrush",
            Core.WakeLockState.SystemHeld => "HcWarningBrush",
            Core.WakeLockState.Free => "HcSuccessBrush",
            _ => "HcTextMutedBrush",
        }) as Avalonia.Media.IBrush;

    private void OnCycleDisplayDc(object? sender, RoutedEventArgs e)
        => PowerTimeoutCycleRequested?.Invoke(Core.PowerTimeoutKind.DisplayDc);

    private void OnCycleDisplayAc(object? sender, RoutedEventArgs e)
        => PowerTimeoutCycleRequested?.Invoke(Core.PowerTimeoutKind.DisplayAc);

    private void OnCycleSleepDc(object? sender, RoutedEventArgs e)
        => PowerTimeoutCycleRequested?.Invoke(Core.PowerTimeoutKind.SleepDc);

    private void OnCycleSleepAc(object? sender, RoutedEventArgs e)
        => PowerTimeoutCycleRequested?.Invoke(Core.PowerTimeoutKind.SleepAc);

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

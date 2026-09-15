using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The quick access sheet: the controller-friendly, top-docked surface that
/// carries the pinned home root, the Session / Steam / Device / Tools / Power roots with
/// their nested pages, the header status pills and the Open apps strip. It covers
/// <see cref="SheetHeightFraction"/> of the display and leaves the game visible below.</summary>
public partial class OverlayWindow : Window
{
    /// <summary>Share of the display height the sheet covers. The rest stays the
    /// game's — a tap there is outside the window rectangle and dismisses the sheet
    /// through the raw-input hit test, which is why the sheet is deliberately NOT
    /// fullscreen.</summary>
    internal const double SheetHeightFraction = 0.8125;

    /// <summary>Raised when a nested page is torn down so auxiliary peer windows close too.</summary>
    public event Action? SubViewClosed;
    private bool _confirmRestart;
    private bool _confirmShutdown;
    private DispatcherTimer? _confirmResetTimer;
    private DispatcherTimer? _slideTimer;
    private PixelPoint _slideStart;
    private PixelPoint _slideEnd;
    private DateTime _slideStartedUtc;
    private readonly HashSet<IPointer> _pressedPointers = [];
    private int _pendingLiveRefreshes;
    private int _liveRefreshScheduled;

    private const int DeviceLiveRefresh = 1;
    private const int PerformanceLiveRefresh = 2;
    private PowerSchemeSelection? _powerSchemeSelection;
    private HybridCoreSelection? _hybridCoreSelection;
    private bool _performanceDetailsExpanded;
    private OverlayPage? _renderedDevicePage;
    private string? _renderedDeviceSection;

    private readonly Dictionary<string, (string Title, Func<Control> Create)> _controlPinFactories = [];

    /// <summary>Raised when the user requests Task Manager.</summary>
    public event Action? TaskManagerRequested;

    internal event Action? OnScreenKeyboardRequested;

    private void OnScreenKeyboard(object? sender, RoutedEventArgs e) => OnScreenKeyboardRequested?.Invoke();

    /// <summary>Raised when the keep-awake row is activated (toggle the manual hold).</summary>
    public event Action? KeepAwakeToggleRequested;

    /// <summary>Raised when an idle-timeout row is activated (cycle to the next preset).</summary>
    public event Action<Core.PowerTimeoutKind>? PowerTimeoutCycleRequested;

    /// <summary>Raised when the overlay is dismissed without another action.</summary>
    public event Action? Dismissed;

    /// <summary>Raised when the user picks an Open apps chip (or cycles with Y).</summary>
    public event Action<AppSwitcherEntry>? WindowPicked;

    /// <summary>Raised when the user activates a tray icon. Arguments: the entry,
    /// whether this is a context-menu (right-click / X) activation, and the screen
    /// pixel position the app should anchor any menu to.</summary>
    public event Action<TrayIconEntry, bool, PixelPoint>? TrayIconActivated;

    /// <summary>Raised when a radio pill is tapped. The flag selects the tab to
    /// open on: true for Bluetooth, false for Wi-Fi.</summary>
    public event Action<bool>? RadioPanelRequested;

    /// <summary>Raised when the audio pill is pressed.</summary>
    public event Action? AudioPanelRequested;

    /// <summary>Raised when the eject pill (or the Tools row) is pressed.</summary>
    public event Action? EjectPanelRequested;

    /// <summary>Raised with a row's id when the user pins or unpins it (X on the
    /// focused row, a touch hold, or a right click). The controller owns the
    /// persisted list and hands the new one back through <see cref="SetPins"/>.</summary>
    public event Action<string>? PinToggleRequested;

    /// <summary>Raised with <c>true</c> while a modal system dialog owns the screen,
    /// and <c>false</c> once it closes.</summary>
    /// <remarks>
    /// A system dialog is its own window OUTSIDE the bar's rectangle, so for its
    /// lifetime the controller must suspend tap-outside dismissal and gamepad
    /// navigation. Without this the first touch inside the file picker read as a tap
    /// outside the bar, closed it, and cancelled the whole flow (user-reproduced);
    /// a B press would likewise have driven the bar hidden behind the dialog.
    /// </remarks>
    public event Action<bool>? SystemDialogActive;

    private bool _confirmCloseLauncher;

    /// <summary>Set once this window instance is gone. Post-action feedback delays
    /// outlive the window they started on, and a dismissal raised from a dead window
    /// would close whatever panel is on screen by then.</summary>
    private bool _closed;

    // Guards the Device render that ShowDestination performs, which re-enters it via ConfigureTabs.
    private bool _showingDestination;
    private readonly CancellationTokenSource _deviceLifetime = new();
    private readonly OverlayNavigation _navigation = new();
    // Window recreation retains navigation within the resident session, without persisting it.
    private static readonly SessionState SharedSession = new();
    private readonly SessionState _session;
    private readonly Action<OverlayWindow> _dock;
    private readonly Action _synchronizeTabs;

    internal sealed class SessionState
    {
        internal OverlayFocusMemory Focus { get; } = new();
        internal OverlayDestination Destination { get; set; } = OverlayDestination.QuickAccess;
    }
    private IDeviceOverlaySource? _deviceBridge;
    private Shell.DevicePrerequisiteSource? _devicePrerequisites;

    /// <summary>Preview tiles by control, rebuilt with the Glyphs page and empty elsewhere.</summary>
    /// <remarks>
    /// Held so the input test can light a tile without re-rendering the page on every sample. The
    /// tiles are owned by the visual tree; this only points at them, and is cleared whenever the
    /// page that made them is replaced.
    /// </remarks>
    private readonly Dictionary<GlyphControlId, Border> _glyphTiles = [];

    private HashSet<GlyphControlId> _pressedGlyphControls = [];
    private IDisposable? _glyphInputObservation;
    private PerformanceOverlayBridge? _performanceSource;
    private IDisposable? _performanceObservation;

    private Shell.SdFormatManager? _format;
    private FormatTargetEntry? _pendingTarget;
    private readonly AppSwitcherViewModel _switcher;

    /// <summary>Set while the peer keyboard owns activation so focus handoff does not
    /// look like a fresh overlay summons and discard the active workflow.</summary>
    internal bool KeyboardOwnsFocus { get; set; }

    /// <summary>Creates the sheet bound to the supplied state.</summary>
    /// <param name="viewModel">The state that drives labels, warnings and the rows.</param>
    /// <param name="switcher">The Open apps chips and tray icons (reconciled in place by the controller).</param>
    /// <param name="status">The live clock/battery/radio/audio status the header pills bind.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI (e.g. 1.5
    /// for a 150% desktop; see DisplayScale.GetUiScalePercent).</param>
    /// <param name="preferredScreenPoint">A physical point in the foreground window that summoned
    /// the sheet. Null falls back to Avalonia's current window or primary-screen selection.</param>
    public OverlayWindow(
        OverlayViewModel viewModel,
        AppSwitcherViewModel switcher,
        SystemStatus status,
        double uiScale = 1.0,
        PixelPoint? preferredScreenPoint = null)
        : this(viewModel, switcher, status, SharedSession,
            static window => window.DockToTopEdge(), static window => window.MaybeAutoSyncTabs(), uiScale, preferredScreenPoint)
    { }

    internal OverlayWindow(
        OverlayViewModel viewModel,
        AppSwitcherViewModel switcher,
        SystemStatus status,
        SessionState session,
        Action<OverlayWindow> dock,
        Action<OverlayWindow> synchronizeTabs,
        double uiScale = 1.0,
        PixelPoint? preferredScreenPoint = null)
    {
        _session = session;
        _dock = dock;
        _synchronizeTabs = () => synchronizeTabs(this);
        _uiScale = uiScale;
        _preferredScreenPoint = preferredScreenPoint;
        _switcher = switcher;
        DataContext = viewModel;
        InitializeComponent();
        // Two subtrees bind different objects than the window (compiled bindings:
        // x:DataType on the TrayScroller / AppsStrip and StatusZone subtrees).
        TrayScroller.DataContext = switcher;
        AppsStrip.DataContext = switcher;
        StatusZone.DataContext = status;
        IndexPinnableRows();

        // Controller navigation moves focus with InputElement.Focus(Directional),
        // which does NOT raise RequestBringIntoView on its own — a chip scrolled
        // out of its strip would take focus invisibly. Ask for it explicitly:
        // Control.BringIntoView() raises RequestBringIntoViewEvent, which the
        // ScrollViewer's presenter handles by scrolling. Arrow keys are safe to
        // leave to the ScrollViewer's own handler because GamepadNavigation marks
        // them handled from a TUNNEL handler on the window.
        TileScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        TrayScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        // Budget the tray against the XAML's declared width right away, so the
        // strip is bounded even on the path where DockToTopEdge bails out (no
        // primary screen); the dock recomputes it against the real display width.
        TrayScroller.MaxWidth = ComputeTrayMaxWidth(Width, _contentScale);
        // Touch and mouse routes to pinning: a hold on a row, or a right click.
        AddHandler(InputElement.HoldingEvent, OnHolding, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPointerPressedForPin, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedForPin, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressedForLiveRefresh, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedForLiveRefresh, RoutingStrategies.Tunnel);
        PointerCaptureLost += OnPointerCaptureLostForLiveRefresh;

        ConfigureTabs(showDevice: false);
        Tabs.SelectionChanged += OnTabSelectionChanged;
        // The panel reopens on the destination the user last had selected (static: the
        // window is recreated per open). Activated covers both the fresh open and a
        // re-summon of a still-open panel. Any nested page is torn down with it.
        Activated += OnActivated;

        foreach (SubView view in SubViews)
        {
            if (view.Host is OverlaySubView host)
            {
                OverlayPage page = view.Page;
                Action leave = () => LeaveSubView(page);
                host.CloseRequested += leave;
                _subViewCloseHandlers.Add((host, leave));
            }
        }
        CardManagerHost.FormatRequested += OnFormatFromCardManager;
        LaunchWrapperHost.Picked += OnLaunchFixGamePicked;
        LaunchWrapperHost.CustomPicked += OnCustomLaunchGamePicked;
        InitializeLaunchFixLabels(viewModel);

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closed += OnClosed;

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
        Win32Properties.AddWndProcHookCallback(
            this,
            Interop.NativeMethods.SwallowTouchSynthesizedMouse);
        Win32Properties.AddWndProcHookCallback(this, DeclineMouseActivationForPanels);
    }

    /// <summary>True while a status panel hangs from the header. A mouse click on the sheet then
    /// reaches its control without activating the sheet, so the click Windows synthesizes from
    /// the tap that opened the panel cannot raise the sheet over it. Set by
    /// <c>OverlayController.SyncSheetMouseActivation</c>; see its remarks for the mechanism.</summary>
    internal bool SuppressMouseActivation { get; set; }

    private nint DeclineMouseActivationForPanels(
        nint hWnd,
        uint msg,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (msg == Interop.NativeMethods.WmMouseActivate && SuppressMouseActivation)
        {
            handled = true;
            return Interop.NativeMethods.MaNoActivate;
        }

        return nint.Zero;
    }

    /// <summary>When set before the first show, the window primes the process-global render
    /// backend off-screen and then closes: <see cref="OnOpened"/> skips docking, focus and the
    /// CEF tab sync so nothing user-visible or Steam-touching happens during the warm pass.</summary>
    internal bool WarmingUp { get; set; }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (WarmingUp)
        {
            return;
        }

        _dock(this);
        SelectDestination(_session.Destination);
        RestoreDestinationState(focus: true);
        _synchronizeTabs();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (KeyboardOwnsFocus)
        {
            return;
        }

        LeaveAllNestedPages();
        SelectDestination(_session.Destination);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        RememberDestinationState(_navigation.Destination);

        // Before _closed, because releasing reads the bridge, and after it the guard inside would
        // skip the release and leave the subscription attached to a dead window.
        UpdateGlyphInputObservation(false);
        _closed = true;
        _pinToastTimer?.Stop();
        _pinToastTimer = null;
        DevicePowerSchemeHost.Attach(null);
        DeviceHybridCoreHost.Attach(null);
        DevicePowerPresetHost.Attach(null);
        _deviceLifetime.Cancel();
        if (_deviceBridge is not null)
        {
            _deviceBridge.Changed -= OnDeviceChanged;
        }
        if (_performanceSource is not null)
        {
            _performanceSource.Changed -= OnPerformanceChanged;
        }
        _performanceObservation?.Dispose();
        _performanceObservation = null;

        // These page controls are window-owned. Detach every cross-control callback and
        // invalidate asynchronous artwork loads at the same lifetime boundary.
        Tabs.SelectionChanged -= OnTabSelectionChanged;
        RemoveHandler(PointerPressedEvent, OnPointerPressedForLiveRefresh);
        RemoveHandler(PointerReleasedEvent, OnPointerReleasedForLiveRefresh);
        PointerCaptureLost -= OnPointerCaptureLostForLiveRefresh;
        _pressedPointers.Clear();
        Interlocked.Exchange(ref _pendingLiveRefreshes, 0);
        foreach ((OverlaySubView host, Action leave) in _subViewCloseHandlers)
        {
            host.CloseRequested -= leave;
        }
        CardManagerHost.FormatRequested -= OnFormatFromCardManager;
        ArtworkHost.Close();
        LaunchWrapperHost.Picked -= OnLaunchFixGamePicked;
        LaunchWrapperHost.CustomPicked -= OnCustomLaunchGamePicked;
        KeyDown -= OnKeyDown;
        Opened -= OnOpened;
        Activated -= OnActivated;
        Closed -= OnClosed;
        StopSlide();
        ResetConfirms();
        ReleasePinMirrors();
        _deviceLifetime.Dispose();
    }
}

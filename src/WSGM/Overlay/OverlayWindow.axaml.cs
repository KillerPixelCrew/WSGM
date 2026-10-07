using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LiveBackdrop;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The quick access sheet: the controller-friendly, top-docked surface that
///     carries the pinned home root, the Session / Steam / Device / Tools / Power roots with
///     their nested pages, the header utilities and the Open apps strip on a fullscreen glass canvas.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int DeviceLiveRefresh = 1;
    private const int PerformanceLiveRefresh = 2;
    private const int GraphicsLiveRefresh = 4;
    private const int DeviceRenderAwaitingOpen = 1;
    private const int PerformanceRenderAwaitingOpen = 2;

    private const int PinsRenderAwaitingOpen = 4;
    private const int GraphicsRenderAwaitingOpen = 8;

    private readonly Dictionary<string, (string Title, Func<Control> Create)> _controlPinFactories = [];
    private readonly CancellationTokenSource _deviceLifetime = new();
    private readonly Action<OverlayWindow> _dock;

    /// <summary>Preview tiles by control, rebuilt with the Glyphs page and empty elsewhere.</summary>
    /// <remarks>
    ///     Held so the input test can light a tile without re-rendering the page on every sample. The
    ///     tiles are owned by the visual tree; this only points at them, and is cleared whenever the
    ///     page that made them is replaced.
    /// </remarks>
    private readonly Dictionary<GlyphControlId, Border> _glyphTiles = [];

    private readonly OverlayNavigation _navigation = new();

    private readonly HashSet<IPointer> _pressedPointers = [];

    // The creating controller owns navigation state across its window recreations.
    private readonly SessionState _session;

    // The session's Steam client; null on a surface without one, such as the Settings preview.
    private readonly SteamClient? _steam;
    private readonly ConfigStore _store;
    private readonly AppSwitcherViewModel _switcher;
    private double _blurRadius = 8;

    /// <summary>
    ///     Set once this window instance is gone. Post-action feedback delays
    ///     outlive the window they started on, and a dismissal raised from a dead window
    ///     would close whatever panel is on screen by then.
    /// </summary>
    private bool _closed;

    private DispatcherTimer? _confirmResetTimer;
    private IDeviceOverlaySource? _deviceBridge;
    private DevicePrerequisiteSource? _devicePrerequisites;
    private bool _devicePrerequisitesReading;
    private bool _devicePrerequisitesStale;

    private SdFormatManager? _format;
    private LiveBackdrop? _glassBackdrop;

    private long _glyphInputKey;
    private IDisposable? _glyphInputObservation;
    private HybridCoreSelection? _hybridCoreSelection;
    private int _liveRefreshScheduled;

    // Renders asked for before the window first opens are held and run once in OnOpened. Each
    // source attached while the sheet is being built otherwise rebuilt the Device page or the pins.
    private bool _opened;
    private int _pendingLiveRefreshes;
    private FormatTargetEntry? _pendingTarget;

    private PerformanceOverlayBridge? _performanceSource;
    private PowerSchemeSelection? _powerSchemeSelection;
    private OverlayPage? _renderedDevicePage;
    private string? _renderedDeviceSection;
    private int _rendersAwaitingOpen;

    private PixelPoint _slideEnd;
    private PixelPoint _slideStart;
    private DateTime _slideStartedUtc;
    private DispatcherTimer? _slideTimer;

    /// <summary>Creates the sheet bound to the supplied state.</summary>
    /// <param name="store">The persistence owner supplied by the process or resident session.</param>
    /// <param name="viewModel">The state that drives labels, warnings and the rows.</param>
    /// <param name="switcher">The Open apps chips and tray icons (reconciled in place by the controller).</param>
    /// <param name="status">The live clock/battery/radio/audio status the header pills bind.</param>
    /// <param name="session">The navigation state the creating controller keeps across its window recreations.</param>
    /// <param name="dock">Places the sheet on its display when it opens.</param>
    /// <param name="uiScale">
    ///     The desktop-DPI scale factor for WSGM UI (e.g. 1.5
    ///     for a 150% desktop; see DisplayScale.GetUiScalePercent).
    /// </param>
    /// <param name="preferredScreenPoint">
    ///     A physical point in the foreground window that summoned
    ///     the sheet. Null falls back to Avalonia's current window or primary-screen selection.
    /// </param>
    /// <param name="steam">
    ///     The session's Steam client, or null for a surface without one (the Settings preview), whose
    ///     Steam rows then report Steam as unreachable.
    /// </param>
    internal OverlayWindow(
        ConfigStore store,
        OverlayViewModel viewModel,
        AppSwitcherViewModel switcher,
        SystemStatus status,
        SessionState session,
        Action<OverlayWindow> dock,
        double uiScale = 1.0,
        PixelPoint? preferredScreenPoint = null,
        SteamClient? steam = null)
    {
        _session = session;
        _store = store;
        _steam = steam;
        _dock = dock;
        _uiScale = uiScale;
        _preferredScreenPoint = preferredScreenPoint;
        _switcher = switcher;
        DataContext = viewModel;
        InitializeComponent();
        LibraryTabsHost.ConfigureStore(store);
        CardManagerHost.ConfigureStore(store);
        AnimationsHost.ConfigureContext(store.Context);
        ArtworkHost.ConfigureContext(store.Context);
        viewModel.PropertyChanged += OnWorkspacePolicyChanged;
        SurfaceRoot.SizeChanged += OnWorkspaceSizeChanged;
        ApplyGlassTransparency();
        InitializePowerEditors();
        InitializeWindowsPolicies();
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
        AddHandler(HoldingEvent, OnHolding, RoutingStrategies.Bubble, true);
        AddHandler(PointerPressedEvent, OnPointerPressedForPin, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedForPin, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressedForLiveRefresh, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedForLiveRefresh, RoutingStrategies.Tunnel);
        PointerCaptureLost += OnPointerCaptureLostForLiveRefresh;

        ConfigureTabs(false);
        Tabs.SelectionChanged += OnTabSelectionChanged;

        // ReSharper disable once UseDeconstruction
        foreach (var view in SubViews)
        {
            if (view.Host is not OverlaySubView host)
            {
                continue;
            }

            host.Steam = steam;
            var page = view.Page;
            var leave = () => LeaveSubView(page);
            host.CloseRequested += leave;
            _subViewCloseHandlers.Add((host, leave));
            var changed = () =>
            {
                if (ReferenceEquals(ActiveSubView?.Host, host))
                {
                    ContentScroller.Offset = default;
                }
            };
            host.LevelChanged += changed;
            _subViewLevelHandlers.Add((host, changed));
        }

        GameLibraryHost.ArtworkRequested += OpenImportedArtwork;
        GameLibraryHost.EmulatorsRequested += OpenEmulators;
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
            NativeMethods.SwallowTouchSynthesizedMouse);
    }

    /// <summary>
    ///     When set before the first show, the window primes the process-global render
    ///     backend off-screen and then closes: <see cref="OnOpened" /> skips docking, focus and the
    ///     CEF tab sync so nothing user-visible or Steam-touching happens during the warm pass.
    /// </summary>
    internal bool WarmingUp { get; init; }

    /// <summary>Updates the attached backdrop without recreating the Overlay window.</summary>
    internal void SetBlurRadius(double radius)
    {
        _blurRadius = double.IsFinite(radius) ? Math.Clamp(radius, 0, LiveBackdrop.MaximumBlurRadius) : 8;
        if (_glassBackdrop is not null)
        {
            _glassBackdrop.BlurRadius = _blurRadius;
        }
    }

    /// <summary>Raised when a nested page is torn down so its auxiliary surface closes too.</summary>
    public event Action? SubViewClosed;

    /// <summary>Raised when the user requests Task Manager.</summary>
    public event Action? TaskManagerRequested;

    internal event Action? OnScreenKeyboardRequested;

    private void OnScreenKeyboard(object? sender, RoutedEventArgs e)
    {
        OnScreenKeyboardRequested?.Invoke();
    }

    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    private void OnTaskManager(object? sender, RoutedEventArgs e)
    {
        TaskManagerRequested?.Invoke();
    }

    /// <summary>Raised when the overlay is dismissed without another action.</summary>
    public event Action? Dismissed;

    /// <summary>Raised when the user picks an Open apps chip (or cycles with Y).</summary>
    public event Action<AppSwitcherEntry>? WindowPicked;

    /// <summary>
    ///     Raised when the user activates a tray icon. Arguments: the entry,
    ///     whether this is a context-menu (right-click / X) activation, and the screen
    ///     pixel position the app should anchor any menu to.
    /// </summary>
    public event Action<TrayIconEntry, bool, PixelPoint>? TrayIconActivated;

    /// <summary>
    ///     Raised when a radio pill is tapped. The flag selects the tab to
    ///     open on: true for Bluetooth, false for Wi-Fi.
    /// </summary>
    public event Action<bool>? RadioPanelRequested;

    /// <summary>Raised when the audio pill is pressed.</summary>
    public event Action? AudioPanelRequested;

    /// <summary>Raised when the eject pill (or the Tools row) is pressed.</summary>
    public event Action? EjectPanelRequested;

    /// <summary>
    ///     Raised with a row's id when the user pins or unpins it (X on the
    ///     focused row, a touch hold, or a right click). The controller owns the
    ///     persisted list and hands the new one back through <see cref="SetPins" />.
    /// </summary>
    public event Action<string>? PinToggleRequested;

    /// <summary>
    ///     Raised with <c>true</c> while a modal system dialog owns the screen,
    ///     and <c>false</c> once it closes.
    /// </summary>
    /// <remarks>
    ///     A native file picker is outside the overlay's focus scope. Suspend overlay gamepad
    ///     navigation until it closes so input cannot activate controls behind the dialog.
    /// </remarks>
    public event Action<bool>? SystemDialogActive;

    private void OnOpened(object? sender, EventArgs e)
    {
        // Before the warm-up return: rendering is part of what the warm pass exercises.
        _opened = true;
        FoldStaticSections();
        RunRendersAwaitingOpen();
        if (WarmingUp)
        {
            return;
        }

        _dock(this);
        _glassBackdrop = LiveBackdrop.Attach(this, _blurRadius);
        _glassBackdrop.StateChanged += OnGlassBackdropChanged;
        ApplyGlassTransparency();
        SelectDestination(_session.Destination);
        RestoreDestinationState(true);
    }

    private void RunRendersAwaitingOpen()
    {
        var pending = _rendersAwaitingOpen;
        _rendersAwaitingOpen = 0;
        if ((pending & PerformanceRenderAwaitingOpen) != 0)
        {
            RefreshPerformancePanel();
        }

        if ((pending & DeviceRenderAwaitingOpen) != 0)
        {
            RefreshDevicePanel();
        }

        if ((pending & GraphicsRenderAwaitingOpen) != 0)
        {
            RefreshGraphicsPanel();
        }

        if ((pending & PinsRenderAwaitingOpen) != 0)
        {
            RenderPins();
        }
    }

    /// <summary>Returns an already open sheet to its selected section when summoned again.</summary>
    internal void ResetForResummon()
    {
        if (HasActiveSurface)
        {
            return;
        }

        LeaveAllNestedPages();
        SelectDestination(_session.Destination);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CloseAllSurfaces();
        if (_glassBackdrop is not null)
        {
            _glassBackdrop.StateChanged -= OnGlassBackdropChanged;
            _glassBackdrop.Dispose();
            _glassBackdrop = null;
        }

        if (DataContext is OverlayViewModel powerState)
        {
            powerState.PropertyChanged -= OnPowerEditorStateChanged;
        }

        RememberDestinationState(_navigation.Destination);

        // Before _closed, because releasing reads the bridge, and after it the guard inside would
        // skip the release and leave the subscription attached to a dead window.
        UpdateGlyphInputObservation(false);
        _closed = true;
        ViewModel.PropertyChanged -= OnWorkspacePolicyChanged;
        _pinToastTimer?.Stop();
        _pinToastTimer = null;
        DevicePowerSchemeHost.Attach(null);
        GameLibraryHost.Attach(null);
        EmulatorManagerHost.Attach(null);
        ThemesHost.Attach(null);
        AnimationsHost.Attach(null);
        SoundsHost.Attach(null);
        ArtworkHost.Attach(null);
        DeviceHybridCoreHost.Attach(null);
        DevicePowerPresetHost.Attach(null);
        _deviceLifetime.Cancel();
        if (_deviceBridge is not null)
        {
            _deviceBridge.Changed -= OnDeviceChanged;
        }

        DetachGraphicsSource();
        if (_performanceSource is not null)
        {
            _performanceSource.Changed -= OnPerformanceChanged;
        }


        // These page controls are window-owned. Detach every cross-control callback and
        // invalidate asynchronous artwork loads at the same lifetime boundary.
        Tabs.SelectionChanged -= OnTabSelectionChanged;
        RemoveHandler(PointerPressedEvent, OnPointerPressedForLiveRefresh);
        RemoveHandler(PointerReleasedEvent, OnPointerReleasedForLiveRefresh);
        PointerCaptureLost -= OnPointerCaptureLostForLiveRefresh;
        _pressedPointers.Clear();
        Interlocked.Exchange(ref _pendingLiveRefreshes, 0);
        foreach (var (host, leave) in _subViewCloseHandlers)
        {
            host.CloseRequested -= leave;
        }

        foreach (var (host, changed) in _subViewLevelHandlers)
        {
            host.LevelChanged -= changed;
        }

        GameLibraryHost.ArtworkRequested -= OpenImportedArtwork;
        GameLibraryHost.EmulatorsRequested -= OpenEmulators;
        LaunchWrapperHost.Picked -= OnLaunchFixGamePicked;
        LaunchWrapperHost.CustomPicked -= OnCustomLaunchGamePicked;
        KeyDown -= OnKeyDown;
        Opened -= OnOpened;
        Closed -= OnClosed;
        StopSlide();
        ResetConfirms();
        ReleasePinMirrors();
        _deviceLifetime.Dispose();
    }

    internal sealed class SessionState
    {
        internal HashSet<string> ExpandedSections { get; } = [];
        internal Dictionary<OverlayDestination, string> Sections { get; } = [];
        internal OverlayFocusMemory Focus { get; } = new();
        internal OverlayDestination Destination { get; set; } = OverlayDestination.QuickAccess;
    }
}

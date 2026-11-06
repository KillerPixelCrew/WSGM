using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SteamUiToolkit;
using WSGM.Core;
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
    private const int PinSourcesLiveRefresh = 8;
    private const int PluginsLiveRefresh = 16;

    private readonly CancellationTokenSource _deviceLifetime = new();

    // Draws every Device, GPU, performance and pinned group description.
    private readonly DeviceGroupRenderer _deviceRenderer;
    private readonly Action<OverlayWindow> _dock;

    private readonly OverlayNavigation _navigation = new();

    // The second-press confirmations shared by the Session and Power pages, the power menu and Back.
    private readonly PowerActionConfirms _powerConfirms;

    private readonly HashSet<IPointer> _pressedPointers = [];

    // Creates each described row with the commands of its source.
    private readonly DeviceRowFactory _rows;

    // The creating controller owns navigation state across its window recreations.
    private readonly SessionState _session;

    // The session's Steam client; null on a surface without one, such as the Settings preview.
    private readonly SteamClient? _steam;
    private readonly ConfigStore _store;
    private readonly AppSwitcherViewModel _switcher;

    /// <summary>
    ///     Set once this window instance is gone. Post-action feedback delays
    ///     outlive the window they started on, and a dismissal raised from a dead window
    ///     would close whatever panel is on screen by then.
    /// </summary>
    private bool _closed;

    private IDeviceOverlaySource? _deviceBridge;

    // Set while the Device panel is hidden and a refresh was asked for; it renders when shown again.
    private bool _devicePanelStale;
    private IGraphicsOverlaySource? _graphicsSource;

    private ChoiceRowSelection? _hybridCoreSelection;
    private int _liveRefreshScheduled;

    // Reads whether the device offers a manual power mode; null until the coordinator attaches.
    private Func<(bool Available, bool Unified)>? _manualTdpMode;

    // Renders wait for the window to open, and OnOpened runs each once. Each source attached while
    // the sheet is being built otherwise rebuilt the Device page or the pins.
    private bool _opened;
    private int _pendingLiveRefreshes;

    private PerformanceOverlayBridge? _performanceSource;
    private CommonPluginOverlaySource? _pluginSource;
    private DevicePowerPresetSelection? _powerPresetSelection;
    private ChoiceRowSelection? _powerSchemeSelection;

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
        _switcher = switcher;
        DataContext = viewModel;
        InitializeComponent();
        AnimationsHost.ConfigureContext(store.Context);
        ArtworkHost.ConfigureContext(store.Context);
        viewModel.PropertyChanged += OnWorkspacePolicyChanged;
        SurfaceRoot.SizeChanged += OnWorkspaceSizeChanged;
        Placement = new OverlayPlacement(this, RootScale, GlassCanvas, TrayScroller, uiScale, preferredScreenPoint);
        _powerConfirms = new PowerActionConfirms(viewModel, PanelPowerActions.RestartButton,
            PanelPowerActions.ShutdownButton, PanelPowerActions.SignOutButton);
        Surfaces = new OverlaySurfaceStack(this, SurfaceRoot, DeckContent, () => DefaultFocusTarget, CoverMedia,
            _powerConfirms.Reset);
        PowerEditors = new OverlayPowerEditors(viewModel, PanelPowerWake.KeepAwakeHost,
            PanelPowerWake.KeepAwakeDetail, PanelPowerTimeouts.PowerTimeoutEditors,
            PanelPowerActions.PowerButtonSection,
            PanelPowerActions.PowerButtonHost);
        WindowsPolicies = new WindowsPolicyRows(PanelSystemTools.UacPolicyHost, PanelPowerWake.LockOnWakePolicyHost);
        _profileScope = new HeaderProfileScope(HeaderProfile, ProfileContext, ManageProfiles, _deviceLifetime.Token);
        // Two subtrees bind different objects than the window (compiled bindings:
        // x:DataType on the TrayScroller / AppsStrip and StatusZone subtrees).
        TrayScroller.DataContext = switcher;
        AppsStrip.DataContext = switcher;
        StatusZone.DataContext = status;
        _pins = new QuickAccessPins(this, PinnedGrid, PinToast, PinToastText, PinToastHint);
        _rows = new DeviceRowFactory(() => _deviceBridge, () => _graphicsSource, () => _performanceSource,
            (bridge, capability) =>
            {
                DeviceColorHost.Open(bridge, capability);
                EnterSubView(OverlayPage.DeviceColor);
            }, _deviceLifetime.Token, () => _closed);
        _deviceRenderer = new DeviceGroupRenderer(_rows.Create,
            (id, title, pinned) => CreateSectionHeader(id, title, pinned), session.ExpandedSections);
        _devicePins = new DevicePins(_deviceRenderer, _rows.Views, () => _deviceBridge?.Snapshot(),
            () => _performanceSource?.Snapshot(), () => _graphicsSource?.Snapshot());
        foreach (var view in SubViews.Where(view => view.PinId is not null))
        {
            var page = view.Page;
            _pins.AddSection(view.PinId!, view.Title!, view.Description ?? string.Empty, view.Icon,
                () => view.Available?.Invoke() ?? true, () => OpenSection(page));
        }

        DevicePage.Configure(_deviceRenderer, session.ExpandedSections);
        PanelSystemPerformance.Configure(_deviceRenderer);
        StorageFormatHost.CloseRequested += () => LeaveSubView(OverlayPage.SteamStorageFormat);
        StorageFormatHost.ScrollToTopRequested += () => ContentScroller.Offset = default;
        ConfigurePages(steam);

        // Controller navigation moves focus with InputElement.Focus(Directional),
        // which does NOT raise RequestBringIntoView on its own — a chip scrolled
        // out of its strip would take focus invisibly. Ask for it explicitly:
        // Control.BringIntoView() raises RequestBringIntoViewEvent, which the
        // ScrollViewer's presenter handles by scrolling. Arrow keys are safe to
        // leave to the ScrollViewer's own handler because GamepadNavigation marks
        // them handled from a TUNNEL handler on the window.
        TileScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        TrayScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        // Touch and mouse routes to pinning: a hold on a row, or a right click.
        AddHandler(HoldingEvent, OnHolding, RoutingStrategies.Bubble, true);
        AddHandler(PointerPressedEvent, OnPointerPressedForPin, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedForPin, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressedForLiveRefresh, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedForLiveRefresh, RoutingStrategies.Tunnel);
        PointerCaptureLost += OnPointerCaptureLostForLiveRefresh;

        ConfigureTabs(DeviceSnapshotOrOff());
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
            host.CloseRequested += () => LeaveSubView(page);
            host.LevelChanged += () =>
            {
                if (ReferenceEquals(ActiveSubView?.Host, host))
                {
                    ContentScroller.Offset = default;
                }
            };
        }

        GameLibraryHost.ArtworkRequested += OpenImportedArtwork;
        GameLibraryHost.EmulatorsRequested += OpenEmulators;
        GameLibraryHost.SettingsRequested += PanelSystemTools.RequestSettings;
        ArtworkHost.SettingsRequested += PanelSystemTools.RequestSettings;

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

    /// <summary>The Wake and Idle timeouts editors, with the Keep Awake indicator.</summary>
    internal OverlayPowerEditors PowerEditors { get; }

    /// <summary>The UAC prompt and sign-in-after-standby switches on the System and Wake pages.</summary>
    internal WindowsPolicyRows WindowsPolicies { get; }

    /// <summary>Raised when a nested page is torn down so its auxiliary surface closes too.</summary>
    public event Action? SubViewClosed;

    /// <summary>Raised when the user asks for the on-screen keyboard, from the header or Tools > Keyboard.</summary>
    internal event Action? OnScreenKeyboardRequested;

    /// <summary>Connects the destination pages to the navigation, surfaces and confirmations the sheet owns.</summary>
    /// <param name="steam">The session's Steam client, or null on a surface without one.</param>
    private void ConfigurePages(SteamClient? steam)
    {
        PanelSteamLibrary.Configure(StorageFormatHost);
        PanelSteamLibrary.PageRequested += OpenPage;
        PanelSteamLibrary.FormatPageRequested += OpenStorageFormat;
        PanelSteamLibrary.SystemDialogActive += active => SystemDialogActive?.Invoke(active);
        PanelSteamLaunch.Steam = steam;
        PanelSteamLaunch.Configure(LaunchWrapperHost, Surfaces);
        PanelSteamLaunch.ConfigurationRequested += () => OpenPage(OverlayPage.SteamLaunchConfiguration);
        PanelSteamLaunch.ConfigurationClosed += () => LeaveSubView(OverlayPage.SteamLaunchConfiguration);
        PanelSteamLaunch.DismissRequested += () => Dismissed?.Invoke();
        PanelSteamLaunch.RefreshLabels();
        PanelSystemStorage.Configure(StorageFormatHost);
        PanelSystemStorage.EjectRequested += () => EjectPanelRequested?.Invoke();
        PanelSystemStorage.FormatPageRequested += OpenStorageFormat;
        PanelSystemController.KeyboardRequested += () => OnScreenKeyboardRequested?.Invoke();
        PanelPowerSession.Configure(_powerConfirms);
        PanelPowerWake.WakeLocksRequested += () => OpenPage(OverlayPage.PowerWakeLocks);
        PanelSystemDisplay.CustomResolutionsRequested += () => OpenPage(OverlayPage.SystemDisplayResolutions);
        PanelPowerActions.Configure(_powerConfirms);
        PanelPowerActions.DismissRequested += () => Dismissed?.Invoke();
        PanelPowerActions.PowerMenuRequested += () => PowerMenuRequested?.Invoke();
        ProgramsHost.ProgramsChanged += () => QueueLiveRefresh(PinSourcesLiveRefresh);
        ProgramsHost.EditorRequested += id =>
        {
            if (OpenSection(OverlayPage.Programs))
            {
                ProgramsHost.OpenEditor(id);
                FocusFirstControl(ProgramsHost);
            }
        };
        ProgramsHost.PinRequested += id => PinToggleRequested?.Invoke(ProgramsService.PinPrefix + id);
        ProgramsHost.UnpinRequested += id =>
        {
            var pin = ProgramsService.PinPrefix + id;
            if (_pins.Contains(pin))
            {
                // Queue the final removal after any older home-pin writes already admitted by the controller.
                PinToggleRequested?.Invoke(pin);
            }
        };
        HotkeysHost.HotkeysChanged += () => QueueLiveRefresh(PinSourcesLiveRefresh);
        HotkeysHost.PinToggleRequested += pin => PinToggleRequested?.Invoke(pin);
        HotkeysHost.UnpinRequested += pin =>
        {
            if (_pins.Contains(pin))
            {
                PinToggleRequested?.Invoke(pin);
            }
        };
        HotkeysHost.CreateHeader = (id, title) => CreateSectionHeader(id, title);
        PanelSystemPlugins.OfferedChanged += () =>
        {
            if (!PanelSystemPlugins.Offered && _navigation.Page == OverlayPage.SystemPlugins)
            {
                SelectDestination(OverlayDestination.System);
            }
            else
            {
                RefreshWorkspace();
            }
        };
    }

    private void OnScreenKeyboard(object? sender, RoutedEventArgs e)
    {
        OnScreenKeyboardRequested?.Invoke();
    }

    private void OnSteamBuildNoticeDismiss(object? sender, RoutedEventArgs e)
    {
        SteamBuildNoticeDismissed?.Invoke();
    }

    /// <summary>Raised when the overlay is dismissed without another action.</summary>
    public event Action? Dismissed;

    /// <summary>Raised when the user dismisses the Steam client build notice.</summary>
    public event Action? SteamBuildNoticeDismissed;

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
        PanelSystemDisplay.Fold(_session.ExpandedSections);
        var performance = _performanceSource?.Snapshot();
        var device = DeviceSnapshotOrOff();
        RefreshNavigationHints();
        RefreshPerformancePanel(performance);
        // Drawn even while the panel is hidden: the warm pass exercises it, and the first Device
        // visit then only refreshes values.
        RefreshDevicePanel(device, performance);
        RenderPins(device, performance);
        if (WarmingUp)
        {
            return;
        }

        _dock(this);
        Placement.AttachBackdrop();
        SelectDestination(_session.Destination);
        RestoreDestinationState(true);
    }

    /// <summary>Returns an already open sheet to its selected section when summoned again.</summary>
    internal void ResetForResummon()
    {
        if (Surfaces.HasActive)
        {
            return;
        }

        LeaveAllNestedPages();
        SelectDestination(_session.Destination);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Surfaces.Detach();
        Placement.Detach();
        PowerEditors.Detach();
        RememberDestinationState(_navigation.Destination);

        // Released against the bridge that granted the lease, before anything else lets go of it.
        ObserveGlyphInput(false);
        _closed = true;
        PanelSteamLibrary.Detach();
        PanelSteamLaunch.Detach();
        _profileScope.Detach();
        ViewModel.PropertyChanged -= OnWorkspacePolicyChanged;
        _pins.Detach();
        GameLibraryHost.Attach(null);
        LibraryTabsHost.Attach(null);
        CardManagerHost.Attach(null);
        ProgramsHost.Attach(null);
        HotkeysHost.Attach(null);
        NotesHost.Attach(null);
        ScreensaversHost.Attach(null);
        EmulatorManagerHost.Attach(null);
        ThemesHost.Attach(null);
        AnimationsHost.Attach(null);
        SoundsHost.Attach(null);
        ArtworkHost.Attach(null);
        GameToolsHost.Attach(null);
        _deviceLifetime.Cancel();
        if (_deviceBridge is not null)
        {
            _deviceBridge.Changed -= OnDeviceChanged;
        }

        DetachGraphicsSource();
        DetachCommonPlugins();
        if (_performanceSource is not null)
        {
            _performanceSource.Changed -= OnPerformanceChanged;
        }

        // Handlers between this window and its own controls die with the window; only pending
        // work is dropped here.
        _pressedPointers.Clear();
        Interlocked.Exchange(ref _pendingLiveRefreshes, 0);
        _powerConfirms.Reset();
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

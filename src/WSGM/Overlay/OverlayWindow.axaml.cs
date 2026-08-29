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
    /// <summary>Raised when a nested page is torn down so auxiliary peer windows close too.</summary>
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
    private readonly CancellationTokenSource _deviceLifetime = new();
    private readonly OverlayNavigation _navigation = new();
    private static readonly OverlayFocusMemory FocusMemory = new();
    private IDeviceOverlaySource? _deviceBridge;
    private IPerformanceOverlaySource? _performanceSource;
    private IDisposable? _performanceObservation;

    private Shell.SdFormatManager? _format;
    private FormatTargetEntry? _pendingTarget;

    /// <summary>Whether the in-place Format/Add-library sub-view is showing. While
    /// it is, LB/RB destination switching is suppressed and B cancels the sub-view rather
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
    /// System, so leaving it restores that destination rather than Steam.</summary>
    internal bool InWakeLockSubView { get; private set; }

    /// <summary>The launch fix waiting on the user to pick a game, and the button
    /// whose title reports the outcome.</summary>
    private (LaunchWrapperMode Mode, CardButton Button)? _pendingLaunchFix;

    /// <summary>Set while the peer keyboard owns activation so focus handoff does not
    /// look like a fresh overlay summons and discard the active workflow.</summary>
    internal bool KeyboardOwnsFocus { get; set; }

    /// <summary>Whether any nested page owns the surface.</summary>
    private bool AnySubView
        => InFormatSubView || InLibraryTabsSubView || InCardManagerSubView || InArtworkSubView
           || InLaunchWrapperSubView || InWakeLockSubView;

    /// <summary>Gives the overlay the shared removable-storage format manager so
    /// its Steam storage page can drive it. Called by the controller right after
    /// construction (the manager outlives the window).</summary>
    /// <param name="format">The controller-owned format manager.</param>
    internal void AttachFormatManager(Shell.SdFormatManager format)
    {
        _format = format;
        PanelFormat.DataContext = format;
    }

    /// <summary>Attaches the semantic coordinator projection used by the optional Device tab.</summary>
    internal void AttachDeviceBridge(IDeviceOverlaySource? bridge)
    {
        if (ReferenceEquals(_deviceBridge, bridge))
        {
            return;
        }

        if (_deviceBridge is not null)
        {
            _deviceBridge.Changed -= OnDeviceChanged;
        }
        _deviceBridge = bridge;
        if (_deviceBridge is not null)
        {
            _deviceBridge.Changed += OnDeviceChanged;
        }

        RefreshDevicePanel();
    }

    /// <summary>Attaches the shared performance projection without transferring its lifetime.</summary>
    internal void AttachPerformanceSource(IPerformanceOverlaySource? source)
    {
        if (ReferenceEquals(_performanceSource, source))
        {
            return;
        }

        if (_performanceSource is not null)
        {
            _performanceSource.Changed -= OnPerformanceChanged;
        }
        _performanceObservation?.Dispose();
        _performanceObservation = null;

        _performanceSource = source;
        if (_performanceSource is not null)
        {
            try
            {
                _performanceSource.Changed += OnPerformanceChanged;
                _performanceObservation = _performanceSource.AcquireObservation();
            }
            catch (Exception ex)
            {
                _performanceSource.Changed -= OnPerformanceChanged;
                _performanceSource = null;
                Log.Warn($"Performance overlay observation could not start: {ex.Message}");
            }
        }

        RefreshPerformancePanel();
    }

    /// <summary>Moves focus to Device when integration is enabled; otherwise leaves the current tab.</summary>
    internal void SelectDeviceDestination()
    {
        if (_deviceBridge?.Snapshot().Visible is true)
        {
            SelectDestination(OverlayDestination.Device);
        }
    }

    private void OnDeviceChanged() => Dispatcher.UIThread.Post(RefreshDevicePanel);

    private void OnPerformanceChanged() => Dispatcher.UIThread.Post(RefreshPerformancePanel);

    private void RefreshDevicePanel()
    {
        if (_closed)
        {
            return;
        }

        DeviceOverlaySnapshot snapshot = _deviceBridge?.Snapshot()
            ?? new DeviceOverlaySnapshot(false, "Device integration off", string.Empty, null, []);
        ConfigureTabs(snapshot.Visible);
        DeviceStatusTitle.Text = snapshot.Status;
        DeviceStatusDetail.Text = snapshot.Detail;
        string? focusedKey = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is Control focused
            ? focused.Tag as string
            : null;
        DeviceCapabilityList.Children.Clear();
        if (snapshot.Capabilities.Count == 0 && snapshot.GlyphSelection is null)
        {
            DeviceCapabilityList.Children.Add(new TextBlock
            {
                Text = "No semantic capabilities are available yet.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(2, 4),
            });
            return;
        }

        DescriptorStatusRow? restoreFocus =
            DeviceOverlaySectionPages.SectionFor(_navigation.Page) is { } section
                ? RenderDeviceSection(snapshot, section, focusedKey)
                : RenderDeviceSectionMenu(snapshot, focusedKey);

        restoreFocus?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// Renders the Device root: one card per section that currently has something in it.
    /// </summary>
    /// <remarks>
    /// A menu rather than one long list. The whole surface is a few rows tall on a handheld, and a
    /// list that needs scrolling is a list a controller cannot cross quickly. Each card carries the
    /// most serious status inside it, so a fault is visible without opening the page.
    /// </remarks>
    private DescriptorStatusRow? RenderDeviceSectionMenu(
        DeviceOverlaySnapshot snapshot,
        string? focusedKey)
    {
        DescriptorStatusRow? restoreFocus = null;
        foreach (DeviceOverlaySectionEntry entry in DeviceOverlaySectionPages.Build(snapshot))
        {
            string key = DeviceOverlaySectionPages.FocusKey(entry.Section);
            DescriptorStatusRow row = new();
            row.Apply(new DescriptorRow(
                key,
                entry.Title,
                entry.Description,
                entry.Count.ToString(CultureInfo.InvariantCulture),
                CanInvoke: true,
                DeviceStatusFor(entry.Status)));
            DeviceOverlaySection section = entry.Section;
            row.Click += (_, _) => EnterDeviceSection(section);
            DeviceCapabilityList.Children.Add(row);
            if (string.Equals(key, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        return restoreFocus;
    }

    /// <summary>Renders one Device section's rows.</summary>
    private DescriptorStatusRow? RenderDeviceSection(
        DeviceOverlaySnapshot snapshot,
        DeviceOverlaySection section,
        string? focusedKey)
    {
        DescriptorStatusRow? restoreFocus = null;
        TextBlock heading = new()
        {
            Text = DeviceSectionLabel(section),
            Margin = new Thickness(2, 2, 2, 2),
        };
        heading.Classes.Add("eyebrow");
        DeviceCapabilityList.Children.Add(heading);

        foreach (DeviceOverlayCapability capability
            in DeviceOverlaySectionPages.CapabilitiesIn(snapshot, section))
        {
            string key = capability.InstanceId is { Length: > 0 }
                ? $"{capability.CapabilityId}#{capability.InstanceId}"
                : capability.CapabilityId;
            DescriptorStatusRow button = CreateDeviceCapabilityRow(capability, key);
            DeviceCapabilityList.Children.Add(button);
            if (string.Equals(key, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = button;
            }
        }

        // Glyph selection is WSGM's own control rather than a plugin capability, so it is placed
        // here explicitly rather than arriving through the capability list.
        if (section is DeviceOverlaySection.Glyphs && snapshot.GlyphSelection is { } glyphSelection)
        {
            const string glyphFocusKey = "device.glyph-selection";
            DescriptorStatusRow button = CreateGlyphSelectionRow(glyphSelection, glyphFocusKey);
            DeviceCapabilityList.Children.Add(button);
            if (string.Equals(glyphFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = button;
            }
        }

        return restoreFocus;
    }

    private void EnterDeviceSection(DeviceOverlaySection section)
    {
        if (!_navigation.Push(
            DeviceOverlaySectionPages.PageFor(section),
            CurrentSemanticFocusKey()))
        {
            return;
        }

        RefreshDevicePanel();
        FocusFirstControl(DeviceCapabilityList);
    }

    private void LeaveDeviceSection(DeviceOverlaySection section)
    {
        string? returnFocusKey = _navigation.Pop()
            ?? DeviceOverlaySectionPages.FocusKey(section);
        RefreshDevicePanel();
        RestoreRootFocus(returnFocusKey);
    }

    private DescriptorStatusRow CreateDeviceCapabilityRow(
        DeviceOverlayCapability capability,
        string key)
    {
        DescriptorStatusRow button = new();
        button.Apply(new DescriptorRow(
            key,
            capability.Title,
            capability.Description,
            capability.TrailingText,
            capability.CanInvoke,
            DeviceStatusFor(capability.Status)));
        button.Click += async (_, _) =>
        {
            IDeviceOverlaySource? bridge = _deviceBridge;
            if (bridge is null || _closed)
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                await bridge.InvokeAsync(capability, _deviceLifetime.Token);
            }
            catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Device overlay command failed: {capability.CapabilityId}, {ex.Message}");
            }
            finally
            {
                if (!_closed)
                {
                    button.IsEnabled = capability.CanInvoke;
                }
            }
        };
        return button;
    }

    private DescriptorStatusRow CreateGlyphSelectionRow(
        DeviceOverlayGlyphSelection glyphSelection,
        string key)
    {
        DescriptorStatusRow button = new();
        button.Apply(new DescriptorRow(
            key,
            glyphSelection.Title,
            glyphSelection.Description,
            glyphSelection.TrailingText,
            glyphSelection.CanCycle,
            DeviceStatusFor(glyphSelection.Status)));
        button.Click += async (_, _) =>
        {
            IDeviceOverlaySource? bridge = _deviceBridge;
            if (bridge is null || _closed)
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                await bridge.CyclePhysicalGlyphSelectionAsync(_deviceLifetime.Token);
            }
            catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Physical glyph selection command failed: {ex.Message}");
            }
            finally
            {
                if (!_closed)
                {
                    button.IsEnabled = glyphSelection.CanCycle;
                }
            }
        };
        return button;
    }

    private void RefreshPerformancePanel()
    {
        if (_closed)
        {
            return;
        }

        PlacePerformanceSection(_navigation.IsVisible(OverlayDestination.Device));
        PerformanceOverlaySnapshot? snapshot = _performanceSource?.Snapshot();
        PerformanceSection.IsVisible = snapshot?.Visible is true;
        PerformanceRows.Children.Clear();
        if (snapshot is not { Visible: true })
        {
            PerformanceStatus.Text = string.Empty;
            return;
        }

        PerformanceStatus.Text = snapshot.Status;
        string? focusedKey = CurrentSemanticFocusKey();
        DescriptorStatusRow? restoreFocus = null;
        foreach (DescriptorRow descriptor in snapshot.Rows)
        {
            DescriptorStatusRow button = new();
            DescriptorRow presentation = descriptor with { Id = $"performance.{descriptor.Id}" };
            button.Apply(presentation);
            button.Click += async (_, _) =>
            {
                IPerformanceOverlaySource? source = _performanceSource;
                if (source is null || _closed || !descriptor.CanInvoke)
                {
                    return;
                }

                button.IsEnabled = false;
                try
                {
                    await source.InvokeAsync(descriptor, _deviceLifetime.Token);
                }
                catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Log.Warn($"Performance overlay command failed: {descriptor.Id}, {ex.Message}");
                }
                finally
                {
                    if (!_closed)
                    {
                        button.IsEnabled = descriptor.CanInvoke;
                    }
                }
            };
            PerformanceRows.Children.Add(button);
            if (string.Equals(presentation.Id, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = button;
            }
        }
        restoreFocus?.Focus(NavigationMethod.Directional);
    }

    private void PlacePerformanceSection(bool deviceVisible)
    {
        StackPanel target = deviceVisible ? PanelDevice : PanelSystem;
        int targetIndex = deviceVisible ? 2 : 3;
        if (target.Children.Contains(PerformanceSection))
        {
            return;
        }

        PanelDevice.Children.Remove(PerformanceSection);
        PanelSystem.Children.Remove(PerformanceSection);
        target.Children.Insert(Math.Min(targetIndex, target.Children.Count), PerformanceSection);
    }

    private static DescriptorStatus DeviceStatusFor(DeviceOverlayStatus status)
        => status switch
        {
            DeviceOverlayStatus.Available => DescriptorStatus.Available,
            DeviceOverlayStatus.Warning => DescriptorStatus.Warning,
            DeviceOverlayStatus.Faulted => DescriptorStatus.Faulted,
            DeviceOverlayStatus.Stale => DescriptorStatus.Stale,
            DeviceOverlayStatus.ExternallyOwned => DescriptorStatus.ExternallyOwned,
            DeviceOverlayStatus.Unsupported => DescriptorStatus.Unsupported,
            DeviceOverlayStatus.Progress => DescriptorStatus.Progress,
            _ => DescriptorStatus.None,
        };

    private static string DeviceSectionLabel(DeviceOverlaySection section) => section switch
    {
        DeviceOverlaySection.Overview => "OVERVIEW",
        DeviceOverlaySection.Profiles => "PROFILES",
        DeviceOverlaySection.PowerAndThermals => "POWER AND THERMALS",
        DeviceOverlaySection.ControllerAndMotion => "CONTROLLER AND MOTION",
        DeviceOverlaySection.Oem => "OEM BUTTONS",
        DeviceOverlaySection.LightingAndFeatures => "LIGHTING AND FEATURES",
        DeviceOverlaySection.Glyphs => "GLYPHS",
        DeviceOverlaySection.Diagnostics => "DIAGNOSTICS AND RECOVERY",
        _ => "DEVICE",
    };

    private void ConfigureTabs(bool showDevice)
    {
        OverlayDestination previous = _navigation.Destination;
        bool visibilityChanged = _navigation.SetDeviceVisible(showDevice);
        if (!visibilityChanged && Tabs.Tabs is not null)
        {
            return;
        }

        if (previous == OverlayDestination.Device && !showDevice)
        {
            RememberDestinationState(previous);
            _lastDestination = OverlayDestination.Home;
        }

        PlacePerformanceSection(showDevice);

        Tabs.Tabs = _navigation.VisibleDestinations.Select(CreateDestinationTab).ToList();
        int selectedIndex = DestinationIndex(_navigation.Destination);
        // Rebuilding a dynamic strip can change the meaning of an unchanged numeric
        // index (System 2 becomes Device 2). Force one descriptor-based selection.
        Tabs.SelectedIndex = -1;
        Tabs.SelectedIndex = selectedIndex;
        ShowDestination(_navigation.Destination, restoreFocus: false);
    }

    private static TabStripItem CreateDestinationTab(OverlayDestination destination) => destination switch
    {
        OverlayDestination.Home => new TabStripItem("Home", Icons.Play, (int)destination),
        OverlayDestination.Steam => new TabStripItem("Steam", Icons.SteamLike, (int)destination),
        OverlayDestination.Device => new TabStripItem("Device", Icons.Gear, (int)destination),
        OverlayDestination.System => new TabStripItem("System", Icons.Power, (int)destination),
        _ => throw new ArgumentOutOfRangeException(nameof(destination)),
    };

    private int DestinationIndex(OverlayDestination destination)
    {
        IReadOnlyList<TabStripItem>? tabs = Tabs.Tabs;
        if (tabs is null)
        {
            return 0;
        }
        for (int i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Tag == (int)destination)
            {
                return i;
            }
        }
        return 0;
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
    /// or when focus tracking is lost: the active destination's first row — HomeAppButton
    /// is invisible on other destinations and focusing it would fall through to
    /// the header close button.</summary>
    internal InputElement DefaultFocusTarget
    {
        get
        {
            // Nested pages retain focus ownership while one is open.
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
            var panel = _navigation.Destination switch
            {
                OverlayDestination.Steam => (Control)PanelSteam,
                OverlayDestination.Device => PanelDevice,
                OverlayDestination.System => PanelSystem,
                _ => PanelHome,
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

    // The destination the user last selected, restored on the next open. Static because
    // the overlay window is recreated per open; deliberately not persisted to config.
    private static OverlayDestination _lastDestination = OverlayDestination.Home;

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

        ConfigureTabs(showDevice: false);
        Tabs.SelectionChanged += OnTabSelectionChanged;
        // The panel reopens on the destination the user last had selected (static: the
        // window is recreated per open). Activated covers both the fresh open and a
        // re-summon of a still-open panel. Any nested page is torn down with it.
        Activated += OnActivated;

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
        SelectDestination(_lastDestination);
        RestoreDestinationState(focus: true);
        MaybeAutoSyncTabs();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (KeyboardOwnsFocus)
        {
            return;
        }

        LeaveAllNestedPages();
        SelectDestination(_lastDestination);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        RememberDestinationState(_navigation.Destination);
        _closed = true;
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
        LibraryTabsHost.CloseRequested -= LeaveLibraryTabsSubView;
        CardManagerHost.CloseRequested -= LeaveCardManagerSubView;
        CardManagerHost.FormatRequested -= OnFormatFromCardManager;
        ArtworkHost.CloseRequested -= LeaveArtworkSubView;
        ArtworkHost.Close();
        LaunchWrapperHost.CloseRequested -= LeaveLaunchWrapperSubView;
        LaunchWrapperHost.Picked -= OnLaunchFixGamePicked;
        LaunchWrapperHost.CustomPicked -= OnCustomLaunchGamePicked;
        WakeLockHost.CloseRequested -= LeaveWakeLockSubView;
        KeyDown -= OnKeyDown;
        Opened -= OnOpened;
        Activated -= OnActivated;
        Closed -= OnClosed;
        StopSlide();
        ResetConfirms();
        _deviceLifetime.Dispose();
    }

    /// <summary>Selects the previous destination (LB), wrapping from the first to the
    /// last. Suppressed while a nested page owns the surface.</summary>
    internal void SelectPreviousTab()
    {
        if (!AnySubView)
        {
            Tabs.SelectPrevious();
        }
    }

    /// <summary>Selects the next destination (RB). Suppressed while a nested page is open.</summary>
    internal void SelectNextTab()
    {
        if (!AnySubView)
        {
            Tabs.SelectNext();
        }
    }

    /// <summary>Handles Back/B in strict dialog, nested-page, destination-root order.
    /// Returns false only when Home is already at its root and the controller should
    /// close the overlay. A format already running keeps running when its page closes.</summary>
    internal bool TryCancelSubView()
    {
        bool confirmationOpen = _confirmCloseLauncher || _confirmRestart || _confirmShutdown;
        switch (_navigation.BackAction(popupOpen: false, dialogOpen: confirmationOpen))
        {
            case OverlayBackAction.CloseDialog:
                ResetConfirms();
                return true;
            case OverlayBackAction.LeaveNestedPage:
                if (InLibraryTabsSubView)
                {
                    // The builder handles its deeper levels; at its root it raises
                    // CloseRequested, which pops this window's page entry.
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
                if (InFormatSubView)
                {
                    LeaveFormatSubViewToOrigin();
                    return true;
                }
                if (DeviceOverlaySectionPages.SectionFor(_navigation.Page) is { } leaving)
                {
                    LeaveDeviceSection(leaving);
                    return true;
                }
                _navigation.Pop();
                return true;
            case OverlayBackAction.ReturnHome:
                SelectDestination(OverlayDestination.Home);
                return true;
            case OverlayBackAction.ClosePopup:
                return true;
            default:
                return false;
        }
    }

    /// <summary>One selection path for touch, mouse and LB/RB: the strip carries stable
    /// destination IDs, while this window owns page visibility and semantic focus.</summary>
    private void OnTabSelectionChanged(object? sender, TabStripSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is null
            || !Enum.IsDefined((OverlayDestination)e.SelectedItem.Tag))
        {
            return;
        }

        OverlayDestination destination = (OverlayDestination)e.SelectedItem.Tag;
        RememberDestinationState(_navigation.Destination);
        LeaveAllNestedPages();
        if (!_navigation.Select(destination))
        {
            return;
        }

        _lastDestination = destination;
        ShowDestination(destination, restoreFocus: true);
    }

    private void SelectDestination(OverlayDestination destination)
    {
        if (!_navigation.IsVisible(destination))
        {
            destination = OverlayDestination.Home;
        }

        int index = DestinationIndex(destination);
        if (Tabs.SelectedIndex != index)
        {
            Tabs.SelectedIndex = index;
            return;
        }

        RememberDestinationState(_navigation.Destination);
        LeaveAllNestedPages();
        _navigation.Select(destination);
        _lastDestination = destination;
        ShowDestination(destination, restoreFocus: true);
    }

    private void ShowDestination(OverlayDestination destination, bool restoreFocus)
    {
        PanelHome.IsVisible = destination == OverlayDestination.Home;
        PanelSteam.IsVisible = destination == OverlayDestination.Steam;
        PanelDevice.IsVisible = destination == OverlayDestination.Device
            && _deviceBridge?.Snapshot().Visible is true;
        PanelSystem.IsVisible = destination == OverlayDestination.System;
        RestoreDestinationState(restoreFocus);
    }

    private Control DestinationPanel() => _navigation.Destination switch
    {
        OverlayDestination.Steam => PanelSteam,
        OverlayDestination.Device => PanelDevice,
        OverlayDestination.System => PanelSystem,
        _ => PanelHome,
    };

    private void RememberDestinationState(OverlayDestination destination)
    {
        OverlayFocusState previous = FocusMemory.Recall(destination);
        string? semanticKey = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is Control { Tag: string key }
            ? key
            : previous.SemanticKey;
        FocusMemory.Remember(destination, semanticKey, ContentScroller.Offset.Y);
    }

    private string? CurrentSemanticFocusKey()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is Control { Tag: string key }
            ? key
            : null;

    private void RestoreRootFocus(string? semanticKey)
    {
        OverlayFocusState state = FocusMemory.Recall(_navigation.Destination);
        FocusMemory.Remember(
            _navigation.Destination,
            semanticKey ?? state.SemanticKey,
            state.ScrollOffset);
        RestoreDestinationState(focus: true);
    }

    private void RestoreDestinationState(bool focus)
    {
        OverlayFocusState state = FocusMemory.Recall(_navigation.Destination);
        ContentScroller.Offset = new Vector(0, state.ScrollOffset);
        if (!focus)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || AnySubView)
            {
                return;
            }

            Control panel = DestinationPanel();
            if (state.SemanticKey is not null)
            {
                foreach (var visual in panel.GetVisualDescendants())
                {
                    if (visual is Control
                        {
                            Tag: string key,
                            Focusable: true,
                            IsEffectivelyEnabled: true,
                            IsEffectivelyVisible: true,
                        } target
                        && string.Equals(key, state.SemanticKey, StringComparison.Ordinal))
                    {
                        target.Focus(NavigationMethod.Directional);
                        return;
                    }
                }
            }

            FocusFirstControl(panel);
        });
    }

    private void LeaveAllNestedPages()
    {
        LeaveFormatSubView();
        LeaveLibraryTabsSubView();
        LeaveCardManagerSubView();
        LeaveArtworkSubView();
        LeaveLaunchWrapperSubView();
        LeaveWakeLockSubView();
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
            if (!TryCancelSubView())
            {
                Dismissed?.Invoke();
            }
            e.Handled = true;
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
        try
        {
            IReadOnlyList<IStorageFile> files;
            // The picker is a separate top-level window, so every touch in it lands
            // outside the bar. Suspend the controller's tap-outside dismissal (and
            // the gamepad driving the bar behind the dialog) until it closes.
            SystemDialogActive?.Invoke(true);
            try
            {
                files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
            }
            finally
            {
                SystemDialogActive?.Invoke(false);
            }
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
            LaunchWrapperHost.OpenCustom(path, await ResolveCurrentGameAsync(CustomLaunchButton));
            if (_closed)
            {
                return;
            }
            EnterLaunchWrapperSubView();
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                CustomLaunchButton.Title = "Couldn't choose a file";
            }
            Log.Error("Could not pick a custom launch action", ex);
        }
    }

    /// <summary>Resolves the game whose Steam page is on screen, so a custom action
    /// applies to it directly. Answers <c>null</c> for the library root and for a
    /// Steam that reported no current app — the caller then asks which game, exactly
    /// as <see cref="ApplyLaunchFixAsync"/> does for the wrapper buttons.</summary>
    private async Task<SteamCollections.AppInfo?> ResolveCurrentGameAsync(CardButton button)
    {
        button.Title = "Asking Steam…";
        var appId = await SteamPageBridge.GetCurrentAppIdAsync();
        if (_closed || appId <= 0)
        {
            return null;
        }
        var match = (await SafeGameLookupAsync()).FirstOrDefault(g => g.AppId == appId);
        // A game Steam knows about but the collection store did not list still
        // resolves: the id came from the page, and the shortcut flag from its range.
        return match ?? new SteamCollections.AppInfo(
            appId, appId.ToString(CultureInfo.InvariantCulture), appId >= 0x80000000L);
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
            if (details is not { } current)
            {
                button.Title = "Steam didn't answer";
                return;
            }
            var existing = await LibraryTabManager.FindLaunchWrapperAsync(game.AppId);
            var originals = existing is null
                ? (current.ShortcutTarget,
                    game.Shortcut ? current.ShortcutArguments : current.LaunchOptions,
                    current.ShortcutStartDir)
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
        if (!_navigation.Push(OverlayPage.SystemWakeLocks, CurrentSemanticFocusKey()))
        {
            return;
        }
        InWakeLockSubView = true;
        PanelSystem.IsVisible = false;
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
        string? returnFocusKey = _navigation.Pop();
        SubViewClosed?.Invoke();
        WakeLockHost.IsVisible = false;
        PanelSystem.IsVisible = _navigation.Destination == OverlayDestination.System;
        if (PanelSystem.IsVisible)
        {
            RestoreRootFocus(returnFocusKey);
        }
    }

    private void EnterLaunchWrapperSubView()
    {
        if (!_navigation.Push(OverlayPage.SteamLaunchConfiguration, CurrentSemanticFocusKey()))
        {
            return;
        }
        InLaunchWrapperSubView = true;
        PanelSteam.IsVisible = false;
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
        string? returnFocusKey = _navigation.Pop();
        _pendingLaunchFix = null;
        // Clears the "Asking Steam…" title left on whichever button opened the
        // picker. A pick re-writes it moments later with the real outcome.
        if (DataContext is OverlayViewModel viewModel)
        {
            InitializeLaunchFixLabels(viewModel);
        }
        SubViewClosed?.Invoke();
        LaunchWrapperHost.IsVisible = false;
        PanelSteam.IsVisible = _navigation.Destination == OverlayDestination.Steam;
        if (PanelSteam.IsVisible)
        {
            RestoreRootFocus(returnFocusKey);
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
    /// the format panel. Both are Steam nested pages, so the old one must be left first
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
    /// rather than the Steam root, because that is where the user opened it from.</summary>
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
        if (!_navigation.Push(OverlayPage.SteamCardManager, CurrentSemanticFocusKey()))
        {
            return;
        }
        InCardManagerSubView = true;
        PanelSteam.IsVisible = false;
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
        string? returnFocusKey = _navigation.Pop();
        SubViewClosed?.Invoke();
        CardManagerHost.IsVisible = false;
        PanelSteam.IsVisible = _navigation.Destination == OverlayDestination.Steam;
        if (PanelSteam.IsVisible)
        {
            RestoreRootFocus(returnFocusKey);
        }
    }

    private void EnterLibraryTabsSubView()
    {
        if (!_navigation.Push(OverlayPage.SteamLibraryTabs, CurrentSemanticFocusKey()))
        {
            return;
        }
        InLibraryTabsSubView = true;
        PanelSteam.IsVisible = false;
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
        string? returnFocusKey = _navigation.Pop();
        SubViewClosed?.Invoke();
        LibraryTabsHost.IsVisible = false;
        PanelSteam.IsVisible = _navigation.Destination == OverlayDestination.Steam;
        if (PanelSteam.IsVisible)
        {
            RestoreRootFocus(returnFocusKey);
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
        if (!_navigation.Push(OverlayPage.SteamArtwork, CurrentSemanticFocusKey()))
        {
            return;
        }
        InArtworkSubView = true;
        PanelSteam.IsVisible = false;
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
        string? returnFocusKey = _navigation.Pop();
        SubViewClosed?.Invoke();
        ArtworkHost.Close();
        ArtworkHost.IsVisible = false;
        PanelSteam.IsVisible = _navigation.Destination == OverlayDestination.Steam;
        if (PanelSteam.IsVisible)
        {
            RestoreRootFocus(returnFocusKey);
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
        if (!_navigation.Push(OverlayPage.SteamStorageFormat, CurrentSemanticFocusKey()))
        {
            return;
        }
        InFormatSubView = true;
        PanelSteam.IsVisible = false;
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
        string? returnFocusKey = _navigation.Pop();
        _pendingTarget = null;
        _formatReturnsToCards = false;
        // Close the format-name peer keyboard on the way out, matching the other
        // Leave*SubView methods; without this the keyboard can outlive its sub-view
        // and keep writing back to the now-hidden name field.
        SubViewClosed?.Invoke();
        PanelFormat.IsVisible = false;
        PanelSteam.IsVisible = _navigation.Destination == OverlayDestination.Steam;
        if (PanelSteam.IsVisible)
        {
            RestoreRootFocus(returnFocusKey);
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
            if (!AnySubView && control.Tag is string semanticKey)
            {
                FocusMemory.Remember(
                    _navigation.Destination,
                    semanticKey,
                    ContentScroller.Offset.Y);
            }
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

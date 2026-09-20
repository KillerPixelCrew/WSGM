using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    internal void AttachPowerPresets(DevicePowerPresetSelection selection)
    {
        DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
        // Polled only while its rows can be seen: on their Device page, or pinned to Quick access.
        refresh.Tick += async (_, _) =>
        {
            if (DevicePowerPresetContainer.IsEffectivelyVisible || PinShowing("section.device.power-presets"))
            {
                await selection.RefreshAsync();
            }
        };
        Opened += async (_, _) =>
        {
            refresh.Start();
            await selection.RefreshAsync();
        };
        Closed += (_, _) =>
        {
            refresh.Stop();
            selection.Dispose();
        };
        DevicePowerPresetHost.Attach(selection);
        DevicePowerPresetContainer.Tag = "section.device.power-presets";
        DevicePowerPresetContainer.Children.Insert(0,
            CreateSectionHeader("section.device.power-presets", "Power assignments"));
        _controlPinFactories["section.device.power-presets"] = ("Power assignments", () =>
                {
                    DevicePowerPresetView view = new();
                    view.Attach(selection);
                    view.DetachedFromVisualTree += (_, _) => view.Attach(null);
                    return view;
                }
            );
        RenderPins();
    }

    internal void AttachManualTdp(DeviceCoordinator coordinator)
    {
        ManualTdpHost.Tag = "section.device.manual-tdp";
        ManualTdpHost.Children.Add(CreateSectionHeader("section.device.manual-tdp", "Manual power mode"));
        var view = Create();
        view.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty)
            {
                ManualTdpHost.IsVisible = view.IsVisible;
            }
        };
        ManualTdpHost.Children.Add(view);
        _controlPinFactories["section.device.manual-tdp"] = ("Manual power mode", Create);
        RenderPins();
        return;

        Control Create()
        {
            return new ManualTdpModeView(() => coordinator.ManualTdpMode, coordinator.SetManualTdpModeAsync);
        }
    }

    internal void AttachBrightness(NativeQamBrightnessService service,
        Func<Task<DisplayModeSnapshot?>>? readMode = null)
    {
        AttachBrightnessSurface(service);
        DisplayBrightnessHost.Tag = "section.display";
        PanelSystemDisplay.Children[0] = CreateSectionHeader("section.display", "Display");
        DisplayBrightnessHost.Children.Add(new DisplayBrightnessView(service));
        DisplayBrightnessHost.Children.Add(new DisplayModeView(readMode));
        _controlPinFactories["section.display"] = ("Display", () => new StackPanel
        {
            Spacing = 4,
            Children = { new DisplayBrightnessView(service), new DisplayModeView(readMode) }
        });
        RenderPins();
    }

    /// <summary>Whether a pinned row is on screen: its id is pinned and the Quick access root shows.</summary>
    private bool PinShowing(string id)
    {
        return PanelQuickAccess.IsEffectivelyVisible && _pins.Contains(id);
    }

    internal void AttachSteamOwnership(Func<SteamControllerHandoff?> getOwner)
    {
        ReleaseSteamOwnership.Click += (_, _) =>
        {
            getOwner()?.ReleaseManually();
            Refresh();
        };
        ReacquireSteamOwnership.Click += (_, _) =>
        {
            getOwner()?.ReacquireManually();
            Refresh();
        };
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        // Polled only while the rows can be seen: on the Controller page, or pinned to Quick access,
        // whose mirrors follow these buttons' enabled state.
        timer.Tick += (_, _) =>
        {
            if (PanelSystemController.IsEffectivelyVisible
                || PinShowing("system.release-steam") || PinShowing("system.reacquire-steam"))
            {
                Refresh();
            }
        };
        Opened += (_, _) =>
        {
            Refresh();
            timer.Start();
        };
        Closed += (_, _) => timer.Stop();
        Refresh();
        return;

        void Refresh()
        {
            var owner = getOwner();
            SteamOwnershipStatus.Text = owner?.State switch
            {
                SteamControllerOwnership.Wsgm => "Owned by WSGM",
                SteamControllerOwnership.Releasing => "Releasing",
                SteamControllerOwnership.Steam => owner.ManualRelease
                    ? "Released to Steam (manual)"
                    : "Released to Steam (temporary)",
                SteamControllerOwnership.Reacquiring => "Reacquiring",
                SteamControllerOwnership.RecoveryRequired => "Failed: controller recovery required",
                _ => "Unavailable"
            };
            ReleaseSteamOwnership.IsEnabled = owner is
            {
                ManualRelease: false,
                State: SteamControllerOwnership.Wsgm or SteamControllerOwnership.Steam
            };
            ReacquireSteamOwnership.IsEnabled = owner is { ManualRelease: true, State: SteamControllerOwnership.Steam }
                or { State: SteamControllerOwnership.RecoveryRequired };
        }
    }

    internal void AttachPowerSchemes(PowerSchemeSelection selection)
    {
        _powerSchemeSelection = selection;
        Opened += async (_, _) => await selection.RefreshAsync();
        Closed += (_, _) => selection.Dispose();
        DevicePowerSchemeHost.Attach(selection);
        DevicePowerSchemeHost.Tag = "section.system.power-profile";
        DevicePowerSchemeHeading.Children.Clear();
        DevicePowerSchemeHeading.Children.Add(
            CreateSectionHeader("section.system.power-profile", "Windows energy plan"));
        _controlPinFactories["section.system.power-profile"] = ("Windows energy plan", () =>
                {
                    PowerSchemeView view = new();
                    view.Attach(selection);
                    view.DetachedFromVisualTree += (_, _) => view.Attach(null);
                    return view;
                }
            );
        RefreshDevicePanel();
    }

    /// <summary>Attaches the hybrid core-placement workflow shown beside the Windows energy plan.</summary>
    /// <param name="selection">The workflow for this open; refreshed when the window opens and disposed when it closes.</param>
    internal void AttachHybridCores(HybridCoreSelection selection)
    {
        _hybridCoreSelection = selection;
        Opened += async (_, _) => await selection.RefreshAsync();
        Closed += (_, _) => selection.Dispose();
        DeviceHybridCoreHost.Attach(selection);
        DeviceHybridCoreHost.Tag = "section.system.processor-cores";
        DeviceHybridCores.Header = CreateSectionHeader("section.system.processor-cores", "Processor cores");
        _controlPinFactories["section.system.processor-cores"] = ("Processor cores", () =>
                {
                    HybridCoreView view = new();
                    view.Attach(selection);
                    view.DetachedFromVisualTree += (_, _) => view.Attach(null);
                    return view;
                }
            );
        RefreshDevicePanel();
    }

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

    /// <summary>
    ///     Gives the overlay the shared removable-storage format manager so
    ///     its Steam storage page can drive it. Called by the controller right after
    ///     construction (the manager outlives the window).
    /// </summary>
    /// <param name="format">The controller-owned format manager.</param>
    internal void AttachFormatManager(SdFormatManager format)
    {
        _format = format;
        PanelFormat.DataContext = format;
    }

    /// <summary>Attaches the semantic coordinator projection used by the optional Device tab.</summary>
    internal void AttachCommonPlugins(CommonPluginOverlaySource? source)
    {
        CommonPluginRows.Children.Clear();
        DeviceWidgetPinsHost.Children.Clear();
        PinnedPluginWidgetsHost.Children.Clear();
        SystemPluginsTile.IsVisible = source is not null;
        if (source is null)
        {
            return;
        }

        var preferences = source.WidgetPreferences;
        CommonPluginPanel panel = new(source, preferences: preferences);
        CommonPluginRows.Children.Add(panel);
        if (source.Device is { } device)
        {
            DeviceWidgetPinsHost.Children.Add(new CommonPluginPanel(device, pinsOnly: true,
                preferences: preferences));
        }

        PinnedPluginWidgetsHost.Children.Add(new PinnedPluginWidgets(source, (pin, category) =>
        {
            // The rows moved a level down when Tools became a menu, so the jump has to open the
            // Plugins category too: selecting the destination alone now lands on the tiles.
            SelectDestination(OverlayDestination.System);
            EnterSubView(OverlayPage.SystemPlugins);
            Dispatcher.UIThread.Post(() => panel.FocusCategory(pin, category));
        }, preferences));
    }

    /// <summary>Supplies the reader behind the Device page's missing-prerequisites banner.</summary>
    /// <param name="prerequisites">The reader, or null in a preview with no session behind it.</param>
    internal void AttachDevicePrerequisites(DevicePrerequisiteSource? prerequisites)
    {
        _devicePrerequisites = prerequisites;
        RefreshDevicePrerequisites();
    }

    internal void AttachDeviceBridge(IDeviceOverlaySource? bridge)
    {
        if (ReferenceEquals(_deviceBridge, bridge))
        {
            return;
        }

        // Released against the outgoing bridge, before the field moves. Doing it after would leave
        // the old bridge holding a subscription and an observer count nothing can reach any more.
        UpdateGlyphInputObservation(false);
        if (_deviceBridge is not null)
        {
            _deviceBridge.Changed -= OnDeviceChanged;
        }

        _deviceBridge = bridge;
        if (_deviceBridge is not null)
        {
            _deviceBridge.Changed += OnDeviceChanged;
        }

        UpdateGlyphInputObservation(
            DeviceOverlaySectionPages.SectionFor(_navigation.Page)
                is DeviceOverlaySection.ControllerAndMotion);
        RefreshDevicePanel();
    }

    /// <summary>Attaches the shared performance projection without transferring its lifetime.</summary>
    internal void AttachPerformanceSource(PerformanceOverlayBridge? source)
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

        PerformanceSection.Tag = "section.performance";
        PerformanceSection.Children[0] = CreateSectionHeader("section.performance", "Performance");
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
}

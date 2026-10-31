using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>Attaches the session services the sheet shows, without taking over their lifetimes.</summary>
    /// <param name="sources">The session's services; a null one hides or empties its page.</param>
    /// <remarks>
    ///     Brightness and the manual power mode attach separately, with the controller's preview rules, and the
    ///     controller connects Notes to its screenshot capture.
    /// </remarks>
    internal void AttachSources(OverlaySources sources)
    {
        AttachDeviceBridge(sources.Device);
        AttachGraphicsSource(sources.Graphics);
        AttachDevicePrerequisites(sources.DevicePrerequisites);
        AttachCommonPlugins(sources.CommonPlugins);
        GameLibraryHost.Attach(sources.GameLibrary);
        EmulatorManagerHost.Attach(sources.Emulators);
        ViewModel.ShowEmulators = sources.Emulators is not null;
        AttachPrograms(sources.Programs);
        AttachLaunchActions(sources.LaunchActions);
        LibraryTabsHost.Attach(sources.LibraryManager);
        CardManagerHost.Attach(sources.LibraryManager);
        ScreensaversHost.Attach(sources.Screensavers);
        if (sources.Browser is { } browser)
        {
            AttachBrowserSession(browser);
        }

        ThemesHost.Attach(sources.Themes);
        AnimationsHost.Attach(sources.Animations);
        SoundsHost.Attach(sources.Sounds, _session.ExpandedSections);
        ArtworkHost.Attach(sources.Artwork);
        AttachPerformanceSource(sources.Performance);
        RefreshWorkspace();
    }

    internal void AttachPowerPresets(DevicePowerPresetSelection selection)
    {
        // Read once on open; after that the selection follows the assignment owner's changes.
        Opened += async (_, _) => await selection.RefreshAsync();
        Closed += (_, _) => selection.Dispose();
        DevicePowerPresetHost.Attach(selection);
        DevicePowerPresetContainer.Tag = "section.device.power-presets";
        DevicePowerPresetContainer.Children.Insert(0,
            CreateSectionHeader("section.device.power-presets", "Power assignments"));
        _controlPinFactories["section.device.power-presets"] = ("Power assignments",
            AttachedPin<DevicePowerPresetView, DevicePowerPresetSelection>(selection,
                (view, model) => view.Attach(model)));
        RenderPins();
    }

    /// <summary>A pinned copy of a view that follows a shared selection while it is in the tree.</summary>
    /// <param name="selection">The selection every copy shares.</param>
    /// <param name="attach">Attaches a view to the selection, or detaches it with null.</param>
    /// <returns>The pin factory.</returns>
    private static Func<Control> AttachedPin<TView, TSelection>(TSelection selection,
        Action<TView, TSelection?> attach) where TView : Control, new() where TSelection : class
    {
        return () =>
        {
            TView view = new();
            attach(view, selection);
            view.DetachedFromVisualTree += (_, _) => attach(view, null);
            return view;
        };
    }

    internal void AttachManualTdp(DeviceCoordinator coordinator)
    {
        ManualTdpHost.Tag = "section.device.manual-tdp";
        ManualTdpHost.Children.Add(CreateSectionHeader("section.device.manual-tdp", "Manual power mode"));
        var view = Create();
        // The device refresh owns the host's visibility, which also depends on the snapshot and decides the card's.
        view.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty)
            {
                OnDeviceChanged();
            }
        };
        ManualTdpHost.Children.Add(view);
        _controlPinFactories["section.device.manual-tdp"] = ("Manual power mode", Create);
        RenderPins();
        return;

        Control Create()
        {
            return new ManualTdpModeView(() => coordinator.Power.ManualTdpMode,
                coordinator.Power.SetManualTdpModeAsync,
                () => coordinator.Power.ManualTdpOverrideId, _ => coordinator.Power.UseGlobalManualTdpAsync());
        }
    }

    /// <summary>Follows a device change in the manual power mode selectors, on their page and pinned.</summary>
    /// <remarks>The mode, its availability and its game override all arrive with the device's change event.</remarks>
    private void RefreshManualTdp()
    {
        foreach (var view in ManualTdpHost.Children.OfType<ManualTdpModeView>())
        {
            view.Refresh();
        }

        const string pinTag = PinTagPrefix + "section.device.manual-tdp";
        if (PinnedSectionsGrid.Children.FirstOrDefault(row => Equals(row.Tag, pinTag)) is { } pinned)
        {
            foreach (var view in pinned.GetLogicalDescendants().OfType<ManualTdpModeView>())
            {
                view.Refresh();
            }
        }
    }

    internal void AttachBrightness(NativeQamBrightnessService service,
        DisplayModeAccess displayModes, Func<string?>? display = null)
    {
        display ??= DisplaySourceName;
        AttachBrightnessSurface(service);
        DisplayBrightnessHost.Tag = "section.display";
        // A shown page already owns a fold whose body contains this host. Replacing that fold would
        // detach the entire Display page instead of changing its heading.
        if (PanelSystemDisplay.Children.FirstOrDefault() is not CollapsibleSection)
        {
            PanelSystemDisplay.Children[0] = CreateSectionHeader("section.display", "Display");
        }

        DisplayBrightnessHost.Children.Clear();
        DisplayBrightnessHost.Children.Add(new DisplayBrightnessView(service));
        DisplayBrightnessHost.Children.Add(new DisplayModeView(displayModes.Read, displayModes.Apply, display,
            displayModes.Changes));
        if (_opened)
        {
            FoldStaticSections();
        }

        _controlPinFactories["section.display"] = ("Display", () => new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new DisplayBrightnessView(service),
                new DisplayModeView(displayModes.Read, displayModes.Apply, display, displayModes.Changes)
            }
        });
        RenderPins();
    }

    internal void AttachPowerSchemes(PowerSchemeSelection selection)
    {
        _powerSchemeSelection = selection;
        ConfigureTabs(DeviceSnapshotOrOff().Visible);
        Opened += async (_, _) => await selection.RefreshAsync();
        Closed += (_, _) => selection.Dispose();
        // The section is shown only once Windows has been read and offers a choice.
        selection.Changed += RefreshDevicePanel;
        DevicePowerSchemeHost.Attach(selection);
        DevicePowerSchemeHost.Tag = "section.system.power-profile";
        DevicePowerSchemeHeading.Children.Clear();
        DevicePowerSchemeHeading.Children.Add(
            CreateSectionHeader("section.system.power-profile", "Windows energy plan"));
        _controlPinFactories["section.system.power-profile"] = ("Windows energy plan",
            AttachedPin<PowerSchemeView, PowerSchemeSelection>(selection, (view, model) => view.Attach(model)));
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
        _controlPinFactories["section.system.processor-cores"] = ("Processor cores",
            AttachedPin<HybridCoreView, HybridCoreSelection>(selection, (view, model) => view.Attach(model)));
        RefreshDevicePanel();
    }

    /// <summary>Raised when the user requests to start or focus the home application.</summary>
    public event Action? HomeAppRequested;

    private void OpenEmulators()
    {
        if (ViewModel.ShowEmulators)
        {
            OpenToolsSection(OverlayPage.EmulatorManager);
        }
    }

    private void OpenImportedArtwork(uint appId, string title)
    {
        if (!ViewModel.ShowArtwork)
        {
            return;
        }

        EnterSubView(OverlayPage.SystemArtwork);
        _ = ArtworkHost.OpenGameAsync(appId, title, true);
    }


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
    /// <param name="source">Borrowed current plugin projection; null detaches the prior source and clears its presentation.</param>
    internal void AttachCommonPlugins(CommonPluginOverlaySource? source)
    {
        DetachCommonPlugins();
        CommonPluginRows.Children.Clear();
        DeviceWidgetPinsHost.Children.Clear();
        PinnedPluginWidgetsHost.Children.Clear();
        SystemPluginsTile.IsVisible = source is not null;
        if (source is null)
        {
            return;
        }

        _pluginSource = source;
        source.Changed += OnPluginsChanged;
        DeviceWidgetsExpander.PropertyChanged += OnDeviceWidgetsExpanded;
        var preferences = source.WidgetPreferences;
        CommonPluginPanel panel = new(source, preferences: preferences, folds: _session.ExpandedSections,
            independent: source.IndependentSnapshot);
        CommonPluginRows.Children.Add(panel);
        SystemPluginsTile.IsVisible = panel.IsVisible;
        panel.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty)
            {
                SystemPluginsTile.IsVisible = panel.IsVisible;
                if (!panel.IsVisible && _navigation.Page == OverlayPage.SystemPlugins)
                {
                    SelectDestination(OverlayDestination.System);
                }
                else
                {
                    RefreshWorkspace();
                }
            }
        };
        if (source.Device is { } device)
        {
            DeviceWidgetPinsHost.Children.Add(new CommonPluginPanel(device, pinsOnly: true,
                preferences: preferences));
        }

        PinnedPluginWidgetsHost.Children.Add(new PinnedPluginWidgets(source, (pin, category) =>
        {
            if (source.Device?.Snapshot().Any(instance => instance.Identity.PluginId == pin.PluginId
                                                          && instance.Identity.InstanceId == pin.InstanceId) == true)
            {
                SelectDestination(OverlayDestination.Device);
                var row = DeviceSnapshotOrOff().Capabilities.FirstOrDefault(capability =>
                    DeviceWidgetSource.KeyFor(capability.CapabilityId, capability.InstanceId) == pin.WidgetId);
                var section = _workspaceSections.FirstOrDefault(entry => entry.PluginSection == row?.PluginSectionId);
                if (section is not null)
                {
                    SelectWorkspaceSection(section, true);
                }

                return;
            }

            // The rows moved a level down when Tools became a menu, so the jump has to open the
            // Plugins category too: selecting the destination alone now lands on the tiles.
            SelectDestination(OverlayDestination.System);
            EnterSubView(OverlayPage.SystemPlugins);
            Dispatcher.UIThread.Post(() => panel.FocusCategory(pin, category));
        }, preferences));
    }

    private void DetachCommonPlugins()
    {
        if (_pluginSource is not null)
        {
            _pluginSource.Changed -= OnPluginsChanged;
            _pluginSource = null;
        }

        DeviceWidgetsExpander.PropertyChanged -= OnDeviceWidgetsExpanded;
    }

    // Raised on whatever thread the plugin host or manager finished its work on.
    private void OnPluginsChanged()
    {
        QueueLiveRefresh(PluginsLiveRefresh);
    }

    private void OnDeviceWidgetsExpanded(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Expander.IsExpandedProperty && DeviceWidgetsExpander.IsExpanded)
        {
            RefreshDeviceWidgetPins();
        }
    }

    /// <summary>Follows a device or plugin-host change in the plugin panels that are showing.</summary>
    /// <param name="device">Whether the device changed.</param>
    /// <param name="plugins">Whether the plugin host changed.</param>
    /// <remarks>
    ///     A hidden panel is refreshed as it is shown instead. The System Plugins panel reads no device, and
    ///     its tile follows whether any plugin runs, so it follows every host change.
    /// </remarks>
    private void RefreshPluginWidgets(bool device, bool plugins)
    {
        if (plugins)
        {
            foreach (var panel in CommonPluginRows.Children.OfType<CommonPluginPanel>())
            {
                panel.Refresh();
            }
        }

        if (device)
        {
            RefreshDeviceWidgetPins();
        }

        if (PanelQuickAccess.IsVisible)
        {
            foreach (var widgets in PinnedPluginWidgetsHost.Children.OfType<PinnedPluginWidgets>())
            {
                widgets.Refresh();
            }
        }
    }

    /// <summary>Refreshes the Device page's widget pin toggles while they show.</summary>
    private void RefreshDeviceWidgetPins()
    {
        if (!DeviceWidgetPinsHost.IsEffectivelyVisible)
        {
            return;
        }

        foreach (var panel in DeviceWidgetPinsHost.Children.OfType<CommonPluginPanel>())
        {
            panel.Refresh();
        }
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

        var snapshot = DeviceSnapshotOrOff();
        ConfigureTabs(snapshot.Visible);
        UpdateGlyphInputObservation(
            DeviceOverlaySectionPages.SectionFor(_navigation.Page)
                is DeviceOverlaySection.ControllerAndMotion);
        RefreshNavigationHints();
        RefreshDevicePrerequisites(snapshot);
        if (DevicePanelShowing())
        {
            RefreshDevicePanel(snapshot, _performanceSource?.Snapshot());
        }

        RenderPins(snapshot);
    }

    /// <summary>Attaches the shared performance projection without transferring its lifetime.</summary>
    /// <param name="source">Borrowed performance projection; null detaches the prior source.</param>
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

        PerformanceSection.Tag = "section.performance";
        if (PerformanceSection.Children.FirstOrDefault() is not CollapsibleSection)
        {
            PerformanceSection.Children.Insert(0, CreateSectionHeader("section.performance", "Performance"));
            FoldStaticSections();
        }

        _performanceSource = source;
        if (_performanceSource is not null)
        {
            try
            {
                _performanceSource.Changed += OnPerformanceChanged;
                _performanceSource.RefreshWindowsState();
            }
            catch (Exception ex)
            {
                _performanceSource.Changed -= OnPerformanceChanged;
                _performanceSource = null;
                Log.Warn($"Performance overlay source could not attach: {ex.Message}");
            }
        }

        RefreshPerformancePanel();
        RenderPins();
    }
}

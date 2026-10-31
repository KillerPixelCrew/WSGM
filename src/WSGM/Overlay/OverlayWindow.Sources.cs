using System;
using System.Linq;
using Avalonia.Controls;
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
        GameToolsHost.Attach(sources.GameTools);
        AttachPerformanceSource(sources.Performance);
        RefreshWorkspace();
    }

    internal void AttachPowerPresets(DevicePowerPresetSelection selection)
    {
        _powerPresetSelection = selection;
        // Read once on open; after that the selection follows the assignment owner's changes.
        Opened += async (_, _) => await selection.RefreshAsync();
        Closed += (_, _) => selection.Dispose();
        // The assignments are shown only once a preset has been read to choose from.
        selection.Changed += RefreshDevicePanel;
        _controlPinFactories[DevicePages.PowerPresetsId] =
            AttachedPin<DevicePowerPresetView, DevicePowerPresetSelection>(selection,
                (view, model) => view.Attach(model));
        RefreshDevicePanel();
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
        // The mode, its availability and its game override all arrive with the device's change event, which
        // redraws the page and refreshes every selector in place.
        _manualTdpMode = () => coordinator.Power.ManualTdpMode;
        _controlPinFactories[DevicePages.ManualTdpId] = () => new ManualTdpModeView(
            () => coordinator.Power.ManualTdpMode, coordinator.Power.SetManualTdpModeAsync,
            () => coordinator.Power.ManualTdpOverrideId, _ => coordinator.Power.UseGlobalManualTdpAsync());
        RefreshDevicePanel();
        RenderPins();
    }

    internal void AttachBrightness(NativeQamBrightnessService service,
        DisplayModeAccess displayModes, Func<string?>? display = null)
    {
        display ??= Placement.DisplaySourceName;
        Surfaces.AttachBrightness(service);
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
            FoldDisplaySection();
        }

        _controlPinFactories[DevicePages.DisplayId] = () => new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new DisplayBrightnessView(service),
                new DisplayModeView(displayModes.Read, displayModes.Apply, display, displayModes.Changes)
            }
        };
        RenderPins();
    }

    /// <summary>Folds the Display page under its pinnable heading, once its heading is in place.</summary>
    private void FoldDisplaySection()
    {
        if (PanelSystemDisplay.Children.FirstOrDefault() is not SectionPinHeader header)
        {
            return;
        }

        var body = new StackPanel { Spacing = PanelSystemDisplay.Spacing, Tag = DevicePages.DisplayId };
        foreach (var child in PanelSystemDisplay.Children.Skip(1).ToArray())
        {
            PanelSystemDisplay.Children.Remove(child);
            body.Children.Add(child);
        }

        PanelSystemDisplay.Children.Clear();
        // The fold's heading carries the title; the header keeps only its pin action there.
        header.ShowPinOnly();
        PanelSystemDisplay.Children.Add(CollapsibleSection.Persisted(header.Title, body, _session.ExpandedSections,
            DevicePages.DisplayId, DevicePages.DisplayId + ".expand", header));
    }

    internal void AttachPowerSchemes(ChoiceRowSelection selection)
    {
        _powerSchemeSelection = selection;
        ConfigureTabs(DeviceSnapshotOrOff());
        Opened += async (_, _) => await selection.RefreshAsync();
        Closed += (_, _) => selection.Dispose();
        // The section is shown only once Windows has been read and offers a choice.
        selection.Changed += RefreshDevicePanel;
        _controlPinFactories[DevicePages.PowerProfileId] =
            AttachedPin<ChoiceRowView, ChoiceRowSelection>(selection, (view, model) => view.Attach(model));
        RefreshDevicePanel();
    }

    /// <summary>Attaches the hybrid core-placement workflow shown beside the Windows energy plan.</summary>
    /// <param name="selection">The workflow for this open; refreshed when the window opens and disposed when it closes.</param>
    internal void AttachHybridCores(ChoiceRowSelection selection)
    {
        _hybridCoreSelection = selection;
        Opened += async (_, _) => await selection.RefreshAsync();
        Closed += (_, _) => selection.Dispose();
        // Only on a hybrid CPU whose active scheme exposes the policy. Everywhere else the section would
        // open on a control that has nothing to offer.
        selection.Changed += RefreshDevicePanel;
        _controlPinFactories[DevicePages.ProcessorCoresId] =
            AttachedPin<ChoiceRowView, ChoiceRowSelection>(selection, (view, model) => view.Attach(model));
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

    /// <summary>Attaches the semantic coordinator projection used by the optional Device tab.</summary>
    /// <param name="source">Borrowed current plugin projection; null detaches the prior source and clears its presentation.</param>
    internal void AttachCommonPlugins(CommonPluginOverlaySource? source)
    {
        DetachCommonPlugins();
        CommonPluginRows.Children.Clear();
        DevicePage.WidgetPins.Children.Clear();
        PinnedPluginWidgetsHost.Children.Clear();
        SystemPluginsTile.IsVisible = source is not null;
        if (source is null)
        {
            return;
        }

        _pluginSource = source;
        source.Changed += OnPluginsChanged;
        DevicePage.PinsExpanded += RefreshDeviceWidgetPins;
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
            DevicePage.WidgetPins.Children.Add(new CommonPluginPanel(device, pinsOnly: true,
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

        DevicePage.PinsExpanded -= RefreshDeviceWidgetPins;
    }

    // Raised on whatever thread the plugin host or manager finished its work on.
    private void OnPluginsChanged()
    {
        QueueLiveRefresh(PluginsLiveRefresh);
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
        if (!DevicePage.WidgetPins.IsEffectivelyVisible)
        {
            return;
        }

        foreach (var panel in DevicePage.WidgetPins.Children.OfType<CommonPluginPanel>())
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
        ObserveGlyphInput(false);
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
        ConfigureTabs(snapshot);
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

        _profileScope.Attach(_performanceSource);
        RefreshPerformancePanel();
        RefreshDevicePanel();
        RenderPins();
    }
}

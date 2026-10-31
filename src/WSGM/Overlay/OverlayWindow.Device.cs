using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>Completes when the current prerequisite read has been published to the banner.</summary>
    internal Task DevicePrerequisitesRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Moves focus to Device when the destination is available; otherwise leaves the current tab.</summary>
    internal void SelectDeviceDestination()
    {
        if (_navigation.IsVisible(OverlayDestination.Device))
        {
            SelectDestination(OverlayDestination.Device);
        }
    }

    private void OnDeviceChanged()
    {
        QueueLiveRefresh(DeviceLiveRefresh);
    }

    /// <summary>Reads the prerequisites again when the integration switch or the device changed.</summary>
    /// <param name="device">The current Device snapshot.</param>
    /// <remarks>
    ///     The read checks files, a driver and the registry, and only the definition and the switch change
    ///     what it finds, so a value or telemetry change does not repeat it.
    /// </remarks>
    private void RefreshDevicePrerequisites(DeviceOverlaySnapshot device)
    {
        (bool Enabled, string Device) subject = (device.Visible, device.Detail);
        if (_devicePrerequisitesFor == subject)
        {
            return;
        }

        _devicePrerequisitesFor = subject;
        RefreshDevicePrerequisites();
    }

    /// <summary>Renders what a device package on this install is missing, if anything.</summary>
    private void RefreshDevicePrerequisites()
    {
        // The read opens every package in the Plugins folder, so it runs on a worker and the banner
        // fills in when it lands. One read runs at a time; a refresh asked for meanwhile reads once
        // more after it.
        if (_devicePrerequisites is not { } source)
        {
            ShowDevicePrerequisites(new DevicePrerequisiteAdvice(""));
            return;
        }

        if (_devicePrerequisitesReading)
        {
            _devicePrerequisitesStale = true;
            return;
        }

        _devicePrerequisitesReading = true;
        DevicePrerequisitesRefresh = ReadDevicePrerequisitesAsync(source);
        Log.Observe(DevicePrerequisitesRefresh, "Overlay device prerequisites");
    }

    private async Task ReadDevicePrerequisitesAsync(DevicePrerequisiteSource source)
    {
        try
        {
            var advice = await Task.Run(() => source.Read());
            if (ReferenceEquals(_devicePrerequisites, source))
            {
                ShowDevicePrerequisites(advice);
            }
        }
        finally
        {
            _devicePrerequisitesReading = false;
            if (_devicePrerequisitesStale)
            {
                _devicePrerequisitesStale = false;
                RefreshDevicePrerequisites();
            }
        }
    }

    private void ShowDevicePrerequisites(DevicePrerequisiteAdvice advice)
    {
        // No page check: the banner is a child of PanelDevice, so that panel's own visibility is
        // the gate. It stays up on the Device sub-pages too, which is where someone hunting a dead
        // device most likely ends up.
        DevicePrerequisiteBanner.IsVisible = advice.HasAdvice;
        // The banner only explains: INV-020 keeps driver installation in setup, because the USB/IP
        // install restarts every USB 3.0 hub and would take the pad, the touch digitiser and the
        // keyboard away from whoever is holding the machine.
        DevicePrerequisiteDetail.Text = advice.Detail;
    }

    /// <summary>
    ///     Redraws the written activation hints as the device's own buttons, where one resolved.
    /// </summary>
    /// <remarks>
    ///     Written letters stay in the markup and remain the fallback, so this only ever adds. That
    ///     matters on the two machines it will not resolve for — one with no glyph profile, and one
    ///     where the input actually reaching WSGM is not the managed handheld's — because a hint showing
    ///     a Claw button while the user holds an Xbox pad is worse than the letter it replaced.
    /// </remarks>
    private void RefreshNavigationHints()
    {
        // FaceSouth rather than "A": the glyph vocabulary is positional, so a device whose bottom
        // face button is printed with something else gets the button it actually has.
        HomeAppButton.TrailingGlyph = _deviceBridge?.NavigationHint(GlyphControlId.FaceSouth);
    }

    private void OnPerformanceChanged()
    {
        QueueLiveRefresh(PerformanceLiveRefresh);
    }

    /// <summary>
    ///     Coalesces telemetry-driven redraws and keeps the current visual tree alive for the
    ///     complete pointer gesture. Replacing a button after pointer-down but before pointer-up drops
    ///     its Click, which presents as a button that needs a second tap.
    /// </summary>
    private void QueueLiveRefresh(int refreshes)
    {
        Interlocked.Or(ref _pendingLiveRefreshes, refreshes);
        ScheduleLiveRefresh();
    }

    private void ScheduleLiveRefresh()
    {
        if (Interlocked.CompareExchange(ref _liveRefreshScheduled, 1, 0) == 0)
        {
            Dispatcher.Post(ProcessLiveRefreshes, DispatcherPriority.Background);
        }
    }

    private void ProcessLiveRefreshes()
    {
        Interlocked.Exchange(ref _liveRefreshScheduled, 0);
        if (_closed || _pressedPointers.Count > 0)
        {
            return;
        }

        var refreshes = Interlocked.Exchange(ref _pendingLiveRefreshes, 0);
        var deviceChanged = (refreshes & DeviceLiveRefresh) != 0;
        var performanceChanged = (refreshes & PerformanceLiveRefresh) != 0;
        var graphicsChanged = (refreshes & GraphicsLiveRefresh) != 0;
        var pluginsChanged = (refreshes & PluginsLiveRefresh) != 0;
        var offset = ContentScroller.Offset;
        var page = _navigation.Page;
        var sectionId = _navigation.SectionId;
        PanelDevice.AddHandler(RequestBringIntoViewEvent, KeepViewport);
        GpuHost.AddHandler(RequestBringIntoViewEvent, KeepViewport);
        PanelSystem.AddHandler(RequestBringIntoViewEvent, KeepViewport);
        try
        {
            // Each source is projected once for the pass and handed to every panel that draws it.
            var device = deviceChanged ? DeviceSnapshotOrOff() : null;
            if (device is not null)
            {
                ConfigureTabs(device);
                RefreshNavigationHints();
                RefreshDevicePrerequisites(device);
                RefreshManualTdp();
            }

            // A hidden Device panel is marked stale instead, and draws when it next shows.
            var renderDevice = (deviceChanged || performanceChanged || graphicsChanged) && DevicePanelShowing();
            var performance = performanceChanged || renderDevice ? _performanceSource?.Snapshot() : null;
            var graphics = graphicsChanged ? _graphicsSource?.Snapshot() ?? GraphicsOverlaySnapshot.Empty : null;
            if (performanceChanged)
            {
                RefreshPerformancePanel(performance);
            }

            if (renderDevice)
            {
                device ??= DeviceSnapshotOrOff();
                RefreshDevicePanel(device, performance, graphics);
            }

            if (graphics is not null)
            {
                ConfigureGpuDestination(graphics.Visible);
                RefreshGraphicsPanel(graphics);
            }

            // Once per pass, whatever changed: program, performance, device and graphics rows can all
            // be pinned.
            RenderPins(device, performance, graphics);
            if (deviceChanged || pluginsChanged)
            {
                RefreshPluginWidgets(deviceChanged, pluginsChanged);
            }

            // Replacing the anchor/focused row is an observation update, not navigation. Complete
            // layout before restoring the offset so its temporary shorter extent cannot clamp it.
            ContentScroller.UpdateLayout();
            if (page == _navigation.Page && sectionId == _navigation.SectionId)
            {
                ContentScroller.Offset = offset;
            }
        }
        finally
        {
            PanelDevice.RemoveHandler(RequestBringIntoViewEvent, KeepViewport);
            GpuHost.RemoveHandler(RequestBringIntoViewEvent, KeepViewport);
            PanelSystem.RemoveHandler(RequestBringIntoViewEvent, KeepViewport);
        }

        if (Volatile.Read(ref _pendingLiveRefreshes) != 0)
        {
            ScheduleLiveRefresh();
        }

        return;

        static void KeepViewport(object? sender, RequestBringIntoViewEventArgs args)
        {
            args.Handled = true;
        }
    }

    private void OnPointerPressedForLiveRefresh(object? sender, PointerPressedEventArgs e)
    {
        _pressedPointers.Add(e.Pointer);
    }

    private void OnPointerReleasedForLiveRefresh(object? sender, PointerReleasedEventArgs e)
    {
        _pressedPointers.Remove(e.Pointer);
        ResumeLiveRefreshAfterPointer();
    }

    private void OnPointerCaptureLostForLiveRefresh(object? sender, PointerCaptureLostEventArgs e)
    {
        _pressedPointers.Remove(e.Pointer);
        ResumeLiveRefreshAfterPointer();
    }

    private void ResumeLiveRefreshAfterPointer()
    {
        if (_pressedPointers.Count == 0 && Volatile.Read(ref _pendingLiveRefreshes) != 0)
        {
            // This tunnel handler runs before Button processes the release. Background priority
            // lets Click finish against the original control before any deferred tree replacement.
            ScheduleLiveRefresh();
        }
    }

    private DeviceOverlaySnapshot DeviceSnapshotOrOff()
    {
        return _deviceBridge?.Snapshot()
               ?? new DeviceOverlaySnapshot(false, "Device integration off", string.Empty, null, []);
    }

    /// <summary>Whether the Device panel can be drawn now; a hidden one is marked stale instead.</summary>
    private bool DevicePanelShowing()
    {
        // The first render runs when the window opens.
        if (_closed || !_opened)
        {
            return false;
        }

        if (PanelDevice.IsVisible)
        {
            return true;
        }

        _devicePanelStale = true;
        return false;
    }

    /// <summary>Draws the Device panel from current snapshots when it shows; otherwise marks it stale.</summary>
    private void RefreshDevicePanel()
    {
        if (DevicePanelShowing())
        {
            RefreshDevicePanel(DeviceSnapshotOrOff(), _performanceSource?.Snapshot());
        }
    }

    /// <summary>Draws the Device panel and the shared performance rows from one read of each source.</summary>
    /// <param name="device">The Device snapshot, when the caller already holds one.</param>
    private void RefreshDevicePage(DeviceOverlaySnapshot? device = null)
    {
        var performance = _performanceSource?.Snapshot();
        device ??= DeviceSnapshotOrOff();
        if (DevicePanelShowing())
        {
            RefreshDevicePanel(device, performance);
        }

        RefreshPerformancePanel(performance);
    }

    /// <summary>Draws the Device panel from snapshots the caller read once for the whole pass.</summary>
    /// <param name="snapshot">The Device snapshot.</param>
    /// <param name="performance">The performance snapshot, or null without a performance source.</param>
    /// <param name="graphics">The graphics snapshot, or null to read it for the pin list.</param>
    private void RefreshDevicePanel(DeviceOverlaySnapshot snapshot, PerformanceOverlaySnapshot? performance,
        GraphicsOverlaySnapshot? graphics = null)
    {
        if (_closed || !_opened)
        {
            return;
        }

        _devicePanelStale = false;
        var sectionPages = DeviceSectionPages(snapshot, performance);
        var pinSections = DevicePinSections(snapshot);
        PlaceUnifiedPowerControls(snapshot, sectionPages);
        RefreshDeviceSectionRail(sectionPages);
        var powerPage = _navigation.Page is OverlayPage.Device || DevicePageOpen(DeviceSections.PowerId);
        DevicePowerSchemeHost.IsVisible = powerPage;
        DeviceWindowsPower.IsVisible = _powerSchemeSelection is { State.Options.Count: > 0 };
        DevicePowerPresetContainer.IsVisible = powerPage && snapshot.Visible && DevicePowerPresetHost.IsVisible;
        ManualTdpHost.IsVisible = snapshot.Visible && ManualTdpShowing();
        var powerControlsVisible = powerPage && (_unifiedPowerDetails || DeviceWindowsPower.IsVisible
                                                 || DevicePowerPresetContainer.IsVisible || ManualTdpHost.IsVisible);
        DevicePowerControlsCard.IsVisible = powerControlsVisible;

        DevicePowerOverview.IsVisible = powerPage && (powerControlsVisible || performance?.Visible is true);
        DeviceWidgetsExpander.IsVisible = _navigation.Page == OverlayPage.Device;
        RefreshDeviceSectionPins(snapshot, pinSections, performance, graphics);
        // Only on a hybrid CPU whose active scheme exposes the policy. Everywhere else the section
        // would open on a control that has nothing to offer.
        DeviceHybridCores.IsVisible = powerPage && _hybridCoreSelection is { State.Options.Count: > 0 };
        DeviceStatusTitle.IsVisible =
            DeviceStatusDetail.IsVisible = _navigation.Page == OverlayPage.Device && snapshot.Visible;
        DeviceStatusTitle.Text = snapshot.Status;
        DeviceStatusDetail.Text = snapshot.Detail;

        if (_renderedDevicePage == _navigation.Page && _renderedDeviceSection == _navigation.SectionId
                                                    && SameDeviceLayout(_deviceLayout, snapshot))
        {
            foreach (var host in DeviceRenderHosts.Where(host => host.Children.Count > 0))
            {
                RefreshDeviceValues(host, snapshot, performance);
            }

            return;
        }

        _deviceLayout = snapshot;

        var focusedKey = CurrentSemanticFocusKey();
        var previousFocus = GetTopLevel(this)?.FocusManager.GetFocusedElement() as Control;
        var foldKey = previousFocus?.GetVisualAncestors().OfType<CollapsibleSection>()
            .FirstOrDefault()?.Heading.Tag;
        var rebuildingFocus = previousFocus is not null
                              && DeviceRenderHosts.Any(host => host.IsVisualAncestorOf(previousFocus));
        foreach (var host in DeviceRenderHosts)
        {
            host.Children.Clear();
        }

        DeviceFansHost.IsVisible = DeviceApplicationProfileHost.IsVisible = false;
        _renderedDevicePage = _navigation.Page;
        _renderedDeviceSection = _navigation.SectionId;

        // Every section is a published page that groups related controls into cards.
        var openPluginSection = _navigation.Page is OverlayPage.DevicePluginSection
            ? _navigation.SectionId
            : null;
        var restoreFocus = openPluginSection is not null
            ? RenderDevicePluginSection(snapshot, sectionPages, pinSections, openPluginSection, focusedKey)
            : RenderDeviceSectionMenu(snapshot, performance, sectionPages, pinSections, focusedKey);

        // A Device page that renders nothing is indistinguishable from a device that published
        // nothing, and the difference is the whole diagnosis. Reported on every render, not only
        // the empty ones, because "16 capabilities arrived and 5 rows were drawn" is the line that
        // separates a delivery problem from a rendering one — and an empty page with no line at
        // all cannot even prove the render ran.
        Log.Change(
            "overlay.device.render",
            $"Device page: page={_navigation.Page}, "
            + $"section={openPluginSection ?? "menu"}, "
            + $"rows={DeviceCapabilityList.Children.Count}, "
            + $"capabilities={snapshot.Capabilities.Count}, "
            + $"glyphSelection={snapshot.GlyphSelection is not null}, "
            + $"autoTdp={snapshot.AutoTdp is not null}, "
            + $"autoTdpTarget={snapshot.AutoTdpTarget is not null}, "
            + $"controller={snapshot.Controller is not null}, "
            + $"performanceProfiles={performance?.ProfileRows.Count ?? 0}, "
            + $"recovery={snapshot.Recovery is not null}",
            DeviceCapabilityList.Children.Count == 0 ? LogLevel.Warn : LogLevel.Info);

        var rendered = DeviceRenderHosts.SelectMany(host => host.GetLogicalDescendants()).OfType<Control>().ToArray();
        var focusTarget = focusedKey is null
            ? null
            : rendered.FirstOrDefault(control => control.Focusable && Equals(control.Tag, focusedKey));
        var target = focusTarget ?? restoreFocus;
        if (target is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
        {
            target.Focus(NavigationMethod.Directional);
        }
        else if (rebuildingFocus)
        {
            var heading = rendered.OfType<Button>()
                .FirstOrDefault(button => Equals(button.Tag, foldKey) && button.IsEffectivelyVisible);
            (heading ?? SelectedSectionButton)?.Focus(NavigationMethod.Directional);
        }

        RestoreSectionHeaderFocus(focusedKey);
        // A page change can show the widget pin toggles, which follow the device only while shown.
        RefreshDeviceWidgetPins();
    }

    /// <summary>
    ///     Renders the Device overview: the application profile switch, then the device's Power and
    ///     Fans folds as the Power page draws them. Performance follows from its own shared tree.
    /// </summary>
    private Control? RenderDeviceSectionMenu(
        DeviceOverlaySnapshot snapshot,
        PerformanceOverlaySnapshot? performance,
        IReadOnlyList<DeviceOverlaySectionEntry> sectionPages,
        DevicePinSection[] pinSections,
        string? focusedKey)
    {
        Control? restoreFocus = null;

        // The per-application profile toggle is the headline of the Device root, the way Steam's own
        // per-game toggle heads the Performance tab: one control, on top of the page, that turns a
        // separate profile for the running application on or off. The section rail owns navigation.
        if (performance is { Visible: true }
            && performance.ProfileRows.FirstOrDefault(row => string.Equals(
                row.Id,
                DeviceOverlaySectionPages.ApplicationProfileRowId,
                StringComparison.Ordinal)) is { } applicationProfile)
        {
            var toggle = CreatePerformanceRow(applicationProfile, ApplicationProfileFocusKey);
            DeviceApplicationProfileHost.Children.Add(toggle);
            DeviceApplicationProfileHost.IsVisible = true;
            if (string.Equals(ApplicationProfileFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = toggle;
            }
        }

        // The Overview is where a handheld user lands mid-game, so it carries the controls they came
        // for: the same Power and Fans folds as the Power page, drawn by the same renderer and keys.
        if (_unifiedPowerDetails && OverviewPowerEntry(snapshot, sectionPages) is { } power)
        {
            restoreFocus = RenderDevicePluginSection(snapshot, sectionPages, pinSections, power.SectionId,
                focusedKey) ?? restoreFocus;
        }

        return restoreFocus;
    }

    /// <summary>Every panel a Device render fills, for clearing, value refresh and focus restore.</summary>
    private Panel[] DeviceRenderHosts => [DeviceCapabilityList, DeviceFansHost, DeviceApplicationProfileHost];

    /// <summary>Renders one section page: lead rows, category groups, then the host-owned rows.</summary>
    private Control? RenderDevicePluginSection(
        DeviceOverlaySnapshot snapshot,
        IReadOnlyList<DeviceOverlaySectionEntry> sectionPages,
        DevicePinSection[] pinSections,
        string sectionId,
        string? focusedKey)
    {
        if (IsControllerSection(snapshot, sectionId))
        {
            return RenderControllerPage(snapshot, pinSections, sectionId, focusedKey);
        }

        if (sectionPages.All(entry => !string.Equals(entry.SectionId, sectionId, StringComparison.Ordinal)))
        {
            // The section vanished with a descriptor generation while its page was open. Saying so
            // beats rendering an empty page that cannot explain itself.
            DeviceCapabilityList.Children.Add(new TextBlock
            {
                Text = "This device section is no longer available.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4)
            });
            return null;
        }

        Control? restoreFocus = null;
        foreach (var section in pinSections.Where(section => section.PluginSectionId == sectionId))
        {
            var content = CreateSection(section.Id, section.Title);
            restoreFocus = AddDeviceSectionRows(snapshot, section, content, focusedKey) ?? restoreFocus;
            if (content.Children.Count == 1)
            {
                continue;
            }

            if (_unifiedPowerDetails && IsFanGroup(section))
            {
                // Inside the unified Power fold every group renders open; the fan group alone keeps its
                // own fold, beside Power, under the same pin and expansion identity.
                DeviceFansHost.Children.Add(new Border
                {
                    Child = FoldSection(content), Tag = content.Tag, VerticalAlignment = VerticalAlignment.Top
                });
                continue;
            }

            DeviceCapabilityList.Children.Add(WrapDeviceSection(content));
        }

        DeviceFansHost.IsVisible = DeviceFansHost.Children.Count > 0;
        return restoreFocus;
    }

    /// <summary>Whether a Power group holds the device's fan controls rather than limits or charging.</summary>
    private static bool IsFanGroup(DevicePinSection section)
    {
        return section.HostRows.Count == 0 && section.Capabilities.Any(capability =>
            capability.Role is CapabilityRole.FanMode or CapabilityRole.FanCurve or CapabilityRole.FanDuty
                or CapabilityRole.FanTargetRpm);
    }

    /// <summary>WSGM's geometry for a declared section icon, or null for the shared default.</summary>
    /// <remarks>Plugin widget icons use the same map, so a power widget and a Power section look alike.</remarks>
    internal static StreamGeometry? SectionIconFor(SectionIcon icon)
    {
        return icon switch
        {
            SectionIcon.Power => Icons.Power,
            SectionIcon.Fan => Icons.Snowflake,
            SectionIcon.Battery => Icons.Battery,
            SectionIcon.Lighting => Icons.Palette,
            SectionIcon.Controller => Icons.Grid4,
            SectionIcon.Display => Icons.Monitor,
            SectionIcon.Gauge => Icons.ListLines,
            SectionIcon.Wrench => Icons.Wrench,
            _ => null
        };
    }

    /// <summary>
    ///     Draws the rows WSGM owns for one group: the ones that are configuration or policy rather
    ///     than device capabilities, and therefore never arrive through the capability list.
    /// </summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="rows">The host-owned rows of the group, in presentation order.</param>
    /// <param name="focusedKey">The focus key to restore, when one of these rows holds it.</param>
    /// <param name="target">Section body receiving the rows.</param>
    /// <param name="includePreview">Whether to show the source page's glyph input preview.</param>
    /// <returns>The row to restore focus to, or null when none of these held it.</returns>
    private Control? RenderOwnedDeviceRows(
        DeviceOverlaySnapshot snapshot,
        IReadOnlyList<DescriptorRow> rows,
        string? focusedKey,
        Panel target,
        bool includePreview = true)
    {
        Control? restoreFocus = null;
        foreach (var row in rows)
        {
            var control = row.Id switch
            {
                // The target slider follows the switch, directly under it, while AutoTDP is on.
                DeviceHostRowIds.AutoTdpTarget => CreateAutoTdpTargetRow(row),
                DeviceHostRowIds.LightingProfile or DeviceHostRowIds.GlyphSelection => CreateHostDeviceRow(snapshot,
                    row),
                // Recovery is an action on the device cycle itself rather than on the device. It appears only
                // while there is something to recover, so it can always be pressed.
                DeviceHostRowIds.Retry => CreateHostDeviceRow(snapshot, Plain(row) with { CanInvoke = true }),
                _ => CreateHostDeviceRow(snapshot, Plain(row))
            };
            target.Children.Add(control);
            if (string.Equals(row.Id, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = control;
            }

            // After the selection row it is the result of, so changing the selection and seeing what it
            // produced reads top to bottom.
            if (includePreview && row.Id == DeviceHostRowIds.GlyphSelection && snapshot.GlyphPreview is { } preview)
            {
                _glyphPreview.Show(preview, target);
            }
        }

        return restoreFocus;

        // The value, range and override of these rows come from their host selection and refresh.
        static DescriptorRow Plain(DescriptorRow row)
        {
            return new DescriptorRow(row.Id, row.Title, row.Description, row.TrailingText, row.CanInvoke, row.Status);
        }
    }

    /// <summary>Opens one plugin-declared section as its own page.</summary>
    private void EnterDevicePluginSection(string sectionId)
    {
        if (!_navigation.Push(
                OverlayPage.DevicePluginSection,
                CurrentSemanticFocusKey(),
                sectionId))
        {
            return;
        }

        // The controller page draws the glyph preview and input test, so it needs the high-rate lease.
        var snapshot = DeviceSnapshotOrOff();
        ObserveGlyphInput(IsControllerSection(snapshot, sectionId));
        RefreshDevicePage(snapshot);
        SyncBackAffordance();
        ContentScroller.Offset = default;
        FocusFirstControl(DevicePowerOverview.IsVisible ? DevicePowerOverview : DeviceCapabilityList);
    }

    /// <summary>Leaves a section page for the Device menu.</summary>
    /// <remarks>
    ///     Both redraws are needed: the capability list still holds the section's rows, and the shared
    ///     performance rows belong to one page, so what leaving shows is decided here.
    /// </remarks>
    private void LeaveDevicePluginSection()
    {
        var sectionId = _navigation.SectionId;
        ObserveGlyphInput(false);
        var returnFocusKey = _navigation.Pop()
                             ?? (sectionId is null ? null : "device.section.plugin." + sectionId);
        RefreshDevicePage();
        RestoreRootFocus(returnFocusKey);
    }

    /// <summary>Whether a Device page of the given section is open.</summary>
    private bool DevicePageOpen(string sectionId)
    {
        return _navigation.Page == OverlayPage.DevicePluginSection
               && string.Equals(_navigation.SectionId, sectionId, StringComparison.Ordinal);
    }

    /// <summary>Whether a section is the controller page: the one Shell placed the controller and glyph rows on.</summary>
    private static bool IsControllerSection(DeviceOverlaySnapshot snapshot, string? sectionId)
    {
        return sectionId is not null && (snapshot.Controller?.SectionId == sectionId
                                         || snapshot.GlyphSelection?.SectionId == sectionId);
    }

    /// <summary>The rail entries for a snapshot, counting the power controls this window draws on Power.</summary>
    private IReadOnlyList<DeviceOverlaySectionEntry> DeviceSectionPages(DeviceOverlaySnapshot snapshot,
        PerformanceOverlaySnapshot? performance)
    {
        var powerControls = (_powerSchemeSelection is { State.Options.Count: > 0 } ? 1 : 0)
                            + (_hybridCoreSelection is { State.Options.Count: > 0 } ? 1 : 0)
                            + (snapshot.Visible && DevicePowerPresetHost.IsVisible ? 1 : 0)
                            + (snapshot.Visible && ManualTdpShowing() ? 1 : 0);
        return DeviceOverlaySectionPages.Build(snapshot, performance, powerControls);
    }

    /// <summary>Whether a manual power mode control has anything to show.</summary>
    private bool ManualTdpShowing()
    {
        return ManualTdpHost.Children.OfType<ManualTdpModeView>().Any(view => view.IsVisible);
    }

    /// <summary>Starts or stops the glyph input test for whether a Controller page is open.</summary>
    /// <param name="controllerPage">Whether the page that draws the samples is showing.</param>
    private void ObserveGlyphInput(bool controllerPage)
    {
        _glyphPreview.Observe(controllerPage && !_closed ? _deviceBridge : null);
    }
}

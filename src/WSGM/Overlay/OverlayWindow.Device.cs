using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>Moves focus to Device when integration is enabled; otherwise leaves the current tab.</summary>
    internal void SelectDeviceDestination()
    {
        if (_deviceBridge?.Snapshot().Visible is true)
        {
            SelectDestination(OverlayDestination.Device);
        }
    }

    private void OnDeviceChanged() => QueueLiveRefresh(DeviceLiveRefresh);

    /// <summary>True while focus is on an interactive value control inside <paramref name="list"/> —
    /// a slider, dropdown, toggle or textbox the user is adjusting. A telemetry-driven rebuild while
    /// one is focused would destroy it under the user, so the refresh is skipped until they leave.</summary>
    /// <param name="list">The panel about to be torn down and rebuilt.</param>
    /// <remarks>
    /// Asked for both row lists. The performance rows are a separate panel from the capability list
    /// but rebuild on the same kind of event — RTSS republishes its readback every poll — so the
    /// frame-limit slider was the one value control on the Device page with no such protection:
    /// focus landed on it and the next readback two seconds later deleted the control under it.
    /// </remarks>
    private bool IsEditingValueIn(Control list)
    {
        if (GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is not Control focused)
        {
            return false;
        }

        // The curve editor belongs here for the same reason as the slider, and more urgently: the
        // live temperature it marks republishes several times a second, so a rebuild would drop the
        // control out from under a finger that is mid-drag on every single sample.
        if (focused is not (Slider or ComboBox or ToggleSwitch or TextBox or CurveEditor))
        {
            return false;
        }

        for (Visual? node = focused; node is not null; node = node.GetVisualParent())
        {
            if (ReferenceEquals(node, list))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Redraws the written activation hints as the device's own buttons, where one resolved.
    /// </summary>
    /// <remarks>
    /// Written letters stay in the markup and remain the fallback, so this only ever adds. That
    /// matters on the two machines it will not resolve for — one with no glyph profile, and one
    /// where the input actually reaching WSGM is not the managed handheld's — because a hint showing
    /// a Claw button while the user holds an Xbox pad is worse than the letter it replaced.
    /// </remarks>
    /// <summary>Renders what a device package on this install is missing, if anything.</summary>
    private void RefreshDevicePrerequisites()
    {
        var advice = _devicePrerequisites?.Read()
                     ?? new DevicePrerequisiteAdvice("", false, false);
        // No page check: the banner is a child of PanelDevice, so that panel's own visibility is
        // the gate. It stays up on the Device sub-pages too, which is where someone hunting a dead
        // device most likely ends up.
        DevicePrerequisiteBanner.IsVisible = advice.HasAdvice;
        DevicePrerequisiteDetail.Text = advice.Detail;
        // The driver half is deliberately not offered here: INV-020 keeps driver installation in
        // setup, because the USB/IP install restarts every USB 3.0 hub and would take the pad, the
        // touch digitiser and the keyboard away from whoever is holding the machine.
        DevicePrerequisiteEnable.IsVisible = advice.CanEnableIntegration;
    }

    private async void OnEnableDeviceIntegration(object? sender, RoutedEventArgs e)
    {
        if (_devicePrerequisites is not { } prerequisites) { return; }
        DevicePrerequisiteEnable.IsEnabled = false;
        try
        {
            await prerequisites.EnableAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Enabling Device Integration from the overlay failed", ex);
        }
        finally
        {
            DevicePrerequisiteEnable.IsEnabled = true;
            RefreshDevicePrerequisites();
        }
    }

    private void RefreshNavigationHints()
    {
        // FaceSouth rather than "A": the glyph vocabulary is positional, so a device whose bottom
        // face button is printed with something else gets the button it actually has.
        HomeAppButton.TrailingGlyph = _deviceBridge?.NavigationHint(GlyphControlId.FaceSouth);
    }

    private void OnPerformanceChanged() => QueueLiveRefresh(PerformanceLiveRefresh);

    /// <summary>Coalesces telemetry-driven redraws and keeps the current visual tree alive for the
    /// complete pointer gesture. Replacing a button after pointer-down but before pointer-up drops
    /// its Click, which presents as a button that needs a second tap.</summary>
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
        var offset = ContentScroller.Offset;
        var page = _navigation.Page;
        var sectionId = _navigation.SectionId;
        void KeepViewport(object? sender, RequestBringIntoViewEventArgs args) => args.Handled = true;
        PanelDevice.AddHandler(RequestBringIntoViewEvent, KeepViewport);
        PanelSystem.AddHandler(RequestBringIntoViewEvent, KeepViewport);
        try
        {
            if ((refreshes & PerformanceLiveRefresh) != 0)
            {
                RefreshPerformancePanel();
            }
            if ((refreshes & DeviceLiveRefresh) != 0
                || (refreshes & PerformanceLiveRefresh) != 0
                    && _navigation.IsVisible(OverlayDestination.Device))
            {
                RefreshDevicePanel();
            }

            // Replacing the anchor/focused row is an observation update, not navigation. Complete
            // layout before restoring the offset so its temporary shorter extent cannot clamp it.
            ContentScroller.UpdateLayout();
            if (page == _navigation.Page && sectionId == _navigation.SectionId)
            { ContentScroller.Offset = offset; }
        }
        finally
        {
            PanelDevice.RemoveHandler(RequestBringIntoViewEvent, KeepViewport);
            PanelSystem.RemoveHandler(RequestBringIntoViewEvent, KeepViewport);
        }

        if (Volatile.Read(ref _pendingLiveRefreshes) != 0)
        {
            ScheduleLiveRefresh();
        }
    }

    private void OnPointerPressedForLiveRefresh(object? sender, PointerPressedEventArgs e)
        => _pressedPointers.Add(e.Pointer);

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

    private void RefreshDevicePanel()
    {
        if (_closed)
        {
            return;
        }
        if (!_opened)
        {
            _rendersAwaitingOpen |= DeviceRenderAwaitingOpen;
            return;
        }

        var snapshot = _deviceBridge?.Snapshot()
                       ?? new DeviceOverlaySnapshot(false, "Device integration off", string.Empty, null, []);
        var performance = _performanceSource?.Snapshot();
        RefreshNavigationHints();
        ConfigureTabs(snapshot.Visible);
        var powerPage = _navigation.Page is OverlayPage.Device or OverlayPage.DevicePowerAndThermals
                        || (_navigation.Page == OverlayPage.DevicePluginSection
                            && DeviceOverlaySectionPages.SectionAbsorbedInto(snapshot, _navigation.SectionId ?? string.Empty)
                            == DeviceOverlaySection.PowerAndThermals);
        DevicePowerSchemeHost.IsVisible = powerPage;
        DeviceWindowsPower.IsVisible = _powerSchemeSelection is not null;
        DevicePowerPresetContainer.IsVisible = powerPage && _navigation.Page != OverlayPage.Device && snapshot.Visible && DevicePowerPresetHost.IsVisible;
        ManualTdpHost.IsVisible = snapshot.Visible && ManualTdpHost.Children.OfType<ManualTdpModeView>().Any(view => view.IsVisible);
        DevicePowerOverview.IsVisible = powerPage;
        DeviceWidgetsExpander.IsVisible = _navigation.Page == OverlayPage.Device;
        RefreshDeviceSectionPins(snapshot);
        // Only on a hybrid CPU whose active scheme exposes the policy. Everywhere else the section
        // would open on a control that has nothing to offer.
        DeviceHybridCores.IsVisible = powerPage && _hybridCoreSelection?.Status.Supported is true;
        DeviceStatusTitle.IsVisible = DeviceStatusDetail.IsVisible = _navigation.Page == OverlayPage.Device && snapshot.Visible;
        DeviceStatusTitle.Text = snapshot.Status;
        DeviceStatusDetail.Text = snapshot.Detail;
        RefreshDevicePrerequisites();

        // Do not tear the list down while the user is operating a value control on it. Read-only
        // telemetry (fan RPM, temperature) streams several samples a second and each one posts a
        // refresh; rebuilding would destroy the focused slider/dropdown mid-adjust — the pad
        // cannot hold Left/Right across it, and the row's debounced write timer would die with the
        // row before it commits. Refresh existing sliders in place; their pending user edits take
        // precedence over readback. The next change after focus moves on rebuilds as normal.
        if (_renderedDevicePage == _navigation.Page && _renderedDeviceSection == _navigation.SectionId
            && IsEditingValueIn(DeviceCapabilityList))
        {
            foreach (var row in DeviceCapabilityList.GetLogicalDescendants().OfType<DeviceSliderRow>())
            {
                var capability = snapshot.Capabilities.FirstOrDefault(item =>
                    Equals(row.Tag, item.InstanceId is { Length: > 0 }
                        ? $"{item.CapabilityId}#{item.InstanceId}" : item.CapabilityId));
                if (capability is not null && RendersAsSlider(capability))
                {
                    row.RefreshReadback(capability.Minimum!.Value, capability.Maximum!.Value,
                        capability.Step ?? 1, capability.CurrentValue?.IntegerValue ?? capability.Minimum.Value,
                        capability.CanInvoke);
                }
                else if (capability is not null)
                {
                    row.RefreshReadback(capability.Minimum ?? 0, capability.Maximum ?? 0,
                        capability.Step ?? 1, capability.CurrentValue?.IntegerValue ?? 0, false);
                }
            }
            return;
        }
        var focusedKey = GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is Control focused
            ? focused.Tag as string
            : null;
        DeviceCapabilityList.Children.Clear();
        _renderedDevicePage = _navigation.Page;
        _renderedDeviceSection = _navigation.SectionId;

        // The tiles belong to the tree that was just cleared. Dropping the references here, before
        // anything can rebuild them, is what stops the input test writing to detached controls.
        _glyphTiles.Clear();
        var sectionPages = DeviceOverlaySectionPages.Build(snapshot, performance);
        if (sectionPages.Count == 0)
        {
            DeviceCapabilityList.Children.Add(new TextBlock
            {
                Text = "No device-plugin controls are available.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4)
            });
            return;
        }

        // Shared sections group related controls into cards; fallback sections keep their own renderer.
        var openSection = DeviceOverlaySectionPages.SectionFor(_navigation.Page);
        var openPluginSection = _navigation.Page is OverlayPage.DevicePluginSection
            ? _navigation.SectionId
            : null;
        var restoreFocus = openSection is { } section
            ? RenderDeviceSection(snapshot, section, focusedKey)
            : openPluginSection is { } pluginSectionId
                ? RenderDevicePluginSection(snapshot, pluginSectionId, focusedKey)
                : RenderDeviceSectionMenu(snapshot, performance, sectionPages, focusedKey);

        // A Device page that renders nothing is indistinguishable from a device that published
        // nothing, and the difference is the whole diagnosis. Reported on every render, not only
        // the empty ones, because "16 capabilities arrived and 5 rows were drawn" is the line that
        // separates a delivery problem from a rendering one — and an empty page with no line at
        // all cannot even prove the render ran.
        Log.Change(
            "overlay.device.render",
            $"Device page: page={_navigation.Page}, "
                + $"section={openSection?.ToString() ?? openPluginSection ?? "menu"}, "
                + $"rows={DeviceCapabilityList.Children.Count}, "
                + $"capabilities={snapshot.Capabilities.Count}, "
                + $"glyphSelection={snapshot.GlyphSelection is not null}, "
                + $"autoTdp={snapshot.AutoTdp is not null}, "
                + $"controller={snapshot.Controller is not null}, "
                + $"profile={snapshot.Profile is not null}, "
                + $"performanceProfiles={performance?.ProfileRows.Count ?? 0}, "
                + $"recovery={snapshot.Recovery is not null}",
            DeviceCapabilityList.Children.Count == 0 ? LogLevel.Warn : LogLevel.Info);

        restoreFocus?.Focus(NavigationMethod.Directional);
        RestoreSectionHeaderFocus(focusedKey);
        RenderPins();
    }

    /// <summary>
    /// Renders the additional Device sections below the Power and Performance overview.
    /// </summary>
    /// <remarks>
    /// A menu rather than one long list. The whole surface is a few rows tall on a handheld, and a
    /// list that needs scrolling is a list a controller cannot cross quickly. Each card carries the
    /// most serious status inside it, so a fault is visible without opening the page.
    /// </remarks>
    private DescriptorStatusRow? RenderDeviceSectionMenu(
        DeviceOverlaySnapshot snapshot,
        PerformanceOverlaySnapshot? performance,
        IReadOnlyList<DeviceOverlaySectionEntry> sectionPages,
        string? focusedKey)
    {
        DescriptorStatusRow? restoreFocus = null;

        // The per-application profile toggle is the headline of the Device root, the way Steam's own
        // per-game toggle heads the Performance tab: one control, on top of the page, that turns a
        // separate profile for the running application on or off. Its settings live on Power and
        // thermals; this is only the switch. Rendered before the section grid so it reads first.
        if (performance is { Visible: true }
            && performance.ProfileRows.FirstOrDefault(row => string.Equals(
                row.Id,
                DeviceOverlaySectionPages.ApplicationProfileRowId,
                StringComparison.Ordinal)) is { } applicationProfile)
        {
            const string toggleFocusKey = "device.application-profile";
            var toggle = CreatePerformanceRow(applicationProfile, toggleFocusKey);
            toggle.Margin = new Thickness(0, 0, 0, 12);
            DeviceCapabilityList.Children.Add(toggle);
            if (string.Equals(toggleFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = toggle;
            }
        }

        // A grid of tile cards rather than a stretched stack: the sheet is wide, and
        // a full-width row per section read as the old sidebar scaled up.
        var grid = new UniformGrid { Columns = 2 };
        foreach (var entry in sectionPages)
        {
            if (!snapshot.Visible && (entry.Section == DeviceOverlaySection.PowerAndThermals
                || entry.PluginSectionId == DeviceSections.PowerId)) { continue; }
            var key = DeviceOverlaySectionPages.FocusKey(entry);
            DescriptorStatusRow row = new();
            row.Classes.Add("tile");
            row.Margin = new Thickness(0, 0, 10, 10);
            row.Apply(new DescriptorRow(
                key,
                entry.Title,
                entry.Description,
                entry.Count.ToString(CultureInfo.InvariantCulture),
                CanInvoke: true,
                entry.Status));
            if (SectionIconFor(entry.Icon) is { } sectionIcon)
            {
                row.IconGeometry = sectionIcon;
            }

            var captured = entry;
            row.Click += (_, _) =>
            {
                if (captured.PluginSectionId is { } pluginSection)
                {
                    EnterDevicePluginSection(pluginSection);
                }
                else
                {
                    EnterDeviceSection(captured.Section);
                }
            };
            grid.Children.Add(row);
            if (string.Equals(key, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        DeviceCapabilityList.Children.Add(grid);
        return restoreFocus;
    }

    /// <summary>Renders one Device section's rows.</summary>
    /// <summary>Renders one plugin-declared section page: lead rows, then category groups.</summary>
    private DescriptorStatusRow? RenderDevicePluginSection(
        DeviceOverlaySnapshot snapshot,
        string sectionId,
        string? focusedKey)
    {
        if (DeviceOverlaySectionPages.SectionAbsorbedInto(snapshot, sectionId) == DeviceOverlaySection.ControllerAndMotion)
        { return RenderControllerPage(snapshot, focusedKey, "section.device.plugin." + sectionId + ".configuration"); }
        var pluginSection = snapshot.PluginSections
            .FirstOrDefault(candidate => string.Equals(
                candidate.SectionId,
                sectionId,
                StringComparison.Ordinal));
        var capabilities =
            DeviceOverlaySectionPages.CapabilitiesInPluginSection(snapshot, sectionId);
        var absorbed =
            DeviceOverlaySectionPages.SectionAbsorbedInto(snapshot, sectionId);
        if (pluginSection is null || (capabilities.Count == 0 && absorbed is null))
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

        DescriptorStatusRow? restoreFocus = null;
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16, Margin = new Thickness(0, 12, 0, 0) };
        StackPanel[] stacks = [new() { Spacing = 16 }, new() { Spacing = 16 }];
        for (var i = 0; i < stacks.Length; i++) { Grid.SetColumn(stacks[i], i); columns.Children.Add(stacks[i]); }
        DeviceCapabilityList.Children.Add(columns);
        var groupIndex = 0;
        foreach (var section in DevicePinSections(snapshot).Where(section => section.PluginSectionId == sectionId))
        {
            var content = CreateSection(section.Id, section.Title);
            restoreFocus = AddDeviceSectionRows(snapshot, section, content, focusedKey) ?? restoreFocus;
            if (content.Children.Count == 1) { continue; }
            stacks[groupIndex++ % 2].Children.Add(new Border { Classes = { "device-group" }, Child = content });
        }
        if (groupIndex == 1) { Grid.SetColumnSpan(stacks[0], 2); }
        return restoreFocus;
    }

    /// <summary>WSGM's geometry for a declared section icon, or null for the shared default.</summary>
    private static Geometry? SectionIconFor(SectionIcon icon) => icon switch
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

    private DescriptorStatusRow? RenderDeviceSection(
        DeviceOverlaySnapshot snapshot,
        DeviceOverlaySection section,
        string? focusedKey)
    {
        if (section == DeviceOverlaySection.ControllerAndMotion)
        { return RenderControllerPage(snapshot, focusedKey, "section.device.controller-and-motion"); }
        var definition = DevicePinSections(snapshot).FirstOrDefault(candidate => candidate.Id == DeviceOverlaySectionPages.FocusKey(section).Replace("device.section.", "section.device.", StringComparison.Ordinal));
        if (definition is null) { return null; }
        var content = CreateSection(definition.Id, definition.Title);
        var restoreFocus = AddDeviceSectionRows(snapshot, definition, content, focusedKey);
        if (content.Children.Count > 1) { DeviceCapabilityList.Children.Add(content); }
        return restoreFocus;
    }

    /// <summary>
    /// Draws the rows WSGM owns for one section: the ones that are configuration or policy rather
    /// than device capabilities, and therefore never arrive through the capability list.
    /// </summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="section">The WSGM-owned section being drawn.</param>
    /// <param name="focusedKey">The focus key to restore, when one of these rows holds it.</param>
    /// <param name="target">Section body receiving the rows.</param>
    /// <param name="includePreview">Whether to show the source page's glyph input preview.</param>
    /// <returns>The row to restore focus to, or null when none of these held it.</returns>
    /// <remarks>
    /// Split out because these rows are drawn on two different pages: the WSGM section's own, and —
    /// when the plugin declares a section for the same subject — that declared page, which absorbs
    /// them. One body for both, so the controller target cannot appear on the page the menu counted
    /// it into and be missing from the page it actually opens.
    /// </remarks>
    private DescriptorStatusRow? RenderOwnedDeviceRows(
        DeviceOverlaySnapshot snapshot,
        DeviceOverlaySection section,
        string? focusedKey,
        Panel? target = null,
        bool includePreview = true)
    {
        target ??= DeviceCapabilityList;
        DescriptorStatusRow? restoreFocus = null;

        // AutoTDP moves the power limit rather than being one, so it sits with the limit it moves
        // instead of arriving through the capability list.
        if (section is DeviceOverlaySection.PowerAndThermals && snapshot.AutoTdp is { } autoTdp)
        {
            const string autoTdpFocusKey = "device.auto-tdp";
            DescriptorStatusRow row = new();
            row.Apply(new DescriptorRow(
                autoTdpFocusKey,
                autoTdp.Title,
                autoTdp.Description,
                autoTdp.TrailingText,
                autoTdp.CanInvoke,
                autoTdp.Status));
            row.Click += (_, _) => _ = RunDeviceCommandAsync(
                "AutoTDP switch", (bridge, token) => bridge.ToggleAutoTdpAsync(token));
            target.Children.Add(row);
            if (string.Equals(autoTdpFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        // The selected hardware profile is stored configuration rather than a device capability, so
        // it is a direct row for the same reason as the others on this surface. It sits with power
        // and thermals now that the per-application profile is the toggle on the Device root.
        if (section is DeviceOverlaySection.PowerAndThermals && snapshot.Profile is { } profile)
        {
            const string profileFocusKey = "device.hardware-profile";
            DescriptorStatusRow row = new();
            row.Apply(new DescriptorRow(
                profileFocusKey,
                profile.Title,
                profile.Description,
                profile.TrailingText,
                profile.CanInvoke,
                profile.Status));
            row.Click += (_, _) => _ = RunDeviceCommandAsync(
                "Hardware profile change", (bridge, token) => bridge.CycleHardwareProfileAsync(token));
            target.Children.Add(row);
            if (string.Equals(profileFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        // The authored fan profile, below the plugin's hardware profile. Two rows on one page
        // because they are genuinely different things: the hardware profile comes from the plugin
        // and switches its own values, while this chooses between curves the user drew in Settings.
        if (section is DeviceOverlaySection.PowerAndThermals && snapshot.AuthoredProfile is { } authored)
        {
            const string authoredFocusKey = "device.authored-profile";
            DescriptorStatusRow authoredRow = new();
            authoredRow.Apply(new DescriptorRow(
                authoredFocusKey,
                authored.Title,
                authored.Description,
                authored.TrailingText,
                authored.CanInvoke,
                authored.Status));
            authoredRow.Click += (_, _) => _ = RunDeviceCommandAsync(
                "Fan profile change", (bridge, token) => bridge.CycleAuthoredProfileAsync(token));
            target.Children.Add(authoredRow);
            if (string.Equals(authoredFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = authoredRow;
            }
        }

        // The controller target is WSGM's own setting, not a plugin capability, so it is placed on
        // its page directly for the same reason AutoTDP and glyph selection are.
        if (section is DeviceOverlaySection.ControllerAndMotion
            && snapshot.Controller is { } controller)
        {
            const string controllerFocusKey = "device.controller-target";
            DescriptorStatusRow row = new();
            row.Apply(new DescriptorRow(
                controllerFocusKey,
                controller.Title,
                controller.Description,
                controller.TrailingText,
                controller.CanInvoke,
                controller.Status));
            row.Click += (_, _) => _ = RunDeviceCommandAsync(
                "Controller target change", (bridge, token) => bridge.CycleControllerTargetAsync(token));
            target.Children.Add(row);
            if (string.Equals(controllerFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        // Recovery is an action on the device cycle itself rather than on the device, so it is not a
        // capability either. It appears only while there is something to recover.
        if (section is DeviceOverlaySection.Diagnostics && snapshot.Recovery is { } recovery)
        {
            const string recoveryFocusKey = "device.retry";
            DescriptorStatusRow row = new();
            row.Apply(new DescriptorRow(
                recoveryFocusKey,
                recovery.Title,
                recovery.Description,
                recovery.TrailingText,
                true,
                recovery.Status));
            row.Click += (_, _) => _ = RunDeviceCommandAsync(
                "Device integration retry", (bridge, token) => bridge.RetryDeviceCycleAsync(token));
            target.Children.Add(row);
            if (string.Equals(recoveryFocusKey, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        // Glyph selection is WSGM's own control rather than a plugin capability, so it is placed
        // here explicitly rather than arriving through the capability list.
        if (section is DeviceOverlaySection.ControllerAndMotion
            && snapshot.GlyphSelection is { } glyphSelection)
        {
            target.Children.Add(CreateGlyphSelectionRow(glyphSelection, snapshot.GlyphMode));
        }

        // After the selection row it is the result of, so changing the selection and seeing what it
        // produced reads top to bottom.
        if (includePreview && section is DeviceOverlaySection.ControllerAndMotion
            && snapshot.GlyphPreview is { } preview)
        {
            RenderGlyphPreview(preview, target);
        }

        return restoreFocus;
    }

    /// <summary>
    /// Draws the plugin's own glyphs, and lights the one being pressed.
    /// </summary>
    /// <param name="preview">The resolved preview.</param>
    /// <param name="target">The section body containing the preview.</param>
    /// <remarks>
    /// The preview answers the two questions a glyph profile can fail at, and it answers them with
    /// the same picture: whether the artwork resolves at all, and whether pressing a control reaches
    /// WSGM as the control the artwork claims. Neither is answerable from a list of names.
    /// <para>
    /// The tiles are not focusable. This is something to look at while pressing buttons on the
    /// device, so making it a focus stop would put a wall of stops between the selection row above
    /// it and whatever follows, for controls that do nothing when activated.
    /// </para>
    /// </remarks>
    private void RenderGlyphPreview(DeviceOverlayGlyphPreview preview, Panel target)
    {
        TextBlock caption = new()
        {
            Text = $"{preview.ProfileName} · {preview.Detail}",
            Margin = new Thickness(2, 6, 2, 2),
            TextWrapping = TextWrapping.Wrap
        };
        caption.Classes.Add("caption");
        target.Children.Add(caption);

        TextBlock hint = new()
        {
            Text = preview.InputTestAvailable
                ? "Press a control on the device to light it here."
                : "Input test unavailable · WSGM is not reading this device's controls.",
            Margin = new Thickness(2, 0, 2, 4),
            TextWrapping = TextWrapping.Wrap
        };
        hint.Classes.Add("caption");
        target.Children.Add(hint);

        WrapPanel tiles = new() { Margin = new Thickness(2, 0, 2, 4) };
        foreach (var item in preview.Items)
        {
            Border tile = new()
            {
                Width = 64,
                Height = 72,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4)
            };
            tile.Classes.Add("glyph-tile");
            StackPanel stack = new() { Spacing = 2 };
            PhysicalGlyphImage image = new()
            {
                Plan = item.Plan,
                Width = 40,
                Height = 40,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            stack.Children.Add(image);
            TextBlock label = new()
            {
                Text = item.Label,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            stack.Children.Add(label);
            tile.Child = stack;
            tiles.Children.Add(tile);
            _glyphTiles[item.Control] = tile;
        }

        target.Children.Add(tiles);
        ApplyGlyphInputTest();
    }

    /// <summary>Applies the last physical sample to the preview tiles.</summary>
    /// <remarks>
    /// Class-based rather than by setting a brush, so the lit appearance lives in the theme with
    /// every other visual state instead of as a literal colour here.
    /// </remarks>
    private void ApplyGlyphInputTest()
    {
        foreach (var (control, tile) in _glyphTiles)
        {
            tile.Classes.Set("pressed", _pressedGlyphControls.Contains(control));
        }
    }

    /// <summary>Runs one direct Device-surface command with the shared cancellation and logging.</summary>
    /// <param name="description">What the command is, for the log line if it fails.</param>
    /// <param name="command">The command to run against the current source.</param>
    /// <returns>A task completing once the command has run or failed.</returns>
    /// <remarks>
    /// These commands are WSGM's own rather than plugin capabilities, so they do not go through the
    /// capability invoke path. They still need its lifetime and failure handling: a device command
    /// that throws must never take the overlay with it, and one that is cancelled by the overlay
    /// closing is not a failure worth logging.
    /// </remarks>
    private async Task RunDeviceCommandAsync(
        string description,
        Func<IDeviceOverlaySource, CancellationToken, Task> command)
    {
        var bridge = _deviceBridge;
        if (bridge is null || _closed)
        {
            return;
        }

        try
        {
            await command(bridge, _deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"{description} failed: {ex.Message}");
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

        // A declared section that absorbs the controller page draws the glyph preview and input
        // test, so it needs the same high-rate lease the WSGM page takes.
        UpdateGlyphInputObservation(
            _deviceBridge?.Snapshot() is { } snapshot
            && DeviceOverlaySectionPages.SectionAbsorbedInto(snapshot, sectionId)
                is DeviceOverlaySection.ControllerAndMotion);
        RefreshDevicePanel();
        RefreshPerformancePanel();
        SyncBackAffordance();
        ContentScroller.Offset = default;
        FocusFirstControl(DevicePowerOverview.IsVisible ? DevicePowerOverview : DeviceCapabilityList);
    }

    private void EnterDeviceSection(DeviceOverlaySection section)
    {
        if (!_navigation.Push(
            DeviceOverlaySectionPages.PageFor(section),
            CurrentSemanticFocusKey()))
        {
            return;
        }

        // The sample stream fires at input rate, so it is leased only for the one page that draws
        // it and released the moment that page is left.
        UpdateGlyphInputObservation(section is DeviceOverlaySection.ControllerAndMotion);
        RefreshDevicePanel();

        // The shared performance rows belong to one Device page, so entering or leaving any page
        // changes whether they are on screen.
        RefreshPerformancePanel();
        SyncBackAffordance();
        ContentScroller.Offset = default;
        FocusFirstControl(DevicePowerOverview.IsVisible ? DevicePowerOverview : DeviceCapabilityList);
    }

    private void LeaveDeviceSection(DeviceOverlaySection section)
    {
        UpdateGlyphInputObservation(false);
        var returnFocusKey = _navigation.Pop()
                             ?? DeviceOverlaySectionPages.FocusKey(section);
        RefreshDevicePanel();
        RefreshPerformancePanel();
        RestoreRootFocus(returnFocusKey);
    }

    /// <summary>Leaves a plugin-declared section page for the Device menu.</summary>
    /// <remarks>
    /// The same body as <see cref="LeaveDeviceSection"/>, for the page type that has no section
    /// enum to name. Both redraws are needed: the capability list still holds the section's rows,
    /// and the shared performance rows belong to one page, so what leaving shows is decided here.
    /// </remarks>
    private void LeaveDevicePluginSection()
    {
        var sectionId = _navigation.SectionId;
        UpdateGlyphInputObservation(false);
        var returnFocusKey = _navigation.Pop()
                             ?? (sectionId is null ? null : "device.section.plugin." + sectionId);
        RefreshDevicePanel();
        RefreshPerformancePanel();
        RestoreRootFocus(returnFocusKey);
    }

    /// <summary>Starts or stops the glyph input test's sample observation.</summary>
    /// <param name="observe">Whether the page that draws the samples is showing.</param>
    /// <remarks>
    /// Idempotent in both directions, because the page can be entered and left by several paths —
    /// the section card, Back, a destination change, and the overlay closing — and each of them
    /// calls this without knowing what the others did.
    /// </remarks>
    private void UpdateGlyphInputObservation(bool observe)
    {
        if (observe == _glyphInputObservation is not null)
        {
            return;
        }

        if (!observe)
        {
            if (_deviceBridge is not null)
            {
                _deviceBridge.PhysicalSampleReceived -= OnPhysicalGlyphSample;
            }

            _glyphInputObservation?.Dispose();
            _glyphInputObservation = null;
            _pressedGlyphControls = [];
            return;
        }

        var bridge = _deviceBridge;
        if (bridge is null || _closed)
        {
            return;
        }

        bridge.PhysicalSampleReceived += OnPhysicalGlyphSample;
        _glyphInputObservation = bridge.ObservePhysicalSamples();
    }

    /// <summary>Marshals one physical sample onto the UI thread and lights what it presses.</summary>
    /// <param name="sample">The unfiltered sample the plugin reported.</param>
    /// <remarks>
    /// The set is compared before posting, so a controller sitting still — which is most samples —
    /// costs one set comparison on the sampling thread and nothing on the UI thread. Without that,
    /// a 250 Hz stream would post 250 dispatcher items a second to change nothing.
    /// </remarks>
    private void OnPhysicalGlyphSample(CanonicalControllerSample sample)
    {
        var pressed = GlyphInputTestMap.Pressed(sample);
        if (pressed.SetEquals(_pressedGlyphControls))
        {
            return;
        }

        _pressedGlyphControls = pressed;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed)
            {
                return;
            }

            ApplyGlyphInputTest();
        });
    }

    /// <summary>True when a capability should render as a slider: a writable integer with a real
    /// declared range. Colour keeps its editor; everything else stays a row.</summary>
    private static bool RendersAsSlider(DeviceOverlayCapability capability) =>
        capability.ValueKind is CapabilityValueKind.Integer
        && capability.Writable
        && capability.Minimum is { } min
        && capability.Maximum is { } max
        && max > min;
}

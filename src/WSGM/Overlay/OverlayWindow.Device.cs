using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
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
        PanelPowerSession.ShowHomeHint(_deviceBridge?.NavigationHint(GlyphControlId.FaceSouth));
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
        DevicePage.AddHandler(RequestBringIntoViewEvent, KeepViewport);
        try
        {
            // Each source is projected once for the pass and handed to every panel that draws it.
            var device = deviceChanged ? DeviceSnapshotOrOff() : null;
            if (device is not null)
            {
                ConfigureTabs(device);
                RefreshNavigationHints();
                DevicePage.FollowPrerequisites(device);
            }

            var graphics = graphicsChanged ? _graphicsSource?.Snapshot() ?? GraphicsOverlaySnapshot.Empty : null;
            if (graphics is not null)
            {
                ConfigureGpuDestination(graphics.Visible);
            }

            // A hidden Device panel is marked stale instead, and draws when it next shows.
            var renderDevice = (deviceChanged || performanceChanged || graphicsChanged) && DevicePanelShowing();
            var performance = performanceChanged || renderDevice ? _performanceSource?.Snapshot() : null;
            if (performanceChanged)
            {
                RefreshPerformancePanel(performance);
            }

            if (renderDevice)
            {
                device ??= DeviceSnapshotOrOff();
                RefreshDevicePanel(device, performance, graphics);
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
            DevicePage.RemoveHandler(RequestBringIntoViewEvent, KeepViewport);
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

        if (DevicePage.IsVisible)
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

    /// <summary>Which of WSGM's composite power views have something to show now.</summary>
    private DeviceCoreControls CoreControls()
    {
        return new DeviceCoreControls(
            _powerPresetSelection is { State.Options.Count: > 0 },
            _powerSchemeSelection is { State.Options.Count: > 0 },
            _manualTdpMode?.Invoke().Available is true,
            _hybridCoreSelection is { State.Options.Count: > 0 });
    }

    /// <summary>Describes the Device pages from one read of each source.</summary>
    private IReadOnlyList<DevicePage> DescribeDevicePages(DeviceOverlaySnapshot device,
        PerformanceOverlaySnapshot? performance)
    {
        return DevicePages.Build(device, performance, CoreControls());
    }

    /// <summary>Draws the page the navigation stack shows from snapshots the caller read once for the pass.</summary>
    /// <param name="snapshot">The Device snapshot.</param>
    /// <param name="performance">The performance snapshot, or null without a performance source.</param>
    /// <param name="graphics">The graphics snapshot, or null to read it for the GPU page and the pin list.</param>
    private void RefreshDevicePanel(DeviceOverlaySnapshot snapshot, PerformanceOverlaySnapshot? performance,
        GraphicsOverlaySnapshot? graphics = null)
    {
        if (_closed || !_opened)
        {
            return;
        }

        _devicePanelStale = false;
        var pages = DescribeDevicePages(snapshot, performance);
        var gpu = DevicePages.Gpu(graphics ?? _graphicsSource?.Snapshot() ?? GraphicsOverlaySnapshot.Empty);
        RefreshDeviceSectionRail(pages);
        RefreshDeviceSectionPins(DevicePages.PinChoices(pages, gpu));
        var page = _navigation.Page switch
        {
            OverlayPage.DeviceGpu => gpu,
            OverlayPage.DevicePluginSection when _navigation.SectionId is { } sectionId =>
                pages.FirstOrDefault(candidate => candidate.SectionId == sectionId && candidate.Count > 0)
                ?? DevicePages.Missing(sectionId),
            _ => pages[0]
        };

        // The controller page draws the glyph preview and input test, so it needs the high-rate lease.
        ObserveGlyphInput(page.ObservesInput);
        if (!DevicePage.Show(page))
        {
            SelectedSectionButton?.Focus(NavigationMethod.Directional);
        }

        // A Device page that renders nothing is indistinguishable from a device that published nothing, and the
        // difference is the whole diagnosis. "16 capabilities arrived and 5 rows were drawn" is the line that
        // separates a delivery problem from a rendering one; the log keeps each distinct line once.
        Log.Change(
            "overlay.device.render",
            $"Device page: page={page.RailKey}, groups={page.Groups.Count}, rows={DevicePage.RowCount}, "
            + $"capabilities={snapshot.Capabilities.Count}, hostRows={snapshot.HostRows.Count()}, "
            + $"performanceRows={performance?.PageRows.Count() ?? 0}",
            page.Groups.Count == 0 ? LogLevel.Warn : LogLevel.Info);

        // A page change can show the widget pin toggles, which follow the device only while shown.
        RefreshDeviceWidgetPins();
    }

    /// <summary>Lists every group the Device pages offer to pin, with its pin action.</summary>
    /// <param name="choices">The pinnable groups in page order.</param>
    private void RefreshDeviceSectionPins(IReadOnlyList<(string Id, string Title)> choices)
    {
        // Preserve the focused pin button when only telemetry changes.
        if (DevicePage.SectionPins.Children.OfType<SectionPinHeader>()
            .Select(header => (header.SectionId, header.Title)).SequenceEqual(choices))
        {
            return;
        }

        DevicePage.SectionPins.Children.Clear();
        foreach (var (id, title) in choices)
        {
            DevicePage.SectionPins.Children.Add(CreateSectionHeader(id, title));
        }
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

    /// <summary>A pinnable heading that follows the pin list while it is in the tree.</summary>
    private SectionPinHeader CreateSectionHeader(string id, string title, bool pinnedSurface = false)
    {
        var header = new SectionPinHeader(id, title, key => PinToggleRequested?.Invoke(key), pinnedSurface);
        _pins.Track(header);
        return header;
    }

    /// <summary>Starts or stops the glyph input test for whether a Controller page is open.</summary>
    /// <param name="controllerPage">Whether the page that draws the samples is showing.</param>
    private void ObserveGlyphInput(bool controllerPage)
    {
        _rows.GlyphPreview.Observe(controllerPage && !_closed ? _deviceBridge : null);
    }

    private void RefreshPerformancePanel()
    {
        RefreshPerformancePanel(_performanceSource?.Snapshot());
    }

    /// <summary>Follows a performance change in the header profile switch and on Tools > Performance.</summary>
    /// <param name="snapshot">The performance snapshot, or null without a performance source.</param>
    /// <remarks>Quick access draws its pinned copy in the same pass.</remarks>
    private void RefreshPerformancePanel(PerformanceOverlaySnapshot? snapshot)
    {
        if (_closed)
        {
            return;
        }

        _profileScope.Refresh();

        // The first render runs when the window opens.
        if (_opened)
        {
            PanelSystemPerformance.Show(snapshot, _navigation.IsVisible(OverlayDestination.Device));
        }
    }
}

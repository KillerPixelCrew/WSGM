using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private void RefreshPerformancePanel()
    {
        if (_closed)
        {
            return;
        }

        RefreshHeaderProfile();

        if (!_opened)
        {
            _rendersAwaitingOpen |= PerformanceRenderAwaitingOpen;
            return;
        }

        PlacePerformanceSection(_navigation.IsVisible(OverlayDestination.Device));
        var snapshot = _performanceSource?.Snapshot();
        PerformanceSection.IsVisible = snapshot?.Visible is true && PerformanceBelongsOnCurrentPage();
        DevicePerformanceCard.IsVisible =
            PerformanceSection.IsVisible && _navigation.IsVisible(OverlayDestination.Device);
        // The Tools root offers Performance only while the rows live there: with Device visible they
        // belong to its Power page instead, and a tile leading to an empty page is a dead end.
        SystemPerformanceTile.IsVisible = snapshot?.Visible is true
                                          && !_navigation.IsVisible(OverlayDestination.Device);
        if (snapshot is not { Visible: true })
        {
            PerformanceRows.Children.Clear();
            PerformanceStatus.Text = string.Empty;
            return;
        }

        PerformanceStatus.Text = snapshot.Status;
        if (PerformanceSection.Children.FirstOrDefault() is CollapsibleSection section)
        {
            section.Summary = snapshot.Status;
            PerformanceStatus.IsVisible = false;
        }

        var onDevice = _navigation.IsVisible(OverlayDestination.Device);
        var descriptors = onDevice
            ? snapshot.ProfileRows.Where(row => row.Id != DeviceOverlaySectionPages.ApplicationProfileRowId)
                .Concat(snapshot.Rows)
            : snapshot.ProfileRows.Concat(snapshot.Rows);
        ReconcilePerformanceRows(PerformanceRows, descriptors, false);
        RenderPins();
    }

    private void ReconcilePerformanceRows(Panel target, IEnumerable<DescriptorRow> descriptors, bool pinned)
    {
        List<Control> rows = [];
        foreach (var descriptor in descriptors)
        {
            var key = (pinned ? PinTagPrefix : "") + "performance." + descriptor.Id;
            var existing = target.Children.OfType<DescriptorControlView>().FirstOrDefault(row => Equals(row.Tag, key));
            if (existing is not null && existing.Matches(descriptor))
            {
                existing.Refresh(descriptor);
            }
            else
            {
                existing = CreatePerformanceRow(descriptor, key);
            }

            rows.Add(existing);
        }

        ReconcileChildren(target, rows);
    }

    private DescriptorControlView CreatePerformanceRow(DescriptorRow descriptor, string focusKey)
    {
        return new DescriptorControlView(descriptor, focusKey, async seen =>
            {
                // The row may have been republished since it was drawn; act only on a row that is still
                // published and invokable.
                if (_performanceSource is not { } source
                    || source.Snapshot() is not { Visible: true } snapshot
                    || snapshot.ProfileRows.Concat(snapshot.Rows)
                        .FirstOrDefault(row => row.Id == seen.Id) is not { CanInvoke: true } current)
                {
                    return;
                }

                await RunCommandAsync(source, $"Performance command {current.Id}",
                    (performance, token) => performance.InvokeAsync(current, token));
            },
            value => _ = RunCommandAsync(_performanceSource, $"Performance row '{descriptor.Id}' set to {value}",
                (source, token) => source.SetValueAsync(descriptor.Id, value, token)),
            overrideId => RunCommandAsync(_performanceSource, $"Use global for '{overrideId}'",
                (source, token) => source.UseGlobalAsync(overrideId, token)));
    }

    /// <summary>Puts the shared performance rows where the user will look for them.</summary>
    /// <param name="deviceVisible">Whether the Device destination exists in this session.</param>
    /// <remarks>
    ///     Without Device, the rows live on Tools. With Device, one shared control tree
    ///     serves its overview and Power page, beside the Windows and device power controls.
    /// </remarks>
    private void PlacePerformanceSection(bool deviceVisible)
    {
        var target = deviceVisible ? DevicePerformanceColumn : PanelSystemPerformance;
        if (target.Children.Contains(PerformanceSection))
        {
            return;
        }

        DevicePerformanceColumn.Children.Remove(PerformanceSection);
        PanelSystemPerformance.Children.Remove(PerformanceSection);
        target.Children.Add(PerformanceSection);
    }

    /// <summary>Whether the performance rows belong on the page currently showing.</summary>
    /// <remarks>
    ///     Device shows performance on its overview and the shared or plugin-declared Power
    ///     page. Unrelated Device pages do not retain these controls.
    /// </remarks>
    private bool PerformanceBelongsOnCurrentPage()
    {
        if (!_navigation.IsVisible(OverlayDestination.Device))
        {
            return true;
        }

        if (_navigation.Page == OverlayPage.Device || DeviceOverlaySectionPages.SectionFor(_navigation.Page)
                is DeviceOverlaySection.PowerAndThermals)
        {
            return true;
        }

        return _navigation.Page is OverlayPage.DevicePluginSection
               && _navigation.SectionId is { } sectionId
               && (_deviceBridge?.Snapshot()
                   ?? new DeviceOverlaySnapshot(false, "Device integration off", string.Empty, null, [])) is
               { } snapshot
               && DeviceOverlaySectionPages.SectionAbsorbedInto(snapshot, sectionId)
                   is DeviceOverlaySection.PowerAndThermals;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.Core;
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
        var rows = descriptors.ToArray();
        var keys = rows.Select(row => (pinned ? PinTagPrefix : "") + "performance." + row.Id).ToArray();
        foreach (var stale in target.Children.Where(child => child is DescriptorControlView
                                                             && !keys.Contains(child.Tag as string,
                                                                 StringComparer.Ordinal)).ToArray())
        {
            target.Children.Remove(stale);
        }

        for (var index = 0; index < rows.Length; index++)
        {
            var key = keys[index];
            var existing = target.Children.OfType<DescriptorControlView>().FirstOrDefault(row => Equals(row.Tag, key));
            if (existing is not null && existing.Matches(rows[index]))
            {
                existing.Refresh(rows[index]);
            }
            else
            {
                if (existing is not null)
                {
                    target.Children.Remove(existing);
                }

                existing = CreatePerformanceRow(rows[index], key);
                target.Children.Add(existing);
            }

            var desiredIndex = index + (pinned ? 1 : 0);
            var currentIndex = target.Children.IndexOf(existing);
            if (currentIndex != desiredIndex)
            {
                target.Children.Move(currentIndex, desiredIndex);
            }
        }
    }

    private void WritePerformanceValue(string rowId, int value)
    {
        var source = _performanceSource;
        if (source is null || _closed)
        {
            return;
        }

        _ = WritePerformanceValueAsync(source, rowId, value);
    }

    private async Task WritePerformanceValueAsync(
        PerformanceOverlayBridge source,
        string rowId,
        int value)
    {
        try
        {
            await source.SetValueAsync(rowId, value, _deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Performance row '{rowId}' could not be set to {value}: {ex.Message}");
        }
    }

    private DescriptorControlView CreatePerformanceRow(DescriptorRow descriptor, string focusKey)
    {
        return new DescriptorControlView(descriptor, focusKey, async current =>
        {
            if (_performanceSource is { } source && !_closed && current.CanInvoke)
            {
                await InvokePerformanceAsync(source, current);
            }
        }, value => WritePerformanceValue(descriptor.Id, value));
    }

    private async Task InvokePerformanceAsync(PerformanceOverlayBridge source, DescriptorRow descriptor)
    {
        try
        {
            await source.InvokeAsync(descriptor, _deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Performance command failed: {descriptor.Id}, {ex.Message}");
        }
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

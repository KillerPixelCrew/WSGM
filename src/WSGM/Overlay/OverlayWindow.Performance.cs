using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
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

        PlacePerformanceSection(_navigation.IsVisible(OverlayDestination.Device));
        PerformanceOverlaySnapshot? snapshot = _performanceSource?.Snapshot();
        PerformanceSection.IsVisible = snapshot?.Visible is true && PerformanceBelongsOnCurrentPage();
        DevicePerformanceCard.IsVisible = PerformanceSection.IsVisible && _navigation.IsVisible(OverlayDestination.Device);
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

        // The status line above is still worth updating, but the rows are not torn down while the
        // user is on the frame-limit slider: RTSS raises a state change on every readback whether
        // anything moved or not, and rebuilding takes the focused slider with it.
        if (IsEditingValueIn(PerformanceRows))
        {
            return;
        }

        PerformanceRows.Children.Clear();
        string? focusedKey = CurrentSemanticFocusKey();
        DescriptorStatusRow? restoreFocus = null;
        // On Device the per-application enable toggle is promoted to the headline toggle on the root,
        // so the Power and thermals rows are the detail (detected application, active layer, reset)
        // plus the shared frame-limit and overlay rows. On System there is no Device root to host the
        // toggle, so it stays inline with the rest.
        bool onDevice = _navigation.IsVisible(OverlayDestination.Device);
        IEnumerable<DescriptorRow> descriptors = onDevice
            ? snapshot.ProfileRows
                .Where(row => !string.Equals(
                    row.Id,
                    DeviceOverlaySectionPages.ApplicationProfileRowId,
                    StringComparison.Ordinal))
                .Concat(snapshot.Rows)
            : snapshot.ProfileRows.Concat(snapshot.Rows);

        StackPanel details = new() { Spacing = 4 };

        foreach (DescriptorRow descriptor in descriptors)
        {
            Panel target = onDevice && snapshot.ProfileRows.Contains(descriptor) ? details : PerformanceRows;
            string key = $"performance.{descriptor.Id}";
            if (TryCreatePerformanceControl(descriptor, key) is { } control)
            {
                target.Children.Add(control);
                continue;
            }

            DescriptorStatusRow button = CreatePerformanceRow(descriptor, key);
            target.Children.Add(button);
            if (string.Equals(button.Tag as string, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = button;
            }
        }

        if (details.Children.Count > 0)
        {
            Expander more = new()
            {
                Header = "Profile details and reset",
                Content = details,
                IsExpanded = _performanceDetailsExpanded,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };
            more.PropertyChanged += (_, change) =>
            { if (change.Property == Expander.IsExpandedProperty) { _performanceDetailsExpanded = more.IsExpanded; } };
            PerformanceRows.Children.Add(more);
        }

        restoreFocus?.Focus(NavigationMethod.Directional);
        RestoreSectionHeaderFocus(focusedKey);
        RenderPins();
    }

    /// <summary>
    /// Builds the control a performance row asks for — a slider for a range, a dropdown for named
    /// options — or null when the row is a button and should stay one.
    /// </summary>
    /// <remarks>
    /// A disabled control is still drawn rather than falling back to a button: RTSS going away
    /// should grey the frame-limit slider, not replace it with a different-looking row that appears
    /// when the service is unhealthy.
    /// </remarks>
    private Control? TryCreatePerformanceControl(DescriptorRow descriptor, string key)
    {
        if (descriptor.Range is { } range && descriptor.Value is { } current)
        {
            return new DeviceSliderRow(
                key,
                descriptor.Title,
                descriptor.Description,
                range.Minimum,
                range.Maximum,
                range.Step,
                CapabilityUnit.None,
                current,
                descriptor.CanInvoke,
                value => WritePerformanceValue(descriptor.Id, SettledValue(range, value)),
                value => FormatFrameRate(SettledValue(range, value)));
        }

        if (descriptor.Options.Count > 0)
        {
            IReadOnlyList<CapabilityChoice> choices =
                [.. descriptor.Options.Select(option => new CapabilityChoice(
                    option.Value.ToString(CultureInfo.InvariantCulture),
                    new CapabilityDisplay
                    {
                        Key = DisplayKey.Custom,
                        CustomLabel = option.Label,
                    }))];
            string? selected = descriptor.Value?.ToString(CultureInfo.InvariantCulture);
            (Border row, _) = DeviceControlRows.Choice(
                key,
                descriptor.Title,
                descriptor.Description,
                choices,
                selected,
                descriptor.CanInvoke,
                value =>
                {
                    if (int.TryParse(value, CultureInfo.InvariantCulture, out int level))
                    {
                        WritePerformanceValue(descriptor.Id, level);
                    }
                });
            return row;
        }

        return null;
    }

    /// <summary>What a slider position actually commits, once the row's off band is applied.</summary>
    /// <remarks>
    /// The label and the write ask the same question, so the number the user is reading while they
    /// drag is the number that lands. Without that the handle would show a cap in the off band and
    /// then write zero.
    /// </remarks>
    private static int SettledValue(DescriptorRange range, int value) =>
        value < range.OffBelow ? 0 : value;

    /// <summary>A frame limit reads as a rate, and zero means the cap is off rather than "0 FPS".</summary>
    private static string FormatFrameRate(int value) => value <= 0
        ? "Off"
        : $"{value.ToString(CultureInfo.CurrentCulture)} FPS";

    private void WritePerformanceValue(string rowId, int value)
    {
        PerformanceOverlayBridge? source = _performanceSource;
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

    private DescriptorStatusRow CreatePerformanceRow(DescriptorRow descriptor, string focusKey)
    {
        DescriptorStatusRow button = new();
        button.Apply(descriptor with { Id = focusKey });
        button.Click += async (_, _) =>
        {
            PerformanceOverlayBridge? source = _performanceSource;
            if (source is null || _closed || !descriptor.CanInvoke)
            {
                return;
            }

            await RunRowCommandAsync(
                button,
                descriptor.CanInvoke,
                restoreFocus: false,
                token => source.InvokeAsync(descriptor, token),
                $"Performance overlay command failed: {descriptor.Id}");
        };
        return button;
    }

    /// <summary>Runs a row's command with the row disabled, under the overlay's device lifetime.</summary>
    /// <param name="button">The row that started the command.</param>
    /// <param name="enabledAfter">Whether the row can be pressed again afterwards.</param>
    /// <param name="restoreFocus">Puts focus back on the row when the command left nothing focused.</param>
    /// <param name="command">The command to run.</param>
    /// <param name="failure">The log line prefix when the command fails.</param>
    private async Task RunRowCommandAsync(
        Button button,
        bool enabledAfter,
        bool restoreFocus,
        Func<CancellationToken, Task> command,
        string failure)
    {
        bool restoreAfterInvoke = restoreFocus && button.IsFocused;
        button.IsEnabled = false;
        try
        {
            await command(_deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"{failure}, {ex.Message}");
        }
        finally
        {
            if (!_closed)
            {
                button.IsEnabled = enabledAfter;
                if (restoreAfterInvoke && button.IsEffectivelyVisible
                    && FocusManager?.GetFocusedElement() is null)
                {
                    button.Focus(NavigationMethod.Directional);
                }
            }
        }
    }

    /// <summary>Puts the shared performance rows where the user will look for them.</summary>
    /// <param name="deviceVisible">Whether the Device destination exists in this session.</param>
    /// <remarks>Without Device, the rows live on Tools. With Device, one shared control tree
    /// serves its overview and Power page, beside the Windows and device power controls.</remarks>
    private void PlacePerformanceSection(bool deviceVisible)
    {
        StackPanel target = deviceVisible ? DevicePerformanceColumn : PanelSystemPerformance;
        if (!target.Children.Contains(PerformanceSection))
        {
            DevicePerformanceColumn.Children.Remove(PerformanceSection);
            PanelSystemPerformance.Children.Remove(PerformanceSection);
            target.Children.Add(PerformanceSection);
        }
    }

    /// <summary>Whether the performance rows belong on the page currently showing.</summary>
    /// <remarks>Device shows performance on its overview and the shared or plugin-declared Power
    /// page. Unrelated Device pages do not retain these controls.</remarks>
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
                ?? new DeviceOverlaySnapshot(false, "Device integration off", string.Empty, null, [])) is { } snapshot
            && DeviceOverlaySectionPages.SectionAbsorbedInto(snapshot, sectionId)
                is DeviceOverlaySection.PowerAndThermals;
    }
}

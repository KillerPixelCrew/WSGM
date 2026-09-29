using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Labs.Panels;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Graphics destination: one rail entry per section a graphics package declares, an adapter or a
///     display, drawn with the Device destination's capability rows.
/// </summary>
/// <remarks>
///     The rows are the same controls as a Device section's, so a toggle, slider, choice or reading
///     behaves, marks a game override and offers Use global exactly as a device row does. Only the source
///     differs: writes go to the graphics coordinator through <see cref="IGraphicsOverlaySource" />, and
///     the destination follows the graphics packages rather than the device integration switch.
/// </remarks>
public partial class OverlayWindow
{
    private const string GraphicsRailPrefix = "graphics.section.";
    private IGraphicsOverlaySource? _graphicsSource;

    // The layout identity of what the controls pane last drew, so a value change refreshes rows in place
    // and keeps focus and drafts, and only a new descriptor or section rebuilds them.
    private string? _graphicsLayout;

    internal void AttachGraphicsSource(IGraphicsOverlaySource? source)
    {
        if (ReferenceEquals(_graphicsSource, source))
        {
            return;
        }

        if (_graphicsSource is not null)
        {
            _graphicsSource.Changed -= OnGraphicsChanged;
        }

        _graphicsSource = source;
        if (_graphicsSource is not null)
        {
            _graphicsSource.Changed += OnGraphicsChanged;
        }

        _graphicsLayout = null;
        RefreshGraphicsPanel();
    }

    private void DetachGraphicsSource()
    {
        if (_graphicsSource is not null)
        {
            _graphicsSource.Changed -= OnGraphicsChanged;
        }
    }

    private void OnGraphicsChanged()
    {
        QueueLiveRefresh(GraphicsLiveRefresh);
    }

    private void RefreshGraphicsSectionRail(GraphicsOverlaySnapshot snapshot)
    {
        if (_navigation.Destination != OverlayDestination.Graphics)
        {
            return;
        }

        List<WorkspaceSection> entries =
        [
            .. snapshot.Sections.Select(section => new WorkspaceSection(GraphicsRailPrefix + section.Key,
                section.Title, section.Icon is SectionIcon.Display ? Icons.Monitor : Icons.Chip,
                OverlayPage.GraphicsSection, section.Key, null))
        ];
        if (entries.Count == 0)
        {
            // A package that runs but publishes nothing yet still has a page, which says why.
            entries.Add(new WorkspaceSection("graphics.status", "Status", Icons.Info, OverlayPage.Graphics, null,
                null));
        }

        ReconcileSectionRail(entries);
        foreach (var section in snapshot.Sections)
        {
            if (_sectionBadges.TryGetValue(GraphicsRailPrefix + section.Key, out var badge))
            {
                badge.Value = section.Capabilities.Count;
                badge.IsVisible = section.Capabilities.Count > 0;
            }
        }
    }

    private void RefreshGraphicsPanel()
    {
        if (_closed)
        {
            return;
        }

        if (!_opened)
        {
            _rendersAwaitingOpen |= GraphicsRenderAwaitingOpen;
            return;
        }

        var snapshot = _graphicsSource?.Snapshot() ?? GraphicsOverlaySnapshot.Empty;
        ConfigureGraphicsTab(snapshot.Visible);
        if (_navigation.Destination != OverlayDestination.Graphics)
        {
            return;
        }

        RefreshGraphicsSectionRail(snapshot);
        var sectionKey = _navigation.Page == OverlayPage.GraphicsSection ? _navigation.SectionId : null;
        var section = sectionKey is null
            ? null
            : snapshot.Sections.FirstOrDefault(candidate => candidate.Key == sectionKey);
        var status = string.Join(" ", snapshot.Publishers
            .Where(publisher => publisher.Note is not null && (section is null || publisher.PluginId == section.PluginId))
            .Select(publisher => publisher.Note));
        GraphicsStatus.Text = status;
        GraphicsStatus.IsVisible = status.Length > 0;

        var layout = GraphicsLayout(sectionKey, section, snapshot.Sections.Count);
        if (layout == _graphicsLayout)
        {
            RefreshGraphicsValues(section);
            return;
        }

        _graphicsLayout = layout;
        var focusedKey = GetTopLevel(this)?.FocusManager.GetFocusedElement() is Control focused
            ? focused.Tag as string
            : null;
        GraphicsCapabilityList.Children.Clear();
        Control? restoreFocus = null;
        if (section is not null)
        {
            restoreFocus = RenderGraphicsSection(section, focusedKey);
        }
        else if (sectionKey is not null || snapshot.Sections.Count == 0)
        {
            // The root with sections published is only passed through on the way to the selected one.
            GraphicsCapabilityList.Children.Add(new TextBlock
            {
                Text = sectionKey is not null
                    ? "This graphics section is no longer available."
                    : "The graphics driver has not published any settings yet.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4)
            });
        }

        Log.Change(
            "overlay.graphics.render",
            $"Graphics page: section={sectionKey ?? "root"}, rows={section?.Capabilities.Count ?? 0}, "
            + $"sections={snapshot.Sections.Count}, publishers={snapshot.Publishers.Count}");
        var focusTarget = focusedKey is null
            ? null
            : GraphicsCapabilityList.GetLogicalDescendants().OfType<Control>()
                .FirstOrDefault(control => control.Focusable && Equals(control.Tag, focusedKey));
        (focusTarget ?? restoreFocus)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>Draws one graphics section: its lead rows, then one group per declared category.</summary>
    private Control? RenderGraphicsSection(GraphicsOverlaySection section, string? focusedKey)
    {
        Control? restoreFocus = null;
        var columns = new FlexPanel
        {
            Direction = FlexDirection.Row, Wrap = FlexWrap.Wrap, ColumnSpacing = 12, RowSpacing = 12,
            AlignItems = AlignItems.FlexStart, Margin = new Thickness(0, 4, 0, 0)
        };
        var detailWidth = ContentScroller.Viewport.Width;
        if (ContentScroller.Content is Control contentHost)
        {
            detailWidth -= contentHost.Margin.Left + contentHost.Margin.Right;
        }

        if (detailWidth <= 0)
        {
            detailWidth = GraphicsCapabilityList.Bounds.Width;
        }

        var columnCount = detailWidth >= 880 ? 2 : 1;
        var columnWidth = Math.Max(300, (detailWidth - (columnCount - 1) * columns.ColumnSpacing) / columnCount);
        var stacks = Enumerable.Range(0, columnCount).Select(_ => new StackPanel { Spacing = 12, MinWidth = 300 })
            .ToArray();
        var heights = new double[columnCount];
        foreach (var stack in stacks)
        {
            Flex.SetGrow(stack, 1);
            Flex.SetBasis(stack, new FlexBasis(0));
            columns.Children.Add(stack);
        }

        GraphicsCapabilityList.Children.Add(columns);
        foreach (var (title, rows) in GraphicsGroups(section))
        {
            var content = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
            content.Children.Add(GraphicsHeading(title));
            restoreFocus = AddGraphicsRows(rows, content, focusedKey) ?? restoreFocus;
            var group = WrapDeviceSection(content);
            var column = Array.IndexOf(heights, heights.Min());
            stacks[column].Children.Add(group);
            group.Measure(new Size(columnWidth, double.PositiveInfinity));
            heights[column] += Math.Max(group.DesiredSize.Height, 60) + 12;
        }

        return restoreFocus;
    }

    /// <summary>A section's groups: rows in no declared category under the section's title, then each category.</summary>
    private static IEnumerable<(string Title, DeviceOverlayCapability[] Rows)> GraphicsGroups(
        GraphicsOverlaySection section)
    {
        var lead = section.Capabilities.Where(capability => capability.CategoryId is null
                                                            || section.Categories.All(category =>
                                                                category.Id != capability.CategoryId)).ToArray();
        if (lead.Length > 0)
        {
            yield return (section.Title, lead);
        }

        foreach (var category in section.Categories)
        {
            var rows = section.Capabilities.Where(capability => capability.CategoryId == category.Id).ToArray();
            if (rows.Length > 0)
            {
                yield return (category.Title, rows);
            }
        }
    }

    private Control? AddGraphicsRows(IReadOnlyList<DeviceOverlayCapability> capabilities, Panel target,
        string? focusedKey)
    {
        Control? restoreFocus = null;
        var readings = new FlexPanel { Wrap = FlexWrap.Wrap, ColumnSpacing = 12, RowSpacing = 4 };
        foreach (var capability in capabilities)
        {
            var key = GraphicsRowKey(capability);
            var row = CreateGraphicsCapabilityRow(PresentDeviceCapability(capability), key);
            if (!capability.Writable && !capability.SupportsAction && capability.ValueKind != CapabilityValueKind.None)
            {
                row.MinWidth = 160;
                Flex.SetGrow(row, 1);
                readings.Children.Add(row);
            }
            else
            {
                if (readings.Children.Count > 0)
                {
                    target.Children.Add(readings);
                    readings = new FlexPanel { Wrap = FlexWrap.Wrap, ColumnSpacing = 12, RowSpacing = 4 };
                }

                target.Children.Add(row);
            }

            if (key == focusedKey)
            {
                restoreFocus = row;
            }
        }

        if (readings.Children.Count > 0)
        {
            target.Children.Add(readings);
        }

        return restoreFocus;
    }

    private void RefreshGraphicsValues(GraphicsOverlaySection? section)
    {
        if (section is null)
        {
            return;
        }

        foreach (var view in GraphicsCapabilityList.GetLogicalDescendants().OfType<DeviceCapabilityControl>())
        {
            if (section.Capabilities.FirstOrDefault(capability => capability.CapabilityId == view.CapabilityId
                                                                  && capability.InstanceId == view.InstanceId) is
                { } current)
            {
                view.Refresh(PresentDeviceCapability(current), null);
            }
        }
    }

    private DeviceCapabilityControl CreateGraphicsCapabilityRow(DeviceOverlayCapability capability, string key)
    {
        return new DeviceCapabilityControl(capability, key, WriteGraphicsValue, current =>
                current.ValueKind == CapabilityValueKind.Color
                    ? Task.CompletedTask
                    : RunGraphicsCommandAsync($"Graphics action {current.CapabilityId}",
                        (source, token) => source.WriteAsync(current, current.SupportsAction ? null : current.NextValue,
                            token)),
            null,
            id => RunGraphicsCommandAsync("Graphics use global", (source, token) => source.UseGlobalAsync(id, token)));
    }

    private void WriteGraphicsValue(DeviceOverlayCapability capability, CapabilityValue value)
    {
        if (_graphicsSource is not { } source || _closed)
        {
            return;
        }

        // The editor may report after the row was republished; it belongs to the descriptor the user saw.
        var current = source.Snapshot().Sections
            .SelectMany(section => section.Capabilities)
            .FirstOrDefault(candidate => candidate.GpuPluginId == capability.GpuPluginId
                                         && candidate.CapabilityId == capability.CapabilityId
                                         && candidate.InstanceId == capability.InstanceId);
        if (current is not { CanInvoke: true }
            || current.DescriptorGeneration != capability.DescriptorGeneration
            || current.CycleGeneration != capability.CycleGeneration)
        {
            return;
        }

        _ = RunGraphicsCommandAsync($"Graphics value write {capability.CapabilityId}",
            (graphics, token) => graphics.WriteAsync(current, value, token));
    }

    /// <summary>Runs one Graphics command with the overlay's cancellation, never letting it fault the overlay.</summary>
    private async Task RunGraphicsCommandAsync(
        string description,
        Func<IGraphicsOverlaySource, CancellationToken, Task> command)
    {
        var source = _graphicsSource;
        if (source is null || _closed)
        {
            return;
        }

        try
        {
            await command(source, _deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"{description} failed: {ex.Message}");
        }
    }

    /// <summary>Opens one graphics section as the Graphics page.</summary>
    private void EnterGraphicsSection(string key)
    {
        if (!_navigation.Push(OverlayPage.GraphicsSection, CurrentSemanticFocusKey(), key))
        {
            return;
        }

        RefreshGraphicsPanel();
        SyncBackAffordance();
        ContentScroller.Offset = default;
        FocusFirstControl(GraphicsCapabilityList);
    }

    /// <summary>Leaves a graphics section for the Graphics root.</summary>
    private void LeaveGraphicsSection()
    {
        var key = _navigation.SectionId;
        var returnFocusKey = _navigation.Pop() ?? (key is null ? null : "rail." + GraphicsRailPrefix + key);
        RefreshGraphicsPanel();
        RestoreRootFocus(returnFocusKey);
    }

    private static string GraphicsRowKey(DeviceOverlayCapability capability)
    {
        return "graphics." + capability.GpuPluginId + "/" + DeviceRowKey(capability);
    }

    private static Control GraphicsHeading(string title)
    {
        return new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 2),
            Children =
            {
                new TextBlock
                {
                    Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
                },
                new Border { Height = 1, Classes = { "section-divider" } }
            }
        };
    }

    /// <summary>What decides whether the controls pane is rebuilt rather than refreshed.</summary>
    /// <remarks>Values never take part, so a reading or a new value keeps the rows, focus and slider drafts.</remarks>
    private static string GraphicsLayout(string? sectionKey, GraphicsOverlaySection? section, int sections)
    {
        StringBuilder layout = new();
        layout.Append(sectionKey ?? "root").Append('|').Append(sections).Append('|');
        if (section is null)
        {
            return layout.ToString();
        }

        layout.Append(section.Title).Append('|');
        foreach (var category in section.Categories)
        {
            layout.Append(category.Id).Append(':').Append(category.Title).Append(';');
        }

        foreach (var capability in section.Capabilities)
        {
            layout.Append('|').Append(DeviceRowKey(capability))
                .Append(':').Append(capability.CycleGeneration)
                .Append(':').Append(capability.DescriptorGeneration)
                .Append(':').Append(capability.ValueKind)
                .Append(':').Append(capability.Writable)
                .Append(':').Append(capability.SupportsAction)
                .Append(':').Append(capability.CategoryId)
                .Append(':').Append(capability.Title);
        }

        return layout.ToString();
    }
}

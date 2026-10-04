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
using Avalonia.LogicalTree;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Device GPU section: each section a graphics package declares, an adapter or a
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
    // The layout identity of each pinned Graphics group on Quick Access, by pin id.
    private readonly Dictionary<string, string> _pinnedGraphicsLayouts = new(StringComparer.Ordinal);

    // The layout identity of what the controls pane last drew, so a value change refreshes rows in place
    // and keeps focus and drafts, and only a new descriptor or section rebuilds them.
    private string? _graphicsLayout;
    private IGraphicsOverlaySource? _graphicsSource;

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
        _pinnedGraphicsLayouts.Clear();
        ConfigureGpuDestination(_graphicsSource?.Snapshot().Visible == true);
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
        GpuUnavailable.IsVisible = !snapshot.Visible;
        RefreshGraphicsPins();
        if (_navigation.Page != OverlayPage.DeviceGpu)
        {
            return;
        }

        var status = string.Join(" ", snapshot.Publishers
            .Where(publisher => publisher.Note is not null)
            .Select(publisher => publisher.Note));
        GraphicsStatus.Text = status;
        GraphicsStatus.IsVisible = status.Length > 0;

        var layout = snapshot.Visible + "|" + string.Join("\n", snapshot.Sections.Select(section =>
            GraphicsLayout(section.Key, section, snapshot.Sections.Count)));
        if (layout == _graphicsLayout)
        {
            foreach (var section in snapshot.Sections)
            {
                RefreshGraphicsValues(GraphicsCapabilityList, section.Capabilities);
            }

            return;
        }

        _graphicsLayout = layout;
        var focusedKey = GetTopLevel(this)?.FocusManager.GetFocusedElement() is Control focused
            ? focused.Tag as string
            : null;
        GraphicsCapabilityList.Children.Clear();
        Control? restoreFocus = null;
        foreach (var section in snapshot.Sections)
        {
            restoreFocus = RenderGraphicsSection(section, focusedKey) ?? restoreFocus;
        }

        if (snapshot.Visible && snapshot.Sections.Count == 0)
        {
            GraphicsCapabilityList.Children.Add(new TextBlock
            {
                Text = "The graphics driver has not published any settings yet.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4)
            });
        }

        Log.Change("overlay.graphics.render",
            $"Device GPU: sections={snapshot.Sections.Count}, publishers={snapshot.Publishers.Count}");
        var focusTarget = focusedKey is null
            ? null
            : GraphicsCapabilityList.GetLogicalDescendants().OfType<Control>()
                .FirstOrDefault(control => control.Focusable && Equals(control.Tag, focusedKey));
        var target = focusTarget ?? restoreFocus;
        if (target is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
        {
            target.Focus(NavigationMethod.Directional);
        }

        RestoreSectionHeaderFocus(focusedKey);
    }

    /// <summary>Follows a Graphics change in the pin list and on Quick Access, whichever destination shows.</summary>
    private void RefreshGraphicsPins()
    {
        RefreshDeviceSectionPins(DeviceSnapshotOrOff());
        if (_pins.Any(id => id.StartsWith(GraphicsSectionPins.Prefix, StringComparison.Ordinal)))
        {
            RenderPins();
        }
    }

    /// <summary>
    ///     A pinned Graphics group on Quick Access, drawn live from the Graphics source with the Graphics
    ///     page's rows, or null when its publisher, section or category is absent now.
    /// </summary>
    private Control? CreatePinnedGraphicsSection(string id)
    {
        if (GraphicsSectionPins.Resolve(_graphicsSource?.Snapshot(), id) is not { } pin)
        {
            return null;
        }

        var layout = GraphicsLayout(id,
            pin.Section with { Title = pin.PinTitle, Categories = [], Capabilities = pin.Rows }, 0);
        var existing = PinnedSectionsGrid.Children.FirstOrDefault(row => Equals(row.Tag, PinTagPrefix + id));
        if (existing is not null && _pinnedGraphicsLayouts.TryGetValue(id, out var previous) && previous == layout)
        {
            RefreshGraphicsValues(existing, pin.Rows);
            return existing;
        }

        _pinnedGraphicsLayouts[id] = layout;
        var panel = CreateSection(id, pin.PinTitle, true);
        AddGraphicsRows(pin.Rows, panel, null, true);
        return WrapDeviceSection(panel);
    }

    /// <summary>Draws one graphics section: its lead rows, then one group per declared category.</summary>
    private Control? RenderGraphicsSection(GraphicsOverlaySection section, string? focusedKey)
    {
        Control? restoreFocus = null;
        var groups = new StackPanel { Spacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        GraphicsCapabilityList.Children.Add(groups);
        foreach (var pin in GraphicsSectionPins.Groups(section))
        {
            var content = CreateSection(pin.Id, pin.PinTitle);
            restoreFocus = AddGraphicsRows(pin.Rows, content, focusedKey) ?? restoreFocus;
            groups.Children.Add(WrapDeviceSection(content));
        }

        return restoreFocus;
    }

    private Control? AddGraphicsRows(IReadOnlyList<DeviceOverlayCapability> capabilities, Panel target,
        string? focusedKey, bool pinned = false)
    {
        Control? restoreFocus = null;
        var readings = new FlexPanel { Wrap = FlexWrap.Wrap, ColumnSpacing = 12, RowSpacing = 4 };
        foreach (var capability in capabilities)
        {
            var key = (pinned ? PinTagPrefix : "") + GraphicsRowKey(capability);
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

    private static void RefreshGraphicsValues(Control root, IReadOnlyList<DeviceOverlayCapability> capabilities)
    {
        foreach (var view in root.GetLogicalDescendants().OfType<DeviceCapabilityControl>())
        {
            if (capabilities.FirstOrDefault(capability => capability.CapabilityId == view.CapabilityId
                                                          && capability.InstanceId == view.InstanceId
                                                          && (Equals(view.Tag, GraphicsRowKey(capability))
                                                              || Equals(view.Tag,
                                                                  PinTagPrefix + GraphicsRowKey(capability)))) is
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

    private static string GraphicsRowKey(DeviceOverlayCapability capability)
    {
        return "graphics." + capability.GpuPluginId + "/" + DeviceRowKey(capability);
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

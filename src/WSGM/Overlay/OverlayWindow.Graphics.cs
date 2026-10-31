using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    // The group each pinned Graphics section on Quick Access was last drawn from, by pin id.
    private readonly Dictionary<string, GraphicsOverlaySection> _pinnedGraphicsLayouts = new(StringComparer.Ordinal);

    // What the controls pane last drew, so a value change refreshes rows in place and keeps focus and
    // drafts, and only a new descriptor or section rebuilds them.
    private GraphicsOverlaySnapshot? _graphicsLayout;
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
        var snapshot = _graphicsSource?.Snapshot() ?? GraphicsOverlaySnapshot.Empty;
        ConfigureGpuDestination(snapshot.Visible);
        RefreshGraphicsPanel(snapshot);
        // Graphics groups are offered on the Device page and drawn on Quick access.
        RefreshDevicePanel();
        RenderPins(graphics: snapshot);
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
        RefreshGraphicsPanel(_graphicsSource?.Snapshot() ?? GraphicsOverlaySnapshot.Empty);
    }

    /// <summary>Draws the GPU page from a snapshot the caller read once for the pass.</summary>
    /// <param name="snapshot">The graphics snapshot.</param>
    /// <remarks>The Device page's pin list and Quick access follow the same pass, not this page.</remarks>
    private void RefreshGraphicsPanel(GraphicsOverlaySnapshot snapshot)
    {
        // The first render runs when the window opens.
        if (_closed || !_opened)
        {
            return;
        }

        GpuUnavailable.IsVisible = !snapshot.Visible;
        if (_navigation.Page != OverlayPage.DeviceGpu)
        {
            return;
        }

        var status = string.Join(" ", snapshot.Publishers
            .Where(publisher => publisher.Note is not null)
            .Select(publisher => publisher.Note));
        GraphicsStatus.Text = status;
        GraphicsStatus.IsVisible = status.Length > 0;

        if (SameGraphicsLayout(_graphicsLayout, snapshot))
        {
            CapabilityRowRenderer.RefreshValues(GraphicsCapabilityList,
                snapshot.Sections.SelectMany(section => section.Capabilities), null);
            return;
        }

        _graphicsLayout = snapshot;
        var focusedKey = CurrentSemanticFocusKey();
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

    /// <summary>
    ///     A pinned Graphics group on Quick Access, drawn live from the Graphics source with the Graphics
    ///     page's rows, or null when its publisher, section or category is absent now.
    /// </summary>
    private Control? CreatePinnedGraphicsSection(string id, GraphicsOverlaySnapshot? snapshot)
    {
        if (GraphicsSectionPins.Resolve(snapshot, id) is not { } pin)
        {
            return null;
        }

        var layout = pin.Section with { Title = pin.PinTitle, Categories = [], Capabilities = pin.Rows };
        var existing = PinnedSectionsGrid.Children.FirstOrDefault(row => Equals(row.Tag, PinTagPrefix + id));
        if (existing is not null && _pinnedGraphicsLayouts.TryGetValue(id, out var previous)
                                 && SameGraphicsSection(previous, layout))
        {
            CapabilityRowRenderer.RefreshValues(existing, pin.Rows, null);
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
        foreach (var capability in capabilities)
        {
            var key = (pinned ? PinTagPrefix : "") + GraphicsRowKey(capability);
            var row = CreateGraphicsCapabilityRow(CapabilityRowRenderer.Present(capability), key);
            CapabilityRowRenderer.AddRow(target, row,
                !capability.Writable && !capability.SupportsAction && capability.ValueKind != CapabilityValueKind.None
                    ? 160
                    : null);
            if (key == focusedKey)
            {
                restoreFocus = row;
            }
        }

        return restoreFocus;
    }

    private DeviceCapabilityControl CreateGraphicsCapabilityRow(DeviceOverlayCapability capability, string key)
    {
        return new DeviceCapabilityControl(capability, key, WriteGraphicsValue, seen =>
                seen.ValueKind == CapabilityValueKind.Color
                || _graphicsSource is not { } source || CurrentGraphicsCapability(source, seen) is not { } current
                    ? Task.CompletedTask
                    : RunCommandAsync(source, $"Graphics action {current.CapabilityId}",
                        (graphics, token) => graphics.WriteAsync(current,
                            current.SupportsAction ? null : current.NextValue, token)),
            null,
            id => RunCommandAsync(_graphicsSource, "Graphics use global",
                (source, token) => source.UseGlobalAsync(id, token)));
    }

    /// <summary>The published graphics row a commit or action may use, or null when the user's row is gone.</summary>
    private static DeviceOverlayCapability? CurrentGraphicsCapability(IGraphicsOverlaySource source,
        DeviceOverlayCapability seen)
    {
        return CapabilityRowRenderer.CurrentInvokable(
            source.Snapshot().Sections.SelectMany(section => section.Capabilities), seen);
    }

    private void WriteGraphicsValue(DeviceOverlayCapability capability, CapabilityValue value)
    {
        // The editor may report after the row was republished; it belongs to the descriptor the user saw.
        if (_graphicsSource is not { } source || _closed
                                              || CurrentGraphicsCapability(source, capability) is not { } current)
        {
            return;
        }

        _ = RunCommandAsync(source, $"Graphics value write {capability.CapabilityId}",
            (graphics, token) => graphics.WriteAsync(current, value, token));
    }

    private static string GraphicsRowKey(DeviceOverlayCapability capability)
    {
        return "graphics." + capability.GpuPluginId + "/" + DeviceRowKey(capability);
    }

    /// <summary>What decides whether the controls pane is rebuilt rather than refreshed.</summary>
    /// <remarks>Values never take part, so a reading or a new value keeps the rows, focus and slider drafts.</remarks>
    private static bool SameGraphicsLayout(GraphicsOverlaySnapshot? before, GraphicsOverlaySnapshot after)
    {
        if (before is null || before.Visible != after.Visible || before.Sections.Count != after.Sections.Count)
        {
            return false;
        }

        for (var index = 0; index < after.Sections.Count; index++)
        {
            if (!SameGraphicsSection(before.Sections[index], after.Sections[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameGraphicsSection(GraphicsOverlaySection before, GraphicsOverlaySection after)
    {
        return before.Key == after.Key && before.Title == after.Title
                                       && before.Categories.SequenceEqual(after.Categories)
                                       && DeviceOverlayCapability.SameLayouts(before.Capabilities,
                                           after.Capabilities);
    }
}

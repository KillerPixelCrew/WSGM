using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>The Tag prefix that marks a Quick access clone; X on one unpins.</summary>
    private const string PinTagPrefix = QuickAccessPins.TagPrefix;

    private readonly QuickAccessPins _pins;

    /// <summary>
    ///     Rebuilds the Quick access root from the persisted pin list. Ids this
    ///     build cannot resolve are skipped (kept in the config for the build or device that
    ///     can).
    /// </summary>
    /// <param name="ids">The pinned row ids in display order.</param>
    internal void SetPins(IReadOnlyList<string> ids)
    {
        _pins.Set(ids);
        RenderPins();
    }

    /// <summary>Draws the Quick access root while it shows; it is drawn again whenever it is shown.</summary>
    /// <param name="device">The Device snapshot of this pass, or null to read it when a pin needs it.</param>
    /// <param name="performance">The performance snapshot of this pass, or null to read it when pinned.</param>
    /// <param name="graphics">The graphics snapshot of this pass, or null to read it when pinned.</param>
    private void RenderPins(DeviceOverlaySnapshot? device = null, PerformanceOverlaySnapshot? performance = null,
        GraphicsOverlaySnapshot? graphics = null)
    {
        // The first render runs when the window opens.
        if (_closed || !_opened || !PanelQuickAccess.IsVisible)
        {
            return;
        }

        var ids = _pins.Ids;
        // Each source is read at most once for the whole list, and only when a pin draws from it.
        (DeviceOverlaySnapshot Snapshot, DevicePinSection[] Sections)? pinnedDevice = null;
        if (_deviceBridge is { } bridge && ids.Any(IsDevicePin))
        {
            device ??= bridge.Snapshot();
            pinnedDevice = (device, DevicePinSections(device));
        }

        if (performance is null && ids.Contains("section.performance"))
        {
            performance = _performanceSource?.Snapshot();
        }

        if (graphics is null && ids.Any(id => id.StartsWith(GraphicsSectionPins.Prefix, StringComparison.Ordinal)))
        {
            graphics = _graphicsSource?.Snapshot();
        }

        var focusedKey = CurrentSemanticFocusKey();
        Control? restoreFocus = null;
        _pins.SyncMirrors();
        List<Control> valueControls = [];
        foreach (var id in ids)
        {
            Control? row;
            if (_pins.IsRow(id))
            {
                row = _pins.Mirror(id);
            }
            else
            {
                row = CreatePinnedSection(id, pinnedDevice, performance, graphics);
                if (row is not null)
                {
                    row.Margin = new Thickness(0);
                    valueControls.Add(row);
                }
            }

            if (row is not null && string.Equals(row.Tag as string, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        // Host controls own subscriptions and user selections. Keep them attached across
        // telemetry refreshes; removing a pin is what ends that view's lifetime.
        ReconcileChildren(PinnedSectionsGrid, valueControls);
        var mirrors = _pins.PlaceGhost(valueControls.Count > 0);
        if (restoreFocus is null && PanelQuickAccess.IsVisible && !AnySubView && focusedKey is not null)
        {
            restoreFocus = FocusSearch.Keyed(PinnedSectionsGrid, focusedKey);
        }

        Log.Change("overlay.pins",
            $"Quick access pins: {mirrors + PinnedSectionsGrid.Children.Count} of {ids.Count} rendered.");

        if (restoreFocus is not null)
        {
            if (restoreFocus.Focusable)
            {
                restoreFocus.Focus(NavigationMethod.Directional);
            }
            else
            {
                FocusFirstControl(restoreFocus);
            }
        }
        else if (PanelQuickAccess.IsVisible && !AnySubView && focusedKey is not null
                 && focusedKey.StartsWith(PinTagPrefix, StringComparison.Ordinal))
        {
            // The focused pin was just unpinned: land on whatever is left rather than on a
            // detached control.
            FocusFirstControl(PanelQuickAccess);
        }
    }

    /// <summary>
    ///     Makes <paramref name="panel" /> hold exactly <paramref name="rows" /> in that order, keeping every
    ///     surviving control in place so its focus, drafts and subscriptions survive.
    /// </summary>
    private static void ReconcileChildren(Panel panel, IReadOnlyList<Control> rows)
    {
        HashSet<Control> current = [.. rows];
        foreach (var stale in panel.Children.Where(child => !current.Contains(child)).ToArray())
        {
            panel.Children.Remove(stale);
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var existing = panel.Children.IndexOf(rows[index]);
            if (existing < 0)
            {
                panel.Children.Insert(index, rows[index]);
            }
            else if (existing != index)
            {
                panel.Children.Move(existing, index);
            }
        }
    }

    private static string DeviceRowKey(DeviceOverlayCapability capability)
    {
        return new DeviceCapabilityKey(capability.CapabilityId, capability.InstanceId).ToString();
    }

    /// <summary>Whether a pin id names a Device group, the one kind drawn from the Device snapshot.</summary>
    private bool IsDevicePin(string id)
    {
        return id.StartsWith("section.", StringComparison.Ordinal)
               && id != "section.performance"
               && !id.StartsWith(GraphicsSectionPins.Prefix, StringComparison.Ordinal)
               && !_pins.IsRow(id)
               && !_controlPinFactories.ContainsKey(id);
    }

    /// <summary>Static actions and whole Device and Graphics sections are the pin targets.</summary>
    private bool IsPinnable(string id, Lazy<DevicePinSection[]?> deviceSections,
        Lazy<GraphicsOverlaySnapshot?> graphics)
    {
        return id.StartsWith(ProgramsService.PinPrefix, StringComparison.Ordinal)
               || _pins.IsRow(id) || _controlPinFactories.ContainsKey(id)
                                         || (id == "section.performance" &&
                                             _performanceSource?.Snapshot().Visible is true)
                                         || (_pins.Contains(id) && id.StartsWith("section.", StringComparison.Ordinal))
                                         || (id.StartsWith(GraphicsSectionPins.Prefix, StringComparison.Ordinal)
                                             && GraphicsSectionPins.Resolve(graphics.Value, id) is not null)
                                         || deviceSections.Value?.Any(section => section.Id == id) is true;
    }

    /// <summary>Resolves the row id a row or its Quick access mirror stands for.</summary>
    private bool TryGetPinId(Control? control, out string id)
    {
        // One read of each source for the whole ancestor walk instead of one per node.
        Lazy<DevicePinSection[]?> deviceSections = new(
            () => _deviceBridge?.Snapshot() is { } snapshot ? DevicePinSections(snapshot) : null,
            LazyThreadSafetyMode.None);
        Lazy<GraphicsOverlaySnapshot?> graphics = new(() => _graphicsSource?.Snapshot(), LazyThreadSafetyMode.None);
        for (Visual? node = control; node is not null; node = node.GetVisualParent())
        {
            if (node is not Control { Tag: string tag })
            {
                continue;
            }

            id = tag.StartsWith(PinTagPrefix, StringComparison.Ordinal) ? tag[PinTagPrefix.Length..] : tag;
            if (IsPinnable(id, deviceSections, graphics))
            {
                return true;
            }
        }

        id = "";
        return false;
    }

    /// <summary>
    ///     Gamepad secondary action (X): the context menu of a focused tray
    ///     icon, otherwise pin/unpin the focused row. Logged either way — this is
    ///     remote-diagnosis territory.
    /// </summary>
    /// <param name="focused">
    ///     Current focus target, or null; contextual pin or tray behavior is selected from its semantic
    ///     identity.
    /// </param>
    internal void RequestSecondaryAction(InputElement? focused)
    {
        if (focused is Control { DataContext: TrayIconEntry entry } control)
        {
            Log.Info($"Gamepad X: tray context menu for '{entry.Tip}'.");
            TrayIconActivated?.Invoke(entry, true, AnchorBelow(control));
            return;
        }

        if (TryGetPinId(focused as Control, out var id))
        {
            Log.Info($"Gamepad X: toggling pin '{id}'.");
            PinToggleRequested?.Invoke(id);
            return;
        }

        Log.Info(
            $"Gamepad X: focused element is not pinnable ({focused?.GetType().Name ?? "none"}, tag {(focused as Control)?.Tag ?? "-"}).");
    }

    private void OnHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started)
        {
            return;
        }

        var row = e.Source as Control;
        if (!TryGetPinId(row, out var id))
        {
            return;
        }

        e.Handled = true;
        Log.Info($"Touch hold: toggling pin '{id}'.");
        PinToggleRequested?.Invoke(id);
    }

    private void OnPointerPressedForPin(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && TryGetPinId(e.Source as Control, out _))
        {
            e.Handled = true;
        }
    }

    private void OnPointerReleasedForPin(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        var row = e.Source as Control;
        if (!TryGetPinId(row, out var id))
        {
            return;
        }

        e.Handled = true;
        PinToggleRequested?.Invoke(id);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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

    private readonly DevicePins _devicePins;
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
        HotkeysHost.SetPins(ids);
        RenderPins();
    }

    /// <summary>Releases the pinned program rows' icons and the Programs page as the session shuts down.</summary>
    internal void ReleaseProgramsPresentation()
    {
        ProgramsHost.Attach(null);
        ProgramsHost.Leave();
        foreach (var row in PinnedSectionsGrid.Children.OfType<ProgramActionRow>())
        {
            row.ReleaseIcon();
        }
    }

    /// <summary>
    ///     Resolves a pin id through the source that owns it: an action row or Tools section, a program, a hotkey,
    ///     or a Device, performance or graphics group.
    /// </summary>
    /// <param name="id">The pin id.</param>
    /// <param name="devices">The pass's snapshot reads for the Device pins.</param>
    /// <returns>The pin, or null for an id no source of this build owns.</returns>
    private QuickAccessPin? ResolvePin(string id, DevicePins.Pass devices)
    {
        return _pins.Resolve(id) ?? ProgramsHost.ResolvePin(id) ?? HotkeysHost.ResolvePin(id) ?? devices.Resolve(id);
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
        var devices = _devicePins.Begin(device, performance, graphics);
        var focusedKey = CurrentSemanticFocusKey();
        Control? restoreFocus = null;
        _pins.SyncMirrors();
        List<Control> valueControls = [];
        List<Control> tiles = [];
        foreach (var id in ids)
        {
            if (ResolvePin(id, devices) is not { } pin)
            {
                continue;
            }

            var drawn = pin.Tile
                ? _pins.Mirror(id)
                : PinnedSectionsGrid.Children.FirstOrDefault(child => Equals(child.Tag, PinTagPrefix + id));
            var row = pin.Draw(drawn);
            if (pin.Tile)
            {
                tiles.Add(row);
            }
            else
            {
                row.Margin = new Thickness(0);
                valueControls.Add(row);
            }

            if (string.Equals(row.Tag as string, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        // Host controls own subscriptions and user selections. Keep them attached across
        // telemetry refreshes; removing a pin is what ends that view's lifetime.
        DeviceGroupRenderer.ReconcileChildren(PinnedSectionsGrid, valueControls);
        var mirrors = _pins.PlaceTiles(tiles, valueControls.Count > 0);
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

    /// <summary>Resolves the pin a row or its Quick access copy stands for.</summary>
    /// <remarks>
    ///     An id is offered to toggle while its source offers it, and while it is pinned, so a pin whose
    ///     provider is gone can still be removed.
    /// </remarks>
    private bool TryGetPinId(Control? control, out string id, out string title)
    {
        // One read of each source for the whole ancestor walk instead of one per node.
        var devices = _devicePins.Begin();
        for (Visual? node = control; node is not null; node = node.GetVisualParent())
        {
            if (node is not Control { Tag: string tag })
            {
                continue;
            }

            id = tag.StartsWith(PinTagPrefix, StringComparison.Ordinal) ? tag[PinTagPrefix.Length..] : tag;
            if (ResolvePin(id, devices) is { } pin && (pin.Available || _pins.Contains(id)))
            {
                title = pin.Title;
                return true;
            }
        }

        id = "";
        title = "";
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

        if (TryGetPinId(focused as Control, out var id, out var title))
        {
            Log.Info($"Gamepad X: toggling pin '{id}' ({title}).");
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
        if (!TryGetPinId(row, out var id, out var title))
        {
            return;
        }

        e.Handled = true;
        Log.Info($"Touch hold: toggling pin '{id}' ({title}).");
        PinToggleRequested?.Invoke(id);
    }

    private void OnPointerPressedForPin(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && TryGetPinId(e.Source as Control, out _, out _))
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
        if (!TryGetPinId(row, out var id, out _))
        {
            return;
        }

        e.Handled = true;
        PinToggleRequested?.Invoke(id);
    }
}

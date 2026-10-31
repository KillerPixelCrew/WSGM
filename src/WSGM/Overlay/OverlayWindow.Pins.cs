using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>The Tag prefix that marks a Quick access clone; X on one unpins.</summary>
    private const string PinTagPrefix = "pin:";

    // Every section heading currently in the window's tree; each adds itself on attach and leaves on detach.
    private readonly HashSet<SectionPinHeader> _pinHeaders = [];

    /// <summary>
    ///     Live mirrors on the Quick access root: each clone follows its source row's
    ///     title, description, badge and visibility through the source's property changes, and
    ///     presses through to the source's Click handlers.
    /// </summary>
    private readonly List<(ActionButton Source, EventHandler<AvaloniaPropertyChangedEventArgs> Handler)> _pinMirrors =
        [];

    /// <summary>
    ///     Every pinnable XAML row, by its stable id (the ActionButton's Tag).
    ///     Device rows are not here: they are rebuilt from the snapshot on every render.
    /// </summary>
    private readonly Dictionary<string, ActionButton> _pinnable = new(StringComparer.Ordinal);

    // The pin list the current mirrors were built for. Mirrors follow their source rows through
    // property changes, so while the list is unchanged a render only revisits the pinned sections.
    private string[]? _mirroredPins;
    private ActionButton? _pinGhost;
    private DispatcherTimer? _pinToastTimer;

    private IReadOnlyList<string> _pins = [];

    /// <summary>False until the first SetPins, so restoring stored pins never toasts.</summary>
    private bool _pinsInitialized;

    private void IndexPinnableRows()
    {
        foreach (var button in this.GetLogicalDescendants().OfType<ActionButton>())
        {
            if (button.Tag is string { Length: > 0 } id && !id.StartsWith(PinTagPrefix, StringComparison.Ordinal))
            {
                _pinnable[id] = button;
            }
        }
    }

    /// <summary>
    ///     Rebuilds the Quick access root from the persisted pin list. Ids this
    ///     build cannot resolve are skipped (kept in the config for the build or device that
    ///     can).
    /// </summary>
    /// <param name="ids">The pinned row ids in display order.</param>
    internal void SetPins(IReadOnlyList<string> ids)
    {
        var previous = _pinsInitialized ? _pins : null;
        _pins = ids;
        _pinsInitialized = true;
        // The markers sit on the source rows, which show while Quick access is hidden.
        UpdatePinnedIndicators();
        RenderPins();
        if (previous is not null && ids.Count != previous.Count)
        {
            ShowPinToast(ids.Count > previous.Count);
        }
    }

    /// <summary>Transient confirmation above the Open apps strip after a pin toggle.</summary>
    private void ShowPinToast(bool added)
    {
        if (_closed)
        {
            return;
        }

        PinToastText.Text = added ? "Pinned to Quick access" : "Unpinned from Quick access";
        PinToastHint.Text = added ? "X again to unpin" : string.Empty;
        PinToast.IsVisible = true;
        if (_pinToastTimer is null)
        {
            DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(2400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                PinToast.IsVisible = false;
            };
            _pinToastTimer = timer;
        }

        _pinToastTimer.Stop();
        _pinToastTimer.Start();
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

        // Each source is read at most once for the whole list, and only when a pin draws from it.
        (DeviceOverlaySnapshot Snapshot, DevicePinSection[] Sections)? pinnedDevice = null;
        if (_deviceBridge is { } bridge && _pins.Any(IsDevicePin))
        {
            device ??= bridge.Snapshot();
            pinnedDevice = (device, DevicePinSections(device));
        }

        if (performance is null && _pins.Contains("section.performance"))
        {
            performance = _performanceSource?.Snapshot();
        }

        if (graphics is null && _pins.Any(id => id.StartsWith(GraphicsSectionPins.Prefix, StringComparison.Ordinal)))
        {
            graphics = _graphicsSource?.Snapshot();
        }

        var focusedKey = CurrentSemanticFocusKey();
        Control? restoreFocus = null;
        var mirrorsCurrent = _mirroredPins is not null && _mirroredPins.SequenceEqual(_pins, StringComparer.Ordinal);
        if (!mirrorsCurrent)
        {
            ReleasePinMirrors();
            PinnedGrid.Children.Clear();
        }

        List<Control> valueControls = [];
        foreach (var id in _pins)
        {
            Control? row;
            if (_pinnable.TryGetValue(id, out var source))
            {
                row = mirrorsCurrent
                    ? PinnedGrid.Children.FirstOrDefault(child => Equals(child.Tag, PinTagPrefix + id))
                    : CreatePinMirror(id, source);
                if (row is null)
                {
                    continue;
                }

                if (!mirrorsCurrent)
                {
                    row.Margin = new Thickness(0, 0, 10, 10);
                    PinnedGrid.Children.Add(row);
                }
            }
            else
            {
                row = CreatePinnedSection(id, pinnedDevice, performance, graphics);
                if (row is null)
                {
                    continue;
                }

                row.Margin = new Thickness(0);
                valueControls.Add(row);
            }

            if (string.Equals(row.Tag as string, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }

        _mirroredPins = [.. _pins];
        // Host controls own subscriptions and user selections. Keep them attached across
        // telemetry refreshes; removing a pin is what ends that view's lifetime.
        ReconcileChildren(PinnedSectionsGrid, valueControls);

        // Once sections are present their headers explain pinning. Avoid an empty
        // action row above a front page containing only sections.
        if (valueControls.Count == 0)
        {
            _pinGhost ??= CreatePinGhost();
            if (!PinnedGrid.Children.Contains(_pinGhost))
            {
                PinnedGrid.Children.Add(_pinGhost);
            }
        }
        else if (_pinGhost is not null)
        {
            PinnedGrid.Children.Remove(_pinGhost);
        }

        PinnedGrid.IsVisible = PinnedGrid.Children.Count > 0;
        if (restoreFocus is null && PanelQuickAccess.IsVisible && !AnySubView && focusedKey is not null)
        {
            restoreFocus = FocusSearch.Keyed(PinnedSectionsGrid, focusedKey);
        }

        var rendered = PinnedGrid.Children.Count + PinnedSectionsGrid.Children.Count -
                       (valueControls.Count == 0 ? 1 : 0);
        Log.Change("overlay.pins", $"Quick access pins: {rendered} of {_pins.Count} rendered.");

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

    /// <summary>Updates the pin marker on every row in its original destination.</summary>
    /// <remarks>
    ///     Section headers register themselves while they are in the tree, so replaced sections drop
    ///     out without a walk of the whole window. Quick access mirrors use a prefixed tag and remain
    ///     unmarked: the icon is the immediate feedback at the source row, where the user pressed X or
    ///     held the card.
    /// </remarks>
    private void UpdatePinnedIndicators()
    {
        var pinned = _pins.ToHashSet(StringComparer.Ordinal);
        foreach (var header in _pinHeaders)
        {
            header.Refresh(pinned.Contains(header.SectionId));
        }

        foreach (var (id, button) in _pinnable)
        {
            button.IsPinned = IsOriginalPinnedRow(id, pinned);
        }
    }

    private static ActionButton CreatePinGhost()
    {
        ActionButton ghost = new()
        {
            IconGeometry = Icons.Pin,
            Title = "Pin a section",
            Description = "Use the tack button in a heading to show its controls here",
            IsEnabled = false,
            Margin = new Thickness(0, 0, 10, 10)
        };
        ghost.Classes.Add("tile");
        ghost.Classes.Add("ghost");
        return ghost;
    }

    /// <summary>Determines whether a tagged card is an original row in the active pin set.</summary>
    /// <param name="tag">Semantic tag attached to the focused control.</param>
    /// <param name="pinned">Current set of pinned group IDs.</param>
    /// <returns>True for an original source row whose group is pinned; pinned copies are excluded.</returns>
    internal static bool IsOriginalPinnedRow(object? tag, IReadOnlySet<string> pinned)
    {
        return tag is string id
               && !id.StartsWith(PinTagPrefix, StringComparison.Ordinal)
               && pinned.Contains(id);
    }

    private ActionButton CreatePinMirror(string id, ActionButton source)
    {
        var clone = new ActionButton { Tag = PinTagPrefix + id };
        clone.Classes.Add("tile");
        foreach (var cls in source.Classes)
        {
            if (cls is "primary" or "danger")
            {
                clone.Classes.Add(cls);
            }
        }

        MirrorPinnedRow(clone, source);
        EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, _) => MirrorPinnedRow(clone, source);
        source.PropertyChanged += handler;
        // Press-through: the source's Click handlers run with the source as sender, so a
        // row that rewrites its own title ("Really?", "Applied to …") does so on the source
        // and the mirror follows.
        clone.Click += (_, _) => source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _pinMirrors.Add((source, handler));
        return clone;
    }

    private void ReleasePinMirrors()
    {
        foreach (var (source, handler) in _pinMirrors)
        {
            source.PropertyChanged -= handler;
        }

        _pinMirrors.Clear();
        _mirroredPins = null;
    }

    private static void MirrorPinnedRow(ActionButton clone, ActionButton source)
    {
        clone.CopyContentFrom(source);
        // The source's OWN IsVisible (bound to a feature flag), not its effective one —
        // the source panel is hidden whenever the Quick access root shows.
        clone.IsVisible = source.IsVisible;
        clone.IsEnabled = source.IsEnabled;
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
               && !_pinnable.ContainsKey(id)
               && !_controlPinFactories.ContainsKey(id);
    }

    /// <summary>Static actions and whole Device and Graphics sections are the pin targets.</summary>
    private bool IsPinnable(string id, Lazy<DevicePinSection[]?> deviceSections,
        Lazy<GraphicsOverlaySnapshot?> graphics)
    {
        return id.StartsWith(ProgramsService.PinPrefix, StringComparison.Ordinal)
               || _pinnable.ContainsKey(id) || _controlPinFactories.ContainsKey(id)
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

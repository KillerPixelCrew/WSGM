using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>The Tag prefix that marks a Quick access clone; X on one unpins.</summary>
    private const string PinTagPrefix = "pin:";

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

    private int _loggedPinRendered = -1;
    private int _loggedPinTotal = -1;

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

    private void RenderPins()
    {
        if (_closed)
        {
            return;
        }

        if (!_opened)
        {
            _rendersAwaitingOpen |= PinsRenderAwaitingOpen;
            return;
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
                row = CreatePinnedSection(id);
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
        HashSet<Control> current = [.. valueControls];
        foreach (var stale in PinnedSectionsGrid.Children.Where(row => !current.Contains(row)).ToArray())
        {
            PinnedSectionsGrid.Children.Remove(stale);
        }

        for (var i = 0; i < valueControls.Count; i++)
        {
            var existing = PinnedSectionsGrid.Children.IndexOf(valueControls[i]);
            if (existing < 0)
            {
                PinnedSectionsGrid.Children.Insert(i, valueControls[i]);
            }
            else if (existing != i)
            {
                PinnedSectionsGrid.Children.Move(existing, i);
            }
        }

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
        UpdatePinnedIndicators();
        if (restoreFocus is null && PanelQuickAccess.IsVisible && !AnySubView && focusedKey is not null)
        {
            restoreFocus = PinnedSectionsGrid.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => control.Focusable && Equals(control.Tag, focusedKey));
        }

        var rendered = PinnedGrid.Children.Count + PinnedSectionsGrid.Children.Count -
                       (valueControls.Count == 0 ? 1 : 0);
        if (rendered != _loggedPinRendered || _pins.Count != _loggedPinTotal)
        {
            _loggedPinRendered = rendered;
            _loggedPinTotal = _pins.Count;
            Log.Change("overlay.pins", $"Quick access pins: {rendered} of {_pins.Count} rendered.");
        }

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

    /// <summary>Updates the pin marker on every row in its original destination.</summary>
    /// <remarks>
    ///     Descriptor generation changes can replace sections, so this walks the current logical
    ///     tree instead of retaining references to removed controls. Quick
    ///     access mirrors use a prefixed tag and remain unmarked: the icon is the immediate feedback at
    ///     the source row, where the user pressed X or held the card.
    /// </remarks>
    private void UpdatePinnedIndicators()
    {
        var pinned = _pins.ToHashSet(StringComparer.Ordinal);
        // One walk of the logical tree for both kinds of indicator.
        foreach (var node in this.GetLogicalDescendants())
        {
            switch (node)
            {
                case SectionPinHeader header:
                    header.Refresh(pinned.Contains(header.SectionId));
                    break;
                case ActionButton button and not PluginWidgetPinControls:
                    button.IsPinned = IsOriginalPinnedRow(button.Tag, pinned);
                    break;
            }
        }
    }

    private static ActionButton CreatePinGhost()
    {
        ActionButton ghost = new()
        {
            IconGeometry = Icons.Pin,
            Title = "Pin a section",
            Description = "Use Pin section in a heading to show its controls here",
            IsEnabled = false,
            Margin = new Thickness(0, 0, 10, 10)
        };
        ghost.Classes.Add("tile");
        ghost.Classes.Add("ghost");
        return ghost;
    }

    /// <summary>Determines whether a tagged card is an original row in the active pin set.</summary>
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
        clone.Title = source.Title;
        clone.Description = source.Description;
        clone.IconGeometry = source.IconGeometry;
        clone.TrailingText = source.TrailingText;
        clone.TrailingGlyph = source.TrailingGlyph;


        // The source's OWN IsVisible (bound to a feature flag), not its effective one —
        // the source panel is hidden whenever the Quick access root shows.
        clone.IsVisible = source.IsVisible;
        clone.IsEnabled = source.IsEnabled;
    }

    private static string DeviceRowKey(DeviceOverlayCapability capability)
    {
        return capability.InstanceId is { Length: > 0 }
            ? $"{capability.CapabilityId}#{capability.InstanceId}"
            : capability.CapabilityId;
    }

    /// <summary>Static actions and whole sections are the pin targets.</summary>
    private bool IsPinnable(string id, Func<DeviceOverlaySnapshot?> deviceSnapshot)
    {
        return _pinnable.ContainsKey(id) || _controlPinFactories.ContainsKey(id)
                                         || (id == "section.performance" &&
                                             _performanceSource?.Snapshot().Visible is true)
                                         || (_pins.Contains(id) && id.StartsWith("section.", StringComparison.Ordinal))
                                         || (deviceSnapshot() is { } snapshot && DevicePinSections(snapshot)
                                             .Any(section => section.Id == id));
    }

    /// <summary>Resolves the row id a row or its Quick access mirror stands for.</summary>
    private bool TryGetPinId(Control? control, out string id)
    {
        // One device snapshot for the whole ancestor walk instead of one per node.
        DeviceOverlaySnapshot? snapshot = null;
        var snapshotRead = false;
        for (Visual? node = control; node is not null; node = node.GetVisualParent())
        {
            if (node is not Control { Tag: string tag })
            {
                continue;
            }

            id = tag.StartsWith(PinTagPrefix, StringComparison.Ordinal) ? tag[PinTagPrefix.Length..] : tag;
            if (IsPinnable(id, DeviceSnapshot))
            {
                return true;
            }
        }

        id = "";
        return false;

        DeviceOverlaySnapshot? DeviceSnapshot()
        {
            if (snapshotRead)
            {
                return snapshot;
            }

            snapshot = _deviceBridge?.Snapshot();
            snapshotRead = true;
            return snapshot;
        }
    }

    /// <summary>
    ///     Gamepad secondary action (X): the context menu of a focused tray
    ///     icon, otherwise pin/unpin the focused row. Logged either way — this is
    ///     remote-diagnosis territory.
    /// </summary>
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

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
    /// <summary>Every pinnable XAML row, by its stable id (the CardButton's Tag).
    /// Device rows are not here: they are rebuilt from the snapshot on every render.</summary>
    private readonly Dictionary<string, CardButton> _pinnable = new(StringComparer.Ordinal);

    /// <summary>Live mirrors on the Quick access root: each clone follows its source row's
    /// title, description, badge and visibility through the source's property changes, and
    /// presses through to the source's Click handlers.</summary>
    private readonly List<(CardButton Source, EventHandler<AvaloniaPropertyChangedEventArgs> Handler)> _pinMirrors = [];

    private IReadOnlyList<string> _pins = [];

    /// <summary>The Tag prefix that marks a Quick access clone; X on one unpins.</summary>
    private const string PinTagPrefix = "pin:";

    /// <summary>False until the first SetPins, so restoring stored pins never toasts.</summary>
    private bool _pinsInitialized;
    private DispatcherTimer? _pinToastTimer;

    private void IndexPinnableRows()
    {
        foreach (var button in this.GetLogicalDescendants().OfType<CardButton>())
        {
            if (button.Tag is string id && id.Length > 0 && !id.StartsWith(PinTagPrefix, StringComparison.Ordinal))
            {
                _pinnable[id] = button;
            }
        }
    }

    /// <summary>Rebuilds the Quick access root from the persisted pin list. Ids this
    /// build cannot resolve are skipped (kept in the config for the build or device that
    /// can).</summary>
    /// <param name="ids">The pinned row ids in display order.</param>
    internal void SetPins(IReadOnlyList<string> ids)
    {
        IReadOnlyList<string>? previous = _pinsInitialized ? _pins : null;
        _pins = ids;
        _pinsInitialized = true;
        RenderPins(preserveEditing: false);
        if (previous is not null && ids.Count != previous.Count)
        {
            ShowPinToast(added: ids.Count > previous.Count);
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

    private void RenderPins(bool preserveEditing = true)
    {
        if (_closed)
        {
            return;
        }

        if (preserveEditing && (IsEditingValueIn(PinnedGrid) || IsEditingValueIn(PinnedSectionsGrid))
            && PinnedSectionProvidersAvailable())
        {
            var snapshot = _deviceBridge?.Snapshot();
            foreach (var slider in PinnedSectionsGrid.GetLogicalDescendants().OfType<DeviceSliderRow>())
            {
                string key = (slider.Tag as string ?? "")[PinTagPrefix.Length..];
                if (key.StartsWith("performance.", StringComparison.Ordinal)
                    && _performanceSource?.Snapshot() is { Visible: true } performance
                    && performance.ProfileRows.Concat(performance.Rows).FirstOrDefault(row => "performance." + row.Id == key)
                        is { Range: { } range, Value: { } value } descriptor)
                {
                    slider.RefreshReadback(range.Minimum, range.Maximum, range.Step, value, descriptor.CanInvoke);
                    continue;
                }
                var capability = snapshot?.Capabilities.FirstOrDefault(item => DeviceRowKey(item) == key);
                slider.RefreshReadback(capability?.Minimum ?? 0, capability?.Maximum ?? 0, capability?.Step ?? 1,
                    capability?.CurrentValue?.IntegerValue ?? 0, snapshot?.Visible is true && capability is not null
                        && RendersAsSlider(capability) && capability.CanInvoke);
            }
            return;
        }
        string? focusedKey = CurrentSemanticFocusKey();
        Control? restoreFocus = null;
        ReleasePinMirrors();
        PinnedGrid.Children.Clear();
        List<Control> valueControls = [];
        foreach (var id in _pins)
        {
            Control? row = _pinnable.TryGetValue(id, out var source)
                ? CreatePinMirror(id, source)
                : CreatePinnedSection(id);
            if (row is null)
            {
                continue;
            }
            row.Margin = new Thickness(0, 0, 10, 10);
            if (row is CardButton) { PinnedGrid.Children.Add(row); }
            else { valueControls.Add(row); }
            if (string.Equals(row.Tag as string, focusedKey, StringComparison.Ordinal))
            {
                restoreFocus = row;
            }
        }
        // Host controls own subscriptions and user selections. Keep them attached across
        // telemetry refreshes; removing a pin is what ends that view's lifetime.
        foreach (var stale in PinnedSectionsGrid.Children.Where(row => !valueControls.Contains(row)).ToArray())
        {
            PinnedSectionsGrid.Children.Remove(stale);
        }
        for (int i = 0; i < valueControls.Count; i++)
        {
            int existing = PinnedSectionsGrid.Children.IndexOf(valueControls[i]);
            if (existing < 0) { PinnedSectionsGrid.Children.Insert(i, valueControls[i]); }
            else if (existing != i) { PinnedSectionsGrid.Children.Move(existing, i); }
        }
        // Once sections are present their headers explain pinning. Avoid an empty
        // action row above a front page containing only sections.
        CardButton ghost = new()
        {
            IconGeometry = Icons.Pin,
            Title = "Pin a section",
            Description = "Use Pin section in a heading to show its controls here",
            IsEnabled = false,
            Margin = new Thickness(0, 0, 10, 10),
        };
        ghost.Classes.Add("tile");
        ghost.Classes.Add("ghost");
        if (valueControls.Count == 0) { PinnedGrid.Children.Add(ghost); }
        PinnedGrid.IsVisible = PinnedGrid.Children.Count > 0;
        UpdatePinnedIndicators();
        if (restoreFocus is null && PanelQuickAccess.IsVisible && !AnySubView && focusedKey is not null)
        {
            restoreFocus = PinnedSectionsGrid.GetLogicalDescendants().OfType<Control>()
                .FirstOrDefault(control => control.Focusable && Equals(control.Tag, focusedKey));
        }
        Log.Change("overlay.pins", $"Quick access pins: {PinnedGrid.Children.Count + PinnedSectionsGrid.Children.Count - (valueControls.Count == 0 ? 1 : 0)} of {_pins.Count} rendered.");
        if (restoreFocus is not null)
        {
            if (restoreFocus.Focusable) { restoreFocus.Focus(NavigationMethod.Directional); }
            else { FocusFirstControl(restoreFocus); }
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
    /// Device and performance rows are rebuilt from snapshots, so this deliberately walks the
    /// current logical tree instead of retaining references to those short-lived controls. Quick
    /// access mirrors use a prefixed tag and remain unmarked: the icon is the immediate feedback at
    /// the source row, where the user pressed X or held the card.
    /// </remarks>
    private void UpdatePinnedIndicators()
    {
        var pinned = _pins.ToHashSet(StringComparer.Ordinal);
        foreach (var header in this.GetLogicalDescendants().OfType<SectionPinHeader>()) { header.Refresh(pinned.Contains(header.SectionId)); }
        foreach (CardButton button in this.GetLogicalDescendants().OfType<CardButton>())
        {
            if (button is not PluginWidgetPinControls) { button.IsPinned = IsOriginalPinnedRow(button.Tag, pinned); }
        }
    }

    /// <summary>Determines whether a tagged card is an original row in the active pin set.</summary>
    internal static bool IsOriginalPinnedRow(object? tag, IReadOnlySet<string> pinned)
        => tag is string id
            && !id.StartsWith(PinTagPrefix, StringComparison.Ordinal)
            && pinned.Contains(id);

    private CardButton CreatePinMirror(string id, CardButton source)
    {
        var clone = new CardButton { Tag = PinTagPrefix + id };
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
    }

    private static void MirrorPinnedRow(CardButton clone, CardButton source)
    {
        clone.Title = source.Title;
        clone.Description = source.Description;
        clone.IconGeometry = source.IconGeometry;
        clone.TrailingText = source.TrailingText;
        clone.TrailingGlyph = source.TrailingGlyph;
        clone.StatusBrush = source.StatusBrush;
        clone.SwatchBrush = source.SwatchBrush;
        // The source's OWN IsVisible (bound to a feature flag), not its effective one —
        // the source panel is hidden whenever the Quick access root shows.
        clone.IsVisible = source.IsVisible;
        clone.IsEnabled = source.IsEnabled;
    }

    private static string DeviceRowKey(DeviceOverlayCapability capability)
        => capability.InstanceId is { Length: > 0 }
            ? $"{capability.CapabilityId}#{capability.InstanceId}"
            : capability.CapabilityId;

    /// <summary>Static actions and whole sections are the pin targets.</summary>
    private bool IsPinnable(string id) => _pinnable.ContainsKey(id) || _controlPinFactories.ContainsKey(id)
        || id == "section.performance" && _performanceSource?.Snapshot().Visible is true
        || _pins.Contains(id) && id.StartsWith("section.", StringComparison.Ordinal)
        || _deviceBridge?.Snapshot() is { } snapshot && DevicePinSections(snapshot).Any(section => section.Id == id);

    /// <summary>Resolves the row id a row or its Quick access mirror stands for.</summary>
    private bool TryGetPinId(Control? control, out string id)
    {
        for (Visual? node = control; node is not null; node = node.GetVisualParent())
        {
            if (node is not Control { Tag: string tag }) { continue; }
            id = tag.StartsWith(PinTagPrefix, StringComparison.Ordinal) ? tag[PinTagPrefix.Length..] : tag;
            if (IsPinnable(id)) { return true; }
        }
        id = "";
        return false;
    }

    /// <summary>Gamepad secondary action (X): the context menu of a focused tray
    /// icon, otherwise pin/unpin the focused row. Logged either way — this is
    /// remote-diagnosis territory.</summary>
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
        Log.Info($"Gamepad X: focused element is not pinnable ({focused?.GetType().Name ?? "none"}, tag {(focused as Control)?.Tag ?? "-"}).");
    }

    private void OnHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started)
        {
            return;
        }
        var row = e.Source as Control;
        if (TryGetPinId(row, out var id))
        {
            e.Handled = true;
            Log.Info($"Touch hold: toggling pin '{id}'.");
            PinToggleRequested?.Invoke(id);
        }
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
        if (TryGetPinId(row, out var id))
        {
            e.Handled = true;
            PinToggleRequested?.Invoke(id);
        }
    }
}

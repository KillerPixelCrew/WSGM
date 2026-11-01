using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Tools › Hotkeys: key combinations the Overlay sends to the game in front, such as Insert for OptiScaler's
///     menu, with their editor and their Quick access tiles. <see cref="IHotkeysSource" /> owns the list and the
///     sends; this page keeps only the editor drafts.
/// </summary>
/// <remarks>
///     An editor works on a draft that survives Back and publications until it is saved or discarded, so a
///     half-made hotkey is not lost to a stray B press. Pressing a hotkey asks the controller to close the
///     Overlay and send it.
/// </remarks>
public sealed class HotkeysView : ServiceSubView
{
    // The press delays offered; a stored value outside them is offered too.
    private static readonly int[] Delays = [0, 20, 50, 100, 250, 500, 1000, 2000, HotkeyDefinition.MaximumPressDelayMs];

    // Editor drafts by hotkey id, including hotkeys not saved yet.
    private readonly Dictionary<string, HotkeyDefinition> _drafts = new(StringComparer.Ordinal);

    // What each new hotkey's editor opened with, so one left untouched is not kept as a draft.
    private readonly Dictionary<string, HotkeyDefinition> _seeds = new(StringComparer.Ordinal);
    private HashSet<string> _pins = new(StringComparer.Ordinal);
    private IHotkeysSource? _service;

    /// <inheritdoc />
    protected override string LogScope => "Hotkeys";

    /// <summary>Creates a pinnable heading that follows the pin list; set by the window that hosts the page.</summary>
    internal Func<string, string, Control>? CreateHeader { get; set; }

    /// <summary>Raised with a hotkey to send: the controller closes the Overlay, then sends it.</summary>
    internal event Action<string>? SendRequested;

    /// <summary>Raised with a hotkey's pin id to pin or unpin it.</summary>
    internal event Action<string>? PinToggleRequested;

    /// <summary>Raised with a deleted hotkey's pin id so a pinned tile goes with it.</summary>
    internal event Action<string>? UnpinRequested;

    /// <summary>Raised on the service's thread when the hotkeys change, so their tiles follow.</summary>
    internal event Action? HotkeysChanged;

    /// <summary>Follows the session's hotkeys, or none with null.</summary>
    /// <param name="service">The owner, or null when the Overlay closes or the session has none.</param>
    internal void Attach(IHotkeysSource? service)
    {
        if (_service is not null)
        {
            _service.Changed -= OnHotkeysChanged;
        }

        _service = service;
        if (service is not null)
        {
            service.Changed += OnHotkeysChanged;
        }

        AttachSource(IsEffectivelyVisible ? service : null);
    }

    /// <summary>Takes the Quick access pin list, so each hotkey offers Pin or Unpin.</summary>
    /// <param name="ids">The pinned ids.</param>
    internal void SetPins(IReadOnlyList<string> ids)
    {
        _pins = ids.ToHashSet(StringComparer.Ordinal);
        if (IsEffectivelyVisible)
        {
            CurrentLevel?.Invoke();
        }
    }

    /// <summary>Resolves a pin id that names a hotkey.</summary>
    /// <param name="pin">The pin id.</param>
    /// <returns>The hotkey's Quick access tile, or null for an id this source does not own.</returns>
    internal QuickAccessPin? ResolvePin(string pin)
    {
        if (!pin.StartsWith(HotkeysService.PinPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = pin[HotkeysService.PinPrefix.Length..];
        var tag = QuickAccessPins.TagPrefix + pin;
        var state = _service?.ReadState().FirstOrDefault(item => item.Definition.Id == id);
        if (state is null)
        {
            return new QuickAccessPin("Hotkey unavailable", false, true, drawn => drawn as ActionButton ?? Tile(
                new ActionButton
                {
                    Tag = tag,
                    Title = "Hotkey unavailable",
                    Description = "X, touch hold or right-click to unpin",
                    IconGeometry = Icons.Keyboard,
                    IsEnabled = true
                }));
        }

        return new QuickAccessPin(state.Definition.Name, true, true, drawn =>
        {
            if (drawn is HotkeyActionRow existing)
            {
                existing.Apply(state);
                return existing;
            }

            return Tile(new HotkeyActionRow(state, () => SendRequested?.Invoke(id), true) { Tag = tag });
        });
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            AttachSource(IsVisible ? _service : null);
        }
    }

    /// <inheritdoc />
    private protected override void RenderHome()
    {
        var body = NewStack("Hotkeys");
        if (_service is not { } service)
        {
            body.Children.Add(Caption("Hotkeys are unavailable in this session."));
            SetContent(body);
            return;
        }

        body.Children.Add(Tagged(Caption(
                "Send a key combination to the game in front, such as Insert for OptiScaler's menu. The Overlay "
                + "closes first, then WSGM presses the keys. Pin a hotkey to send it from Quick access."),
            "hotkeys.intro"));
        body.Children.Add(Tagged(PrimaryRow("New hotkey", "Name it and choose its keys", Icons.Keyboard,
            () => OpenNew("New hotkey", [])), "hotkeys.new"));
        var states = service.ReadState();
        ForgetUnchangedDrafts(states);
        foreach (var draft in _drafts.Values.Where(draft => states.All(state => state.Definition.Id != draft.Id)))
        {
            var id = draft.Id;
            body.Children.Add(Tagged(Row("Finish " + draft.Name, "Not saved yet · " + HotkeyKeys.Describe(draft.Keys),
                Icons.ListLines, () => Navigate(() => RenderEditor(id))), "hotkey.draft:" + id));
        }

        if (states.Length == 0)
        {
            body.Children.Add(Tagged(Caption("No hotkeys. Create one with New hotkey."), "hotkeys.empty"));
        }
        else
        {
            body.Children.Add(Tagged(SectionLabel("Your hotkeys"), "hotkeys.heading"));
            foreach (var state in states)
            {
                body.Children.Add(Item(state));
            }
        }

        SetContent(body);
    }

    /// <summary>One hotkey on the page: the hotkey itself, which sends it, then Pin and Edit.</summary>
    private Grid Item(HotkeyState state)
    {
        var id = state.Definition.Id;
        var pin = HotkeysService.PinPrefix + id;
        var pinned = _pins.Contains(pin);
        var name = state.Definition.Name;
        var shown = state.Notice.Length == 0 && _drafts.ContainsKey(id)
            ? state with { Notice = "Unsaved changes in the editor" }
            : state;
        var row = new HotkeyActionRow(shown, () => SendRequested?.Invoke(id)) { Tag = pin, IsPinned = pinned };
        var pinCommand = ItemCommand("hotkey.pin:" + id, pinned ? "Unpin" : "Pin", Icons.Pin,
            (pinned ? "Unpin " : "Pin ") + name, () => PinToggleRequested?.Invoke(pin));
        var edit = ItemCommand("hotkey.edit:" + id, "Edit", Icons.Gear, "Edit " + name, () => OpenEditor(id));
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8,
            Tag = "hotkey.item:" + id
        };
        grid.Children.Add(row);
        Grid.SetColumn(pinCommand, 1);
        grid.Children.Add(pinCommand);
        Grid.SetColumn(edit, 2);
        grid.Children.Add(edit);
        return grid;
    }

    private static ActionButton ItemCommand(string tag, string title, Geometry icon, string name, Action onClick)
    {
        var command = Row(title, "", icon, onClick);
        command.Tag = tag;
        command.AccessibleName = name;
        // One width for Pin and Unpin keeps the columns aligned down the list.
        command.Width = 116;
        command.VerticalAlignment = VerticalAlignment.Stretch;
        return command;
    }

    private void OpenNew(string name, IReadOnlyList<int> keys)
    {
        var draft = new HotkeyDefinition { Name = name, Keys = [.. keys] };
        _drafts[draft.Id] = draft;
        _seeds[draft.Id] = draft.Copy();
        Navigate(() => RenderEditor(draft.Id));
    }

    private void OpenEditor(string id)
    {
        if (!_drafts.ContainsKey(id) && Saved(id) is { } saved)
        {
            _drafts[id] = saved.Definition.Copy();
        }

        Navigate(() => RenderEditor(id));
    }

    /// <summary>Drops drafts with nothing to keep: an edit equal to the saved hotkey, an untouched new one.</summary>
    private void ForgetUnchangedDrafts(IReadOnlyList<HotkeyState> states)
    {
        foreach (var (id, draft) in _drafts.ToArray())
        {
            var saved = states.FirstOrDefault(state => state.Definition.Id == id)?.Definition;
            var original = saved ?? _seeds.GetValueOrDefault(id);
            if (original is not null && Same(original, draft))
            {
                Forget(id);
            }
        }
    }

    private void Forget(string id)
    {
        _drafts.Remove(id);
        _seeds.Remove(id);
    }

    private HotkeyState? Saved(string id)
    {
        return _service?.ReadState().FirstOrDefault(state => state.Definition.Id == id);
    }

    private void RenderEditor(string id)
    {
        if (_service is null || !_drafts.TryGetValue(id, out var draft))
        {
            RenderMessage("Hotkey removed", "This hotkey no longer exists. Back returns to the list.");
            return;
        }

        var saved = Saved(id);
        var body = NewStack("");
        body.Children.Add(Tagged(Caption("Hotkeys  ›  " + (saved?.Definition.Name ?? draft.Name)), "editor.crumb"));
        body.Children.Add(saved is not null && CreateHeader is { } header
            ? header(HotkeysService.PinPrefix + id, saved.Definition.Name)
            : new TextBlock
            {
                Text = saved is null ? "New hotkey" : saved.Definition.Name, FontSize = 20,
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
            });
        if (saved is { Notice.Length: > 0 })
        {
            body.Children.Add(new FAInfoBar
            {
                Tag = "editor.notice", IsOpen = true, IsClosable = false, IsIconVisible = true,
                Severity = FAInfoBarSeverity.Warning, Title = "Last press not sent", Message = saved.Notice,
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
        }

        body.Children.Add(Preview(draft));
        body.Children.Add(Tagged(Row("Name", draft.Name, Icons.ListLines,
                () => EditText("Hotkey name", draft.Name, 80,
                    value => Update(id,
                        current => current with { Name = value.Trim().Length == 0 ? current.Name : value.Trim() }))),
            "editor.name"));
        body.Children.Add(Tagged(SectionLabel("Keys"), "editor.keys.heading"));
        body.Children.Add(Tagged(CommandStrip([
            .. HotkeyKeys.Modifiers.Select(modifier =>
            {
                var on = draft.Keys.Contains(modifier);
                var label = HotkeyKeys.Label(modifier);
                var command = Command("editor.modifier:" + label, on ? "✓ " + label : label,
                    () => Update(id, current => current with
                    {
                        Keys = HotkeyKeys.WithModifier(current.Keys, modifier, !current.Keys.Contains(modifier))
                    }), on);
                command.AccessibleName = label + (on ? ", on" : ", off");
                return (Control)command;
            })
        ]), "editor.modifiers"));
        var others = draft.Keys.Where(key => !HotkeyKeys.IsModifier(key)).ToArray();
        body.Children.Add(Tagged(Row("Key",
            others.Length == 0 ? "Choose the key to press" : HotkeyKeys.Describe(others),
            Icons.Keyboard, () => Navigate(() => RenderKeyPicker(id))), "editor.key"));
        body.Children.Add(Tagged(Row("Press the keys…",
            "On a keyboard: hold the combination, then let go", Icons.Keyboard, () => Capture(id)), "editor.capture"));
        body.Children.Add(Tagged(ChoiceRow("Hold keys for",
                [.. Delays.Append(draft.PressDelayMs).Distinct().Order().Select(delay => (delay, DelayLabel(delay)))],
                draft.PressDelayMs, delay => Update(id, current => current with { PressDelayMs = delay })),
            "editor.delay"));
        body.Children.Add(Tagged(Caption(
                "Some games miss a press that is released at once. Hold the keys longer if the game does not react."),
            "editor.delay.caption"));

        var changed = saved is null || !Same(saved.Definition, draft);
        body.Children.Add(Tagged(PrimaryRow(saved is null ? "Save hotkey" : "Save changes",
                draft.Keys.Count == 0 ? "Choose at least one key first" : "Sends " + HotkeyKeys.Describe(draft.Keys),
                Icons.Pin, draft.Keys.Count == 0 || !changed ? null : () => _ = RunSafelyAsync(SaveAsync(id), "save")),
            "editor.save"));
        if (changed)
        {
            body.Children.Add(Tagged(Row(saved is null ? "Discard this hotkey" : "Discard changes",
                saved is null ? "Nothing was saved" : "Keep the saved keys", Icons.ArrowLeft, () =>
                {
                    Forget(id);
                    Back();
                }), "editor.discard"));
        }

        if (saved is not null)
        {
            body.Children.Add(Tagged(DangerRow("Delete hotkey", "Removes it and its Quick access tile", Icons.Close,
                () => ConfirmDelete(id, saved.Definition.Name)), "editor.delete"));
        }

        SetContent(body);
    }

    private static Border Preview(HotkeyDefinition draft)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(draft.Keys.Count == 0
            ? Caption("No keys yet. Turn on the modifiers and choose a key, or press the keys on a keyboard.")
            : Keycaps.Create(draft.Keys, true));
        stack.Children.Add(Caption(draft.PressDelayMs == 0
            ? "Pressed and released at once."
            : "Held for " + DelayLabel(draft.PressDelayMs) + ", then released."));
        return new Border { Child = stack, Classes = { "device-group" }, Tag = "editor.preview" };
    }

    private void RenderKeyPicker(string id)
    {
        if (!_drafts.TryGetValue(id, out var draft))
        {
            RenderMessage("Hotkey removed", "This hotkey no longer exists. Back returns to the list.");
            return;
        }

        var body = NewStack("Choose a key");
        body.Children.Add(Tagged(Caption(
            "The key pressed with the modifiers. Ctrl, Alt, Shift and Win are switches in the editor."), "keys.intro"));
        int? selected = null;
        foreach (var (group, keys) in HotkeyKeys.Groups)
        {
            body.Children.Add(Tagged(SectionLabel(group), "keys.group:" + group));
            body.Children.Add(Tagged(CommandStrip([
                .. keys.Select(key =>
                {
                    var on = draft.Keys.Contains(key.Key);
                    if (on)
                    {
                        selected ??= key.Key;
                    }

                    var value = key.Key;
                    return (Control)Command("key:" + value, key.Label, () =>
                    {
                        // Back draws the editor with the new key.
                        Update(id, current => current with { Keys = HotkeyKeys.WithKey(current.Keys, value) }, false);
                        Back();
                    }, on);
                })
            ]), "keys.strip:" + group));
        }

        SetStaticContent(body);
        if (selected is { } current)
        {
            // Land on the key already chosen rather than the first one.
            Dispatcher.UIThread.Post(() => FindTagged(body, "key:" + current)?.Focus(NavigationMethod.Directional));
        }
    }

    private void Capture(string id)
    {
        if (TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        var field = new HotkeyCapture { Tag = "capture.field" };
        var content = new StackPanel { Spacing = 14, Tag = "capture" };
        content.Children.Add(new TextBlock
        {
            Text = "Hold the combination on a keyboard, then let go. Esc or B cancels.",
            TextWrapping = TextWrapping.Wrap, LineHeight = 22
        });
        content.Children.Add(field);
        var cancel = Command("capture.cancel", "Cancel", () => window.Surfaces.CloseActive());
        cancel.MinWidth = 120;
        cancel.Padding = new Thickness(16, 6);
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        content.Children.Add(cancel);
        field.Captured += keys =>
        {
            Update(id, current => current with { Keys = [.. keys] });
            window.Surfaces.CloseActive();
        };
        window.Surfaces.ShowConfirm(content, "Press the keys", field);
    }

    private void ConfirmDelete(string id, string name)
    {
        if (TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        var content = new StackPanel { Spacing = 14, Tag = "confirm" };
        content.Children.Add(new TextBlock
        {
            Text = "WSGM forgets this hotkey and removes its Quick access tile. The game and its own key bindings "
                   + "are not changed.",
            TextWrapping = TextWrapping.Wrap, LineHeight = 22
        });
        var cancel = Command("confirm.cancel", "Cancel", () => window.Surfaces.CloseActive());
        var delete = Command("confirm.accept", "Delete", () =>
        {
            window.Surfaces.CloseActive();
            _ = RunSafelyAsync(DeleteAsync(id), "delete");
        });
        delete.Classes.Add("danger");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        };
        foreach (var button in new[] { cancel, delete })
        {
            button.MinWidth = 120;
            button.Padding = new Thickness(16, 6);
            buttons.Children.Add(button);
        }

        content.Children.Add(buttons);
        window.Surfaces.ShowConfirm(content, "Delete “" + name + "”?", cancel);
    }

    private async Task SaveAsync(string id)
    {
        if (_service is not { } service || !_drafts.TryGetValue(id, out var draft))
        {
            return;
        }

        var generation = NavigationGeneration;
        try
        {
            await service.SaveAsync(draft.Copy());
        }
        catch (InvalidOperationException ex)
        {
            if (generation == NavigationGeneration)
            {
                Toast(ex.Message);
            }

            return;
        }

        Forget(id);
        if (generation == NavigationGeneration)
        {
            Back();
            FocusItem(id);
        }
    }

    private async Task DeleteAsync(string id)
    {
        if (_service is not { } service)
        {
            return;
        }

        var generation = NavigationGeneration;
        UnpinRequested?.Invoke(HotkeysService.PinPrefix + id);
        await service.RemoveAsync(id);
        Forget(id);
        if (generation == NavigationGeneration)
        {
            Back();
        }
    }

    private void Update(string id, Func<HotkeyDefinition, HotkeyDefinition> change, bool redraw = true)
    {
        if (_drafts.TryGetValue(id, out var draft))
        {
            _drafts[id] = change(draft.Copy());
        }

        if (redraw)
        {
            CurrentLevel?.Invoke();
        }
    }

    private void FocusItem(string id)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Content is Control root && FindTagged(root, HotkeysService.PinPrefix + id) is { } row)
            {
                row.Focus(NavigationMethod.Directional);
            }
        });
    }

    private static Control? FindTagged(Control root, string tag)
    {
        return root.GetLogicalDescendants().OfType<Control>()
            .FirstOrDefault(control => Equals(control.Tag, tag) && control.Focusable);
    }

    private static bool Same(HotkeyDefinition saved, HotkeyDefinition draft)
    {
        return saved.Name == draft.Name && saved.PressDelayMs == draft.PressDelayMs
                                        && saved.Keys.SequenceEqual(draft.Keys);
    }

    private static string DelayLabel(int milliseconds)
    {
        return milliseconds switch
        {
            0 => "Instant",
            >= 1000 when milliseconds % 1000 == 0 => milliseconds / 1000 + " s",
            _ => milliseconds + " ms"
        };
    }

    private static T Tile<T>(T tile) where T : ActionButton
    {
        tile.Classes.Set("tile", true);
        tile.Margin = new Thickness(0, 0, 10, 10);
        return tile;
    }

    private void OnHotkeysChanged()
    {
        HotkeysChanged?.Invoke();
    }
}

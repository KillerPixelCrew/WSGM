using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace WSGM.Overlay;

/// <summary>
///     The user's saved power presets in a focused utility: each one's name and values with its Rename, Update
///     and Delete commands. It follows the shared selection, so a change made in Steam shows here too.
/// </summary>
internal sealed class DevicePowerSavedPresetsView : UserControl
{
    private readonly StackPanel _items = new() { Spacing = 10 };
    private readonly DevicePowerPresetSelection _model;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };

    /// <summary>Draws the saved presets and follows the selection until the utility closes.</summary>
    /// <param name="model">The open overlay's shared preset selection.</param>
    internal DevicePowerSavedPresetsView(DevicePowerPresetSelection model)
    {
        _model = model;
        MinWidth = 420;
        MaxWidth = 640;
        Content = new StackPanel { Spacing = 12, Children = { _status, _items } };
        model.Changed += Render;
        DetachedFromVisualTree += (_, _) => model.Changed -= Render;
        Render();
    }

    /// <summary>The first command, or the status when there is nothing to act on.</summary>
    internal InputElement? DefaultFocusTarget =>
        _items.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.IsEnabled);

    private void Render()
    {
        var state = _model.State;
        var saved = state.Saved ?? [];
        var focusedTag = (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control)?.Tag;
        _status.Text = _model.Busy ? "Saving…" : _model.LastError ?? string.Empty;
        _status.IsVisible = _status.Text.Length > 0;
        _items.Children.Clear();
        if (saved.Count == 0)
        {
            _items.Children.Add(new TextBlock
            {
                Text = "No saved presets. When the active profile reads Custom, Save as preset keeps those values "
                       + "under a name of your own.",
                TextWrapping = TextWrapping.Wrap,
                Tag = "device.power-preset.saved.empty"
            });
        }

        foreach (var item in saved)
        {
            var commands = new WrapPanel { Orientation = Orientation.Horizontal };
            commands.Children.Add(Command(item.Id + ":rename", "Rename…", () => Rename(item.Id, item.Name)));
            if (state.CanSave)
            {
                commands.Children.Add(Command(item.Id + ":update", "Update…",
                    () => ConfirmUpdate(item.Id, item.Name, state.CurrentValues)));
            }

            var delete = Command(item.Id + ":delete", "Delete…", () => ConfirmDelete(item.Id, item.Name));
            delete.Classes.Add("danger");
            commands.Children.Add(delete);
            _items.Children.Add(new Border
            {
                Classes = { "tile" },
                Tag = "device.power-preset.saved:" + item.Id,
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = item.Name, TextWrapping = TextWrapping.Wrap, Classes = { "setting-title" }
                        },
                        new TextBlock
                        {
                            Text = item.Active ? item.Detail + " · Active" : item.Detail,
                            TextWrapping = TextWrapping.Wrap, Classes = { "caption" }
                        },
                        commands
                    }
                }
            });
        }

        // Rebuilt controls take focus back where it was, or the first command after a delete. Focus held by a
        // surface over this one, such as the keyboard or a confirmation, is left where it is.
        Dispatcher.UIThread.Post(() =>
        {
            var manager = TopLevel.GetTopLevel(this)?.FocusManager;
            if (manager is null
                || (manager.GetFocusedElement() is Visual { } current && TopLevel.GetTopLevel(current) is not null))
            {
                return;
            }

            (_items.GetLogicalDescendants().OfType<Button>()
                 .FirstOrDefault(button => button.IsEnabled && Equals(button.Tag, focusedTag))
             ?? DefaultFocusTarget as Control)?.Focus();
        });
    }

    private OverlayActionButton Command(string id, string label, System.Action onClick)
    {
        return new OverlayActionButton
        {
            Title = label,
            Tag = "device.power-preset.saved.command:" + id,
            Activate = onClick,
            IsEnabled = _model.CanEdit,
            MinWidth = 52,
            Padding = new Thickness(6, 6),
            Margin = new Thickness(0, 4, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
    }

    private void Rename(string id, string name)
    {
        if (!OverlayWindow.RequestTextFor(this, "Preset name", name, 0, value => _ = _model.RenameAsync(id, value)))
        {
            _status.Text = "Keyboard unavailable. Reopen the overlay to retry.";
            _status.IsVisible = true;
        }
    }

    private void ConfirmUpdate(string id, string name, string values)
    {
        Confirm("Update “" + name + "”?",
            "Its values become the current ones (" + values + "). An assignment that uses it keeps it and applies "
            + "the new values the next time it applies.", "Update", false, () => _ = _model.UpdateAsync(id));
    }

    private void ConfirmDelete(string id, string name)
    {
        Confirm("Delete “" + name + "”?",
            "A plugged-in or battery assignment that uses it keeps its values as Custom, so nothing on the device "
            + "changes.", "Delete", true, () => _ = _model.DeleteAsync(id));
    }

    // The centred in-window confirmation with the safe answer focused, as every overlay confirmation is.
    private void Confirm(string title, string text, string accept, bool danger, System.Action onAccept)
    {
        if (TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        var content = new StackPanel { Spacing = 14, Tag = "confirm" };
        content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        var cancel = new OverlayActionButton
        {
            Title = "Cancel", Tag = "confirm.cancel", Activate = () => window.Surfaces.CloseActive(), MinWidth = 120,
            Padding = new Thickness(16, 6)
        };
        var confirm = new OverlayActionButton
        {
            Title = accept, Tag = "confirm.accept", MinWidth = 120, Padding = new Thickness(16, 6),
            Activate = () =>
            {
                window.Surfaces.CloseActive();
                onAccept();
            }
        };
        confirm.Classes.Add(danger ? "danger" : "primary");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0), Children = { cancel, confirm }
        };
        content.Children.Add(buttons);
        window.Surfaces.ShowConfirm(content, title, cancel);
    }
}

using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using WSGM.Controls;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Tools › Display › Custom resolutions: the GPU driver's resolution list for each display, with the same
///     service and operations as the WSGM section of Steam's Display settings. Removing a size, restoring the
///     original list and restarting the graphics driver each ask first.
/// </summary>
/// <remarks>
///     Confirmations use the centred in-window confirm surface with Cancel focused. One editor serves the window's lifetime, so leaving the page never cancels a driver write that is under way;
///     the page re-reads when it opens and redraws in place, keeping focus on the same row, as the editor reports.
/// </remarks>
public sealed class CustomResolutionsView : OverlaySubView
{
    private const string Title = "Custom resolutions";
    private ScaledResolutionEditor? _editor;
    private bool _opening;
    private bool _redrawQueued;

    /// <inheritdoc />
    protected override string LogScope => "Custom resolutions";

    /// <summary>Whether a graphics owner is attached, so Tools › Display offers this page.</summary>
    internal bool Available => _editor is not null;

    /// <summary>Uses the session's editor; the page reads the driver each time it opens.</summary>
    /// <param name="editor">The editor over the shared custom-resolution service.</param>
    internal void Attach(ScaledResolutionEditor editor)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorChanged;
        }

        _editor = editor;
        editor.PropertyChanged += OnEditorChanged;
    }

    /// <inheritdoc />
    public override void Open()
    {
        NavigationStack.Clear();
        CurrentLevel = null;
        _opening = true;
        if (_editor is not { } editor)
        {
            Navigate(() => RenderMessage(Title, "Custom resolutions need the graphics integration, which is not running."));
            return;
        }

        Navigate(RenderList);
        if (!editor.Busy)
        {
            editor.RefreshCommand.Execute(null);
        }
    }

    /// <summary>Redraws the list in place once per burst of editor changes, keeping the focused row.</summary>
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_redrawQueued)
        {
            return;
        }

        _redrawQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _redrawQueued = false;
            if (CurrentLevel == RenderList)
            {
                RenderList();
            }
        }, DispatcherPriority.Background);
    }

    private void RenderList()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        var stack = NewStack(Title);
        stack.Children.Add(Caption(
            "The GPU driver's list of sizes a game or Windows can choose. Your active resolution stays the same, and "
            + "the native resolution is protected."));
        if (editor.Displays.Count > 1)
        {
            stack.Children.Add(ChoiceRow("Display",
                editor.Displays.Select(display => (display.Path, display.Name)).ToArray(),
                editor.Display?.Path ?? "",
                path => editor.Display = editor.Displays.FirstOrDefault(display => display.Path == path)));
        }

        stack.Children.Add(Caption(editor.Displays.Count == 1
            ? $"{editor.Displays[0].Name} · {editor.AdapterText}"
            : editor.AdapterText));
        if (editor.NeedsRestart)
        {
            stack.Children.Add(Banner("resolutions.activation", FAInfoBarSeverity.Warning, null,
                editor.ActivationNotice));
        }

        if (!editor.CanEdit)
        {
            stack.Children.Add(Banner("resolutions.unavailable", FAInfoBarSeverity.Informational,
                editor.Busy ? "Reading the display" : "Resolutions unavailable", editor.Status));
            stack.Children.Add(Keyed(Row("Refresh", "Read the displays and their driver again", Icons.Restart,
                WhenIdle(editor, () => editor.RefreshCommand.Execute(null))), "resolutions.refresh"));
            SetContent(stack);
            return;
        }

        stack.Children.Add(SectionLabel("Add"));
        stack.Children.Add(Keyed(PrimaryRow("Add a resolution", "Type a width × height, such as 1760 × 1100",
            Icons.FolderPlus, WhenIdle(editor, () => EditText("Resolution in pixels (width × height)", "", 13, size =>
            {
                editor.Size = size;
                if (editor.CanAdd)
                {
                    editor.AddCommand.Execute(null);
                }
                else
                {
                    Toast("Enter width × height, for example 1760 × 1100. Each side is 100 to 15999 pixels.");
                }
            }))), "resolutions.add"));
        stack.Children.Add(Keyed(Row("Add 5% steps", "Native 100% down to 50%. Sizes already listed stay.",
            Icons.ListLines, editor.CanAddSteps ? WhenIdle(editor, () => editor.AddStepsCommand.Execute(null)) : null),
            "resolutions.steps"));

        stack.Children.Add(SectionLabel($"Resolutions ({editor.Resolutions.Count})"));
        if (editor.Resolutions.Count == 0)
        {
            stack.Children.Add(Caption("The driver lists no resolutions for this display."));
        }

        foreach (var item in editor.Resolutions)
        {
            // Every size is listed so the user sees the whole list; only the removable ones open their actions.
            var removable = editor.IsRemovable(item);
            stack.Children.Add(Keyed(Row(item.Label, removable ? "Remove…" : "Protected or set by the driver",
                Icons.Monitor, removable ? WhenIdle(editor, () => ConfirmRemove(item)) : null),
                "resolutions.item:" + item.Width + "x" + item.Height));
        }

        stack.Children.Add(SectionLabel("Driver"));
        if (editor.CanRestore)
        {
            stack.Children.Add(Keyed(Row("Restore original list", "Puts back the list saved before your first edit",
                Icons.Restart, WhenIdle(editor, () => Ask("Restore the original list?",
                    "Sizes you added since your first edit are removed from the driver's list.", "Restore",
                    () => editor.RestoreCommand.Execute(null)))), "resolutions.restore"));
        }

        if (editor.HasMissing)
        {
            stack.Children.Add(Keyed(Row("Restore missing resolutions",
                "Adds your sizes again, for example after a driver update", Icons.FolderPlus,
                WhenIdle(editor, () => editor.ReapplyCommand.Execute(null))), "resolutions.reapply"));
        }

        if (editor.NeedsRestart)
        {
            stack.Children.Add(Keyed(DangerRow("Restart graphics driver",
                "Activates changes. Displays may go blank and games may be interrupted.", Icons.Power,
                WhenIdle(editor, () => Ask("Restart the graphics driver?",
                    "This activates your resolution changes. Displays may go blank and games may be interrupted. "
                    + "Save your game first.", "Restart now", () => editor.RestartCommand.Execute(null)))),
            "resolutions.restart"));
        }

        stack.Children.Add(Keyed(Row("Refresh", "Read the displays and their driver again", Icons.Restart,
            WhenIdle(editor, () => editor.RefreshCommand.Execute(null))), "resolutions.refresh"));
        stack.Children.Add(Caption(editor.Busy ? "Working…" : editor.Status));
        SetContent(stack);
    }

    private void ConfirmRemove(ScaledResolutionItem item)
    {
        Ask($"Remove {item.Width} × {item.Height}?",
            "Games and Windows can no longer choose this size on this display. Restore original list puts it back.",
            "Remove", () =>
            {
                _editor!.Resolution = item;
                _editor.RemoveCommand.Execute(null);
            });
    }

    /// <summary>Asks in the centred confirmation surface, with Cancel focused, and runs the operation on yes.</summary>
    private void Ask(string title, string text, string accept, Action operation)
    {
        if (TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        var content = new StackPanel { Spacing = 14, Tag = "confirm" };
        content.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 22
        });
        var cancel = Command("confirm.cancel", "Cancel", () => window.Surfaces.CloseActive());
        var confirm = Command("confirm.accept", accept, () =>
        {
            window.Surfaces.CloseActive();
            operation();
        });
        confirm.Classes.Add("danger");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        };
        foreach (var button in new[] { cancel, confirm })
        {
            button.MinWidth = 120;
            button.Padding = new Thickness(16, 6);
            buttons.Children.Add(button);
        }

        content.Children.Add(buttons);
        window.Surfaces.ShowConfirm(content, title, cancel);
    }

    /// <summary>
    ///     Runs the action when the editor is idle. Rows stay enabled while a change runs, so focus keeps its place;
    ///     a press meanwhile says to wait instead of starting a second driver write.
    /// </summary>
    private Action WhenIdle(ScaledResolutionEditor editor, Action action)
    {
        return () =>
        {
            if (editor.Busy)
            {
                Toast("Wait for the current change to finish.");
                return;
            }

            action();
        };
    }

    private static ActionButton Keyed(ActionButton row, string key)
    {
        row.Tag = key;
        return row;
    }

    /// <summary>
    ///     Replaces the content. Opening the page focuses its first row; a redraw keeps focus on the same row, and
    ///     leaves it alone when it is outside the page, on the rail or in a confirmation.
    /// </summary>
    private protected override void SetContent(StackPanel stack)
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var inside = focused is not null && this.IsVisualAncestorOf(focused);
        var opening = _opening;
        _opening = false;
        Content = stack;
        if (!inside && !opening)
        {
            return;
        }

        var key = inside ? focused!.Tag as string : null;
        Dispatcher.UIThread.Post(() =>
            (FocusSearch.Keyed(stack, key) ?? FocusSearch.FirstNavigable(stack))?.Focus(NavigationMethod.Directional));
    }
}

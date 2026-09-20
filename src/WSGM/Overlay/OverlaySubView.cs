using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Overlay;

/// <summary>
///     Base for the self-drawing, gamepad-driven overlay sub-views (tab builder,
///     card manager, artwork changer, launch wrappers, wake locks): the render-thunk
///     navigation stack, the shared row/label builders, and text entry. Each navigation
///     level rebuilds <see cref="ContentControl.Content" /> with actions and explicit value editors.
///     <para>
///         <see cref="_navigationGeneration" /> is also the invalidation token for
///         asynchronous work: leaving a level bumps it, so a load that completes afterwards
///         discards its result instead of drawing over the level the user moved to.
///     </para>
/// </summary>
public abstract class OverlaySubView : UserControl
{
    // Navigation: a stack of render thunks. Push goes deeper; Back pops.
    private protected readonly Stack<Action> _stack = new();
    private protected Action? _current;
    private protected int _navigationGeneration;

    // One-shot message shown at the top of the next rendered level, then consumed.
    private protected string? _notice;

    /// <summary>Short name used to prefix log lines from this sub-view.</summary>
    protected abstract string LogScope { get; }

    /// <summary>
    ///     Raised when the user backs out of the top level (the overlay then
    ///     returns to the Tools list).
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>
    ///     Asks the host to close this sub-view, for the rows that offer an explicit
    ///     way out rather than waiting for a Back press.
    /// </summary>
    private protected void RequestClose()
    {
        CloseRequested?.Invoke();
    }

    /// <summary>
    ///     Handles a Back/B press: pops one level, or requests close at the top.
    ///     Returns true when it consumed the press.
    /// </summary>
    public bool Back()
    {
        _navigationGeneration++;
        if (_stack.Count == 0)
        {
            CloseRequested?.Invoke();
            return true;
        }

        _current = _stack.Pop();
        _current();
        return true;
    }

    private protected void Navigate(Action render)
    {
        _navigationGeneration++;
        if (_current is not null)
        {
            _stack.Push(_current);
        }

        _current = render;
        render();
    }

    private protected void Replace(Action render)
    {
        _current = render;
        render();
    }

    private protected void PopIfAny()
    {
        if (_stack.Count > 0)
        {
            _stack.Pop();
        }
    }

    private protected async Task RunSafelyAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Log.Error($"{LogScope} {operation} failed.", ex);
        }
    }

    /// <summary>
    ///     Lists the Steam library, degrading to an empty list so a picker renders
    ///     "no games" instead of failing the whole sub-view when Steam cannot answer.
    /// </summary>
    private protected async Task<IReadOnlyList<SteamLibraryApp>> SafeGamesAsync()
    {
        try
        {
            return await SteamLibraryData.ListGamesAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"{LogScope}: could not list games: {ex.Message}");
            return [];
        }
    }

    private protected void Toast(string message)
    {
        Log.Info($"{LogScope}: {message}");
        _notice = message;
        _current?.Invoke();
    }

    // ---- Shared builders ----

    private protected StackPanel NewStack(string heading)
    {
        var stack = new StackPanel { Spacing = 10 };
        if (!string.IsNullOrEmpty(heading))
        {
            stack.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 20,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });
        }

        if (string.IsNullOrEmpty(_notice))
        {
            return stack;
        }

        stack.Children.Add(Caption(_notice));
        _notice = null;
        return stack;
    }

    private protected void RenderMessage(string heading, string message)
    {
        var stack = NewStack(heading);
        stack.Children.Add(Caption(message));
        SetContent(stack);
    }

    private protected void RenderLoading(string title)
    {
        RenderMessage(title, "Loading from Steam…");
    }

    private protected static ActionButton Row(string title, string desc, Geometry? icon, Action? onClick)
    {
        var button = new ActionButton { Title = title, Description = desc, IconGeometry = icon };
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
    }

    private protected static ActionButton PrimaryRow(string title, string desc, Geometry? icon, Action onClick)
    {
        var button = Row(title, desc, icon, onClick);
        button.Classes.Add("primary");
        return button;
    }

    private protected static ActionButton DangerRow(string title, string desc, Geometry? icon, Action onClick)
    {
        var button = Row(title, desc, icon, onClick);
        button.Classes.Add("danger");
        return button;
    }

    private protected static Control ChoiceRow<T>(string label, IReadOnlyList<(T Value, string Label)> options,
        T current, Action<T> onSelect)
    {
        var values = options.Select(option => new ChoiceValue<T>(option.Value, option.Label)).ToArray();
        var committed = current;
        var choice = new ComboBox
        {
            ItemsSource = values,
            SelectedItem = values.FirstOrDefault(value => EqualityComparer<T>.Default.Equals(value.Value, current)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 44
        };
        var open = false;
        choice.DropDownOpened += (_, _) => open = true;
        choice.DropDownClosed += (_, _) =>
        {
            open = false;
            CommitChoice();
        };
        choice.SelectionChanged += (_, _) =>
        {
            if (!open && !choice.IsDropDownOpen)
            {
                CommitChoice();
            }
        };
        var row = new Grid
            { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16, Margin = new Thickness(0, 4) };
        row.Children.Add(new TextBlock
            { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(choice, 1);
        row.Children.Add(choice);
        return row;

        void CommitChoice()
        {
            if (choice.SelectedItem is not ChoiceValue<T> selected
                || EqualityComparer<T>.Default.Equals(committed, selected.Value))
            {
                return;
            }

            committed = selected.Value;
            onSelect(selected.Value);
        }
    }

    private protected static TextBlock Caption(string text)
    {
        return new TextBlock
        {
            Text = text,
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 4)
        };
    }

    private protected static TextBlock SectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Classes = { "eyebrow" },
            Margin = new Thickness(2, 6, 2, 2)
        };
    }

    // No inner ScrollViewer: the overlay's ContentScroller owns scrolling and its
    // GotFocus→BringIntoView keeps the focused control (incl. keyboard keys) on screen.
    // A nested scroller would swallow that scroll-into-view.
    private protected virtual void SetContent(StackPanel stack)
    {
        Content = stack;
        FocusFirst(stack);
    }

    // A row laid out inside a panel (a Grid of columns, a WrapPanel of thumbnails) is
    // still the first thing the user should land on, so the search descends one level.
    private static void FocusFirst(StackPanel stack)
    {
        Dispatcher.UIThread.Post(() => FocusSearch.FirstNavigable(stack)?.Focus(NavigationMethod.Directional));
    }

    // ---- Text entry ----

    private protected void EditText(string title, string current, int maxLen, Action<string> onAccept)
    {
        // Accept ordering matters: the rows show values straight off the model, so the mutation
        // has to land BEFORE anything re-renders or the user sees the old text. The keyboard
        // surface pushes no navigation level, so this re-renders the
        // current level itself instead of relying on a pop to do it.
        if (KeyboardService.Request(title, current, maxLen, v =>
            {
                onAccept(v);
                _current?.Invoke();
            }))
        {
            return;
        }

        // No keyboard surface means there is no way to type at all, so say so. The alternative —
        // a screen carrying a bare TextBox — is unusable here by design: GamepadNavigation skips
        // TextBoxes so the Windows touch keyboard cannot pop, which means focus never lands on it
        // and nothing types. See "Text entry in the panel is a press-to-edit ROW" in
        // docs\overlay-and-input.md.
        Log.Warn($"{LogScope}: cannot edit '{title}' — no keyboard surface is available.");
        Toast("Text entry needs the overlay keyboard, which is not available.");
    }

    private sealed record ChoiceValue<T>(T Value, string Label)
    {
        public override string ToString()
        {
            return Label;
        }
    }
}

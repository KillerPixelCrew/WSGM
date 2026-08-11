using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>Base for the self-drawing, gamepad-driven Tools sub-views (tab builder,
/// card manager): the render-thunk navigation stack, the shared row/label builders,
/// and text entry. Each navigation level rebuilds <see cref="ContentControl.Content"/>,
/// and every interactive element is a <see cref="Button"/> so D-pad navigation and A/B
/// work with no extra focus plumbing.</summary>
public abstract class OverlaySubView : UserControl
{
    // Navigation: a stack of render thunks. Push goes deeper; Back pops.
    private protected readonly System.Collections.Generic.Stack<Action> _stack = new();
    private protected Action? _current;
    private protected int _navigationGeneration;
    private string? _notice;

    /// <summary>Raised when the user backs out of the top level (the overlay then
    /// returns to the Tools list).</summary>
    public event Action? CloseRequested;

    /// <summary>Short name used to prefix log lines from this sub-view.</summary>
    protected abstract string LogScope { get; }

    /// <summary>Handles a Back/B press: pops one level, or requests close at the top.
    /// Returns true when it consumed the press.</summary>
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
        try { await task; }
        catch (Exception ex) { Log.Error($"{LogScope} {operation} failed.", ex); }
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
        var stack = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrEmpty(heading))
        {
            stack.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
            });
        }
        if (!string.IsNullOrEmpty(_notice))
        {
            stack.Children.Add(Caption(_notice));
            _notice = null;
        }
        return stack;
    }

    private protected void RenderLoading(string title)
    {
        var stack = NewStack(title);
        stack.Children.Add(Caption("Loading from Steam…"));
        SetContent(stack);
    }

    private protected CardButton Row(string title, string desc, Geometry? icon, Action? onClick)
    {
        var button = new CardButton { Title = title, Description = desc, IconGeometry = icon };
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }
        return button;
    }

    private protected CardButton PrimaryRow(string title, string desc, Geometry? icon, Action onClick)
    {
        var button = Row(title, desc, icon, onClick);
        button.Classes.Add("primary");
        return button;
    }

    private protected CardButton DangerRow(string title, string desc, Geometry? icon, Action onClick)
    {
        var button = Row(title, desc, icon, onClick);
        button.Classes.Add("danger");
        return button;
    }

    private protected CardButton CycleRow(string label, string value, Action onClick)
        => Row(label, value, Icons.Restart, onClick).Also(b => b.TrailingText = "↔");

    private protected TextBlock Caption(string text) => new()
    {
        Text = text,
        Classes = { "caption" },
        TextWrapping = TextWrapping.Wrap,
        Margin = new Avalonia.Thickness(2, 0, 2, 4),
    };

    private protected TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Classes = { "eyebrow" },
        Margin = new Avalonia.Thickness(2, 6, 2, 2),
    };

    // No inner ScrollViewer: the overlay's ContentScroller owns scrolling and its
    // GotFocus→BringIntoView keeps the focused control (incl. keyboard keys) on screen.
    private protected void SetContent(StackPanel stack)
    {
        Content = stack;
        FocusFirst(stack);
    }

    private protected void FocusFirst(StackPanel stack) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var child in stack.Children)
        {
            if (child is Button { IsEffectivelyEnabled: true } b)
            {
                b.Focus(NavigationMethod.Directional);
                return;
            }
            if (child is Grid grid)
            {
                foreach (var gc in grid.Children)
                {
                    if (gc is Button gb)
                    {
                        gb.Focus(NavigationMethod.Directional);
                        return;
                    }
                }
            }
        }
    });

    // ---- Text entry ----

    private protected void EditText(string title, string current, int maxLen, Action<string> onAccept)
    {
        // Prefer the separate keyboard window beside the sidebar (game mode); fall back
        // to an inline keyboard screen if none is available.
        //
        // Accept ordering matters on both paths: the rows show values straight off the
        // model, so the mutation has to land BEFORE anything re-renders or the user sees
        // the old text. The keyboard path pushes no navigation level (the window is a
        // peer, not a screen), so it must re-render the current level itself instead of
        // relying on a pop to do it.
        if (KeyboardService.Request(title, current, maxLen, v =>
        {
            onAccept(v ?? "");
            _current?.Invoke();
        }))
        {
            return;
        }
        Navigate(() =>
        {
            var stack = NewStack(title);
            var box = new TextBox { Text = current, MaxLength = maxLen, Margin = new Avalonia.Thickness(0, 0, 0, 6) };
            stack.Children.Add(box);
            var keyboard = new OnScreenKeyboard { Target = box };
            keyboard.Accepted += (_, _) => { onAccept(box.Text ?? ""); Back(); };
            stack.Children.Add(keyboard);
            stack.Children.Add(PrimaryRow("Accept", "Save this text", Icons.Play,
                () => { onAccept(box.Text ?? ""); Back(); }));
            stack.Children.Add(Row("Cancel", "Discard", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }
}

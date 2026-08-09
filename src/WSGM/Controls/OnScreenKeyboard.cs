using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace WSGM.Controls;

/// <summary>An on-screen keyboard drawn by WSGM itself.
///
/// Windows' own touch keyboard is not an option in game mode. It is rendered by
/// TextInputHost, part of the same immersive-shell AppX family as `ms-settings`,
/// and that cannot activate with no Explorer in the session — the same wall the
/// settings-activation work already hit. Starting TabTip.exe does nothing either:
/// on Windows 11 it is already running, so a second launch just exits.
///
/// So the only text entry that can be relied on for a Wi-Fi password or a
/// Bluetooth PIN is one this process draws. Being ours has a second benefit: the
/// keys are ordinary buttons, so controller navigation works on them for free.
/// </summary>
public sealed class OnScreenKeyboard : Decorator
{
    /// <summary>Defines the <see cref="Target"/> property.</summary>
    public static readonly StyledProperty<TextBox?> TargetProperty =
        AvaloniaProperty.Register<OnScreenKeyboard, TextBox?>(nameof(Target));

    /// <summary>Raised when the user presses the accept key.</summary>
    public event EventHandler? Accepted;

    private readonly Panel _root = new StackPanel { Spacing = 4 };
    private bool _shift;

    /// <summary>Which key layer is showing: 0 letters, 1 symbols, 2 the rest of
    /// the symbols. Three layers because a WPA passphrase may contain any
    /// printable ASCII character and this keyboard is the only way to type one
    /// in game mode — a character it cannot reach is a network that cannot be
    /// joined.</summary>
    private int _layer;

    private const int LayerLetters = 0;
    private const int LayerSymbols = 1;
    private const int LayerMoreSymbols = 2;

    /// <summary>Creates the keyboard.</summary>
    public OnScreenKeyboard()
    {
        Child = _root;
        Build();
    }

    /// <summary>Gets or sets the text box that receives the keystrokes.</summary>
    public TextBox? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    // Rows are the standard phone layout rather than a full PC one: a password
    // field does not need function keys, and wider keys are what a thumb needs.
    private static readonly string[] LettersLower = ["qwertyuiop", "asdfghjkl", "zxcvbnm"];
    private static readonly string[] LettersUpper = ["QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM"];
    private static readonly string[] Symbols = ["1234567890", "-/:;()$&@\"", ".,?!'#%*+="];

    /// <summary>The printable ASCII the first symbol page has no room for.
    /// Together with the letters, digits, space and <see cref="Symbols"/> this
    /// completes the set a WPA passphrase is allowed to contain.</summary>
    private static readonly string[] MoreSymbols = ["[]{}<>", "\\|~`^_"];

    private void Build()
    {
        _root.Children.Clear();
        var rows = _layer switch
        {
            LayerSymbols => Symbols,
            LayerMoreSymbols => MoreSymbols,
            _ => _shift ? LettersUpper : LettersLower,
        };
        foreach (var row in rows)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            foreach (var key in row)
            {
                panel.Children.Add(KeyButton(key.ToString(), () => Insert(key.ToString())));
            }
            _root.Children.Add(panel);
        }

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // One cycling key rather than two: the label names the layer it leads
        // to, so every character is reachable without a second modifier the
        // gamepad cursor would have to hunt for.
        controls.Children.Add(KeyButton(
            _layer switch
            {
                LayerSymbols => "#+=",
                LayerMoreSymbols => "abc",
                _ => "?123",
            },
            () =>
            {
                _layer = (_layer + 1) % 3;
                Build();
            },
            width: 58));
        controls.Children.Add(KeyButton("Shift", () =>
        {
            _shift = !_shift;
            _layer = LayerLetters;
            Build();
        }, width: 62));
        controls.Children.Add(KeyButton("Space", () => Insert(" "), width: 150));
        controls.Children.Add(KeyButton("Back", Backspace, width: 62));
        controls.Children.Add(KeyButton("Enter", () => Accepted?.Invoke(this, EventArgs.Empty),
            width: 68));
        _root.Children.Add(controls);
    }

    private Button KeyButton(string label, Action action, double width = 38)
    {
        var button = new Button
        {
            Content = label,
            Width = width,
            Height = 40,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            // Constant border, no adorner: the repo's focus discipline, so a
            // controller cursor never changes a key's size as it moves.
            BorderThickness = new Thickness(2),
            FocusAdorner = null,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void Insert(string text)
    {
        if (Target is not { } target)
        {
            return;
        }
        var current = target.Text ?? "";
        // Respect the caret rather than always appending: a mistyped character
        // in the middle of a long password is otherwise unfixable.
        var caret = Math.Clamp(target.CaretIndex, 0, current.Length);
        target.Text = current[..caret] + text + current[caret..];
        target.CaretIndex = caret + text.Length;
        // One-shot shift, the way every phone keyboard behaves.
        if (_shift && _layer == LayerLetters)
        {
            _shift = false;
            Build();
        }
    }

    private void Backspace()
    {
        if (Target is not { } target)
        {
            return;
        }
        var current = target.Text ?? "";
        var caret = Math.Clamp(target.CaretIndex, 0, current.Length);
        if (caret == 0 || current.Length == 0)
        {
            return;
        }
        target.Text = current[..(caret - 1)] + current[caret..];
        target.CaretIndex = caret - 1;
    }

    /// <summary>Resets to the lower-case letter layer.</summary>
    public void Reset()
    {
        if (_shift || _layer != LayerLetters)
        {
            _shift = false;
            _layer = LayerLetters;
            Build();
        }
    }

    /// <summary>The key rows, exposed so a test can assert the layout covers
    /// what a WPA passphrase is allowed to contain. The space bar is a control
    /// key rather than a row, so it is included here explicitly.</summary>
    internal static IReadOnlyList<string> AllKeys() =>
        [.. LettersLower, .. LettersUpper, .. Symbols, .. MoreSymbols, " "];
}

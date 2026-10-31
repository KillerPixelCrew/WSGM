using System;
using Avalonia.Controls;

namespace WSGM.Overlay;

/// <summary>
///     The overlay's press-to-edit text field: a button showing the text, or a placeholder while it is empty,
///     that opens the window's shared controller keyboard. The overlay never focuses a bare text box, because
///     the Windows touch keyboard would open over the game.
/// </summary>
internal sealed class PressToEditButton : Button
{
    private readonly string _placeholder;
    private string _text = string.Empty;

    /// <summary>Creates the field.</summary>
    /// <param name="prompt">The keyboard's accessible field label.</param>
    /// <param name="text">The current text, or null for none.</param>
    /// <param name="maximumLength">The character limit, or zero for no explicit limit.</param>
    /// <param name="placeholder">What the button reads while the text is empty.</param>
    /// <param name="accepted">Receives the accepted text once the keyboard closes with it.</param>
    /// <param name="unavailable">Runs when the window can no longer show a keyboard.</param>
    internal PressToEditButton(string prompt, string? text, int maximumLength, string placeholder,
        Action<string> accepted, Action unavailable)
    {
        _placeholder = placeholder;
        Text = text ?? string.Empty;
        Click += (_, _) =>
        {
            if (!OverlayWindow.RequestTextFor(this, prompt, Text, maximumLength, value =>
                {
                    Text = value;
                    accepted(value);
                }))
            {
                unavailable();
            }
        };
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The current text; setting it shows it, or the placeholder while it is empty.</summary>
    internal string Text
    {
        get => _text;
        set
        {
            _text = value;
            Content = value.Length > 0 ? value : _placeholder;
        }
    }
}

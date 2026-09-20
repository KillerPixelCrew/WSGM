using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSGM.Input;

namespace WSGM.Overlay;

/// <summary>In-window keyboard controls backed by the existing overlay actions.</summary>
public partial class KeyboardPanel : UserControl
{
    private bool _closePending;
    private bool _committed;

    /// <summary>Design-time constructor for the XAML loader.</summary>
    // ReSharper disable once UnusedMember.Global
    public KeyboardPanel()
        : this("Enter text", "", 256)
    {
    }

    /// <summary>Creates the keyboard surface for one field.</summary>
    /// <param name="prompt">The label shown above the field.</param>
    /// <param name="initial">The starting text.</param>
    /// <param name="maxLength">Maximum accepted character count.</param>
    /// <param name="sensitive">Whether to mask credential text while editing.</param>
    public KeyboardPanel(string prompt, string initial, int maxLength, bool sensitive = false)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        Input.Text = initial;
        Input.MaxLength = maxLength;
        Input.PasswordChar = sensitive ? '●' : '\0';
        RevealButton.IsVisible = sensitive;
        Keyboard.Target = Input;
        Keyboard.Accepted += (_, _) => Commit();
        Keyboard.Cancelled += (_, _) => DeferredClose();
        DetachedFromVisualTree += (_, _) => _closePending = true;

        AttachedToVisualTree += (_, _) =>
        {
            Input.CaretIndex = Input.Text?.Length ?? 0;
            FocusDefault();
        };
    }

    /// <summary>The first typing key, used by the overlay's single navigation owner.</summary>
    internal InputElement DefaultFocusTarget => FocusSearch.First<Button>(
                                                    Keyboard,
                                                    key => key is
                                                    {
                                                        IsEffectivelyEnabled: true, IsEffectivelyVisible: true,
                                                        Tag: Key.Q
                                                    })
                                                ?? FocusSearch.First<Button>(Keyboard,
                                                    key => key.IsEffectivelyEnabled && key.IsEffectivelyVisible)
                                                ?? AcceptButton;

    /// <summary>Requests closure of this keyboard surface.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised with the final text when the user accepts.</summary>
    public event Action<string>? Accepted;


    private void OnReveal(object? sender, RoutedEventArgs e)
    {
        var reveal = Input.PasswordChar != '\0';
        Input.PasswordChar = reveal ? '\0' : '●';
        RevealButton.Content = reveal ? "Hide" : "Show";
    }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        Commit();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        DeferredClose();
    }

    private void Commit()
    {
        if (_committed || _closePending)
        {
            return;
        }

        _committed = true;
        Accepted?.Invoke(Input.Text ?? "");
        DeferredClose();
    }

    private void DeferredClose()
    {
        if (_closePending)
        {
            return;
        }

        _closePending = true;
        CloseRequested?.Invoke();
    }


    /// <summary>
    ///     Focuses the first key so the user can start typing immediately (used on
    ///     open and after the surface becomes active).
    /// </summary>
    public void FocusDefault()
    {
        DefaultFocusTarget.Focus(NavigationMethod.Directional);
    }
}

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Overlay;

/// <summary>Tools > Keyboard: the on-screen keyboard for the current mode.</summary>
public sealed partial class KeyboardPage : StackPanel
{
    /// <summary>Builds the row.</summary>
    public KeyboardPage()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user asks for the on-screen keyboard.</summary>
    internal event Action? KeyboardRequested;

    private void OnScreenKeyboard(object? sender, RoutedEventArgs e)
    {
        KeyboardRequested?.Invoke();
    }
}

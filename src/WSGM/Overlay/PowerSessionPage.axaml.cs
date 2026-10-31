using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>Power > Session: Big Picture, the desktop, leaving Big Picture and quitting Steam.</summary>
/// <remarks>Quitting Steam asks for a second press through the sheet's <see cref="PowerActionConfirms" />.</remarks>
public sealed partial class PowerSessionPage : StackPanel
{
    private PowerActionConfirms? _confirms;

    /// <summary>Builds the rows; the window supplies the confirmation with <see cref="Configure" />.</summary>
    public PowerSessionPage()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user requests to start or focus the home application.</summary>
    internal event Action? HomeAppRequested;

    /// <summary>Raised when the user requests a desktop/game-mode transition.</summary>
    internal event Action? DesktopRequested;

    /// <summary>Raised when the user requests to leave Steam Big Picture mode.</summary>
    internal event Action? ExitBigPictureRequested;

    /// <summary>Raised after the user confirms closing the home application.</summary>
    internal event Action? CloseLauncherRequested;

    /// <summary>Supplies the second-press confirmation that quitting Steam shares with the Power page.</summary>
    /// <param name="confirms">The sheet's confirmations.</param>
    internal void Configure(PowerActionConfirms confirms)
    {
        _confirms = confirms;
    }

    /// <summary>Shows the device's own button for the home row's activation hint, where one resolved.</summary>
    /// <param name="glyph">The button's glyph, or null to keep the written letter.</param>
    internal void ShowHomeHint(PhysicalGlyphRenderPlan? glyph)
    {
        HomeAppButton.TrailingGlyph = glyph;
    }

    private void OnHomeApp(object? sender, RoutedEventArgs e)
    {
        HomeAppRequested?.Invoke();
    }

    private void OnDesktop(object? sender, RoutedEventArgs e)
    {
        DesktopRequested?.Invoke();
    }

    private void OnExitBigPicture(object? sender, RoutedEventArgs e)
    {
        ExitBigPictureRequested?.Invoke();
    }

    private void OnCloseLauncher(object? sender, RoutedEventArgs e)
    {
        if (_confirms is null || !_confirms.Confirm(PowerActionConfirms.Kind.CloseLauncher))
        {
            return;
        }

        _confirms.Reset();
        CloseLauncherRequested?.Invoke();
    }
}

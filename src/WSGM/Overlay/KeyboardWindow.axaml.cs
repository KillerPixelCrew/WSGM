using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace WSGM.Overlay;

/// <summary>The shared on-screen keyboard, in its own window beside the quick-access
/// sidebar. Owns its editing <see cref="TextBox"/>; the final text is handed back via
/// <see cref="Accepted"/> when the user confirms. Opened and gamepad-coordinated by
/// <c>OverlayController</c> (focus crosses left/right between this window and the
/// sidebar at their edges).</summary>
public partial class KeyboardWindow : Window
{
    private readonly double _uiScale;
    private bool _committed;

    /// <summary>Raised with the final text when the user accepts.</summary>
    public event Action<string>? Accepted;

    /// <summary>Raised when the window closes without accepting.</summary>
    public event Action? Cancelled;

    /// <summary>Design-time constructor for the XAML loader.</summary>
    public KeyboardWindow()
        : this("Enter text", "", 1.0)
    {
    }

    /// <summary>Creates the keyboard window for one field.</summary>
    /// <param name="prompt">The label shown above the field.</param>
    /// <param name="initial">The starting text.</param>
    /// <param name="uiScale">Desktop-DPI scale factor for WSGM UI.</param>
    public KeyboardWindow(string prompt, string initial, double uiScale = 1.0)
    {
        _uiScale = uiScale;
        InitializeComponent();
        PromptText.Text = prompt;
        Input.Text = initial;
        Keyboard.Target = Input;
        Keyboard.Accepted += (_, _) => Commit();

        Opened += (_, _) =>
        {
            ApplyScale();
            Input.CaretIndex = Input.Text?.Length ?? 0;
            FocusDefault();
        };
        Closed += (_, _) =>
        {
            if (!_committed)
            {
                Cancelled?.Invoke();
            }
        };
    }

    private void ApplyScale()
    {
        var factor = Math.Clamp(_uiScale / DesktopScaling, 1.0, 3.0);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            RootScale.LayoutTransform = new ScaleTransform(factor, factor);
        }
    }

    private void OnAccept(object? sender, RoutedEventArgs e) => Commit();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void Commit()
    {
        _committed = true;
        Accepted?.Invoke(Input.Text ?? "");
        Close();
    }

    /// <summary>Focuses the first key so the user can start typing immediately (used on
    /// open and when gamepad focus crosses in from the sidebar).</summary>
    public void FocusDefault()
    {
        foreach (var visual in Keyboard.GetVisualDescendants())
        {
            if (visual is Button { IsEffectivelyEnabled: true } key && key.IsEffectivelyVisible)
            {
                key.Focus(NavigationMethod.Directional);
                return;
            }
        }
        AcceptButton.Focus(NavigationMethod.Directional);
    }
}

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Interop;

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
    private bool _closePending;

    /// <summary>Raised with the final text when the user accepts.</summary>
    public event Action<string>? Accepted;

    /// <summary>Raised when the window closes without accepting.</summary>
    public event Action? Cancelled;

    /// <summary>Design-time constructor for the XAML loader.</summary>
    public KeyboardWindow()
        : this("Enter text", "", 256, 1.0)
    {
    }

    /// <summary>Creates the keyboard window for one field.</summary>
    /// <param name="prompt">The label shown above the field.</param>
    /// <param name="initial">The starting text.</param>
    /// <param name="uiScale">Desktop-DPI scale factor for WSGM UI.</param>
    /// <param name="maxLength">Maximum accepted character count.</param>
    public KeyboardWindow(string prompt, string initial, int maxLength, double uiScale = 1.0)
    {
        _uiScale = uiScale;
        InitializeComponent();
        PromptText.Text = prompt;
        Input.Text = initial;
        Input.MaxLength = maxLength;
        Keyboard.Target = Input;
        Keyboard.Accepted += (_, _) => Commit();
        Keyboard.PasteRequested += OnPasteRequested;
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);

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

    private void OnCancel(object? sender, RoutedEventArgs e) => DeferredClose();

    private async void OnPasteRequested(object? sender, EventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (_closePending || clipboard is null)
        {
            return;
        }
        try
        {
            var text = await clipboard.GetTextAsync();
            if (!_closePending && !string.IsNullOrEmpty(text))
            {
                Keyboard.InsertExternalText(text);
            }
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Keyboard paste failed: {ex.Message}");
        }
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
        DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(150));
    }

    private static IntPtr WndProcHook(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is NativeMethods.WmMouseMove or NativeMethods.WmLButtonDown or NativeMethods.WmLButtonUp)
        {
            var extra = (uint)NativeMethods.GetMessageExtraInfo();
            if ((extra & NativeMethods.MiWpSignatureMask) == NativeMethods.MiWpSignature)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
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

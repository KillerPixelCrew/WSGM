using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private BrowserView? _browserPage;

    /// <summary>Connects an inert browser page to the one session-owned browser.</summary>
    internal void AttachBrowserSession(BrowserSession session)
    {
        _browserPage = this.FindControl<BrowserView>("BrowserHost");
        if (_browserPage is null)
        {
            return;
        }

        _browserPage.Attach(session);
        RefreshWorkspace();
        PropertyChanged += OnBrowserWindowPropertyChanged;
        Closed += OnBrowserWindowClosed;
        AddHandler(KeyDownEvent, OnBrowserKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnBrowserTextInput, RoutingStrategies.Tunnel);
    }

    /// <summary>Called after shared route visibility changes. Never initializes a nonselected page.</summary>
    internal void RefreshBrowserPageLifetime()
    {
        if (_browserPage is null)
        {
            return;
        }

        if (IsVisible && ReferenceEquals(ActiveSubView?.Host, _browserPage))
        {
            _browserPage.Enter();
            _browserPage.Cover(HasActiveSurface);
        }
        else
        {
            _browserPage.Leave();
        }
    }

    internal void CoverBrowserPage(bool covered) => _browserPage?.Cover(covered);

    /// <summary>Browser native focus consumes one Back before the ordinary surface/rail close path.</summary>
    internal bool TryReturnBrowserToChrome() => _browserPage?.TryReturnToChrome() == true;

    private void OnBrowserKeyDown(object? sender, KeyEventArgs e)
    {
        if (HasActiveSurface || _browserPage?.ViewportOwnsInput != true)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            // TextInput supplies the character; do not also activate the last Avalonia button.
            e.Handled = true;
            return;
        }

        var key = e.Key switch
        {
            Key.Enter => "Enter",
            Key.Tab => e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "ShiftTab" : "Tab",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            _ => null
        };
        if (key is not null && _browserPage.PageKey(key))
        {
            e.Handled = true;
        }
    }

    private void OnBrowserTextInput(object? sender, TextInputEventArgs e)
    {
        if (!HasActiveSurface && _browserPage?.ViewportOwnsInput == true && !string.IsNullOrEmpty(e.Text))
        {
            _browserPage.PageText(e.Text);
            e.Handled = true;
        }
    }

    /// <summary>Filters existing semantic UI presses, without another poller, input lease or mouse mapping.</summary>
    internal IUiButtonSource BrowserInput(IUiButtonSource source, Func<bool>? nintendoLayout)
    {
        var filtered = new BrowserButtons(source, buttons =>
        {
            if (_browserPage?.ViewportOwnsInput != true || HasActiveSurface)
            {
                return false;
            }

            var back = nintendoLayout?.Invoke() == true ? GamepadButtons.A : GamepadButtons.B;
            if (buttons.HasFlag(back))
            {
                _browserPage.TryReturnToChrome();
            }
            else if (buttons.HasFlag(GamepadButtons.Y))
            {
                _browserPage.PageKeyboard();
            }
            else
            {
                _browserPage.HandleViewportButtons(buttons, nintendoLayout?.Invoke() == true);
            }

            return true;
        });
        Closed += (_, _) => filtered.Dispose();
        return filtered;
    }

    private void OnBrowserWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            RefreshBrowserPageLifetime();
        }
    }

    private void OnBrowserWindowClosed(object? sender, EventArgs e)
    {
        _browserPage?.Detach();
        _browserPage = null;
        PropertyChanged -= OnBrowserWindowPropertyChanged;
        Closed -= OnBrowserWindowClosed;
        RemoveHandler(KeyDownEvent, OnBrowserKeyDown);
        RemoveHandler(TextInputEvent, OnBrowserTextInput);
    }

    private sealed class BrowserButtons : IUiButtonSource, IDisposable
    {
        private readonly IUiButtonSource _source;
        private readonly Func<GamepadButtons, bool> _consume;

        internal BrowserButtons(IUiButtonSource source, Func<GamepadButtons, bool> consume)
        {
            _source = source;
            _consume = consume;
            source.ButtonPressed += OnButtons;
        }

        public event Action<GamepadButtons>? ButtonPressed;

        private void OnButtons(GamepadButtons buttons)
        {
            if (!_consume(buttons))
            {
                ButtonPressed?.Invoke(buttons);
            }
        }

        public void Dispose() => _source.ButtonPressed -= OnButtons;
    }
}

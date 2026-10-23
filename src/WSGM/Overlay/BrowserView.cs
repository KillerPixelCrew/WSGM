using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Browser;
using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Quick lookup chrome; the native viewport never owns application policy or input leases.</summary>
public sealed class BrowserView : OverlaySubView
{
    private readonly BrowserViewport _viewport = new() { Height = 320, MinHeight = 240 };
    private readonly Button _address = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _addressText = new() { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly Button _gameSearch = new() { MinHeight = 44, MaxWidth = 210, IsVisible = false };
    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _reload;
    private readonly Button _enter;
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } };
    private readonly TextBlock _zoom = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly OverlayChoice<BrowserMemoryBehavior> _memory;
    private BrowserSession? _session;
    private readonly StackPanel _stack;
    private bool _entered;
    private string? _pendingPageText;
    private BrowserMemoryBehavior? _renderedPolicy;

    /// <summary>Builds only Avalonia chrome. No browser environment or helper starts here.</summary>
    public BrowserView()
    {
        _stack = NewStack("");
        _address.Content = _addressText;
        _addressText.Text = "Search or enter an address…";
        _address.Click += (_, _) => EditAddress();
        var addressRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        addressRow.Children.Add(_address);
        _gameSearch.Click += (_, _) => EditAddress(withGame: true);
        Grid.SetColumn(_gameSearch, 1);
        addressRow.Children.Add(_gameSearch);
        _stack.Children.Add(addressRow);
        var commands = new WrapPanel { Orientation = Orientation.Horizontal };
        _back = AddButton(commands, "Back", () => _session?.Command("back"));
        _forward = AddButton(commands, "Forward", () => _session?.Command("forward"));
        _reload = AddButton(commands, "Reload", () =>
        {
            if (_session is { } session)
            {
                if (session.Lifecycle == BrowserLifecycle.Creating)
                {
                    session.Close();
                }
                else if (session.Lifecycle is BrowserLifecycle.Failed or BrowserLifecycle.NotCreated)
                {
                    session.Close();
                    session.Open();
                }
                else
                {
                    session.Command(session.Loading ? "stop" : "reload");
                }
            }
        });
        AddButton(commands, "Keyboard", PageKeyboard);
        _enter = AddButton(commands, "Enter page", () =>
        {
            _viewport.BringIntoView();
            Dispatcher.UIThread.Post(() => _session?.FocusViewport(), DispatcherPriority.Render);
        });
        AddButton(commands, "−", () => _session?.SetZoom((_session?.Options.ZoomFactor ?? 1) - 0.1));
        commands.Children.Add(_zoom);
        AddButton(commands, "+", () => _session?.SetZoom((_session?.Options.ZoomFactor ?? 1) + 0.1));
        _stack.Children.Add(commands);
        _stack.Children.Add(_status);
        _stack.Children.Add(_viewport);
        var secondary = new WrapPanel { Orientation = Orientation.Horizontal };
        AddButton(secondary, "Home", () => _session?.Navigate(BrowserNavigation.Home));
        AddButton(secondary, "External", () => _session?.OpenExternally());
        _stack.Children.Add(secondary);
        _memory = MemoryChoice(BrowserMemoryBehavior.UnloadWhenClosed);
        _memory.DropDownOpened += (_, _) => _session?.Cover(true);
        _memory.DropDownClosed += (_, _) =>
        {
            _session?.Cover(TopLevel.GetTopLevel(this) is OverlayWindow { HasActiveSurface: true });
            _renderedPolicy = null;
            Refresh();
        };
        var memoryRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        memoryRow.Children.Add(new TextBlock { Text = "Browser memory behavior", VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Grid.SetColumn(_memory, 1);
        memoryRow.Children.Add(_memory);
        _stack.Children.Add(memoryRow);
        _stack.Children.Add(Caption("Page: D-pad scrolls, LB/RB selects links, Confirm activates, Y opens the keyboard. Back / Escape returns to browser controls. Downloads and application links are disabled."));
        Content = _stack;
        LayoutUpdated += FitViewport;
        Refresh();
    }

    /// <inheritdoc />
    protected override string LogScope => "Browser";

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_session is not null && change.Property == IsVisibleProperty)
        {
            if (IsVisible && TopLevel.GetTopLevel(this) is Window { IsVisible: true })
            {
                Enter();
            }
            else
            {
                Leave();
            }
        }
    }

    internal bool ViewportOwnsInput => _session?.ViewportFocused == true;

    internal void Attach(BrowserSession session)
    {
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        if (_session is not null)
        {
            _session.Changed -= Refresh;
            _session.ChromeFocusRequested -= FocusChrome;
        }

        _session = session;
        _session.Changed += Refresh;
        _session.ChromeFocusRequested += FocusChrome;
        _viewport.Attach(session);
        Refresh();
    }

    internal void Enter()
    {
        _entered = true;
        _viewport.IsVisible = true;
        _session?.Open();
        Refresh();
    }

    internal override void Leave()
    {
        if (_entered)
        {
            NavigationGeneration++;
        }

        _entered = false;
        _pendingPageText = null;
        _session?.Close();
        _viewport.IsVisible = false;
        // Keep the ordinary Avalonia chrome mounted. KeepAlive belongs to BrowserSession,
        // and survives destruction/recreation of this native anchor or the entire OverlayWindow.
    }

    internal void Detach()
    {
        Leave();
        if (_session is not null)
        {
            _session.Changed -= Refresh;
            _session.ChromeFocusRequested -= FocusChrome;
        }

        _viewport.Attach(null);
        _session = null;
    }

    internal void Cover(bool covered)
    {
        _session?.Cover(covered);
        if (!covered && _pendingPageText is { } text)
        {
            _pendingPageText = null;
            _session?.InsertText(text);
        }
    }

    internal bool TryReturnToChrome() => _session?.ReturnToChrome() == true;

    internal void HandleViewportButtons(GamepadButtons buttons, bool nintendoLayout)
    {
        var confirm = nintendoLayout ? GamepadButtons.B : GamepadButtons.A;
        var key = buttons.HasFlag(confirm) || buttons.HasFlag(GamepadButtons.Start) ? "Enter"
            : buttons.HasFlag(GamepadButtons.DPadDown) ? "ArrowDown"
            : buttons.HasFlag(GamepadButtons.DPadUp) ? "ArrowUp"
            : buttons.HasFlag(GamepadButtons.DPadLeft) ? "ArrowLeft"
            : buttons.HasFlag(GamepadButtons.DPadRight) ? "ArrowRight"
            : buttons.HasFlag(GamepadButtons.RightShoulder) ? "Tab"
            : buttons.HasFlag(GamepadButtons.LeftShoulder) ? "ShiftTab"
            : buttons.HasFlag(GamepadButtons.RightTrigger) ? "PageDown"
            : buttons.HasFlag(GamepadButtons.LeftTrigger) ? "PageUp" : null;
        if (key is not null)
        {
            _session?.PageKey(key);
        }
    }

    internal void PageKeyboard()
    {
        if (!_entered || _session is null)
        {
            return;
        }

        _session.ReturnToChrome();
        if (TopLevel.GetTopLevel(this) is OverlayWindow window)
        {
            var generation = NavigationGeneration;
            window.RequestText("Type into the selected web field", "", 4096, text =>
            {
                if (_entered && generation == NavigationGeneration)
                {
                    _pendingPageText = text;
                }
            });
        }
    }

    private void FitViewport(object? sender, EventArgs e)
    {
        if (!_entered || !IsEffectivelyVisible)
        {
            return;
        }

        var scroller = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is null || scroller.Viewport.Height <= 0
            || _viewport.TranslatePoint(default, scroller) is not { } origin)
        {
            return;
        }

        // The shared deck measures page stacks with unbounded height. Reserve its actual viewport
        // for address/navigation and the native page; secondary options remain below it in the deck.
        // Add the scroll offset back so visiting those options never changes the page's size.
        var height = Math.Max(_viewport.MinHeight,
            scroller.Viewport.Height - origin.Y - scroller.Offset.Y - _stack.Spacing);
        if (Math.Abs(height - _viewport.Height) > 0.5)
        {
            _viewport.Height = height;
        }
    }

    private void EditAddress(bool withGame = false)
    {
        if (_session is not { } session || TopLevel.GetTopLevel(this) is not OverlayWindow window)
        {
            return;
        }

        session.ReturnToChrome();
        var initial = session.Address == BrowserNavigation.Home || withGame ? "" : session.Address;
        var game = withGame ? session.CurrentGame : null;
        var prompt = game is not null ? "Search " + game.Title : "Search or enter an address";
        var generation = NavigationGeneration;
        window.RequestText(prompt, initial, BrowserNavigation.MaximumAddressLength,
            text =>
            {
                if (_entered && generation == NavigationGeneration && ReferenceEquals(_session, session))
                {
                    if (game is not null)
                    {
                        session.NavigateGameSearch(text, game);
                    }
                    else
                    {
                        session.Navigate(text);
                    }
                }
            });
    }

    private void FocusChrome()
    {
        if (!_entered || TopLevel.GetTopLevel(this) is not OverlayWindow { IsVisible: true, HasActiveSurface: false } window
            || _memory.IsDropDownOpen)
        {
            return;
        }

        window.Activate();
        _address.Focus(NavigationMethod.Directional);
        _address.BringIntoView();
    }

    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        var session = _session;
        _addressText.Text = session?.Address is { } address && address != BrowserNavigation.Home
            ? address
            : "Search or enter an address…";
        _back.IsEnabled = session?.CanGoBack == true;
        _forward.IsEnabled = session?.CanGoForward == true;
        _reload.Content = session?.Lifecycle switch
        {
            BrowserLifecycle.Creating => "Cancel",
            BrowserLifecycle.Failed => "Retry",
            BrowserLifecycle.NotCreated => "Open",
            _ => session?.Loading == true ? "Stop" : "Reload"
        };
        _enter.IsEnabled = session?.Lifecycle == BrowserLifecycle.Active;
        _zoom.Text = $"{(session?.Options.ZoomFactor ?? 1):P0}";
        _status.Text = session?.Status ?? "Browser is unavailable in this session.";
        _gameSearch.IsVisible = session?.CurrentGame is not null;
        _gameSearch.Content = session?.CurrentGame is { } game ? "Search " + game.Title + "…" : "Search current game…";
        var behavior = session?.Options.MemoryBehavior;
        _memory.IsEnabled = session is not null;
        if (behavior != _renderedPolicy && !_memory.IsDropDownOpen && session is not null)
        {
            _renderedPolicy = behavior;
            _memory.RefreshFrom(MemoryChoice(session.Options.MemoryBehavior));
        }
    }

    private OverlayChoice<BrowserMemoryBehavior> MemoryChoice(BrowserMemoryBehavior current)
    {
        return new OverlayChoice<BrowserMemoryBehavior>(
            new[]
            {
                (BrowserMemoryBehavior.UnloadWhenClosed, "Unload when closed"),
                (BrowserMemoryBehavior.KeepAlive, "Keep alive")
            }, current, value => _session?.SetMemoryBehavior(value))
        {
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private static Button AddButton(Panel panel, string title, Action action)
    {
        var button = new Button { Content = title, MinHeight = 44, MinWidth = 44, Margin = new Thickness(0, 0, 6, 6) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
        return button;
    }
}

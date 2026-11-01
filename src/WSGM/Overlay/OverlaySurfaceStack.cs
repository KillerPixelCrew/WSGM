using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The transient in-window surfaces of the Quick access sheet: utility panels, the text-entry
///     keyboard, the file picker and the centred power menu, stacked over the deck.
/// </summary>
/// <remarks>
///     The stack keeps the window's capture and navigation owner. While a surface is open the deck
///     stops taking pointer and tab input, and closing the last surface returns focus to the
///     control that opened it.
/// </remarks>
internal sealed class OverlaySurfaceStack
{
    private readonly Action<bool> _coverMedia;
    private readonly Control _deck;
    private readonly Func<InputElement> _defaultFocus;
    private readonly Stack<SurfaceFrame> _frames = new();
    private readonly Window _owner;
    private readonly List<IDisposable> _powerMenuBindings = [];
    private readonly Action _powerMenuClosed;
    private readonly Panel _root;
    private NativeQamBrightnessService? _brightness;
    private IDisposable? _closeTimer;
    private bool _detached;
    private Grid? _layer;

    /// <summary>Creates the stack over a window's deck.</summary>
    /// <param name="owner">The window whose focus manager and resources the surfaces use.</param>
    /// <param name="root">The panel the surface layer is added to, above the deck.</param>
    /// <param name="deck">The deck the surfaces cover; it stops taking input while one is open.</param>
    /// <param name="defaultFocus">The deck control focus returns to when no invoker survives.</param>
    /// <param name="coverMedia">Pauses or resumes media behind the surfaces.</param>
    /// <param name="powerMenuClosed">Called when the power menu closes, to disarm its confirms.</param>
    internal OverlaySurfaceStack(Window owner, Panel root, Control deck, Func<InputElement> defaultFocus,
        Action<bool> coverMedia, Action powerMenuClosed)
    {
        _owner = owner;
        _root = root;
        _deck = deck;
        _defaultFocus = defaultFocus;
        _coverMedia = coverMedia;
        _powerMenuClosed = powerMenuClosed;
    }

    /// <summary>Whether a transient surface currently contains overlay focus.</summary>
    internal bool HasActive => _frames.Count > 0;

    /// <summary>Whether the centred power menu owns focus.</summary>
    internal bool IsPowerMenuOpen => _frames.TryPeek(out var frame) && frame.Kind == SurfaceKind.Power;

    /// <summary>Preferred focus for the current modal surface.</summary>
    internal InputElement? FocusTarget => _frames.TryPeek(out var frame)
        ? frame.Content switch
        {
            KeyboardPanel keyboard => keyboard.DefaultFocusTarget,
            OverlayFilePicker picker => picker.DefaultFocusTarget ?? frame.PreferredFocus,
            _ => frame.PreferredFocus
        }
        : null;

    /// <summary>The active surface's directional focus boundary.</summary>
    internal InputElement? NavigationRoot => _frames.TryPeek(out var frame)
        ? frame.Container
        : null;

    /// <summary>Reports completion of a transient surface's closure.</summary>
    internal event Action? Closed;

    /// <summary>Moves focus inside a file picker surface, which owns its own directional layout.</summary>
    /// <param name="direction">The requested direction.</param>
    /// <returns>True when the active picker consumed the move.</returns>
    internal bool NavigateDirection(NavigationDirection direction)
    {
        return _frames.TryPeek(out var frame) && frame.Content is OverlayFilePicker picker
                                              && picker.Navigate(direction);
    }

    /// <summary>Routes shoulder buttons into radio tabs while a surface is open.</summary>
    /// <param name="next">True for the next utility tab, false for the previous tab.</param>
    /// <returns>True while a surface consumes tab navigation, including a surface with no tab switch.</returns>
    internal bool NavigateTab(bool next)
    {
        if (!HasActive)
        {
            return false;
        }

        if (_frames.Peek().Content is RadioPanel radio)
        {
            if (next)
            {
                radio.SelectNextTab();
            }
            else
            {
                radio.SelectPreviousTab();
            }
        }

        return true;
    }

    /// <summary>Opens the local keyboard surface for a single-line or multiline field.</summary>
    /// <param name="prompt">Accessible field label.</param>
    /// <param name="initial">Text copied into the editor.</param>
    /// <param name="maxLength">Character limit, or zero for no explicit limit.</param>
    /// <param name="onAccept">UI-thread callback invoked once with accepted text; cancellation does not invoke it.</param>
    /// <param name="multiline">Whether Enter starts a new line instead of accepting.</param>
    /// <param name="changed">
    ///     UI-thread callback with the text after every edit, so an owner can keep a draft that outlives a
    ///     cancelled or closed surface; null when the caller only needs the accepted text.
    /// </param>
    /// <param name="sensitive">Whether the field holds a secret, such as a network password.</param>
    /// <returns>True when a keyboard was created; false after the window closed.</returns>
    internal bool RequestText(string prompt, string initial, int maxLength, Action<string> onAccept,
        bool multiline = false, Action<string>? changed = null, bool sensitive = false)
    {
        if (_detached)
        {
            return false;
        }

        var keyboard = new KeyboardPanel(prompt, initial, maxLength, sensitive, multiline);
        keyboard.Accepted += onAccept;
        if (changed is not null)
        {
            keyboard.TextChanged += changed;
        }

        ShowKeyboard(keyboard);
        return true;
    }

    /// <summary>Shares the existing session brightness owner with the utility surface.</summary>
    /// <param name="service">Borrowed session brightness owner also used by Steam and the main Display page.</param>
    internal void AttachBrightness(NativeQamBrightnessService service)
    {
        _brightness = service;
    }

    /// <summary>Shows the same brightness editor as the display page.</summary>
    internal void ShowBrightness()
    {
        Control content = _brightness is { } service
            ? new DisplayBrightnessView(service)
            : new TextBlock { Text = "Brightness unavailable for this display", TextWrapping = TextWrapping.Wrap };
        Show(content, SurfaceKind.Utility, "Brightness", null);
    }

    /// <summary>Shows live radio controls in the overlay.</summary>
    /// <param name="panel">Utility panel mounted in this window; its credential requests use the same surface stack.</param>
    internal void ShowRadio(RadioPanel panel)
    {
        panel.CloseRequested += () => Close(panel);
        panel.TextEntryRequested += (prompt, initial, accept) =>
            RequestText(prompt, initial, 0, accept, sensitive: true);
        Show(panel, SurfaceKind.Utility, "Connections", null);
    }

    /// <summary>Shows live audio controls in the overlay.</summary>
    /// <param name="panel">Audio utility panel to mount inside the current overlay capture.</param>
    internal void ShowAudio(AudioPanel panel)
    {
        Show(panel, SurfaceKind.Utility, "Audio", panel.DefaultFocusTarget);
    }

    /// <summary>Shows removable-drive controls in the overlay.</summary>
    /// <param name="panel">Removable-drive utility panel to mount inside the current overlay capture.</param>
    internal void ShowEject(EjectPanel panel)
    {
        panel.CloseRequested += () => Close(panel);
        Show(panel, SurfaceKind.Utility, "Safely remove", null);
    }

    /// <summary>Shows a utility surface below the control that opened it.</summary>
    /// <param name="content">The surface content; the stack scrolls it.</param>
    /// <param name="title">The heading and the close button's accessible name.</param>
    /// <param name="preferredFocus">The control to focus first, or null for the first navigable one.</param>
    internal void ShowUtility(Control content, string title, InputElement? preferredFocus)
    {
        Show(content, SurfaceKind.Utility, title, preferredFocus);
    }

    /// <summary>
    ///     Shows a confirmation as a utility surface centred in the window, so its whole question and both answers
    ///     stay visible however low the control that asked sits.
    /// </summary>
    /// <param name="content">The question and its answers; the stack scrolls it only when the window is too short.</param>
    /// <param name="title">The question's heading and the close button's accessible name.</param>
    /// <param name="preferredFocus">The safe answer, focused first.</param>
    internal void ShowConfirm(Control content, string title, InputElement preferredFocus)
    {
        Show(content, SurfaceKind.Utility, title, preferredFocus, true);
    }

    /// <summary>Pushes the controller-accessible local picker and resolves its first completion.</summary>
    /// <param name="folder">True for directory selection, false for file selection.</param>
    /// <param name="extensions">Allowed file extensions; empty permits all file types.</param>
    /// <returns>Accepted path, or null for cancellation/detach; close is deferred through the surface owner.</returns>
    internal Task<string?> PickLocalPathAsync(bool folder, params string[] extensions)
    {
        return PickLocalPathAsync(new OverlayFilePicker(folder, extensions), folder);
    }

    /// <summary>Pushes a configured local picker and resolves its first completion.</summary>
    /// <param name="picker">The picker to show.</param>
    /// <param name="folder">True for directory selection, false for file selection.</param>
    /// <returns>Accepted path, or null for cancellation/detach.</returns>
    internal Task<string?> PickLocalPathAsync(OverlayFilePicker picker, bool folder)
    {
        var completion = new TaskCompletionSource<string?>();
        picker.Completed += path =>
        {
            if (completion.TrySetResult(path))
            {
                Close(picker);
            }
        };
        picker.TextEntryRequested += (initial, accept) => RequestText("Local path", initial, 0, accept);
        Show(picker, SurfaceKind.Utility, folder ? "Choose a folder" : "Choose a file", null);
        return completion.Task;
    }

    /// <summary>Shows the internal text-entry keyboard across the bottom of this window.</summary>
    /// <param name="panel">Local text editor to mount at the bottom of the surface stack; it sends no global keyboard input.</param>
    internal void ShowKeyboard(KeyboardPanel panel)
    {
        panel.CloseRequested += () => Close(panel);
        Show(panel, SurfaceKind.Keyboard, "Text entry", panel.DefaultFocusTarget);
    }

    /// <summary>Projects power action controls into the centred menu.</summary>
    /// <param name="sources">The page's visible actions, in menu order.</param>
    /// <param name="destructive">Actions drawn in the danger style beyond those whose source already is.</param>
    /// <remarks>
    ///     Placement differs from the linear page, but titles, descriptions, availability and actions
    ///     come from the source controls through bindings the menu releases when it closes.
    /// </remarks>
    internal void ShowPowerMenu(IReadOnlyList<ActionButton> sources, IReadOnlyCollection<ActionButton> destructive)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Choose an action for this session",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Classes = { "caption" }
        });
        var actions = new Grid
            { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12, RowSpacing = 12 };
        for (var index = 0; index < sources.Count; index++)
        {
            if (index % 2 == 0)
            {
                actions.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var action = CreatePowerMenuAction(sources[index], destructive.Contains(sources[index]));
            Grid.SetColumn(action, index % 2);
            Grid.SetRow(action, index / 2);
            actions.Children.Add(action);
        }

        content.Children.Add(actions);
        var keepPlaying = new Button
        {
            Content = "Keep playing", Classes = { "primary" }, MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(4)
        };
        keepPlaying.Click += (_, _) => CloseActive();
        content.Children.Add(keepPlaying);
        Show(content, SurfaceKind.Power, "Power & session", keepPlaying);
    }

    private Button CreatePowerMenuAction(ActionButton source, bool destructive)
    {
        var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (source.IconGeometry is { } geometry)
        {
            var icon = new Path
            {
                Data = geometry, Width = 20, Height = 20, Stretch = Stretch.Uniform,
                StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _powerMenuBindings.Add(icon.Bind(Shape.StrokeProperty, _owner.GetResourceObservable("DeckTextBrush")));
            content.Children.Add(icon);
        }

        var title = new TextBlock
        {
            FontSize = 16, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch, TextWrapping = TextWrapping.Wrap
        };
        var description = new TextBlock
        {
            FontSize = 12, MinHeight = 32, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "caption" }
        };
        _powerMenuBindings.Add(title.Bind(TextBlock.TextProperty, source.GetObservable(ActionButton.TitleProperty)));
        _powerMenuBindings.Add(description.Bind(TextBlock.TextProperty,
            source.GetObservable(ActionButton.DescriptionProperty)));
        content.Children.Add(title);
        content.Children.Add(description);
        var button = new Button
        {
            Content = content, Tag = source, MinHeight = 100, Padding = new Thickness(12, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(4)
        };
        button.Classes.Set("danger", source.Classes.Contains("danger") || destructive);
        _powerMenuBindings.Add(button.Bind(AutomationProperties.NameProperty,
            source.GetObservable(ActionButton.TitleProperty)));
        _powerMenuBindings.Add(button.Bind(InputElement.IsEnabledProperty,
            source.GetObservable(InputElement.IsEnabledProperty)));
        button.Click += (_, _) => source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent) { Source = source });
        return button;
    }

    private void Show(Control content, SurfaceKind kind, string title, InputElement? preferredFocus,
        bool centered = false)
    {
        var settlingClose = _closeTimer is not null;
        if (settlingClose)
        {
            _closeTimer?.Dispose();
            _closeTimer = null;
            CloseTop();
        }

        _coverMedia(true);
        var invokingControl = _owner.FocusManager?.GetFocusedElement() as InputElement;
        if (kind != SurfaceKind.Keyboard)
        {
            CloseAll();
        }
        else if (_frames.TryPeek(out var previousKeyboard) && previousKeyboard.Kind == SurfaceKind.Keyboard)
        {
            invokingControl = previousKeyboard.Invoker;
            CloseTop();
        }

        if (_frames.TryPeek(out var covered))
        {
            covered.Container.IsEnabled = true;
            covered.Container.IsVisible = false;
        }

        if (_layer is null)
        {
            // Keep a hit-test shield without repainting the entire deck on every open.
            _layer = new Grid { Background = Brushes.Transparent };
            _root.Children.Add(_layer);
        }

        var close = new Button { Content = "×", MinHeight = 44, MinWidth = 44, FontSize = 24 };
        close.Click += (_, _) => CloseActive();
        AutomationProperties.SetName(close, kind == SurfaceKind.Power ? "Close power menu" : "Close " + title);
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(kind == SurfaceKind.Power ? "44,*,44" : "*,Auto"),
            Margin = new Thickness(0, 0, 0, 12),
            IsVisible = kind != SurfaceKind.Keyboard
        };
        var headingText = new TextBlock
        {
            Text = title, FontSize = kind == SurfaceKind.Power ? 28 : 20, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = kind == SurfaceKind.Power ? HorizontalAlignment.Center : HorizontalAlignment.Left
        };
        Grid.SetColumn(headingText, kind == SurfaceKind.Power ? 1 : 0);
        heading.Children.Add(headingText);
        Grid.SetColumn(close, kind == SurfaceKind.Power ? 2 : 1);
        heading.Children.Add(close);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(heading);
        var scroller = content is OverlayFilePicker
            ? content
            : new ScrollViewer
                { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1);
        body.Children.Add(scroller);
        var anchor =
            (invokingControl as Control)?.TranslatePoint(new Point(0, (invokingControl as Control)?.Bounds.Height ?? 0),
                _root);
        var picker = content is OverlayFilePicker;
        var top = kind == SurfaceKind.Utility && !picker && !centered
            ? Math.Clamp((anchor?.Y ?? 72) + 12, 16, Math.Max(16, _root.Bounds.Height - 260))
            : 16;
        var container = new Border
        {
            Child = body,
            Padding = kind == SurfaceKind.Keyboard ? default : new Thickness(20),
            Margin = kind == SurfaceKind.Keyboard ? default : new Thickness(16, top, 16, 16),
            CornerRadius = kind == SurfaceKind.Keyboard ? default : new CornerRadius(6),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = picker || centered ? HorizontalAlignment.Center :
                kind == SurfaceKind.Keyboard ? HorizontalAlignment.Stretch :
                kind == SurfaceKind.Power ? HorizontalAlignment.Center : HorizontalAlignment.Right,
            VerticalAlignment = picker || centered ? VerticalAlignment.Center
                : kind == SurfaceKind.Keyboard ? VerticalAlignment.Bottom
                : kind == SurfaceKind.Utility ? VerticalAlignment.Top : VerticalAlignment.Center,
            MaxWidth = picker ? Math.Min(1000, _root.Bounds.Width - 32) :
                kind == SurfaceKind.Keyboard ? double.PositiveInfinity :
                kind == SurfaceKind.Power ? 640 : 620,
            Width = picker ? Math.Min(1000, _root.Bounds.Width - 32) :
                kind == SurfaceKind.Keyboard ? double.NaN :
                kind == SurfaceKind.Power ? 640 : 620,
            Height = picker ? Math.Min(620, _root.Bounds.Height - 48) : double.NaN,
            MaxHeight = Math.Max(240, _root.Bounds.Height - top - 16)
        };
        container.Bind(Border.BackgroundProperty, _owner.GetResourceObservable("DeckSurfaceBrush"));
        container.Bind(Border.BorderBrushProperty, _owner.GetResourceObservable("DeckDividerBrush"));
        KeyboardNavigation.SetTabNavigation(container, KeyboardNavigationMode.Cycle);
        preferredFocus ??= FocusSearch.FirstNavigable(content) ?? close;
        _frames.Push(new SurfaceFrame(content, container, kind, preferredFocus, invokingControl));
        _layer.Children.Add(container);
        // A disabled deck changes the styling of every child. The surface layer blocks
        // pointer input, and tab navigation stays inside the active surface.
        _deck.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(_deck, KeyboardNavigationMode.None);
        _owner.UpdateLayout();
        preferredFocus.Focus(NavigationMethod.Directional);
        Dispatcher.UIThread.Post(() =>
        {
            if (_frames.TryPeek(out var active) && ReferenceEquals(active.Container, container))
            {
                (FocusTarget ?? active.PreferredFocus).Focus(NavigationMethod.Directional);
            }
        }, DispatcherPriority.Loaded);
        if (settlingClose)
        {
            Closed?.Invoke();
        }
    }

    private void Close(Control content)
    {
        if (_frames.TryPeek(out var frame) && ReferenceEquals(frame.Content, content))
        {
            CloseActive();
        }
        else
        {
            // A path can complete while its keyboard still covers the picker. Retire
            // that completed surface when the keyboard's close grace settles.
            var covered = _frames.FirstOrDefault(candidate => ReferenceEquals(candidate.Content, content));
            if (covered is not null)
            {
                covered.Container.IsEnabled = false;
            }
        }
    }

    /// <summary>Closes the top surface, preserving the overlay's capture and navigation owner.</summary>
    /// <returns>True when an active surface is present and its close is handled; false when the stack is empty.</returns>
    internal bool CloseActive()
    {
        if (!HasActive)
        {
            return false;
        }

        _frames.Peek().Container.IsEnabled = false;
        _closeTimer ??= DispatcherTimer.RunOnce(() =>
        {
            _closeTimer = null;
            CloseTop();
            _coverMedia(HasActive);
            Closed?.Invoke();
        }, TouchInput.CloseGrace);
        return true;
    }

    private void CloseTop()
    {
        var restoreFocus = _owner.IsVisible && (_owner.FocusManager?.GetFocusedElement() is not InputElement focused
                                                || TopLevel.GetTopLevel(focused) == _owner);
        if (!_frames.TryPop(out var frame))
        {
            return;
        }

        _layer?.Children.Remove(frame.Container);
        if (frame.Kind == SurfaceKind.Power)
        {
            _powerMenuClosed();
            foreach (var binding in _powerMenuBindings)
            {
                binding.Dispose();
            }

            _powerMenuBindings.Clear();
        }

        if (_frames.TryPeek(out var previous))
        {
            if (!previous.Container.IsEnabled)
            {
                CloseTop();
                return;
            }

            previous.Container.IsVisible = true;
        }
        else
        {
            if (_layer is not null)
            {
                _root.Children.Remove(_layer);
                _layer = null;
            }

            KeyboardNavigation.SetTabNavigation(_deck, KeyboardNavigationMode.Continue);
            _deck.IsHitTestVisible = true;
        }

        var target = frame.Invoker is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true } invoker
                     && TopLevel.GetTopLevel(invoker) == _owner
            ? invoker
            : FocusTarget ?? _defaultFocus();
        if (restoreFocus)
        {
            if (!_frames.TryPeek(out var active) || active.Content is not OverlayFilePicker picker
                                                 || !picker.RestorePendingFocus())
            {
                target.Focus(NavigationMethod.Directional);
            }
        }
    }

    /// <summary>Immediately releases all transient contents during owner teardown.</summary>
    internal void CloseAll()
    {
        _closeTimer?.Dispose();
        _closeTimer = null;
        while (HasActive)
        {
            CloseTop();
        }
    }

    /// <summary>Releases every surface as the window closes and refuses later keyboard requests.</summary>
    internal void Detach()
    {
        CloseAll();
        _detached = true;
    }

    private enum SurfaceKind
    {
        Utility,
        Keyboard,
        Power
    }

    private sealed record SurfaceFrame(
        Control Content,
        Border Container,
        SurfaceKind Kind,
        InputElement PreferredFocus,
        InputElement? Invoker);
}

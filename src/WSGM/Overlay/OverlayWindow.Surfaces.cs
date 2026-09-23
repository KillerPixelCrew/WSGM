using System;
using System.Collections.Generic;
using System.Linq;
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

public partial class OverlayWindow
{
    private readonly List<IDisposable> _powerMenuBindings = [];
    private readonly Stack<SurfaceFrame> _surfaceFrames = new();
    private NativeQamBrightnessService? _surfaceBrightness;
    private IDisposable? _surfaceCloseTimer;
    private Grid? _surfaceLayer;

    /// <summary>Whether a transient surface currently contains overlay focus.</summary>
    internal bool HasActiveSurface => _surfaceFrames.Count > 0;

    /// <summary>Whether the centred power menu owns focus.</summary>
    internal bool IsPowerMenuOpen => _surfaceFrames.TryPeek(out var frame) && frame.Kind == SurfaceKind.Power;

    /// <summary>Preferred focus for the current modal surface.</summary>
    internal InputElement? ActiveSurfaceFocusTarget => _surfaceFrames.TryPeek(out var frame)
        ? frame.Content is KeyboardPanel keyboard ? keyboard.DefaultFocusTarget : frame.PreferredFocus
        : null;

    /// <summary>The active surface's directional focus boundary.</summary>
    internal InputElement? ActiveSurfaceNavigationRoot => _surfaceFrames.TryPeek(out var frame)
        ? frame.Container
        : null;

    /// <summary>Requests the centred power menu through the surface owner.</summary>
    internal event Action? PowerMenuRequested;

    /// <summary>Reports completion of a transient surface's closure.</summary>
    internal event Action? SurfaceClosed;

    private void OnOpenPowerMenu(object? sender, RoutedEventArgs e)
    {
        PowerMenuRequested?.Invoke();
    }

    /// <summary>Shares the existing session brightness owner with the utility surface.</summary>
    internal void AttachBrightnessSurface(NativeQamBrightnessService service)
    {
        _surfaceBrightness = service;
    }

    /// <summary>Shows the same brightness editor as the display page.</summary>
    internal void ShowBrightnessSurface()
    {
        Control content = _surfaceBrightness is { } service
            ? new DisplayBrightnessView(service)
            : new TextBlock { Text = "Brightness unavailable for this display", TextWrapping = TextWrapping.Wrap };
        ShowSurface(content, SurfaceKind.Utility, "Brightness", null);
    }

    /// <summary>Shows live radio controls in the overlay.</summary>
    internal void ShowRadioSurface(RadioPanel panel)
    {
        panel.CloseRequested += () => CloseSurface(panel);
        panel.TextEntryRequested += (prompt, initial, accept) =>
        {
            var keyboard = new KeyboardPanel(prompt, initial, 256, true);
            keyboard.Accepted += accept;
            ShowKeyboardSurface(keyboard);
        };
        ShowSurface(panel, SurfaceKind.Utility, "Connections", null);
    }

    /// <summary>Shows live audio controls in the overlay.</summary>
    internal void ShowAudioSurface(AudioPanel panel)
    {
        ShowSurface(panel, SurfaceKind.Utility, "Audio", panel.DefaultFocusTarget);
    }

    /// <summary>Shows removable-drive controls in the overlay.</summary>
    internal void ShowEjectSurface(EjectPanel panel)
    {
        panel.CloseRequested += () => CloseSurface(panel);
        ShowSurface(panel, SurfaceKind.Utility, "Safely remove", null);
    }

    /// <summary>Shows the internal text-entry keyboard across the bottom of this window.</summary>
    internal void ShowKeyboardSurface(KeyboardPanel panel)
    {
        panel.CloseRequested += () => CloseSurface(panel);
        ShowSurface(panel, SurfaceKind.Keyboard, "Text entry", panel.DefaultFocusTarget);
    }

    /// <summary>Projects the Power destination's real action controls into the centred menu.</summary>
    internal void ShowPowerMenu()
    {
        CloseAllSurfaces();
        ResetConfirms();
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Choose an action for this session",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Classes = { "caption" }
        });
        var sources = PanelPowerActions.Children.OfType<ActionButton>().Where(source => source.IsVisible).ToList();
        // Placement differs from the linear page, but titles, descriptions, availability and
        // actions come from its controls. New Power actions are included automatically.
        if (sources.Remove(SignOutButton))
        {
            var shutdown = sources.IndexOf(ShutdownButton);
            sources.Insert(shutdown >= 0 ? shutdown : sources.Count, SignOutButton);
        }

        if (DesktopButton.IsVisible)
        {
            sources.Insert(Math.Min(2, sources.Count), DesktopButton);
        }

        var actions = new Grid
            { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12, RowSpacing = 12 };
        for (var index = 0; index < sources.Count; index++)
        {
            if (index % 2 == 0)
            {
                actions.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var action = CreatePowerMenuAction(sources[index]);
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
        keepPlaying.Click += (_, _) => CloseActiveSurface();
        content.Children.Add(keepPlaying);
        ShowSurface(content, SurfaceKind.Power, "Power & session", keepPlaying);
    }

    private Button CreatePowerMenuAction(ActionButton source)
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
            _powerMenuBindings.Add(icon.Bind(Shape.StrokeProperty, this.GetResourceObservable("DeckTextBrush")));
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
        button.Classes.Set("danger",
            source.Classes.Contains("danger") || source == RestartButton || source == SignOutButton);
        _powerMenuBindings.Add(button.Bind(AutomationProperties.NameProperty,
            source.GetObservable(ActionButton.TitleProperty)));
        _powerMenuBindings.Add(button.Bind(IsEnabledProperty, source.GetObservable(IsEnabledProperty)));
        button.Click += (_, _) => source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent) { Source = source });
        return button;
    }

    private void ShowSurface(Control content, SurfaceKind kind, string title, InputElement? preferredFocus)
    {
        var invokingControl = FocusManager?.GetFocusedElement() as InputElement;
        if (kind != SurfaceKind.Keyboard)
        {
            CloseAllSurfaces();
        }
        else if (_surfaceFrames.TryPeek(out var previousKeyboard) && previousKeyboard.Kind == SurfaceKind.Keyboard)
        {
            invokingControl = previousKeyboard.Invoker;
            CloseTopSurface();
        }

        _surfaceCloseTimer?.Dispose();
        _surfaceCloseTimer = null;
        if (_surfaceFrames.TryPeek(out var covered))
        {
            covered.Container.IsEnabled = true;
            covered.Container.IsVisible = false;
        }

        if (_surfaceLayer is null)
        {
            // Keep a hit-test shield without repainting the entire deck on every open.
            _surfaceLayer = new Grid { Background = Brushes.Transparent };
            SurfaceRoot.Children.Add(_surfaceLayer);
        }

        var close = new Button { Content = "×", MinHeight = 44, MinWidth = 44, FontSize = 24 };
        close.Click += (_, _) => CloseActiveSurface();
        AutomationProperties.SetName(close, kind == SurfaceKind.Power ? "Close power menu" : "Close " + title);
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(kind == SurfaceKind.Power ? "44,*,44" : "*,Auto"),
            Margin = new Thickness(0, 0, 0, 12)
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
        var scroller = new ScrollViewer
            { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1);
        body.Children.Add(scroller);
        var anchor =
            (invokingControl as Control)?.TranslatePoint(new Point(0, (invokingControl as Control)?.Bounds.Height ?? 0),
                SurfaceRoot);
        var top = kind == SurfaceKind.Utility
            ? Math.Clamp((anchor?.Y ?? 72) + 12, 16, Math.Max(16, SurfaceRoot.Bounds.Height - 260))
            : 16;
        var container = new Border
        {
            Child = body,
            Padding = new Thickness(20),
            Margin = new Thickness(16, top, 16, 16),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = kind == SurfaceKind.Keyboard ? HorizontalAlignment.Stretch :
                kind == SurfaceKind.Power ? HorizontalAlignment.Center : HorizontalAlignment.Right,
            VerticalAlignment = kind == SurfaceKind.Keyboard ? VerticalAlignment.Bottom
                : kind == SurfaceKind.Utility ? VerticalAlignment.Top : VerticalAlignment.Center,
            MaxWidth = kind == SurfaceKind.Keyboard ? double.PositiveInfinity : kind == SurfaceKind.Power ? 640 : 620,
            Width = kind == SurfaceKind.Keyboard ? double.NaN : kind == SurfaceKind.Power ? 640 : 620,
            MaxHeight = Math.Max(240, SurfaceRoot.Bounds.Height - top - 16)
        };
        container.Bind(Border.BackgroundProperty, this.GetResourceObservable("DeckSurfaceBrush"));
        container.Bind(Border.BorderBrushProperty, this.GetResourceObservable("DeckDividerBrush"));
        KeyboardNavigation.SetTabNavigation(container, KeyboardNavigationMode.Cycle);
        preferredFocus ??= FocusSearch.FirstNavigable(content) ?? close;
        _surfaceFrames.Push(new SurfaceFrame(content, container, kind, preferredFocus, invokingControl));
        _surfaceLayer.Children.Add(container);
        // A disabled deck changes the styling of every child. The surface layer blocks
        // pointer input, and tab navigation stays inside the active surface.
        DeckContent.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(DeckContent, KeyboardNavigationMode.None);
        UpdateLayout();
        preferredFocus.Focus(NavigationMethod.Directional);
        Dispatcher.UIThread.Post(() =>
        {
            if (_surfaceFrames.TryPeek(out var active) && ReferenceEquals(active.Container, container))
            {
                (ActiveSurfaceFocusTarget ?? active.PreferredFocus).Focus(NavigationMethod.Directional);
            }
        }, DispatcherPriority.Loaded);
    }

    private void CloseSurface(Control content)
    {
        if (_surfaceFrames.TryPeek(out var frame) && ReferenceEquals(frame.Content, content))
        {
            CloseActiveSurface();
        }
    }

    /// <summary>Closes the top surface, preserving the overlay's capture and navigation owner.</summary>
    internal bool CloseActiveSurface()
    {
        if (!HasActiveSurface)
        {
            return false;
        }

        _surfaceFrames.Peek().Container.IsEnabled = false;
        _surfaceCloseTimer ??= DispatcherTimer.RunOnce(() =>
        {
            _surfaceCloseTimer = null;
            CloseTopSurface();
            SurfaceClosed?.Invoke();
        }, TouchInput.CloseGrace);
        return true;
    }

    private void CloseTopSurface()
    {
        if (!_surfaceFrames.TryPop(out var frame))
        {
            return;
        }

        _surfaceLayer?.Children.Remove(frame.Container);
        if (frame.Kind == SurfaceKind.Power)
        {
            ResetConfirms();
            foreach (var binding in _powerMenuBindings)
            {
                binding.Dispose();
            }

            _powerMenuBindings.Clear();
        }

        if (_surfaceFrames.TryPeek(out var previous))
        {
            previous.Container.IsEnabled = true;
            previous.Container.IsVisible = true;
        }
        else
        {
            if (_surfaceLayer is not null)
            {
                SurfaceRoot.Children.Remove(_surfaceLayer);
                _surfaceLayer = null;
            }

            KeyboardNavigation.SetTabNavigation(DeckContent, KeyboardNavigationMode.Continue);
            DeckContent.IsHitTestVisible = true;
        }

        var target = frame.Invoker is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true } invoker
                     && GetTopLevel(invoker) == this
            ? invoker
            : ActiveSurfaceFocusTarget ?? DefaultFocusTarget;
        target.Focus(NavigationMethod.Directional);
    }

    /// <summary>Immediately releases all transient contents during owner teardown.</summary>
    internal void CloseAllSurfaces()
    {
        _surfaceCloseTimer?.Dispose();
        _surfaceCloseTimer = null;
        while (HasActiveSurface)
        {
            CloseTopSurface();
        }
    }

    /// <summary>Routes shoulder buttons into radio tabs while a surface is open.</summary>
    internal bool NavigateSurfaceTab(bool next)
    {
        if (!HasActiveSurface)
        {
            return false;
        }

        if (_surfaceFrames.Peek().Content is RadioPanel radio)
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

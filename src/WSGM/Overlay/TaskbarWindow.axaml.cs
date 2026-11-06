using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Input;
using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Game Mode taskbar: a slim strip on the bottom edge with a Start menu of the user's programs, the open
///     windows and the tray icons.
/// </summary>
/// <remarks>
///     The window covers its display; the dimmed area above the strip dismisses it. It renders state and reports
///     intent: <see cref="OverlayController" /> owns its lifetime, input lease, capture and the activations.
/// </remarks>
public partial class TaskbarWindow : Window
{
    /// <summary>Downward travel, in DIPs, after which a touch or pen drag dismisses the strip.</summary>
    internal const double SwipeDownDistance = 48;

    private const string ProgramTagPrefix = "taskbar.program:";

    private readonly Action<TaskbarWindow> _dock;
    private readonly PixelPoint? _preferredScreenPoint;
    private readonly double _uiScale;
    private InputElement? _menuInvoker;
    private IProgramsSource? _programs;
    private DispatcherTimer? _slideTimer;
    private DateTime _slideStartedUtc;
    private double _slideFrom;
    private Point? _swipeStart;
    private bool _swipeDismissed;

    /// <summary>Parameterless constructor for the XAML loader and previewer.</summary>
    public TaskbarWindow() : this(new AppSwitcherViewModel())
    {
    }

    /// <summary>Creates the taskbar over the open windows and tray icons of <paramref name="switcher" />.</summary>
    /// <param name="switcher">The Open apps state shared with the Overlay's strip.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI (1.5 for a 150% desktop).</param>
    /// <param name="preferredScreenPoint">A physical point on the display to cover, or null for the primary one.</param>
    /// <param name="dock">
    ///     Places the opened window; null covers the display with <see cref="Dock" />. Headless captures size the
    ///     window themselves, as they do for <see cref="OverlayWindow" />.
    /// </param>
    internal TaskbarWindow(AppSwitcherViewModel switcher, double uiScale = 1.0, PixelPoint? preferredScreenPoint = null,
        Action<TaskbarWindow>? dock = null)
    {
        _uiScale = uiScale;
        _dock = dock ?? (static window => window.Dock());
        _preferredScreenPoint = preferredScreenPoint;
        InitializeComponent();
        DataContext = switcher;
        // Directional focus does not raise RequestBringIntoView on its own; see OverlayWindow.OnStripGotFocus.
        TileScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        TrayScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        StartScroller.AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        Scrim.PointerPressed += OnScrimPressed;
        // Budget the tray against the bar's laid-out width (inside the content scale), at open and on resize.
        SurfaceRoot.SizeChanged += (_, e) =>
            TrayScroller.MaxWidth = OverlayPlacement.ComputeTrayMaxWidth(e.NewSize.Width, 1);
        AddHandler(PointerPressedEvent, OnSwipePressed, RoutingStrategies.Tunnel, true);
        AddHandler(PointerMovedEvent, OnSwipeMoved, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, OnSwipeReleased, RoutingStrategies.Tunnel, true);
        AddHandler(PointerCaptureLostEvent, (_, _) => _swipeStart = null, RoutingStrategies.Bubble, true);
        // Escape shares Back's route; GamepadNavigation only dedupes it against the pad's B.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                e.Handled = true;
                Back();
            }
        };
        Opened += OnOpened;
        Closed += OnClosed;
        // Same touch-promotion defense as OverlayWindow: a tap's late synthesized click is eaten here.
        Win32Properties.AddWndProcHookCallback(this, NativeMethods.SwallowTouchSynthesizedMouse);
    }

    /// <summary>Whether the Start menu is open.</summary>
    internal bool IsStartMenuOpen => StartMenu.IsVisible;

    /// <summary>Where controller and keyboard focus may move: the Start menu while it is open, else the whole bar.</summary>
    internal InputElement NavigationRoot => StartMenu.IsVisible ? StartMenu : SurfaceRoot;

    /// <summary>
    ///     Where focus lands when the bar opens: the chip of the window it opened over, else the first chip, else
    ///     Start.
    /// </summary>
    internal InputElement? DefaultFocusTarget
    {
        get
        {
            if (StartMenu.IsVisible)
            {
                return FocusSearch.FirstNavigable(StartMenu);
            }

            var chips = AppTiles.GetRealizedContainers()
                .Select(container => FocusSearch.FirstNavigable(container))
                .OfType<Button>()
                .ToList();
            return chips.FirstOrDefault(chip => chip.Classes.Contains("active"))
                   ?? chips.FirstOrDefault()
                   ?? (InputElement)StartButton;
        }
    }

    /// <summary>Raised with the open window the user picked.</summary>
    internal event Action<AppSwitcherEntry>? WindowPicked;

    /// <summary>Raised with the open window the user asked to close.</summary>
    internal event Action<AppSwitcherEntry>? WindowCloseRequested;

    /// <summary>Raised with a tray icon, whether its context menu was asked for, and the screen anchor for a menu.</summary>
    internal event Action<TrayIconEntry, bool, PixelPoint>? TrayIconActivated;

    /// <summary>Raised with a program to launch or whose window to focus.</summary>
    internal event Action<string>? ProgramChosen;

    /// <summary>
    ///     Raised to open the Overlay's Programs page, with the program whose editor explains why it cannot launch,
    ///     or null for the page itself.
    /// </summary>
    internal event Action<string?>? ManageProgramsRequested;

    /// <summary>Raised when a tap outside, a swipe down or Back dismisses the bar.</summary>
    internal event Action? DismissRequested;

    /// <summary>Follows the session's Programs owner for the Start menu, or none with null.</summary>
    /// <param name="programs">The owner, or null when the session has none.</param>
    internal void AttachPrograms(IProgramsSource? programs)
    {
        if (_programs is not null)
        {
            _programs.Changed -= OnProgramsChanged;
        }

        _programs = programs;
        if (programs is not null)
        {
            programs.Changed += OnProgramsChanged;
        }

        if (StartMenu.IsVisible)
        {
            RenderStartMenu();
        }
    }

    /// <summary>Opens the Start menu and moves focus into it.</summary>
    internal void OpenStartMenu()
    {
        if (StartMenu.IsVisible)
        {
            return;
        }

        _menuInvoker = StartButton;
        RenderStartMenu();
        StartMenu.IsVisible = true;
        StartButton.Classes.Set("open", true);
        StartScroller.Offset = default;
        Dispatcher.UIThread.Post(
            () => FocusSearch.FirstNavigable(StartMenu)?.Focus(NavigationMethod.Directional),
            DispatcherPriority.Background);
    }

    /// <summary>Closes the Start menu, releases its rows' borrowed icons and returns focus to Start.</summary>
    /// <returns>Whether the menu was open.</returns>
    internal bool CloseStartMenu()
    {
        if (!StartMenu.IsVisible)
        {
            return false;
        }

        StartMenu.IsVisible = false;
        StartButton.Classes.Set("open", false);
        ReleaseStartItems();
        (_menuInvoker ?? StartButton).Focus(NavigationMethod.Directional);
        _menuInvoker = null;
        return true;
    }

    /// <summary>One Back step: the Start menu closes first, then the bar asks to be dismissed.</summary>
    internal void Back()
    {
        if (!CloseStartMenu())
        {
            DismissRequested?.Invoke();
        }
    }

    /// <summary>
    ///     Controller X: a focused tray icon opens its context menu and a focused window chip closes its window.
    /// </summary>
    /// <param name="focused">The focused element, or null.</param>
    internal void RequestSecondaryAction(InputElement? focused)
    {
        switch (focused)
        {
            case Control { DataContext: TrayIconEntry entry } control:
                Log.Info($"Taskbar X: tray context menu for '{entry.Tip}'.");
                TrayIconActivated?.Invoke(entry, true, AnchorAbove(control));
                return;
            case Control { DataContext: AppSwitcherEntry window }:
                WindowCloseRequested?.Invoke(window);
                return;
            default:
                Log.Info($"Taskbar X: focused element has no secondary action ({focused?.GetType().Name ?? "none"}).");
                return;
        }
    }

    /// <summary>
    ///     Re-reads the programs into the open Start menu, keeping focus on the same entry when it is still listed.
    /// </summary>
    private void RenderStartMenu()
    {
        var focusedTag = (FocusManager?.GetFocusedElement() as Control)?.Tag as string;
        ReleaseStartItems();
        StartItems.Children.Clear();
        if (_programs is not { } programs)
        {
            StartCaption.Text = "Programs are unavailable in this session.";
            StartResult.IsVisible = false;
            StartScroller.IsVisible = false;
            ManagePrograms.IsVisible = false;
            return;
        }

        ManagePrograms.IsVisible = true;
        var states = programs.ReadState();
        StartCaption.Text = states.Length == 0
            ? "No programs added yet. Add the desktop tools you use under Manage programs."
            : "Launch a program, or focus its window when it already runs.";
        // The outcome of the last launch, as the Programs page shows it, so a refusal is explained here too.
        StartResult.Text = programs.LastResult;
        StartResult.IsVisible = programs.LastResult.Length > 0;
        foreach (var state in states)
        {
            var id = state.Definition.Id;
            StartItems.Children.Add(new ProgramActionRow(state, () => ChooseProgram(id)) { Tag = ProgramTagPrefix + id });
        }

        StartScroller.IsVisible = states.Length > 0;

        if (focusedTag is not null && StartMenu.IsVisible)
        {
            (FocusSearch.Keyed(StartMenu, focusedTag) ?? FocusSearch.FirstNavigable(StartMenu))
                ?.Focus(NavigationMethod.Directional);
        }
    }

    /// <summary>Launches or focuses a program, or opens its editor when it can do neither, as the Programs page does.</summary>
    private void ChooseProgram(string id)
    {
        var state = _programs?.ReadState().FirstOrDefault(item => item.Definition.Id == id);
        if (state is { CanLaunch: false, Running: false })
        {
            ManageProgramsRequested?.Invoke(id);
            return;
        }

        ProgramChosen?.Invoke(id);
    }

    private void ReleaseStartItems()
    {
        foreach (var row in StartItems.Children.OfType<ProgramActionRow>())
        {
            row.ReleaseIcon();
        }

        StartItems.Children.Clear();
    }

    // The owner raises Changed on its own thread, possibly in bursts.
    private void OnProgramsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (StartMenu.IsVisible)
            {
                RenderStartMenu();
            }
        });
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _dock(this);
        DefaultFocusTarget?.Focus(NavigationMethod.Directional);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopSlide();
        AttachPrograms(null);
        ReleaseStartItems();
    }

    /// <summary>Covers the summoning window's display, renders at the desktop's DPI and slides the strip up.</summary>
    internal void Dock()
    {
        var screens = Screens;
        var screen = _preferredScreenPoint is { } point ? screens.ScreenFromPoint(point) : null;
        screen ??= screens.ScreenFromWindow(this) ?? screens.Primary;
        if (screen is null && screens.ScreenCount > 0)
        {
            screen = screens.All[0];
        }

        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        Position = new PixelPoint(bounds.X, bounds.Y);
        var scaling = StatusPanel.CurrentWindowScale(this);
        var factor = OverlayPlacement.ComputeContentScale(_uiScale, scaling, bounds.Width, bounds.Height);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            RootScale.LayoutTransform = new ScaleTransform(factor, factor);
        }

        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;
        UpdateLayout();
        StartSlide();
    }

    private void StartSlide()
    {
        StopSlide();
        _slideFrom = Strip.Bounds.Height;
        StripSlide().Y = _slideFrom;
        _slideStartedUtc = DateTime.UtcNow;
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slideTimer.Tick += OnSlideTick;
        _slideTimer.Start();
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        const double durationMs = 180;
        var progress = Math.Clamp((DateTime.UtcNow - _slideStartedUtc).TotalMilliseconds / durationMs, 0, 1);
        // The same cubic ease-out as the Quick access sheet.
        StripSlide().Y = _slideFrom * Math.Pow(1 - progress, 3);
        if (progress >= 1)
        {
            StopSlide();
        }
    }

    private void StopSlide()
    {
        if (_slideTimer is null)
        {
            return;
        }

        _slideTimer.Stop();
        _slideTimer.Tick -= OnSlideTick;
        _slideTimer = null;
        StripSlide().Y = 0;
    }

    private TranslateTransform StripSlide()
    {
        return (TranslateTransform)Strip.RenderTransform!;
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        DismissRequested?.Invoke();
    }

    // A swipe down anywhere on the bar dismisses it. Touch and pen only: a mouse drag is a click attempt.
    private void OnSwipePressed(object? sender, PointerPressedEventArgs e)
    {
        _swipeDismissed = false;
        _swipeStart = e.Pointer.Type is PointerType.Touch or PointerType.Pen ? e.GetPosition(SurfaceRoot) : null;
    }

    private void OnSwipeMoved(object? sender, PointerEventArgs e)
    {
        if (_swipeStart is not { } start || _swipeDismissed)
        {
            return;
        }

        var position = e.GetPosition(SurfaceRoot);
        if (!IsSwipeDown(position.X - start.X, position.Y - start.Y))
        {
            return;
        }

        _swipeDismissed = true;
        _swipeStart = null;
        DismissRequested?.Invoke();
    }

    private void OnSwipeReleased(object? sender, PointerReleasedEventArgs e)
    {
        // The release that ends a dismissing swipe must not also activate the control it started on.
        if (_swipeDismissed)
        {
            e.Handled = true;
        }

        _swipeStart = null;
    }

    /// <summary>Whether a drag is a deliberate swipe down: far enough, and mostly downward.</summary>
    /// <param name="dx">Net horizontal travel in DIPs.</param>
    /// <param name="dy">Net vertical travel in DIPs; positive is down.</param>
    /// <returns>True once the drag dismisses the bar.</returns>
    internal static bool IsSwipeDown(double dx, double dy)
    {
        return dy >= SwipeDownDistance && dy >= 2 * Math.Abs(dx);
    }

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (!CloseStartMenu())
        {
            OpenStartMenu();
        }
    }

    private void OnManagePrograms(object? sender, RoutedEventArgs e)
    {
        ManageProgramsRequested?.Invoke(null);
    }

    private void OnPickWindow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AppSwitcherEntry entry)
        {
            WindowPicked?.Invoke(entry);
        }
    }

    private void OnCloseWindow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AppSwitcherEntry entry)
        {
            WindowCloseRequested?.Invoke(entry);
        }
    }

    /// <summary>Tap, A or left click: the icon's primary activation.</summary>
    private void OnTrayClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TrayIconEntry entry } control)
        {
            TrayIconActivated?.Invoke(entry, false, AnchorAbove(control));
        }
    }

    /// <summary>Right click: the icon's context menu. Button.Click never fires for the right button.</summary>
    private void OnTrayPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right
            || sender is not Control { DataContext: TrayIconEntry entry } control)
        {
            return;
        }

        e.Handled = true;
        TrayIconActivated?.Invoke(entry, true, AnchorAbove(control));
    }

    /// <summary>The screen point just above an icon's centre, where its app anchors a popup menu.</summary>
    private static PixelPoint AnchorAbove(Control control)
    {
        var point = control.PointToScreen(new Point(control.Bounds.Width / 2, 0));
        return new PixelPoint(point.X, point.Y);
    }

    private static void OnStripGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control and not ScrollViewer)
        {
            control.BringIntoView();
        }
    }
}

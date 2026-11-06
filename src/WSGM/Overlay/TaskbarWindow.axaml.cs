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
    private readonly OverlaySlide _slide;
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
        // The strip slides up from below the bottom edge.
        _slide = new OverlaySlide(progress => StripSlide().Y = _slideFrom * (1 - progress));
        InitializeComponent();
        DataContext = switcher;
        OpenAppsStrip.Attach(TileScroller, TrayScroller, entry => WindowPicked?.Invoke(entry),
            (entry, contextMenu, pill) =>
                TrayIconActivated?.Invoke(entry, contextMenu, OpenAppsStrip.AnchorAbove(pill)),
            entry => WindowCloseRequested?.Invoke(entry));
        StartScroller.AddHandler(GotFocusEvent, OpenAppsStrip.BringFocusedIntoView, RoutingStrategies.Bubble);
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

    /// <summary>
    ///     Raised with a chosen program. The controller launches or focuses it, or opens its editor when it can do
    ///     neither, by the same rule as the Programs page.
    /// </summary>
    internal event Action<string>? ProgramChosen;

    /// <summary>Raised to open the Overlay's Programs page, where programs are added, edited and removed.</summary>
    internal event Action? ManageProgramsRequested;

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
                TrayIconActivated?.Invoke(entry, true, OpenAppsStrip.AnchorAbove(control));
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
            StartItems.Children.Add(new ProgramActionRow(state, () => ProgramChosen?.Invoke(id))
                { Tag = ProgramTagPrefix + id });
        }

        StartScroller.IsVisible = states.Length > 0;

        if (focusedTag is not null && StartMenu.IsVisible)
        {
            (FocusSearch.Keyed(StartMenu, focusedTag) ?? FocusSearch.FirstNavigable(StartMenu))
                ?.Focus(NavigationMethod.Directional);
        }
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
        _slide.Stop();
        AttachPrograms(null);
        ReleaseStartItems();
    }

    /// <summary>Covers the summoning window's display, renders at the desktop's DPI and slides the strip up.</summary>
    internal void Dock()
    {
        if (OverlayPlacement.Cover(this, RootScale, _uiScale, _preferredScreenPoint, "Taskbar") is null)
        {
            return;
        }

        // The strip's height must be final before the slide starts from it.
        UpdateLayout();
        _slideFrom = Strip.Bounds.Height;
        _slide.Start();
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
        ManageProgramsRequested?.Invoke();
    }
}

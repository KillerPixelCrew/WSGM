using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LoadingIndicators.Avalonia;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     The borderless startup splash shown while Steam Big Picture launches.
///     Its whole look (background, vignette, text, spinner, logo, placements) comes
///     from a <see cref="SplashConfig" />; elements that are disabled or fail to load
///     are omitted from the visual tree entirely.
/// </summary>
public partial class BootSplashWindow : Window
{
    private const double SweepPeriodMs = 1600;
    private const double SweepLineThickness = 3;

    /// <summary>
    ///     Bottom margin of the bottom-edge sweep line, keeping it clear of
    ///     the desktop button (which occupies roughly the bottom 68 px on the right).
    /// </summary>
    private const double SweepBottomClearance = 88;

    private readonly List<(Control Control, SplashElementPlacement Placement)> _absoluteElements = [];
    private readonly bool _preview;
    private readonly RotateTransform _spinnerRotate = new();

    private readonly SplashConfig _splash;
    private readonly TranslateTransform _sweepTransform = new();
    private Canvas? _absoluteCanvas;
    private Bitmap? _backgroundBitmap;
    private Action? _fadeDone;
    private TimeSpan _fadeDuration;
    private DateTime _fadeStartedUtc;
    private DispatcherTimer? _fadeTimer;
    private nint _hwnd;
    private Bitmap? _logoBitmap;

    private Arc? _ringSpinner;
    private DateTime _spinnerStartedUtc;
    private DispatcherTimer? _spinnerTimer;
    private Panel? _sweepHost;
    private Border? _sweepLine;

    /// <summary>XAML-designer/default constructor: classic look, boot behavior.</summary>
    // ReSharper disable once UnusedMember.Global
    public BootSplashWindow()
        : this(new SplashConfig())
    {
    }

    /// <summary>Creates the splash window styled by <paramref name="splash" />.</summary>
    /// <param name="splash">The splash customization to render. Must not be null.</param>
    /// <param name="preview">
    ///     True when shown as a Settings preview: Escape and any
    ///     pointer press close the window; all rendering behavior stays identical.
    /// </param>
    public BootSplashWindow(SplashConfig splash, bool preview = false)
    {
        ArgumentNullException.ThrowIfNull(splash);
        _splash = splash;
        _preview = preview;
        InitializeComponent();
        Background = new SolidColorBrush(SplashStyle.ParseColor(splash.BackgroundColor, Colors.Black));
        BuildStyledContent();
        Opened += OnOpened;
        Closed += (_, _) =>
        {
            StopTimers();
            // Bitmaps are disposed strictly AFTER the timers stopped — a late tick
            // must never touch a disposed source.
            _backgroundBitmap?.Dispose();
            _backgroundBitmap = null;
            _logoBitmap?.Dispose();
            _logoBitmap = null;
        };

        // Touch pass-through defense, same as OverlayWindow: Avalonia never marks
        // touch raw events handled, so WM_POINTER falls to DefWindowProc, which
        // promotes a tap into a delayed synthesized mouse click. Eat those here so
        // a splash tap can never land on whatever the splash was covering.
        Win32Properties.AddWndProcHookCallback(
            this,
            NativeMethods.SwallowTouchSynthesizedMouse);

        if (!_preview)
        {
            return;
        }

        KeyDown += OnPreviewKeyDown;
        PointerPressed += OnPreviewPointerPressed;
    }

    /// <summary>
    ///     The control gamepad navigation should land on for the first D-pad
    ///     press. Nothing is focused on open, so a stray A press activates nothing.
    /// </summary>
    internal InputElement DefaultFocusTarget => DesktopButton;

    /// <summary>Raised when the user chooses the desktop fallback.</summary>
    public event Action? DesktopRequested;

    /// <summary>Shows, or clears, the line describing what the transition is doing.</summary>
    /// <param name="line">The line to show; null or blank hides the element again.</param>
    internal void SetStatus(string? line)
    {
        StatusText.Text = line ?? "";
        StatusText.IsVisible = !string.IsNullOrWhiteSpace(line);
    }

    /// <summary>
    ///     Renames the button. The same control means "cancel this" before Game Mode has
    ///     taken the session and "give up on it" afterwards, and only the label can say which.
    /// </summary>
    /// <param name="label">The label to show.</param>
    internal void SetActionLabel(string label)
    {
        DesktopButton.Content = label;
    }


    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Close();
    }

    /// <summary>
    ///     Builds the configured visual tree. Every element is optional and
    ///     only ever ADDED when enabled and loadable — disabled/broken elements are
    ///     left out of the tree entirely (StackPanel Spacing would otherwise gap
    ///     around invisible children). The desktop button stays the last child of
    ///     <c>RootPanel</c> so it remains topmost.
    /// </summary>
    private void BuildStyledContent()
    {
        var textBrush = new SolidColorBrush(SplashStyle.ParseColor(_splash.TextColor, Colors.White));
        var captionBrush = new SolidColorBrush(SplashStyle.ParseColor(_splash.CaptionColor, Color.Parse("#666666")));
        var spinnerBrush = new SolidColorBrush(SplashStyle.ParseColor(_splash.SpinnerColor, Colors.White));

        // Background image (below everything else).
        var backgroundImageLoaded = false;
        if (!string.IsNullOrWhiteSpace(_splash.BackgroundImagePath))
        {
            _backgroundBitmap = TryLoadBitmap(_splash.BackgroundImagePath, SplashDecode.BackgroundWidth);
            if (_backgroundBitmap is not null)
            {
                AddLayer(new Image { Source = _backgroundBitmap, Stretch = Stretch.UniformToFill });
                backgroundImageLoaded = true;
            }
        }

        // Vignette (darkens edges, above the background, below the elements).
        if (_splash.VignetteEnabled)
        {
            AddLayer(CreateVignette());
        }

        // Logo.
        Control? logo = null;
        if (!string.IsNullOrWhiteSpace(_splash.LogoImagePath))
        {
            var maxSize = Math.Max(1, _splash.LogoMaxSize);
            _logoBitmap = TryLoadBitmap(
                _splash.LogoImagePath,
                (sourceWidth, sourceHeight) => SplashDecode.LogoWidth(maxSize, sourceWidth, sourceHeight));
            if (_logoBitmap is not null)
            {
                logo = new Image
                {
                    Source = _logoBitmap,
                    Stretch = Stretch.Uniform,
                    MaxWidth = maxSize,
                    MaxHeight = maxSize,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            }
        }

        // Spinner. SweepLine is edge-hosted and ignores SpinnerPlacement; the
        // other styles produce a control placed like any element.
        Control? spinner = null;
        var spinnerSize = Math.Max(1, _splash.SpinnerSize);
        switch (_splash.SpinnerStyle)
        {
            case SplashSpinnerStyle.Off:
                break;
            case SplashSpinnerStyle.Ring:
                _ringSpinner = new Arc
                {
                    Width = spinnerSize,
                    Height = spinnerSize,
                    Stroke = spinnerBrush,
                    StrokeThickness = 3,
                    StartAngle = 0,
                    SweepAngle = 270,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    RenderTransform = _spinnerRotate
                };
                spinner = _ringSpinner;
                break;
            case SplashSpinnerStyle.SweepLine:
                BuildSweepLine(spinnerBrush);
                break;
            case SplashSpinnerStyle.LiArc:
            case SplashSpinnerStyle.LiArcs:
            case SplashSpinnerStyle.LiArcsRing:
            case SplashSpinnerStyle.LiDoubleBounce:
            case SplashSpinnerStyle.LiFlipPlane:
            case SplashSpinnerStyle.LiPulse:
            case SplashSpinnerStyle.LiRing:
            case SplashSpinnerStyle.LiThreeDots:
            case SplashSpinnerStyle.LiWave:
            default:
                spinner = new LoadingIndicator
                {
                    Mode = MapIndicatorMode(_splash.SpinnerStyle),
                    Foreground = spinnerBrush,
                    Width = spinnerSize,
                    Height = spinnerSize,
                    IsActive = true,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                break;
        }

        // Text stack: logo above title, caption under title, spinner below the
        // caption — WithText elements ride this stack (Spacing 26, classic look).
        var captionVisible = _splash.TextEnabled && !string.IsNullOrEmpty(_splash.Caption);
        var stack = new StackPanel { Spacing = 26 };
        if (logo is not null && _splash.LogoPlacement.Mode == SplashPlacementMode.WithText)
        {
            stack.Children.Add(logo);
        }

        if (_splash.TextEnabled && !string.IsNullOrEmpty(_splash.Text))
        {
            stack.Children.Add(new TextBlock
            {
                Text = _splash.Text,
                FontSize = Math.Max(1, _splash.TitleFontSize),
                FontWeight = FontWeight.Light,
                Foreground = textBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        if (captionVisible)
        {
            stack.Children.Add(new TextBlock
            {
                Text = _splash.Caption,
                FontSize = Math.Max(1, _splash.CaptionFontSize),
                Foreground = captionBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        if (spinner is not null && _splash.SpinnerPlacement.Mode == SplashPlacementMode.WithText)
        {
            stack.Children.Add(spinner);
        }

        if (stack.Children.Count > 0)
        {
            PlaceElement(stack, _splash.TextPlacement);
        }

        // Independently placed spinner/logo (Anchor or Absolute modes).
        if (spinner is not null && _splash.SpinnerPlacement.Mode != SplashPlacementMode.WithText)
        {
            PlaceElement(spinner, _splash.SpinnerPlacement);
        }

        if (logo is not null && _splash.LogoPlacement.Mode != SplashPlacementMode.WithText)
        {
            PlaceElement(logo, _splash.LogoPlacement);
        }

        Log.Info(
            $"Splash style: bg={_splash.BackgroundColor}, bgImage={(backgroundImageLoaded ? "yes" : "no")}, " +
            $"logo={(logo is not null ? "yes" : "no")}, text={(_splash.TextEnabled ? "on" : "off")}, " +
            $"caption={(captionVisible ? "on" : "off")}, spinner={_splash.SpinnerStyle} {spinnerSize}px, " +
            $"textPlacement={DescribePlacement(_splash.TextPlacement)}, preview={_preview}");
    }

    /// <summary>
    ///     Adds a layer to the root panel just before the desktop button,
    ///     which must always stay the last (topmost) child.
    /// </summary>
    private void AddLayer(Control control)
    {
        RootPanel.Children.Insert(RootPanel.Children.Count - 1, control);
    }

    /// <summary>
    ///     Places an element per its configured placement: alignment + margin
    ///     directly on the element for anchor mode, or onto the shared Canvas (position
    ///     applied once the window covers the screen) for absolute mode.
    /// </summary>
    private void PlaceElement(Control control, SplashElementPlacement placement)
    {
        if (placement.Mode == SplashPlacementMode.Absolute)
        {
            if (_absoluteCanvas is null)
            {
                _absoluteCanvas = new Canvas();
                AddLayer(_absoluteCanvas);
            }

            _absoluteCanvas.Children.Add(control);
            _absoluteElements.Add((control, placement));
            return;
        }

        // Anchor mode ignores the screen/element sizes entirely.
        var layout = SplashStyle.MapPlacement(placement, default, default);
        control.HorizontalAlignment = layout.HorizontalAlignment;
        control.VerticalAlignment = layout.VerticalAlignment;
        control.Margin = layout.Margin;
        AddLayer(control);
    }

    /// <summary>
    ///     Recomputes Canvas positions for absolute-mode elements against the
    ///     current window size (called after the window covers the screen and again
    ///     after display changes; clamping needs real dimensions).
    /// </summary>
    private void UpdateAbsolutePositions()
    {
        if (_absoluteElements.Count == 0)
        {
            return;
        }

        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        foreach (var (control, placement) in _absoluteElements)
        {
            control.Measure(Size.Infinity);
            var layout = SplashStyle.MapPlacement(placement, size, control.DesiredSize);
            Canvas.SetLeft(control, layout.CanvasX);
            Canvas.SetTop(control, layout.CanvasY);
        }
    }

    private void BuildSweepLine(IBrush brush)
    {
        _sweepLine = new Border
        {
            Height = SweepLineThickness,
            Background = brush,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _sweepTransform
        };
        var bottom = _splash.SweepEdge == SweepEdge.Bottom;
        _sweepHost = new Panel
        {
            Height = SweepLineThickness,
            VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            // Bottom edge keeps clear of the desktop button; the top edge has
            // nothing to collide with.
            Margin = bottom ? new Thickness(0, 0, 0, SweepBottomClearance) : default
        };
        _sweepHost.Children.Add(_sweepLine);
        AddLayer(_sweepHost);
    }

    private static LoadingIndicatorMode MapIndicatorMode(SplashSpinnerStyle style)
    {
        return style switch
        {
            SplashSpinnerStyle.LiArc => LoadingIndicatorMode.Arc,
            SplashSpinnerStyle.LiArcs => LoadingIndicatorMode.Arcs,
            SplashSpinnerStyle.LiArcsRing => LoadingIndicatorMode.ArcsRing,
            SplashSpinnerStyle.LiDoubleBounce => LoadingIndicatorMode.DoubleBounce,
            SplashSpinnerStyle.LiFlipPlane => LoadingIndicatorMode.FlipPlane,
            SplashSpinnerStyle.LiPulse => LoadingIndicatorMode.Pulse,
            SplashSpinnerStyle.LiRing => LoadingIndicatorMode.Ring,
            SplashSpinnerStyle.LiThreeDots => LoadingIndicatorMode.ThreeDots,
            SplashSpinnerStyle.LiWave => LoadingIndicatorMode.Wave,
            _ => LoadingIndicatorMode.Ring
        };
    }

    private static string DescribePlacement(SplashElementPlacement placement)
    {
        return placement.Mode == SplashPlacementMode.Absolute
            ? $"Absolute({placement.X},{placement.Y})"
            : $"Anchor({placement.Anchor})";
    }

    private static Border CreateVignette()
    {
        return new Border
        {
            IsHitTestVisible = false,
            Background = new RadialGradientBrush
            {
                Center = RelativePoint.Center,
                GradientOrigin = RelativePoint.Center,
                RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55),
                    new GradientStop(Color.FromArgb(0xA0, 0, 0, 0), 1)
                }
            }
        };
    }

    /// <summary>Loads a splash image, or returns null and logs why the element is left out.</summary>
    /// <param name="path">Full path to the image file.</param>
    /// <param name="decodeWidthFor">The element's decode width bound for the source's declared size.</param>
    private static Bitmap? TryLoadBitmap(string path, Func<int, int, int> decodeWidthFor)
    {
        try
        {
            if (!File.Exists(path))
            {
                Log.Warn($"Splash: image not found, skipping element: {path}");
                return null;
            }

            // The header's declared size, read without a decode, refuses absurd dimensions before any
            // pixel buffer exists. A header that lies fails the decode below like any other bad file.
            if (!ImageHeader.TryReadBoundedSize(
                    path, "Splash", $"skipping element: {path}", out var sourceWidth, out var sourceHeight))
            {
                return null;
            }

            if (SplashDecode.ScaledWidth(decodeWidthFor(sourceWidth, sourceHeight), sourceWidth) is not { } width)
            {
                return new Bitmap(path);
            }

            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash: failed to load image '{path}', skipping element: {ex.Message}");
            return null;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        CoverPrimaryScreen();
        UpdateAbsolutePositions();
        // ClientSize lags the CoverPrimaryScreen resize until the platform delivers
        // it — absolute-mode clamping must recompute once the real size arrives.
        Resized += OnResized;
        Closed += (_, _) => Resized -= OnResized;
        // Service boots apply the 100% game-mode scale while the splash is already
        // up (the cover must precede the posture change) — re-cover so the DPI
        // change can't leave desktop pixels exposed around a stale-sized splash.
        Screens.Changed += OnScreensChanged;
        Closed += (_, _) => Screens.Changed -= OnScreensChanged;

        // Layered style applied once, fully opaque — flipping it mid-fade risks a
        // first-frame flicker. The fade is cosmetic; without an HWND it is skipped.
        _hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (_hwnd != 0)
        {
            var ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GwlExStyle);
            _ = NativeMethods.SetWindowLong(_hwnd, NativeMethods.GwlExStyle,
                ex | NativeMethods.WsExLayered);
            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, NativeMethods.LwaAlpha);
        }

        // The animation timer exists only for the in-repo spinners; the Li*
        // styles animate themselves and Off has nothing to animate.
        if (_ringSpinner is null && _sweepLine is null)
        {
            return;
        }

        _spinnerStartedUtc = DateTime.UtcNow;
        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spinnerTimer.Tick += OnSpinnerTick;
        _spinnerTimer.Start();
    }

    private void OnSpinnerTick(object? sender, EventArgs e)
    {
        // Time-based so a busy UI thread can't slow it (ring: one revolution per
        // second; sweep line: one edge-to-edge pass per 1.6 s).
        var elapsed = (DateTime.UtcNow - _spinnerStartedUtc).TotalMilliseconds;
        if (_ringSpinner is not null)
        {
            _spinnerRotate.Angle = elapsed * 0.36 % 360;
        }

        if (_sweepLine is null || _sweepHost is null)
        {
            return;
        }

        var hostWidth = _sweepHost.Bounds.Width;
        if (hostWidth <= 0)
        {
            return;
        }

        var lineWidth = Math.Min(hostWidth, Math.Max(120, hostWidth * 0.2));
        _sweepLine.Width = lineWidth;
        var progress = elapsed % SweepPeriodMs / SweepPeriodMs;
        _sweepTransform.X = progress * (hostWidth - lineWidth);
    }

    /// <summary>
    ///     Fades the whole window (layered alpha) over what's underneath, then
    ///     invokes <paramref name="onDone" />. Degrades to an immediate callback when the
    ///     platform handle is unavailable or another fade is active. Call on the UI thread.
    /// </summary>
    /// <param name="duration">Positive fade duration.</param>
    /// <param name="onDone">UI-thread callback invoked after fading, or synchronously when no handle exists or a fade is already active.</param>
    public void BeginFadeOut(TimeSpan duration, Action onDone)
    {
        if (_hwnd == 0 || _fadeTimer is not null)
        {
            onDone();
            return;
        }

        _fadeDuration = duration;
        _fadeDone = onDone;
        _fadeStartedUtc = DateTime.UtcNow;
        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _fadeTimer.Tick += OnFadeTick;
        _fadeTimer.Start();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        var progress = Math.Clamp(
            (DateTime.UtcNow - _fadeStartedUtc).TotalMilliseconds / _fadeDuration.TotalMilliseconds, 0, 1);
        NativeMethods.SetLayeredWindowAttributes(
            _hwnd, 0, (byte)Math.Round(255 * (1 - progress)), NativeMethods.LwaAlpha);
        if (progress < 1)
        {
            return;
        }

        _fadeTimer?.Stop();
        var done = _fadeDone;
        _fadeDone = null;
        done?.Invoke();
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        UpdateAbsolutePositions();
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        CoverPrimaryScreen();
        UpdateAbsolutePositions();
        Log.Info("Boot splash resized after display change.");
    }

    /// <summary>
    ///     Primary display only — same assumption as the overlay; startup apps
    ///     on a secondary screen may still flash (accepted on single-screen handhelds).
    /// </summary>
    private void CoverPrimaryScreen()
    {
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        // Window scaling, not screen.Scaling — the screens cache is stale after a
        // runtime display-scale flip (see OverlayWindow.DockToRightEdge).
        var scaling = DesktopScaling;
        Position = new PixelPoint(bounds.X, bounds.Y);
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;
    }

    private void OnDesktop(object? sender, RoutedEventArgs e)
    {
        DesktopRequested?.Invoke();
    }

    private void StopTimers()
    {
        _spinnerTimer?.Stop();
        _spinnerTimer = null;
        _fadeTimer?.Stop();
        _fadeTimer = null;
        _fadeDone = null;
    }
}

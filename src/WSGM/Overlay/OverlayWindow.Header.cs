using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    /// <summary>
    ///     Share of the bottom rail's inner width the tray may claim before scrolling,
    ///     leaving room for open apps and their actions.
    /// </summary>
    private const double TrayWidthFraction = 0.30;

    /// <summary>
    ///     Floor for the tray budget: one tray pill plus its spacing, so a
    ///     single icon is never clipped even on an absurdly narrow display.
    /// </summary>
    private const double TrayMinWidth = 40;

    /// <summary>
    ///     Horizontal padding of the bottom apps and tray rail.
    /// </summary>
    private const double RailHorizontalPadding = 32;

    private bool _profileScopePopupOpen;
    private bool _profileScopeSynchronizing;
    private string? _profileScopeTarget;
    private bool _profileScopeWriting;

    private void OnManageProfiles(object? sender, RoutedEventArgs e)
    {
        if (_performanceSource is not { } source)
        {
            return;
        }

        var editor = new ApplicationProfilesView(source, _deviceLifetime.Token);
        ShowSurface(editor, SurfaceKind.Utility, "Application profiles", editor.DefaultFocusTarget);
    }

    private void RefreshHeaderProfile()
    {
        var scope = _performanceSource?.ProfileScope;
        var target = scope?.Target;
        ManageProfiles.IsEnabled = _performanceSource is not null;
        _profileScopeSynchronizing = true;
        try
        {
            if (_profileScopeTarget != target?.ApplicationId)
            {
                HeaderProfile.IsDropDownOpen = false;
                _profileScopePopupOpen = false;
            }

            _profileScopeTarget = target?.ApplicationId;
            if (!_profileScopePopupOpen)
            {
                HeaderProfile.SelectedIndex = scope?.Enabled == true ? 1 : 0;
            }

            HeaderProfile.IsEnabled = target is not null && !_profileScopeWriting;
            var matched = ApplicationProfileRules.Match(_performanceSource?.Profiles ?? [], target?.ApplicationId,
                target?.RtssProfileName, item => item.ApplicationId, item => item.ProcessNames);
            var name = matched is { Name.Length: > 0 }
                ? matched.Name
                : target?.RtssProfileName ?? target?.ApplicationId;
            ProfileContext.Text = name is null ? "Profile" : "Profile: " + name;
            ToolTip.SetTip(HeaderProfile, name is null
                ? "Start or focus an application to give it separate settings."
                : $"Settings profile for {name}. Global uses shared defaults; Per-application keeps separate values.");
        }
        finally
        {
            _profileScopeSynchronizing = false;
        }
    }

    private void OnHeaderProfileOpened(object? sender, EventArgs e)
    {
        _profileScopePopupOpen = true;
    }

    private async void OnHeaderProfileClosed(object? sender, EventArgs e)
    {
        _profileScopePopupOpen = false;
        await CommitHeaderProfileAsync();
    }

    private async void OnHeaderProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_performanceSource is not null && !_profileScopePopupOpen && sender is ComboBox { IsDropDownOpen: false })
        {
            await CommitHeaderProfileAsync();
        }
    }

    private async Task CommitHeaderProfileAsync()
    {
        if (_profileScopeSynchronizing || _profileScopeWriting || _closed
            || _performanceSource is not { } source || _profileScopeTarget is not { } target)
        {
            return;
        }

        var enabled = HeaderProfile.SelectedIndex == 1;
        if (source.ProfileScope.Target?.ApplicationId != target || source.ProfileScope.Enabled == enabled)
        {
            RefreshHeaderProfile();
            return;
        }

        _profileScopeWriting = true;
        HeaderProfile.IsEnabled = false;
        string? failure = null;
        try
        {
            await source.SetProfileScopeAsync(target, enabled, _deviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_deviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = "Profile change was not confirmed: " + ex.Message;
            Log.Warn(failure);
        }
        finally
        {
            _profileScopeWriting = false;
            if (!_closed)
            {
                RefreshHeaderProfile();
                if (failure is not null)
                {
                    ToolTip.SetTip(HeaderProfile, failure);
                }
            }
        }
    }

    private void OnWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Classes.Set("compact", e.NewSize.Width < 1150);
        TrayScroller.MaxWidth = ComputeTrayMaxWidth(e.NewSize.Width, 1);
    }

    /// <summary>
    ///     Lands controller focus on the first Open apps chip — the bottom-swipe
    ///     entry point, which exists to reach the running programs in one gesture.
    /// </summary>
    internal void FocusOpenApps()
    {
        FocusSearch.FirstNavigable(AppTiles)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    ///     Y: switch to the window after the foreground one in strip order,
    ///     wrapping — an Alt+Tab step from the sheet. Nothing to do with fewer than two
    ///     windows.
    /// </summary>
    internal void CycleNextApp()
    {
        var entries = _switcher.Entries;
        if (entries.Count < 2)
        {
            Log.Info("Next app: fewer than two windows open.");
            return;
        }

        var active = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].IsActive)
            {
                continue;
            }

            active = i;
            break;
        }

        WindowPicked?.Invoke(entries[(active + 1) % entries.Count]);
    }

    private void OnPickWindow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AppSwitcherEntry entry)
        {
            WindowPicked?.Invoke(entry);
        }
    }

    /// <summary>Tap / A-button / left click → the icon's primary activation.</summary>
    private void OnTrayClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TrayIconEntry entry } control)
        {
            TrayIconActivated?.Invoke(entry, false, AnchorBelow(control));
        }
    }

    /// <summary>
    ///     Right mouse button → the icon's context menu (many tray apps only
    ///     respond to this). Button.Click never fires for the right button, so this
    ///     rides PointerReleased.
    /// </summary>
    private void OnTrayPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right
            || sender is not Control { DataContext: TrayIconEntry entry } control)
        {
            return;
        }

        e.Handled = true;
        TrayIconActivated?.Invoke(entry, true, AnchorBelow(control));
    }

    /// <summary>
    ///     Screen position just below the pill's centre — where the app
    ///     should anchor a popup menu (v4 coordinate protocol).
    /// </summary>
    private static PixelPoint AnchorBelow(Control control)
    {
        var point = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height));
        return new PixelPoint(point.X, point.Y);
    }

    private void OnRadioTileClicked(object? sender, RoutedEventArgs e)
    {
        // The panel stays in this window's focus scope and navigation owner.
        RadioPanelRequested?.Invoke((sender as Control)?.Tag as string == "bluetooth");
    }

    private void OnAudioTileClicked(object? sender, RoutedEventArgs e)
    {
        AudioPanelRequested?.Invoke();
    }

    private void OnEjectTileClicked(object? sender, RoutedEventArgs e)
    {
        EjectPanelRequested?.Invoke();
    }

    /// <summary>
    ///     Scrolls a newly focused chip into its strip's viewport (app chips
    ///     and tray icons share this handler). Bubbles from the buttons; the scroll
    ///     viewers themselves are not focusable, and the call is a no-op when the chip
    ///     is already fully visible.
    /// </summary>
    private static void OnStripGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control and not ScrollViewer)
        {
            control.BringIntoView();
        }
    }

    /// <summary>
    ///     The widest the tray strip may become before it scrolls, so that
    ///     the open-app actions always fit. Pure: the width budget is unit-tested
    ///     against this method rather than against a live window.
    /// </summary>
    /// <param name="windowWidth">The sheet window's logical (DIP) width.</param>
    /// <param name="contentScale">
    ///     The factor RootScale applies to the content
    ///     (see <see cref="DockToTopEdge" />); 1.0 when untransformed.
    /// </param>
    /// <returns>A MaxWidth in the rail's pre-transform layout units.</returns>
    internal static double ComputeTrayMaxWidth(double windowWidth, double contentScale)
    {
        if (!double.IsFinite(windowWidth) || !double.IsFinite(contentScale) || contentScale <= 0)
        {
            return TrayMinWidth;
        }

        var inner = windowWidth / contentScale - RailHorizontalPadding;
        return Math.Max(TrayMinWidth, inner * TrayWidthFraction);
    }

    /// <summary>
    ///     Covers the summoning window's display and slides the live glass sheet into place.
    /// </summary>
    private void DockToTopEdge()
    {
        var screen = _preferredScreenPoint is { } point
            ? Screens.ScreenFromPoint(point)
            : null;
        screen ??= Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null && Screens.ScreenCount > 0)
        {
            screen = Screens.All[0];
        }

        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        // A new top-level starts on Windows' default monitor. Move its HWND onto the
        // selected display before reading its effective DPI; otherwise a secondary
        // display is sized using the primary display's scale. Do not use
        // screen.Scaling: Avalonia's screen cache can retain the pre-game-mode DPI
        // when no window existed to receive the display transition.
        Position = new PixelPoint(bounds.X, bounds.Y);
        var scaling = StatusPanel.CurrentWindowScale(this);
        // Render at the desktop's DPI: game mode forces displays to 100%, which
        // otherwise shrinks this DIP-sized sheet to millimeters on dense
        // handheld screens (device-reported). The content lays out in
        // desktop-DIP space (the factor divides the available size), the window
        // takes the scaled-up physical footprint.
        var factor = ComputeContentScale(_uiScale, scaling, bounds.Width, bounds.Height);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            Log.Info($"Quick access UI scale {factor:0.##}x (desktop DPI over current {scaling:0.##}).");
            _contentScale = factor;
            RootScale.LayoutTransform = new ScaleTransform(factor, factor);
        }

        Width = bounds.Width / scaling;
        Height = Math.Round(bounds.Height / scaling);
        TrayScroller.MaxWidth = ComputeTrayMaxWidth(Width, _contentScale);

        var heightPx = (int)Math.Ceiling(Height * scaling);
        _slideEnd = new PixelPoint(bounds.X, bounds.Y);
        _slideStart = new PixelPoint(bounds.X, bounds.Y - heightPx);
        Position = _slideStart;

        StopSlide();
        _slideStartedUtc = DateTime.UtcNow;
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slideTimer.Tick += OnSlideTick;
        _slideTimer.Start();
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        const double durationMs = 180;
        var progress = Math.Clamp((DateTime.UtcNow - _slideStartedUtc).TotalMilliseconds / durationMs, 0, 1);
        // Cubic ease-out keeps the movement quick without a sharp stop.
        var eased = 1 - Math.Pow(1 - progress, 3);
        Position = new PixelPoint(
            _slideEnd.X,
            (int)Math.Round(_slideStart.Y + (_slideEnd.Y - _slideStart.Y) * eased));

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
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is OnScreenKeyboard.EditorKeyEventArgs)
        {
            return;
        }

        if (e.Key is Key.PageUp or Key.PageDown && !HasActiveSurface)
        {
            if (e.Key == Key.PageUp)
            {
                SelectPreviousTab();
            }
            else
            {
                SelectNextTab();
            }

            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        if (!CloseActiveSurface() && !TryCancelSubView())
        {
            Dismissed?.Invoke();
        }

        e.Handled = true;
    }

    private void OnHomeApp(object? sender, RoutedEventArgs e)
    {
        HomeAppRequested?.Invoke();
    }

    private void OnDesktop(object? sender, RoutedEventArgs e)
    {
        DesktopRequested?.Invoke();
    }

    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    private void OnExitBigPicture(object? sender, RoutedEventArgs e)
    {
        ExitBigPictureRequested?.Invoke();
    }

    private void OnTaskManager(object? sender, RoutedEventArgs e)
    {
        TaskManagerRequested?.Invoke();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
    }

    internal static double ComputeContentScale(double uiScale, double renderScale, double width, double height)
    {
        if (!double.IsFinite(uiScale) || !double.IsFinite(renderScale) || renderScale <= 0
            || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return 1;
        }

        var maximumFit = Math.Min(width / renderScale / 980, height / renderScale / 640);
        // Game Mode supplies the saved desktop scale while Windows runs at 100%. Desktop mode
        // supplies one because native DPI already scales the window. Only the viewport fit may
        // reduce that native factor; it never changes the saved preference.
        return Math.Min(Math.Clamp(uiScale / renderScale, 1.0, 3.0), maximumFit);
    }

    private void OnGlassTransparencyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ActualTransparencyLevelProperty)
        {
            ApplyGlassTransparency();
        }
    }

    private void ApplyGlassTransparency()
    {
        GlassCanvas.Background = this.FindResource(ActualTransparencyLevel == WindowTransparencyLevel.None
            ? "DeckCanvasBrush"
            : "DeckGlassCanvasBrush") as IBrush;
    }
}

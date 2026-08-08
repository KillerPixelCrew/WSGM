using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Settings;

/// <summary>The interactive settings window for shell and game-mode configuration:
/// a bumper <see cref="TabStrip"/> over five always-alive pages (toggled by
/// visibility so their state survives switching) and a bottom status strip.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel = new();
    private readonly GamepadService _gamepad = new();
    private readonly ShortcutRecorders _recorders;
    private GamepadNavigation? _navigation;
    private OverlayController? _testOverlay;
    private BootSplashWindow? _splashPreview;
    private bool _closed;

    // When Settings is the on-screen surface in game mode it must hold the Steam
    // Input lease, exactly like the overlay: without it Steam's desktop profile
    // stays live over this window, grabs the pad from SDL and injects its own
    // desktop bindings (invariant 1) — the ghost/double input.
    //
    // The lease is HANDED OVER from the sidebar, not re-taken: the overlay keeps
    // its (shared, static SteamInputBlocker) lease held across the open instead of
    // releasing it, so Steam's controller is never dropped and re-revoked in the
    // handoff — the churn the user saw as "controller gone again seconds later".
    // This window then owns that same lease and drives it via SteamInputBlocker.
    //
    // It tracks focus, not just lifetime: held only while this window (or the
    // splash preview it drives by pad) is the active, non-minimized foreground,
    // so unfocusing or minimizing Settings hands the controller straight back to
    // Big Picture. The reconciler keeps at most one inject/release in flight and
    // re-runs on completion, so rapid focus flips coalesce instead of thrashing.
    private readonly bool _gameModeSurface;
    private readonly object _leaseSync = new();
    private bool _leaseEnabled;
    private bool _leaseHeld;
    private bool _leaseDesired;
    private bool _leaseBusy;

    // In game mode WSGM hosts the only taskbar, and it excludes own-process windows
    // (the overlay/taskbar/tray chrome). This window opts in so it stays reachable
    // after it drops behind Big Picture.
    private nint _switchableHwnd;

    /// <summary>Creates the settings window, builds the tab strip and connects
    /// controller navigation and the shortcut recorders.</summary>
    /// <param name="gameModeSurface">True when opened as the on-screen surface in
    /// game mode (from the overlay), which makes the window hold a Steam Input
    /// lease for its lifetime. The desktop settings paths leave it false.</param>
    public SettingsWindow(bool gameModeSurface = false)
    {
        _gameModeSurface = gameModeSurface;
        InitializeComponent();
        DataContext = _viewModel;
        _recorders = new ShortcutRecorders(_viewModel, () => _closed);

        Tabs.Tabs = new List<TabStripItem>
        {
            new("System", Icons.Monitor, 0),
            new("Steam", Icons.SteamLike, 1),
            new("Startup", Icons.Rocket, 2),
            new("Quick access", Icons.Panel, 3),
            new("Appearance", Icons.Palette, 4),
        };
        Tabs.SelectionChanged += OnTabSelectionChanged;

        // Controller navigation for the settings window itself. LB/RB cycle the
        // tab strip (which wraps at both ends).
        // Focus changes drive the lease; the value is fixed for the window's life.
        if (_gameModeSurface)
        {
            _leaseEnabled = ConfigStore.Load().SteamInputLeaseEnabled;
            Activated += (_, _) => UpdateLeaseDesired();
            Deactivated += (_, _) => UpdateLeaseDesired();
            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty)
                {
                    UpdateLeaseDesired();
                }
            };
        }
        Opened += (_, _) =>
        {
            _navigation = CreateWindowNavigation();
            _gamepad.Start();
            InheritSteamInputLease();
            if (_gameModeSurface)
            {
                _switchableHwnd = TryGetPlatformHandle()?.Handle ?? 0;
                WindowFinder.IncludeOwnWindow(_switchableHwnd);
            }
            // Brackets the window's lifetime for splash-theme imports: an imported
            // theme's images live in a temp staging directory this process pins open
            // until the matching EndImportSession below, because an unsaved import must
            // stay materializable for as long as this window can still save it. Opening
            // the session also sweeps orphans left by earlier sessions. Paired with
            // Opened (not the constructor) so a window that is built but never shown
            // cannot leave a session — and therefore a pinned directory — behind.
            SplashTheme.BeginImportSession();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _gamepad.Stop();
            WindowFinder.ExcludeOwnWindow(_switchableHwnd);
            // _closed makes the lease unwanted; the reconciler releases it.
            UpdateLeaseDesired();
            _navigation?.Dispose();
            _navigation = null;
            // The splash preview must not outlive Settings; its Closed handler
            // sees _closed and skips recreating window navigation.
            _splashPreview?.Close();
            _splashPreview = null;
            _testOverlay?.Dispose();
            _testOverlay = null;
            // The Appearance page live-applies accent picks to the running
            // Application as a preview. In the long-lived shell process an
            // unsaved close would otherwise leak that preview accent onto every
            // surface, so re-apply the persisted accent here (after a save this
            // re-applies the same color; after an abandoned preview it restores
            // the saved one).
            if (Avalonia.Application.Current is { } app)
            {
                Themes.AccentPalette.Apply(
                    app, Themes.AccentPalette.Parse(ConfigStore.Load().AccentColor));
            }
            // Same slot the two recorders were disposed in before they moved
            // into ShortcutRecorders (key recorder first, chord second).
            _recorders.Dispose();
            // LAST: nothing above may still read a staged import. Any save has long
            // committed the staged images into the stable splash assets by now, and an
            // abandoned import is exactly what this frees — up to ~128 MB of staged
            // images per import that used to stay pinned until the shell process
            // exited. Counted, so a second settings window's unsaved import (and any
            // other process's) survives this.
            SplashTheme.EndImportSession();
        };
    }

    /// <summary>One selection path for touch, mouse, keyboard and the LB/RB
    /// shoulder buttons: the TabStrip owns the index, this toggles the five
    /// always-alive pages' visibility.</summary>
    private void OnTabSelectionChanged(object? sender, TabStripSelectionChangedEventArgs e)
    {
        PageSystem.IsVisible = e.NewIndex == 0;
        PageSteam.IsVisible = e.NewIndex == 1;
        PageStartup.IsVisible = e.NewIndex == 2;
        PageQuickAccess.IsVisible = e.NewIndex == 3;
        PageAppearance.IsVisible = e.NewIndex == 4;

        // Land controller focus inside the newly shown page — without this the
        // next D-pad press falls back to the window's first focusable, which is
        // always the "System" tab button regardless of the active tab.
        var page = e.NewIndex switch
        {
            0 => (Control)PageSystem,
            1 => PageSteam,
            2 => PageStartup,
            3 => PageQuickAccess,
            _ => PageAppearance,
        };
        FocusFirstControl(page);
    }

    private static void FocusFirstControl(Control page)
    {
        foreach (var visual in page.GetVisualDescendants())
        {
            // TextBoxes are excluded for the same reason D-pad traversal skips
            // them: focusing one pops the touch keyboard.
            if (visual is InputElement { Focusable: true, IsEffectivelyEnabled: true } element
                && element is not TextBox
                && element.IsEffectivelyVisible)
            {
                element.Focus(NavigationMethod.Directional);
                return;
            }
        }
    }

    /// <summary>Shows the quick access panel for a local test (called by the
    /// Quick access page). Uses the real controller so behavior matches shell
    /// mode exactly; rebuilt for every test so unsaved glyph/input changes take
    /// effect immediately.</summary>
    internal void ShowTestOverlay()
    {
        _testOverlay?.Dispose();
        var config = _viewModel.SnapshotForTest();
        _testOverlay = new OverlayController(config, monitor: null, new SessionModes(config, monitor: null));
        _testOverlay.ShowOverlay();
    }

    /// <summary>Shows the game-mode taskbar for a local test (called by the
    /// Quick access page). Direct ShowTaskbar: the swipe routing's game-mode
    /// gate would bounce a dev desktop (explorer alive) back to quick access,
    /// so the button bypasses routing to make the bar locally testable.</summary>
    internal void ShowTestTaskbar()
    {
        _testOverlay?.Dispose();
        var config = _viewModel.SnapshotForTest();
        _testOverlay = new OverlayController(config, monitor: null, new SessionModes(config, monitor: null));
        _testOverlay.ShowTaskbar();
    }

    /// <summary>Creates the controller navigation attached to this window
    /// (initial Opened wiring and restoration after a splash preview closes).</summary>
    private GamepadNavigation CreateWindowNavigation() => new(_gamepad, this, back: Close,
        isNintendoLayout: () => _viewModel.GlyphStyleIndex == 2,
        tabPrevious: Tabs.SelectPrevious,
        tabNext: Tabs.SelectNext);

    /// <summary>Whether the lease should be held right now: only in game mode with
    /// the user opt-in, while this window is open, not minimized, and either active
    /// or driving the splash preview by pad. Reads UI state — UI thread only.</summary>
    private bool ShouldHoldLease()
        => _gameModeSurface && _leaseEnabled && !_closed
           && WindowState != WindowState.Minimized
           && (IsActive || _splashPreview is not null);

    /// <summary>Takes over the lease the sidebar handed off. It is already held, so
    /// this is a no-op that avoids releasing/re-injecting (the churn); the reconcile
    /// only acts if the handoff lease was somehow absent. UI thread.</summary>
    private void InheritSteamInputLease()
    {
        if (!_gameModeSurface || !_leaseEnabled)
        {
            return;
        }
        lock (_leaseSync)
        {
            // Held by the sidebar right up to this handoff; keep it as the window's.
            _leaseHeld = SteamInputBlocker.IsApplied;
            // Shown as the foreground surface — do not gate the initial state on
            // IsActive, which can still be false at Opened and would drop the lease.
            _leaseDesired = true;
        }
        ReconcileLease();
    }

    /// <summary>Recomputes whether the lease is wanted and kicks the reconciler.
    /// Called on every focus, window-state and child-surface change (UI thread).</summary>
    private void UpdateLeaseDesired()
    {
        lock (_leaseSync)
        {
            _leaseDesired = ShouldHoldLease();
        }
        ReconcileLease();
    }

    /// <summary>Moves the shared lease toward the desired state with at most one
    /// inject/release in flight. The in-flight worker re-runs this on completion,
    /// so focus changes during a multi-second injection are honoured afterwards.
    /// Touches only lock-guarded state, so a worker thread may call it.</summary>
    private void ReconcileLease()
    {
        lock (_leaseSync)
        {
            if (_leaseBusy || _leaseDesired == _leaseHeld)
            {
                return;
            }
            _leaseBusy = true;
            if (_leaseDesired)
            {
                Task.Run(AcquireLeaseWork);
            }
            else
            {
                Task.Run(ReleaseLeaseWork);
            }
        }
    }

    private void AcquireLeaseWork()
    {
        // SteamInputBlocker is a no-op when the lease is already held (the handoff
        // case) and injects only on a real 0-held transition; it logs its own
        // outcome and never throws.
        SteamInputBlocker.Acquire();
        bool held = SteamInputBlocker.IsApplied;
        lock (_leaseSync)
        {
            _leaseHeld = held;
            _leaseBusy = false;
        }
        // Re-reconcile only on success; a failed inject waits for the next focus
        // change rather than spinning against an unavailable Steam.
        if (held)
        {
            ReconcileLease();
        }
    }

    private void ReleaseLeaseWork()
    {
        SteamInputBlocker.ReleaseBestEffort("settings surface inactive");
        lock (_leaseSync)
        {
            _leaseHeld = false;
            _leaseBusy = false;
        }
        // Focus may have returned during release — re-acquire if so.
        ReconcileLease();
    }

    /// <summary>Shows the boot-splash preview (called by the Appearance page) and
    /// swaps controller navigation onto the preview window so B closes the preview
    /// instead of Settings; navigation returns here when the preview closes. The
    /// preview never outlives this window (see the Closed handler).</summary>
    internal void ShowSplashPreview(SplashConfig splash)
    {
        // Closing a previous preview restores window navigation via its Closed
        // handler before the swap below moves it to the new preview.
        _splashPreview?.Close();
        var preview = new BootSplashWindow(splash, preview: true);
        _splashPreview = preview;
        // The preview has no boot flow to hand off to — the desktop button just
        // dismisses it (otherwise the preview's most prominent, focused control
        // would be inert on a touch handheld).
        preview.DesktopRequested += preview.Close;
        preview.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_splashPreview, preview))
            {
                return;
            }
            _splashPreview = null;
            _navigation?.Dispose();
            _navigation = _closed ? null : CreateWindowNavigation();
            // The preview no longer needs the pad; re-evaluate in case focus did
            // not return to this window (so the lease is not held while unfocused).
            UpdateLeaseDesired();
        };
        // Show BEFORE the navigation swap: a Show() failure must leave Settings
        // fully controller-navigable (the page's catch reports the error).
        preview.Show();
        _navigation?.Dispose();
        _navigation = new GamepadNavigation(_gamepad, preview, back: preview.Close,
            isNintendoLayout: () => _viewModel.GlyphStyleIndex == 2,
            preferredFocus: () => preview.DefaultFocusTarget);
    }

    /// <summary>Starts hotkey recording (called by the Quick access page).</summary>
    internal void RecordHotkey() => _recorders.RecordHotkey();

    /// <summary>Clears the recorded hotkey (called by the Quick access page).</summary>
    internal void ClearHotkey() => _recorders.ClearHotkey();

    /// <summary>Starts controller-chord recording (called by the Quick access page).</summary>
    internal void RecordChord() => _recorders.RecordChord();

    /// <summary>Clears the recorded chord (called by the Quick access page).</summary>
    internal void ClearChord() => _recorders.ClearChord();
}

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
/// a bumper <see cref="TabStrip"/> over six always-alive pages (toggled by
/// visibility so their state survives switching) and a bottom status strip.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel = new();
    private readonly GamepadService _gamepad = new();
    private readonly ShortcutRecorders _recorders;
    private GamepadNavigation? _navigation;
    private OverlayController? _testOverlay;
    private BootSplashWindow? _splashPreview;
    private Window? _keyboardDialog;
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
    private static int _nextLeaseOwnerId;
    // Owner-scoped, like OverlayController's: the lease is shared static state, so a
    // surface that merely observes IsApplied cannot tell "I hold it" from "someone
    // else does" — and its release then drops the block out from under whichever
    // surface is still on screen (invariant 1).
    private readonly string _leaseOwner =
        $"settings-window#{System.Threading.Interlocked.Increment(ref _nextLeaseOwnerId)}";
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
            new("Integration", Icons.Wrench, 2),
            new("Startup", Icons.Rocket, 3),
            new("Quick access", Icons.Panel, 4),
            new("Appearance", Icons.Palette, 5),
        };
        Tabs.SelectionChanged += OnTabSelectionChanged;

        // Controller navigation for the settings window itself. LB/RB cycle the
        // tab strip (which wraps at both ends).
        // Focus changes drive the lease; the value is fixed for the window's life.
        if (_gameModeSurface)
        {
            // From the view model, which already loaded config.json for this
            // window — a second ConfigStore.Load here takes the cross-process
            // mutex again on the UI thread for a value that is already in memory.
            _leaseEnabled = _viewModel.SteamInputLeaseEnabled;
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
        // Every other GamepadNavigation host handles Escape itself; Settings did not,
        // and GamepadNavigation's keyboard-Escape branch arms its cross-source
        // suppression window whether or not anything acted on the key — so an Escape
        // arriving here swallowed the next controller B press instead of going back.
        KeyDown += OnWindowKeyDown;
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
            // Neither may the keyboard dialog; its own Closed handler restores
            // this window's navigation, which the line above already disposed.
            _keyboardDialog?.Close();
            _keyboardDialog = null;
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
    /// shoulder buttons: the TabStrip owns the index, this toggles the six
    /// always-alive pages' visibility.</summary>
    private void OnTabSelectionChanged(object? sender, TabStripSelectionChangedEventArgs e)
    {
        PageSystem.IsVisible = e.NewIndex == 0;
        PageSteam.IsVisible = e.NewIndex == 1;
        PageIntegration.IsVisible = e.NewIndex == 2;
        PageStartup.IsVisible = e.NewIndex == 3;
        PageQuickAccess.IsVisible = e.NewIndex == 4;
        PageAppearance.IsVisible = e.NewIndex == 5;

        // Land controller focus inside the newly shown page — without this the
        // next D-pad press falls back to the window's first focusable, which is
        // always the "System" tab button regardless of the active tab.
        var page = e.NewIndex switch
        {
            0 => (Control)PageSystem,
            1 => PageSteam,
            2 => PageIntegration,
            3 => PageStartup,
            4 => PageQuickAccess,
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
        _testOverlay = new OverlayController(config, monitor: null, new SessionModes(config, monitor: null),
            previewOnly: true);
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
        _testOverlay = new OverlayController(config, monitor: null, new SessionModes(config, monitor: null),
            previewOnly: true);
        _testOverlay.ShowTaskbar();
    }

    /// <summary>Creates the controller navigation attached to this window
    /// (initial Opened wiring and restoration after a splash preview closes).</summary>
    private GamepadNavigation CreateWindowNavigation() => new(_gamepad, this, back: BackOrClose,
        isNintendoLayout: () => _viewModel.GlyphStyleIndex == 2,
        tabPrevious: Tabs.SelectPrevious,
        tabNext: Tabs.SelectNext);

    /// <summary>The controller Back action. A color-picker flyout the Appearance
    /// page has open takes B first: its content lives in a popup root that
    /// gamepad navigation cannot enter, so without this B would close the whole
    /// window and discard every unsaved edit on all six pages.</summary>
    private void BackOrClose()
    {
        if (PageAppearance.TryCloseColorFlyout())
        {
            return;
        }
        Close();
    }

    /// <summary>Routes a keyboard Escape through the same Back action the controller's
    /// B button uses, so an open colour flyout is closed first rather than the whole
    /// window with every unsaved edit on it.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            BackOrClose();
        }
    }

    /// <summary>Opens the on-screen keyboard for a text box in its own dialog and
    /// moves controller navigation onto it (called by the Steam page for the
    /// SteamGridDB key). The window owns this because it owns the gamepad service
    /// and the navigation swap: the keyboard's keys are only reachable by pad once
    /// a <see cref="GamepadNavigation"/> is attached to THAT window, and this
    /// window's own navigation has to be parked meanwhile — Avalonia's modal
    /// dialog disables the owner at the Win32 level only, so its controls stay
    /// effectively enabled and a pad press would otherwise still act on the page
    /// behind the dialog (a machine-policy toggle sits there).</summary>
    /// <param name="target">The text box the keystrokes are typed into.</param>
    /// <param name="title">The dialog window title.</param>
    internal void ShowOnScreenKeyboard(TextBox target, string title)
    {
        var keyboard = new OnScreenKeyboard { Target = target };
        var window = new Window
        {
            Title = title,
            Width = 760,
            Height = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = keyboard,
        };
        keyboard.Accepted += (_, _) => window.Close();
        GamepadNavigation? keyboardNavigation = null;
        window.Opened += (_, _) =>
        {
            if (_navigation is not null)
            {
                _navigation.IsEnabled = false;
            }
            keyboardNavigation = new GamepadNavigation(_gamepad, window, back: window.Close,
                isNintendoLayout: () => _viewModel.GlyphStyleIndex == 2);
        };
        window.Closed += (_, _) =>
        {
            keyboardNavigation?.Dispose();
            keyboardNavigation = null;
            if (_navigation is not null)
            {
                _navigation.IsEnabled = true;
            }
            if (ReferenceEquals(_keyboardDialog, window))
            {
                _keyboardDialog = null;
            }
            // Same re-evaluation the splash preview does on close, in case focus
            // did not return to this window.
            UpdateLeaseDesired();
        };
        // The dialog deactivates this window, and an unfocused Settings drops the
        // Steam Input lease — which in game mode hands the pad straight back to
        // Steam's desktop profile and makes the keyboard unusable by controller.
        // Tracked like the splash preview so the lease follows the child surface.
        _keyboardDialog = window;
        UpdateLeaseDesired();
        _ = window.ShowDialog(this);
    }

    /// <summary>Whether the lease should be held right now: only in game mode with
    /// the user opt-in, while this window is open, not minimized, and either active
    /// or driving one of its child surfaces (the splash preview, the on-screen
    /// keyboard dialog) by pad. Reads UI state — UI thread only.</summary>
    private bool ShouldHoldLease()
        => _gameModeSurface && _leaseEnabled && !_closed
           && WindowState != WindowState.Minimized
           && (IsActive || _splashPreview is not null || _keyboardDialog is not null);

    /// <summary>Takes over the lease the sidebar handed off. It is already held, so
    /// this is a no-op that avoids releasing/re-injecting (the churn); the reconcile
    /// only acts if the handoff lease was somehow absent. UI thread.</summary>
    private void InheritSteamInputLease()
    {
        if (!_gameModeSurface || !_leaseEnabled)
        {
            return;
        }
        // Held by the sidebar right up to this handoff; REGISTER a claim on it rather
        // than inferring ownership from IsApplied, so the overlay re-opening over this
        // window cannot be left unblocked and this window's own release cannot drop the
        // panel's lease. AcquireFor is a no-op inside the blocker while the lease is
        // live, so the handoff stays free of release/re-inject churn. Claimed only when
        // it really is live — a cold acquire belongs on the reconciler's worker, never
        // on the UI thread — and outside _leaseSync to keep the lock order one-way.
        var held = SteamInputBlocker.IsApplied;
        if (held)
        {
            SteamInputBlocker.AcquireFor(_leaseOwner);
        }
        lock (_leaseSync)
        {
            _leaseHeld = held;
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
        SteamInputBlocker.AcquireFor(_leaseOwner);
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
        // ReleaseFor, not ReleaseBestEffort: the quick-access panel may have been
        // re-summoned over this window and still own the lease.
        SteamInputBlocker.ReleaseFor(_leaseOwner, "settings surface inactive");
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
    internal void RecordHotkey() => Observe(_recorders.RecordHotkey(), "Hotkey recording");

    /// <summary>Clears the recorded hotkey (called by the Quick access page).</summary>
    internal void ClearHotkey() => _recorders.ClearHotkey();

    /// <summary>Starts controller-chord recording (called by the Quick access page).</summary>
    internal void RecordChord() => Observe(_recorders.RecordChord(), "Chord recording");

    /// <summary>Observes an armed recorder: the recorders are manager operations,
    /// not framework event handlers, so a throw after their arming delay is logged
    /// here instead of reaching the dispatcher unobserved (which in the shell
    /// process is a crash rather than a reported failure).</summary>
    private static void Observe(Task task, string operation) =>
        task.ContinueWith(
            t => Log.Error($"{operation} failed", t.Exception!),
            System.Threading.CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    /// <summary>Clears the recorded chord (called by the Quick access page).</summary>
    internal void ClearChord() => _recorders.ClearChord();
}

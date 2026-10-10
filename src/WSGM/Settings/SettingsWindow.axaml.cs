using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;
using WSGM.Themes;

namespace WSGM.Settings;

/// <summary>
///     The interactive settings window for shell and game-mode configuration:
///     a bumper <see cref="TabStrip" /> over its always-alive pages (toggled by
///     visibility so their state survives switching) and a bottom status strip.
/// </summary>
public partial class SettingsWindow : Window
{
    // When Settings is the focused surface it must hold the Steam
    // Input lease, exactly like the overlay: without it Steam's desktop profile
    // stays live over this window, grabs the pad from SDL and injects its own
    // desktop bindings; see docs\steam-input.md, the ghost/double input.
    //
    // Opened from the sheet, this window claims the lease before the sheet's
    // deferred close ends the sheet's claim, so Steam's controller is never
    // dropped and re-revoked across the switch, the churn the user saw as
    // "controller gone again seconds later".
    //
    // It tracks focus, not just lifetime: claimed only while this window (or the
    // splash preview it drives by pad) is the active, non-minimized foreground,
    // so unfocusing or minimizing Settings hands the controller straight back to
    // Big Picture. SteamInputBlocker does the native work on its own worker.
    private readonly GamepadService _gamepad;

    private readonly bool _leaseEnabled;

    // Owner-scoped, like OverlayController's: the lease is shared by the process's surfaces, so
    // this window's release must end only its own claim, never the block a
    // surface still on screen needs; see docs\steam-input.md.
    private readonly string _leaseOwner = SteamInputBlocker.NewOwner("settings-window");
    private readonly Control[] _pages;
    private readonly SettingsWindowServices _services;
    private readonly SettingsViewModel _viewModel;
    private int _chordGeneration;
    private GamepadChordRecorder? _chordRecorder;
    private bool _closeAfterSave;
    private bool _closed;

    // Bumped by every arm AND every clear, so the continuation after the arming
    // delay can tell whether its own request is still the one the user wants.
    private int _hotkeyGeneration;

    // --- Shortcut recorders (keyboard hotkey + controller chord) ---
    // The 200 ms arming delay keeps the press that STARTED recording out of the
    // recording, and the re-check after that delay prevents installing a
    // low-level keyboard hook with nothing left to dispose it — or one the user
    // already cancelled.
    private KeyRecorder? _keyRecorder;
    private Window? _keyboardDialog;
    private GamepadNavigation? _navigation;
    private bool _opened;
    private BootSplashWindow? _splashPreview;

    // In game mode WSGM hosts the only taskbar, and it excludes own-process windows
    // (the overlay/taskbar/tray chrome). This window opts in so it stays reachable
    // after it drops behind Big Picture.
    private bool _switchable;
    private nint _switchableHwnd;
    private IDisposable? _testOverlay;

    /// <summary>
    ///     Creates the settings window, builds the tab strip and connects
    ///     controller navigation and the shortcut recorders. Every Settings window leases while focused.
    /// </summary>
    /// <param name="viewModel">The view model the window edits.</param>
    /// <param name="services">The window's lifetime operations, supplied by its composer.</param>
    internal SettingsWindow(SettingsViewModel viewModel, SettingsWindowServices services)
    {
        _viewModel = viewModel;
        _services = services;
        _gamepad = services.Gamepad;
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // The one page table: it drives the tab strip, the visibility toggle and
        // the focus landing alike (the XAML hosts the same pages in this order).
        (string Title, StreamGeometry Icon, Control Page)[] pages =
        [
            ("System", Icons.Monitor, PageSystem),
            ("Steam", Icons.SteamLike, PageSteam),
            ("Integration", Icons.Wrench, PageIntegration),
            ("Device setup", Icons.SteamLike, PageDevice),
            ("Startup", Icons.Rocket, PageStartup),
            ("Quick access", Icons.Panel, PageQuickAccess),
            ("Display", Icons.Monitor, PageDisplay),
            ("Appearance", Icons.Palette, PageAppearance),
            ("About", Icons.Info, PageAbout),
            ("Plugins", Icons.Wrench, PagePlugins),
            ("Device profiles", Icons.Wrench, PageDeviceProfiles)
        ];
        _pages = [.. pages.Select(static entry => entry.Page)];
        Tabs.Tabs = [.. pages.Select((entry, index) => new TabStripItem(entry.Title, entry.Icon, index))];
        Tabs.SelectionChanged += OnTabSelectionChanged;

        // Controller navigation for the settings window itself. LB/RB cycle the
        // tab strip (which wraps at both ends).
        // Focus changes drive the lease; the opt-out is snapshotted once and stays
        // fixed for this window's life. Turning the lease off on the Quick access
        // page therefore takes effect at the NEXT surface open, not on this
        // one: dropping the lease the moment the user pressed "Save changes"
        // would hand the pad straight back to Steam's desktop profile, which swallows
        // it from SDL system-wide, and the controller user would be stranded in a
        // settings window they can no longer navigate. Same rule as
        // OverlayController.AcquireSteamInputLease (docs\steam-input.md).
        // From the view model, which already loaded config.json for this
        // window — a second ConfigStore.Read here takes the cross-process
        // mutex again on the UI thread for a value that is already in memory.
        _leaseEnabled = _viewModel.SteamInputLeaseEnabled;
        PropertyChanged += (_, e) =>
        {
            // Activated fires before Avalonia sets IsActive. Observe the committed state so
            // the first foreground activation acquires a lease without a second Activate call.
            if (e.Property == IsActiveProperty || e.Property == WindowStateProperty)
            {
                UpdateLeaseDesired();
            }
        };
        // Every other GamepadNavigation host handles Escape itself; Settings did not,
        // and GamepadNavigation's keyboard-Escape branch arms its cross-source
        // suppression window whether or not anything acted on the key — so an Escape
        // arriving here swallowed the next controller B press instead of going back.
        KeyDown += OnWindowKeyDown;
        Closing += OnClosing;
        Opened += (_, _) =>
        {
            _opened = true;
            _navigation = CreateWindowNavigation();
            _services.StartInput();
            UpdateLeaseDesired();
            if (_switchable)
            {
                IncludeAsSwitchable();
            }

            // Brackets the window's lifetime for splash-theme imports: an imported
            // theme's images live in this process's staging directory, which stays until
            // the last session ends, because an unsaved import must stay materializable
            // for as long as this window can still save it. Opening the session also
            // deletes what processes that no longer run left staged. Paired with Opened
            // (not the constructor) so a window that is built but never shown cannot
            // leave a session, and with it the staged images, behind.
            _services.BeginImportSession();
            Log.Observe(_viewModel.StartInventoryDiscoveryAsync(), "Settings machine inventory");
            Log.Observe(_viewModel.StartDisplayDiscoveryAsync(), "Settings display discovery");
            _viewModel.StartAudioDiscovery();
            Log.Observe(_services.RefreshDeviceOwner(), "Settings device owner read");
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _viewModel.StopInventoryDiscovery();
            _viewModel.StopDisplayDiscovery();
            _viewModel.StopUpdateWork();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _services.StopInput();
            WindowFinder.ExcludeOwnWindow(_switchableHwnd);
            // _closed makes the lease unwanted, which ends this window's claim.
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
            if (Application.Current is { } app)
            {
                AccentPalette.Apply(
                    app, AccentPalette.Parse(_services.ReadSavedAccent()));
            }

            // Recorder disposal keeps its historical slot and order (key recorder
            // first, chord second) so the hooks are gone on every close path.
            _keyRecorder?.Dispose();
            _keyRecorder = null;
            _chordRecorder?.Dispose();
            _chordRecorder = null;
            // LAST: nothing above may still read a staged import. Any save has long
            // committed the staged images into the stable splash assets by now, and an
            // abandoned import is exactly what this frees. Counted: only the last
            // session in this process deletes its staging directory, so a second
            // settings window's unsaved import survives this.
            _services.EndImportSession();
        };
    }

    /// <summary>
    ///     One selection path for touch, mouse, keyboard and the LB/RB
    ///     shoulder buttons: the TabStrip owns the index, this toggles the
    ///     always-alive pages' visibility.
    /// </summary>
    private void OnTabSelectionChanged(object? sender, TabStripSelectionChangedEventArgs e)
    {
        for (var index = 0; index < _pages.Length; index++)
        {
            _pages[index].IsVisible = index == e.NewIndex;
        }

        // Land controller focus inside the newly shown page — without this the
        // next D-pad press falls back to the window's first focusable, which is
        // always the "System" tab button regardless of the active tab.
        FocusFirstControl(_pages[Math.Clamp(e.NewIndex, 0, _pages.Length - 1)]);
    }

    private static void FocusFirstControl(Control page)
    {
        FocusSearch.FirstNavigable(page)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    ///     Shows the quick access panel for a local test (called by the
    ///     Quick access page). Uses the real controller so behavior matches shell
    ///     mode exactly; rebuilt for every test so unsaved glyph/input changes take
    ///     effect immediately.
    /// </summary>
    internal void ShowTestOverlay()
    {
        _testOverlay?.Dispose();
        _testOverlay = null;
        _testOverlay = _services.ShowTestSheet(_viewModel.SnapshotForPreview());
    }

    /// <summary>
    ///     Keeps this window reachable from the Open apps strip in game mode, where WSGM's own windows are
    ///     otherwise left out. Idempotent; before the window has opened it takes effect once it does.
    /// </summary>
    internal void IncludeAsSwitchable()
    {
        _switchable = true;
        if (!_opened || _closed || _switchableHwnd != 0)
        {
            return;
        }

        _switchableHwnd = TryGetPlatformHandle()?.Handle ?? 0;
        WindowFinder.IncludeOwnWindow(_switchableHwnd);
    }

    /// <summary>
    ///     Defers a close that arrives while a save is running, so the post-save work (the Steam Input
    ///     shim and the takeovers) is not cut short, then closes once the save completes. Signing out and
    ///     application shutdown are never held up.
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_viewModel.IsSaving
            || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            return;
        }

        e.Cancel = true;
        _closeAfterSave = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.IsSaving))
        {
            return;
        }

        UpdateSettingsEnabled();
        if (_closeAfterSave && !_viewModel.IsSaving)
        {
            _closeAfterSave = false;
            Close();
        }
    }

    /// <summary>
    ///     Keeps the page controls inert while a save is persisting its immutable snapshot.
    ///     This prevents a post-capture edit from being followed by a misleading "Saved"
    ///     acknowledgement.
    /// </summary>
    private void UpdateSettingsEnabled()
    {
        SettingsRoot.IsEnabled = !_viewModel.IsSaving;
    }

    /// <summary>
    ///     Whether the unsaved glyph selection is the Nintendo family, whose
    ///     A/B labels are swapped relative to Xbox — shared by every
    ///     <see cref="GamepadNavigation" /> this window creates.
    /// </summary>
    private bool IsNintendoLayout()
    {
        return (GlyphStyle)_viewModel.GlyphStyleIndex == GlyphStyle.Nintendo;
    }

    /// <summary>
    ///     Creates the controller navigation attached to this window
    ///     (initial Opened wiring and restoration after a splash preview closes).
    /// </summary>
    private GamepadNavigation CreateWindowNavigation()
    {
        return new GamepadNavigation(_gamepad, this, BackOrClose,
            IsNintendoLayout,
            tabPrevious: Tabs.SelectPrevious,
            tabNext: Tabs.SelectNext);
    }

    /// <summary>
    ///     The controller Back action. A color-picker flyout the Appearance
    ///     page has open takes B first: its content lives in a popup root that
    ///     gamepad navigation cannot enter, so without this B would close the whole
    ///     window and discard every unsaved edit on every page.
    /// </summary>
    private void BackOrClose()
    {
        if (PageAppearance.TryCloseColorFlyout())
        {
            return;
        }

        Close();
    }

    /// <summary>
    ///     Routes a keyboard Escape through the same Back action the controller's
    ///     B button uses, so an open colour flyout is closed first rather than the whole
    ///     window with every unsaved edit on it.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            BackOrClose();
        }
    }

    /// <summary>
    ///     Opens the on-screen keyboard for a text box in its own dialog and
    ///     moves controller navigation onto it (called by the Steam page for the
    ///     SteamGridDB key). The window owns this because it owns the gamepad service
    ///     and the navigation swap: the keyboard's keys are only reachable by pad once
    ///     a <see cref="GamepadNavigation" /> is attached to THAT window, and this
    ///     window's own navigation has to be parked meanwhile — Avalonia's modal
    ///     dialog disables the owner at the Win32 level only, so its controls stay
    ///     effectively enabled and a pad press would otherwise still act on the page
    ///     behind the dialog (a machine-policy toggle sits there).
    /// </summary>
    /// <param name="target">The text box the keystrokes are typed into.</param>
    /// <param name="title">The dialog window title.</param>
    internal void ShowOnScreenKeyboard(TextBox target, string title)
    {
        ArgumentNullException.ThrowIfNull(target);
        OpenKeyboardEditor(
            target.Text ?? string.Empty,
            target.MaxLength,
            title,
            value =>
            {
                target.Text = value;
                target.CaretIndex = value.Length;
                target.SelectionStart = value.Length;
                target.SelectionEnd = value.Length;
                return null;
            });
    }

    /// <summary>
    ///     Opens the controller keyboard for a value that has no fixed TextBox,
    ///     such as a row created from a plugin manifest.
    /// </summary>
    /// <param name="initialValue">Initial text shown to the user.</param>
    /// <param name="maximumLength">Hard input bound.</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="accept">Applies the result and returns an error to keep the dialog open, or null.</param>
    internal void ShowOnScreenKeyboard(
        string initialValue,
        int maximumLength,
        string title,
        Func<string, string?> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        OpenKeyboardEditor(initialValue, Math.Max(0, maximumLength), title, accept);
    }

    private void OpenKeyboardEditor(
        string initialValue,
        int maximumLength,
        string title,
        Func<string, string?> accept)
    {
        var editor = new TextBox
        {
            Text = initialValue,
            MaxLength = Math.Max(0, maximumLength),
            Margin = new Thickness(12, 12, 12, 0),
            MinHeight = 44
        };
        editor.CaretIndex = editor.Text?.Length ?? 0;
        editor.SelectionStart = editor.CaretIndex;
        editor.SelectionEnd = editor.CaretIndex;
        var keyboard = new OnScreenKeyboard { Target = editor };
        var validation = new TextBlock
        {
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 8, 12, 0)
        };
        var content = new StackPanel();
        content.Children.Add(editor);
        content.Children.Add(validation);
        content.Children.Add(keyboard);
        var window = new Window
        {
            Title = title,
            Width = 760,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        keyboard.Accepted += (_, _) =>
        {
            try
            {
                var error = accept(editor.Text ?? string.Empty);
                if (!string.IsNullOrEmpty(error))
                {
                    validation.Text = error;
                    validation.IsVisible = true;
                    return;
                }

                window.Close();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"On-screen keyboard value apply failed: {ex.Message}");
                validation.Text = $"Could not apply that value: {ex.Message}";
                validation.IsVisible = true;
            }
        };
        GamepadNavigation? keyboardNavigation = null;
        window.Opened += (_, _) =>
        {
            _navigation?.IsEnabled = false;
            keyboardNavigation = new GamepadNavigation(_gamepad, window, window.Close,
                IsNintendoLayout);
        };
        window.Closed += (_, _) =>
        {
            keyboardNavigation?.Dispose();
            keyboardNavigation = null;
            _navigation?.IsEnabled = true;
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

    /// <summary>
    ///     Claims or ends this window's claim on the Steam Input lease to match its state: with
    ///     the user opt-in, while this window is open, not minimized, and either active
    ///     or driving one of its child surfaces (the splash preview, the on-screen
    ///     keyboard dialog) by pad. Called on every focus, window-state and child-surface
    ///     change. Neither call waits for native work, so this is safe on the UI thread.
    /// </summary>
    private void UpdateLeaseDesired()
    {
        if (!_leaseEnabled)
        {
            return;
        }

        if (!_closed && WindowState != WindowState.Minimized
                     && (IsActive || _splashPreview is not null || _keyboardDialog is not null))
        {
            _services.HoldSteamInput(_leaseOwner);
        }
        else
        {
            _services.DropSteamInput(_leaseOwner, "settings surface inactive");
        }
    }

    /// <summary>
    ///     Shows the boot-splash preview (called by the Appearance page) and
    ///     swaps controller navigation onto the preview window so B closes the preview
    ///     instead of Settings; navigation returns here when the preview closes. The
    ///     preview never outlives this window (see the Closed handler).
    /// </summary>
    /// <param name="splash">Unsaved splash snapshot; a preview owner replaces any previous preview.</param>
    internal void ShowSplashPreview(SplashConfig splash)
    {
        // Closing a previous preview restores window navigation via its Closed
        // handler before the swap below moves it to the new preview.
        _splashPreview?.Close();
        var preview = new BootSplashWindow(splash, true);
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
            // ReSharper disable once AccessToDisposedClosure
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
        _navigation = new GamepadNavigation(_gamepad, preview, preview.Close,
            IsNintendoLayout,
            () => preview.DefaultFocusTarget);
    }

    /// <summary>Starts hotkey recording (called by the Quick access page).</summary>
    internal void RecordHotkey()
    {
        Log.Observe(ArmHotkeyRecorder(), "Hotkey recording", true);
    }

    /// <summary>
    ///     Arms keyboard-shortcut recording (200 ms delayed, cancel- and
    ///     closed-window safe).
    /// </summary>
    /// <returns>
    ///     A task that completes once the recorder is armed, or once this
    ///     request has been superseded.
    /// </returns>
    private async Task ArmHotkeyRecorder()
    {
        // Small delay so the key/controller press that started recording (Enter, A)
        // isn't the thing we record — same trick Handheld Companion uses.
        _viewModel.SetHotkeyRecording(true);
        var generation = ++_hotkeyGeneration;
        await Task.Delay(200);
        if (_closed || generation != _hotkeyGeneration)
        {
            // Window closed during the delay: creating the recorder now would
            // install a low-level keyboard hook with nothing left to dispose it.
            // A cleared/restarted recording is the same hazard from the other
            // side — the UI already says nothing is being recorded, so the hook
            // would swallow the user's next keystroke anywhere and silently make
            // it the hotkey (the hook exists only while recording); see
            // docs\overlay-and-input.md, "Raw input is observed, never intercepted".
            return;
        }

        _keyRecorder?.Dispose();
        _keyRecorder = new KeyRecorder();
        _keyRecorder.Recorded += hotkey =>
        {
            _viewModel.ApplyRecordedHotkey(hotkey);
            _keyRecorder?.Dispose();
            _keyRecorder = null;
        };
        try
        {
            _keyRecorder.Start();
        }
        catch
        {
            // Nothing is listening, so the page must not keep saying "Press keys...". The
            // shortcut already bound stays bound; only an explicit Clear removes it.
            _keyRecorder.Dispose();
            _keyRecorder = null;
            _viewModel.SetHotkeyRecording(false);
            throw;
        }
    }

    /// <summary>
    ///     Clears the recorded hotkey and stops any active recording
    ///     (called by the Quick access page).
    /// </summary>
    internal void ClearHotkey()
    {
        _hotkeyGeneration++;
        _keyRecorder?.Dispose();
        _keyRecorder = null;
        _viewModel.ClearHotkey();
    }

    /// <summary>Starts controller-chord recording (called by the Quick access page).</summary>
    internal void RecordChord()
    {
        Log.Observe(ArmChordRecorder(), "Chord recording", true);
    }

    /// <summary>
    ///     Arms controller-chord recording (200 ms delayed, cancel- and
    ///     closed-window safe).
    /// </summary>
    /// <returns>
    ///     A task that completes once the recorder is armed, or once this
    ///     request has been superseded.
    /// </returns>
    private async Task ArmChordRecorder()
    {
        _viewModel.SetChordRecording(true);
        var generation = ++_chordGeneration;
        await Task.Delay(200);
        if (_closed || generation != _chordGeneration)
        {
            // Same races as the hotkey recorder: no recorder after the window is
            // gone, and none after the user cleared or restarted the recording.
            return;
        }

        _chordRecorder?.Dispose();
        // The window's own polling service — the chord recorder shares it rather
        // than running a second 16 ms SDL poller.
        _chordRecorder = new GamepadChordRecorder(_gamepad);
        _chordRecorder.Recorded += (buttons, hold) =>
        {
            if (buttons != 0)
            {
                _viewModel.ApplyRecordedChord(buttons, hold);
            }
            else
            {
                _viewModel.SetChordRecording(false);
            }

            _chordRecorder?.Dispose();
            _chordRecorder = null;
        };
        try
        {
            _chordRecorder.Start();
        }
        catch
        {
            // As for the hotkey: stop showing "Press buttons..." and keep the bound chord.
            _chordRecorder.Dispose();
            _chordRecorder = null;
            _viewModel.SetChordRecording(false);
            throw;
        }
    }

    /// <summary>
    ///     Clears the recorded chord and stops any active recording
    ///     (called by the Quick access page).
    /// </summary>
    internal void ClearChord()
    {
        _chordGeneration++;
        _chordRecorder?.Dispose();
        _chordRecorder = null;
        _viewModel.ClearChord();
    }
}

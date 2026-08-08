using System.Collections.Generic;
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

    /// <summary>Creates the settings window, builds the tab strip and connects
    /// controller navigation and the shortcut recorders.</summary>
    public SettingsWindow()
    {
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
        Opened += (_, _) =>
        {
            _navigation = CreateWindowNavigation();
            _gamepad.Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _gamepad.Stop();
            _navigation?.Dispose();
            _navigation = null;
            // The splash preview must not outlive Settings; its Closed handler
            // sees _closed and skips recreating window navigation.
            _splashPreview?.Close();
            _splashPreview = null;
            _testOverlay?.Dispose();
            _testOverlay = null;
            // Same slot the two recorders were disposed in before they moved
            // into ShortcutRecorders (key recorder first, chord second).
            _recorders.Dispose();
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

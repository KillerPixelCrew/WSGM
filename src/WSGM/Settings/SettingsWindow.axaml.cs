using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;
using WSGM.Themes;

namespace WSGM.Settings;

/// <summary>
///     The interactive settings window for shell and game-mode configuration: a section rail with
///     search over its always-alive pages (toggled by visibility so their state survives switching)
///     and a footer with Save and the status line.
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
    private readonly SettingsRowMarks _rowMarks = new();
    private readonly SettingsSection[] _sections;
    private bool _changeRefreshQueued;
    private readonly Button[] _railButtons;
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
    private int _section;
    private BootSplashWindow? _splashPreview;

    // In game mode WSGM hosts the only taskbar, and it excludes own-process windows
    // (the overlay/taskbar/tray chrome). This window opts in so it stays reachable
    // after it drops behind Big Picture.
    private bool _switchable;
    private nint _switchableHwnd;
    private IDisposable? _testOverlay;

    /// <summary>
    ///     Creates the settings window, builds the section rail and connects
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
        _viewModel.Clipboard = text => Clipboard is { } clipboard
            ? clipboard.SetTextAsync(text)
            : throw new InvalidOperationException("this window has no clipboard");

        // The one section table: it drives the rail, the heading, the visibility toggle, the focus landing
        // and the search. A nested level names the rail entry it belongs to.
        _sections =
        [
            new SettingsSection("General", "How WSGM starts, updates and looks after Steam and this device.",
                PageGeneral),
            new SettingsSection("Steam", "How Steam runs, what WSGM adds inside it, and defaults for new titles.",
                PageSteam),
            new SettingsSection("Device", "What WSGM may take over on this handheld, and how its controller appears.",
                PageDevice),
            new SettingsSection("Quick access", "How the Overlay opens and how it looks over a game.",
                PageQuickAccess),
            new SettingsSection("Performance",
                "Frame limiting, the performance overlay and the graphics drivers WSGM manages.", PagePerformance),
            new SettingsSection("Display", "How your screens are set up in each mode. Applied on the next switch.",
                PageDisplay),
            new SettingsSection("Startup", "Programs that run at sign-in, in order, before Steam starts.",
                PageStartup),
            new SettingsSection("Appearance", "The accent colour and the splash shown while WSGM starts Steam.",
                PageAppearance),
            new SettingsSection("Plugins", "Plugins bundled with this release. Changes apply when WSGM restarts.",
                PagePlugins),
            new SettingsSection("Diagnostics", "What to send with a bug report, and how to get back to the desktop.",
                PageDiagnostics),
            new SettingsSection("About", string.Empty, PageAbout),
            new SettingsSection("Fan curves and colours",
                "Make profiles here. Pick the active one in the Overlay or Steam.", PageDeviceProfiles, DeviceSection),
            new SettingsSection("Library", "What WSGM adds to Steam's library.", PageSteamLibrary, SteamSection),
            new SettingsSection("Client", "What WSGM changes in the Big Picture client.", PageSteamClient, SteamSection),
            new SettingsSection("Steam Input", "Whether WSGM may take the controller from Steam.", PageSteamInput,
                SteamSection),
            new SettingsSection("Artwork", "Where WSGM looks for artwork, and the order of its tabs.", PageSteamArtwork,
                SteamSection),
            new SettingsSection("Edit app", "What this app runs at sign-in, and how.", PageStartupApp, StartupSection),
            new SettingsSection("Add app", "Pick a companion app found on this PC, or choose any program.",
                PageStartupAdd, StartupSection)
        ];
        _railButtons =
        [
            .. _sections.Where(static section => section.Parent is null).Select((section, index) =>
            {
                var button = new Button
                {
                    Content = section.Title, Classes = { "settings-rail-item" },
                    Tag = "rail." + section.Title.ToLowerInvariant().Replace(' ', '-')
                };
                button.Click += (_, _) => SelectSection(index);
                return button;
            })
        ];
        SectionRail.Children.AddRange(_railButtons);
        ShowSection(0);
        PageSteam.RefreshSummaries(_viewModel);
        // A row whose button carries "open:<title>" opens that nested page of the section it is on.
        AddHandler(Button.ClickEvent, OnOpenNested, RoutingStrategies.Bubble);
        // Y moves a performance overlay widget while one has focus, and searches everywhere else.
        // X shows a startup app's actions while its card has focus.
        AddHandler(GotFocusEvent, (_, e) => UpdateHints(e.Source as IInputElement), RoutingStrategies.Bubble);
        SearchBox.TextChanged += (_, _) => UpdateSearch();
        SearchBox.KeyDown += OnSearchKeyDown;
        SearchBox.LostFocus += (_, _) => Dispatcher.UIThread.Post(CloseSearchWhenFocusLeft);

        // Controller navigation for the settings window itself. LB/RB step through the
        // sections (wrapping at both ends).
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
        // Editors that change a row's own state without a view-model property, such as a plugin row's switch.
        AddHandler(ToggleButton.IsCheckedChangedEvent, (_, _) => QueueChangeRefresh(), RoutingStrategies.Bubble, true);
        AddHandler(SelectingItemsControl.SelectionChangedEvent, (_, _) => QueueChangeRefresh(), RoutingStrategies.Bubble,
            true);
        AddHandler(TextBox.TextChangedEvent, (_, _) => QueueChangeRefresh(), RoutingStrategies.Bubble, true);
        AddHandler(RangeBase.ValueChangedEvent, (_, _) => QueueChangeRefresh(), RoutingStrategies.Bubble, true);
        AddHandler(Button.ClickEvent, (_, _) => QueueChangeRefresh(), RoutingStrategies.Bubble, true);
        Closing += OnClosing;
        Opened += (_, _) =>
        {
            _opened = true;
            // Before the window's own reads start: what they then change, such as a display they rebind,
            // is something Save would write and counts like any other change.
            _viewModel.RecordSavedState();
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
            // Once the first layout has bound every editor, the rows remember what they show.
            Dispatcher.UIThread.Post(RecordRowState, DispatcherPriority.Loaded);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _viewModel.StopInventoryDiscovery();
            _viewModel.StopDisplayDiscovery();
            _viewModel.StopUpdateWork();
            _viewModel.StopLogUpload();
            _viewModel.Clipboard = null;
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

    private void RecordRowState()
    {
        if (!_closed)
        {
            _rowMarks.Record(MarkedPages());
        }
    }

    /// <summary>Counts the unsaved changes again once the current input has been handled, at most once per pass.</summary>
    private void QueueChangeRefresh()
    {
        if (_changeRefreshQueued || _closed)
        {
            return;
        }

        _changeRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _changeRefreshQueued = false;
            if (_closed)
            {
                return;
            }

            _viewModel.RefreshUnsavedChanges();
            _rowMarks.Mark(MarkedPages(), _viewModel.HasUnsavedChanges);
            PageSteam.RefreshSummaries(_viewModel);
        }, DispatcherPriority.Background);
    }

    /// <summary>The rail entry of the Steam section, which its library, client, Steam Input and artwork pages nest under.</summary>
    private const int SteamSection = 1;

    /// <summary>The rail entry of the Device section, which the profile editor nests under.</summary>
    private const int DeviceSection = 2;

    /// <summary>The rail entry of the Startup section, which the app editor and Add app nest under.</summary>
    internal const int StartupSection = 6;

    /// <summary>
    ///     The pages whose rows show pending marks. The app editor shows whichever app is open, so its rows are not
    ///     compared; that app's card on the Startup page carries the mark instead.
    /// </summary>
    private IEnumerable<Control> MarkedPages()
    {
        return _sections.Select(static section => section.Page).Where(page => page != PageStartupApp);
    }

    /// <summary>The button that opened the nested page on screen, which Back returns focus to.</summary>
    private Action? _returnFocus;

    /// <summary>Names what X and Y do where the focus is.</summary>
    /// <param name="focused">The focused element.</param>
    private void UpdateHints(IInputElement? focused)
    {
        TertiaryHint.Text = Pages.PerformancePage.IsWidgetRow(focused) ? "Move" : "Search";
        SecondaryHint.IsVisible = Pages.StartupPage.IsAppCard(focused);
    }

    /// <summary>Opens a nested page of a section; Back returns to the section and calls <paramref name="returnFocus" />.</summary>
    /// <param name="parent">The section's rail entry.</param>
    /// <param name="title">The nested page's title.</param>
    /// <param name="returnFocus">Puts focus back where the user came from.</param>
    internal void OpenNested(int parent, string title, Action returnFocus)
    {
        var index = Array.FindIndex(_sections, section => section.Parent == parent && section.Title == title);
        if (index >= 0)
        {
            _returnFocus = returnFocus;
            SelectSection(index);
        }
    }

    private void OnOpenNested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string tag } button || !tag.StartsWith("open:", StringComparison.Ordinal))
        {
            return;
        }

        var title = tag["open:".Length..];
        var parent = _sections[_section].Parent ?? _section;
        var index = Array.FindIndex(_sections, section => section.Parent == parent && section.Title == title);
        if (index < 0)
        {
            return;
        }

        e.Handled = true;
        _returnFocus = () => button.Focus(NavigationMethod.Directional);
        SelectSection(index);
    }

    /// <summary>
    ///     One selection path for touch, mouse, keyboard, search and the LB/RB shoulder buttons: shows the
    ///     section and lands focus inside it. Without the landing the next D-pad press falls back to the
    ///     window's first focusable, which is always the search box.
    /// </summary>
    /// <param name="index">The section's index in the table.</param>
    private void SelectSection(int index)
    {
        ShowSection(index);
        FocusFirstControl(_sections[index].Page);
    }

    private void ShowSection(int index)
    {
        _section = index;
        var section = _sections[index];
        for (var other = 0; other < _sections.Length; other++)
        {
            _sections[other].Page.IsVisible = other == index;
        }

        var railIndex = section.Parent ?? index;
        for (var other = 0; other < _railButtons.Length; other++)
        {
            _railButtons[other].Classes.Set("selected", other == railIndex);
        }

        SectionTitle.Text = section.Parent is { } parent
            ? $"{_sections[parent].Title}  ›  {section.Title}"
            : section.Title;
        SectionCaption.Text = section.Caption;
        SectionCaption.IsVisible = section.Caption.Length > 0;
        UpdateHints(FocusManager?.GetFocusedElement());
    }

    /// <summary>Steps through the rail, from a nested level as from the section it belongs to; wraps at both ends.</summary>
    /// <param name="step">One forward or back.</param>
    private void StepSection(int step)
    {
        var railIndex = _sections[_section].Parent ?? _section;
        SelectSection((railIndex + step + _railButtons.Length) % _railButtons.Length);
    }

    /// <summary>Rebuilds the suggestions for what the search box holds.</summary>
    private void UpdateSearch()
    {
        var query = SearchBox.Text ?? string.Empty;
        var found = SettingsSearch.Find(
            // The app editor shows whichever app is open, so its rows are reached through the app's card instead.
            _sections.SelectMany((section, index) => section.Page == PageStartupApp
                ? []
                : SettingsSearch.Collect(index, section.Parent is { } parent
                    ? $"{_sections[parent].Title} › {section.Title}"
                    : section.Title, section.Page)),
            query);
        SearchList.Children.Clear();
        foreach (var entry in found)
        {
            SearchList.Children.Add(SearchRow(entry));
        }

        SearchCount.Text = found.Count == 1 ? "1 setting" : $"{found.Count} settings";
        SearchResults.IsVisible = query.Trim().Length > 0;
    }

    private Button SearchRow(SettingsSearchEntry entry)
    {
        var titles = new StackPanel { Spacing = 1 };
        titles.Children.Add(new TextBlock
            { Text = entry.Title, Classes = { "setting-title" }, TextWrapping = TextWrapping.Wrap });
        titles.Children.Add(new TextBlock { Text = entry.Path, Classes = { "caption" } });
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        layout.Children.Add(titles);
        var value = new TextBlock
            { Text = entry.Value, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(value, 1);
        layout.Children.Add(value);
        var row = new Button { Content = layout, Classes = { "settings-search-row" } };
        row.Click += (_, _) => GoToSetting(entry);
        return row;
    }

    /// <summary>Shows the section, scrolls to the row and focuses it, leaving the search closed.</summary>
    /// <param name="entry">The chosen setting.</param>
    private void GoToSetting(SettingsSearchEntry entry)
    {
        CloseSearch();
        ShowSection(entry.SectionIndex);
        SelectTabOf(entry.Row);
        // After layout: the page has only just become visible, so the row has no place to scroll to yet.
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed)
            {
                return;
            }

            entry.Row?.BringIntoView();
            if (entry.Row is { } row)
            {
                (FocusSearch.FirstNavigable(row) ?? FocusSearch.FirstNavigable(_sections[entry.SectionIndex].Page))
                    ?.Focus(NavigationMethod.Directional);
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Selects the tab whose panel holds a row, when that panel is hidden behind another tab.</summary>
    /// <param name="row">The row a search result points at.</param>
    private static void SelectTabOf(Control? row)
    {
        var panel = row?.GetLogicalAncestors().OfType<Control>()
            .FirstOrDefault(static control => control.Classes.Contains("settings-tab-panel"));
        if (panel is not { IsVisible: false, Name: { } name } || panel.Parent is not Panel host)
        {
            return;
        }

        var tab = host.GetLogicalDescendants().OfType<RadioButton>()
            .FirstOrDefault(button => Equals(button.Tag, name));
        tab?.IsChecked = true;
    }

    private void CloseSearch()
    {
        SearchBox.Text = string.Empty;
        SearchResults.IsVisible = false;
    }

    private void CloseSearchWhenFocusLeft()
    {
        if (SearchResults.IsVisible && FocusManager?.GetFocusedElement() is Visual focused
                                    && !SearchResults.IsVisualAncestorOf(focused) && !ReferenceEquals(focused, SearchBox))
        {
            CloseSearch();
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        var first = SearchList.Children.OfType<Button>().FirstOrDefault();
        switch (e.Key)
        {
            case Key.Enter when first is not null:
                first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
                break;
            case Key.Down when first is not null:
                first.Focus(NavigationMethod.Directional);
                e.Handled = true;
                break;
        }
    }

    /// <summary>The controller's way into search: a text box takes no pad input, so Y opens the on-screen keyboard.</summary>
    private void OpenSearchKeyboard()
    {
        ShowOnScreenKeyboard(SearchBox.Text ?? string.Empty, 0, "Search settings", value =>
        {
            SearchBox.Text = value;
            Dispatcher.UIThread.Post(
                () => SearchList.Children.OfType<Button>().FirstOrDefault()?.Focus(NavigationMethod.Directional),
                DispatcherPriority.Loaded);
            return null;
        });
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
        if (e.PropertyName is not (nameof(SettingsViewModel.UnsavedChangeCount)
            or nameof(SettingsViewModel.HasUnsavedChanges) or nameof(SettingsViewModel.UnsavedChangesText)
            or nameof(SettingsViewModel.StatusText)))
        {
            QueueChangeRefresh();
        }

        if (e.PropertyName != nameof(SettingsViewModel.IsSaving))
        {
            return;
        }

        if (!_viewModel.IsSaving)
        {
            // A save just finished: the rows show what is saved now, apart from edits made while it ran.
            _rowMarks.Record(MarkedPages());
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
            tabPrevious: () => StepSection(-1),
            tabNext: () => StepSection(1),
            tertiary: focused =>
            {
                if (!PagePerformance.ToggleMove(focused))
                {
                    OpenSearchKeyboard();
                }
            },
            navigate: PagePerformance.MovePicked,
            secondary: focused => PageStartup.ToggleActions(focused),
            focusScope: () => SearchResults.IsVisible ? SearchResults : null);
    }

    /// <summary>
    ///     The controller Back action. Open search suggestions close first and a nested level returns to
    ///     its section. A color-picker flyout the Appearance page has open also takes B first: its content
    ///     lives in a popup root that gamepad navigation cannot enter, so without this B would close the
    ///     whole window and discard every unsaved edit on every page.
    /// </summary>
    private void BackOrClose()
    {
        if (SearchResults.IsVisible)
        {
            CloseSearch();
            SearchBox.Focus(NavigationMethod.Directional);
            return;
        }

        if (PagePerformance.EndMove() || PageStartup.CloseActions())
        {
            return;
        }

        if (_sections[_section].Parent is { } parent)
        {
            ShowSection(parent);
            _returnFocus?.Invoke();
            _returnFocus = null;
            return;
        }

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

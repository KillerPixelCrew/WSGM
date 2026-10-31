using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private readonly Dictionary<OverlayPage, double> _nestedScroll = [];

    // A backing field rather than `field`: with `field ??=` the compiler loses the non-null flow
    // at the foreach in OverlayWindow.axaml.cs and fails the warning-clean build (CS8602).
    // ReSharper disable once ReplaceWithFieldKeyword
    private SubView[]? _subViews;

    /// <summary>
    ///     The nested pages, built once the XAML fields exist. The open one is identified by
    ///     <see cref="OverlayNavigation.Page" /> rather than tracked in a parallel flag per page, which
    ///     is what let the two disagree.
    /// </summary>
    private SubView[] SubViews => _subViews ??=
    [
        // The sections each destination's rail offers. Steam, Tools and Power have no page of their own: a
        // section is a page one level down from the root, and its parent is null. A page opened from inside
        // a section names that section as its parent, so Back returns to it. A section with a pin id can be
        // pinned to Quick access as a card that opens it.
        new SubView(OverlayPage.SteamLibrary, PanelSteamLibrary, null, OverlayDestination.Steam,
            Title: "Steam library", Icon: Icons.SteamLike),
        new SubView(OverlayPage.SteamLaunchFixes, PanelSteamLaunch, null, OverlayDestination.Steam,
            Title: "Per-game launch fixes", Icon: Icons.Rocket),
        new SubView(OverlayPage.SteamGameTools, GameToolsHost, null, OverlayDestination.Steam,
            Title: "Game Tools", Icon: Icons.Wrench, Available: () => ViewModel.ShowGameTools),
        new SubView(OverlayPage.SystemTools, PanelSystemTools, null, OverlayDestination.System, Title: "System",
            Icon: Icons.Gear),
        // The Device pages are one view, which draws whichever page the stack names: entering or leaving one
        // redraws it, and the Controller page holds the glyph input lease while it shows.
        new SubView(OverlayPage.DevicePluginSection, DevicePage, DevicePage, OverlayDestination.Device,
            LeaveDevicePage, OnEnter: RefreshDevicePanel),
        new SubView(OverlayPage.DeviceGpu, DevicePage, DevicePage, OverlayDestination.Device, LeaveDevicePage,
            OnEnter: RefreshDevicePanel),
        new SubView(OverlayPage.SystemPerformance, PanelSystemPerformance, null, OverlayDestination.System,
            Title: "Performance", Icon: Icons.Monitor, Available: () => PanelSystemPerformance.Offered),
        new SubView(OverlayPage.SystemStorage, PanelSystemStorage, null, OverlayDestination.System,
            Title: "Storage", Icon: Icons.SdCard),
        new SubView(OverlayPage.SystemDisplay, PanelSystemDisplay, null, OverlayDestination.System,
            Title: "Display", Icon: Icons.Monitor),
        new SubView(OverlayPage.SystemPlugins, PanelSystemPlugins, null, OverlayDestination.System,
            Title: "Plugins", Icon: Icons.Wrench, Available: () => PanelSystemPlugins.Offered),
        new SubView(OverlayPage.SystemThemes, ThemesHost, null, OverlayDestination.System,
            Title: "CSS Loader", Icon: Icons.Palette, Available: () => ViewModel.ShowThemes,
            PinId: "system.themes", Description: "Restyle Big Picture with CSS Loader themes from DeckThemes"),
        new SubView(OverlayPage.SystemAnimations, AnimationsHost, null, OverlayDestination.System,
            Title: "Video Switcher", Icon: Icons.Play, Available: () => ViewModel.ShowAnimations,
            PinId: "system.animations", Description: "The boot movie, from SteamDeckRepo or your own"),
        new SubView(OverlayPage.SystemSounds, SoundsHost, null, OverlayDestination.System,
            SoundsHost.StopPreview, "Sounds", Icons.Play, () => ViewModel.ShowSounds,
            PinId: "system.sounds", Description: "Install, preview and select Steam UI sound packs"),
        new SubView(OverlayPage.SystemArtwork, ArtworkHost, null, OverlayDestination.System,
            Title: "Steam Artwork Changer", Icon: Icons.Palette, Available: () => ViewModel.ShowArtwork),
        new SubView(OverlayPage.SteamGameLibrary, GameLibraryHost, null, OverlayDestination.System,
            Title: "Library Importer", Icon: Icons.Grid4, Available: () => ViewModel.ShowGameLibrary,
            PinId: "system.game-library",
            Description: "Bring games from other launchers into Steam, with their artwork"),
        new SubView(OverlayPage.EmulatorManager, EmulatorManagerHost, null, OverlayDestination.System,
            Title: "Emulators", Icon: Icons.Grid4, Available: () => ViewModel.ShowEmulators,
            PinId: "system.emulator-manager", Description: "Install, update and configure emulators"),
        new SubView(OverlayPage.Programs, ProgramsHost, null, OverlayDestination.System,
            Title: "Programs", Icon: Icons.Rocket),
        new SubView(OverlayPage.Notes, NotesHost, null, OverlayDestination.System,
            Title: "Notes", Icon: Icons.ListLines, PinId: "system.notes",
            Description: "Game notes with text, drawings and screenshots"),
        new SubView(OverlayPage.SystemBrowser, BrowserHost, null, OverlayDestination.System,
            Title: "Browser", Icon: Icons.Monitor),
        new SubView(OverlayPage.Screensavers, ScreensaversHost, null, OverlayDestination.System,
            Title: "Screensavers", Icon: Icons.Monitor, PinId: "system.screensavers",
            Description: "Local image folders and scene packs"),
        new SubView(OverlayPage.SystemController, PanelSystemController, null,
            OverlayDestination.System, Title: "Keyboard", Icon: Icons.Keyboard),
        new SubView(OverlayPage.SystemAbout, PanelSystemAbout, null, OverlayDestination.System, Title: "About",
            Icon: Icons.Info),
        new SubView(OverlayPage.PowerWake, PanelPowerWake, null, OverlayDestination.Power, Title: "Wake",
            Icon: Icons.Mug),
        new SubView(OverlayPage.PowerTimeouts, PanelPowerTimeouts, null, OverlayDestination.Power,
            Title: "Idle timeouts", Icon: Icons.Moon),
        new SubView(OverlayPage.PowerActions, PanelPowerActions, null, OverlayDestination.Power, Title: "Power",
            Icon: Icons.Power),
        new SubView(OverlayPage.PowerSession, PanelPowerSession, null, OverlayDestination.Power, Title: "Session",
            Icon: Icons.Play),

        new SubView(OverlayPage.SteamStorageFormat, StorageFormatHost, PanelSystemStorage, OverlayDestination.System,
            StorageFormatHost.ForgetTarget),
        new SubView(OverlayPage.SteamLibraryTabs, LibraryTabsHost, PanelSteamLibrary, OverlayDestination.Steam),
        new SubView(OverlayPage.SteamCardManager, CardManagerHost, PanelSteamLibrary, OverlayDestination.Steam),

        new SubView(OverlayPage.SteamLaunchConfiguration, LaunchWrapperHost, PanelSteamLaunch,
            OverlayDestination.Steam, PanelSteamLaunch.ForgetPendingFix),
        new SubView(OverlayPage.PowerWakeLocks, WakeLockHost, PanelPowerWake, OverlayDestination.Power),
        new SubView(OverlayPage.DeviceColor, DeviceColorHost, DevicePage, OverlayDestination.Device)
    ];

    /// <summary>The nested page currently owning the surface, or null at a destination root.</summary>
    private SubView? ActiveSubView =>
        SubViews.FirstOrDefault(view => view.Page == _navigation.Page);

    /// <summary>
    ///     Whether any nested page owns the surface. While one does, LB/RB destination
    ///     switching is suppressed and B cancels the page rather than closing the overlay.
    /// </summary>
    private bool AnySubView => ActiveSubView is not null;

    /// <summary>
    ///     The control gamepad navigation should land on when the panel opens
    ///     or when focus tracking is lost: the active destination's first row — HomeAppButton
    ///     is invisible on other destinations and focusing it would fall through to
    ///     the header close button.
    /// </summary>
    internal InputElement DefaultFocusTarget
    {
        get
        {
            if (Surfaces.FocusTarget is { } surfaceTarget)
            {
                return surfaceTarget;
            }

            if (_navigation.Depth <= 2 && SelectedSectionButton is { } sectionButton)
            {
                return sectionButton;
            }

            // Nested pages retain focus ownership while one is open.
            if (ActiveSubView is { } nested && FocusSearch.First<Button>(nested.Host, IsFocusableButton) is
                    { } nestedButton)
            {
                return nestedButton;
            }

            if (DestinationPanel() is { } root && FocusSearch.First<Button>(root, IsFocusableButton) is { } button)
            {
                return button;
            }

            // An empty Quick access root has no row: land on the first tab button so LB/RB
            // and the D-pad still lead somewhere visible. The close pill is header chrome and
            // always present, which the Session row it used to fall back to no longer is.
            return FocusSearch.FirstNavigable(Tabs) ?? CloseButton;
        }
    }

    /// <summary>Opens a nested page over its parent.</summary>
    /// <param name="page">The page.</param>
    /// <param name="sectionId">The published section a Device section page draws, or null.</param>
    private void EnterSubView(OverlayPage page, string? sectionId = null)
    {
        var view = SubViews.First(candidate => candidate.Page == page);
        if (ActiveSubView is { } current)
        {
            _nestedScroll[current.Page] = ContentScroller.Offset.Y;
        }

        if (!_navigation.Push(page, CurrentSemanticFocusKey(), sectionId))
        {
            return;
        }

        if (view.Host is OverlaySubView host)
        {
            host.Open();
        }

        foreach (var candidate in SubViews)
        {
            if (candidate.Host.IsVisible && !ReferenceEquals(candidate.Host, view.Host))
            {
                candidate.Host.IsVisible = false;
            }
        }

        view.Parent?.IsVisible = false;
        view.Host.IsVisible = true;
        ContentScroller.Offset = default;
        view.OnEnter?.Invoke();
        SyncBackAffordance();
        FocusFirstControl(view.Host);
    }

    private void LeaveSubView(OverlayPage page)
    {
        if (_navigation.Page == page)
        {
            LeaveActiveSubView();
        }
    }

    private void LeaveActiveSubView()
    {
        if (ActiveSubView is not { } view)
        {
            return;
        }

        var returnFocusKey = _navigation.Pop();
        view.OnLeave?.Invoke();
        if (view.Host is OverlaySubView leaving)
        {
            leaving.Leave();
        }

        // Closes any keyboard surface the page opened; without it the keyboard can outlive its
        // sub-view and keep writing back to a now-hidden field.
        SubViewClosed?.Invoke();
        view.Host.IsVisible = false;
        view.Parent?.IsVisible = _navigation.Destination == view.Destination;
        if (ActiveSubView is { } caller)
        {
            caller.Host.IsVisible = true;
            if (view.Parent is { } parent && !ReferenceEquals(parent, caller.Host))
            {
                parent.IsVisible = false;
            }

            FocusByKey(caller.Host, returnFocusKey);
            ContentScroller.Offset = new Vector(0, _nestedScroll.GetValueOrDefault(caller.Page));
        }

        // The Device panel skips refreshes while a page covers it, so it draws what it missed now.
        if (_devicePanelStale && DevicePage.IsVisible)
        {
            RefreshDevicePanel();
        }

        SyncBackAffordance();
        // A section without a parent leaves its destination at the root, where the rail takes focus.
        if (ActiveSubView is null && view.Parent?.IsVisible != false)
        {
            if (view.Parent is { } parent && !ReferenceEquals(parent, DestinationPanel()))
            {
                FocusByKey(parent, returnFocusKey);
            }
            else
            {
                RestoreRootFocus(returnFocusKey);
            }
        }
    }

    /// <summary>Shows or hides the Device destination for a snapshot and leaves a page it no longer publishes.</summary>
    /// <param name="device">The Device snapshot.</param>
    private void ConfigureTabs(DeviceOverlaySnapshot device)
    {
        var showDevice = device.Visible;
        if (_powerSchemeSelection is not null
            && _navigation.NeedsDeviceRoot(device.PluginSections.Select(section => section.SectionId)))
        {
            // Keep Core controls reachable when the plugin owning the open section disappears.
            // The normal root transition also releases nested-page resources and restores focus.
            SelectDestination(OverlayDestination.Device);
        }

        if (!showDevice)
        {
            // A coordinator can retract Device while the Controller page is still selected. No tab
            // selection event is raised for that removal, so release the high-rate sample observer
            // here before the page and its tiles disappear.
            ObserveGlyphInput(false);
        }

        var previous = _navigation.Destination;
        var visibilityChanged = _navigation.SetDeviceVisible(showDevice, _powerSchemeSelection is not null);
        var deviceAvailable = _navigation.IsVisible(OverlayDestination.Device);
        if (!visibilityChanged && Tabs.Tabs is not null)
        {
            return;
        }

        if (previous == OverlayDestination.Device && !deviceAvailable)
        {
            RememberDestinationState(previous);
            _session.Destination = OverlayDestination.QuickAccess;
        }

        RebuildDestinationTabs();
        // The shared performance rows live on Tools only while there is no Device destination.
        RefreshPerformancePanel();
    }

    private void ConfigureGpuDestination(bool showGraphics)
    {
        if (_navigation.SetGraphicsVisible(showGraphics) || Tabs.Tabs is null)
        {
            RebuildDestinationTabs();
            RefreshPerformancePanel();
        }
    }

    private void RebuildDestinationTabs()
    {
        Tabs.Tabs = [.. _navigation.VisibleDestinations.Select(CreateDestinationTab)];
        var selectedIndex = DestinationIndex(_navigation.Destination);
        // Rebuilding a dynamic strip can change the meaning of an unchanged numeric
        // index (System 2 becomes Device 2). Force one descriptor-based selection.
        Tabs.SelectedIndex = -1;
        Tabs.SelectedIndex = selectedIndex;
        ShowDestination(_navigation.Destination, false);
    }

    // Labels are uppercased for the sheet's tracked strip; DestinationLabel stays the
    // sentence-case name everything else (the eyebrow uppercases itself) uses.
    private static TabStripItem CreateDestinationTab(OverlayDestination destination)
    {
        var icon = destination switch
        {
            OverlayDestination.QuickAccess => Icons.Panel,
            OverlayDestination.Steam => Icons.SteamLike,
            OverlayDestination.Device => Icons.Gear,
            OverlayDestination.System => Icons.Wrench,
            OverlayDestination.Power => Icons.Power,
            _ => throw new ArgumentOutOfRangeException(nameof(destination))
        };
        return new TabStripItem(DestinationLabel(destination).ToUpperInvariant(), icon, (int)destination);
    }

    /// <summary>The user-facing name of a destination — the strip label and the header eyebrow.</summary>
    /// <param name="destination">Stable top-level destination.</param>
    /// <returns>The visible destination label.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The destination is not a defined top-level route.</exception>
    internal static string DestinationLabel(OverlayDestination destination)
    {
        return destination switch
        {
            OverlayDestination.QuickAccess => "Quick access",
            OverlayDestination.Steam => "Steam",
            OverlayDestination.Device => "Device",
            OverlayDestination.System => "Tools",
            OverlayDestination.Power => "Power",
            _ => throw new ArgumentOutOfRangeException(nameof(destination))
        };
    }

    private int DestinationIndex(OverlayDestination destination)
    {
        var tabs = Tabs.Tabs;
        if (tabs is null)
        {
            return 0;
        }

        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Tag == (int)destination)
            {
                return i;
            }
        }

        return 0;
    }

    private static bool IsFocusableButton(Button button)
    {
        return button is { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true };
    }

    /// <summary>
    ///     Selects the previous destination (LB), wrapping from the first to the
    ///     last. Suppressed while a nested page owns the surface.
    /// </summary>
    internal void SelectPreviousTab()
    {
        if (!Surfaces.HasActive)
        {
            Tabs.SelectPrevious();
        }
    }

    /// <summary>Selects the next destination (RB). Suppressed while a nested page is open.</summary>
    internal void SelectNextTab()
    {
        if (!Surfaces.HasActive)
        {
            Tabs.SelectNext();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e is OnScreenKeyboard.EditorKeyEventArgs)
        {
            return;
        }

        if (e.Key is Key.PageUp or Key.PageDown && !Surfaces.HasActive)
        {
            if (e.Key == Key.PageUp)
            {
                Tabs.SelectPrevious();
            }
            else
            {
                Tabs.SelectNext();
            }

            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        if (!TryCancelSubView())
        {
            Dismissed?.Invoke();
        }

        e.Handled = true;
    }

    /// <summary>The header's up-affordance: the same action B takes, for touch and mouse.</summary>
    /// <remarks>
    ///     In the fixed header rather than in the page, because a way out placed in scrolling content
    ///     is not reachable from where the user actually is — a long Device section pushes it past the
    ///     bottom edge. One button for every nested page, since every one of them is left the same way.
    /// </remarks>
    private void OnHeaderBack(object? sender, RoutedEventArgs e)
    {
        TryCancelSubView();
    }

    /// <summary>
    ///     Shows the header's Back button exactly while there is a level above the open page.
    ///     Called after every transition that can change the depth of the nested-page stack.
    /// </summary>
    private void SyncBackAffordance()
    {
        BackButton.IsVisible = _navigation.Depth > 1;
        SyncSectionSelection();
    }

    /// <summary>
    ///     Handles Back/B in strict dialog, nested-page, destination-root order.
    ///     Returns false only when Home is already at its root and the controller should
    ///     close the overlay. A format already running keeps running when its page closes.
    /// </summary>
    /// <remarks>
    ///     Every way back — B, Escape and the header button — arrives here, so the header affordance is
    ///     resolved once on the way out instead of at each branch's own return.
    /// </remarks>
    /// <returns>True when the current nested editor or route consumes Back; false when rail/home handling should continue.</returns>
    internal bool TryCancelSubView()
    {
        if (BrowserHost.TryReturnToChrome())
        {
            return true;
        }

        var handled = CancelOpenPage();
        SyncBackAffordance();
        return handled;
    }

    private bool CancelOpenPage()
    {
        var selected = SelectedSectionButton;
        var focused = GetTopLevel(this)?.FocusManager.GetFocusedElement();
        var context = new OverlayBackContext(
            _powerConfirms.IsArmed,
            Surfaces.HasActive,
            ActiveSubView?.Host is OverlaySubView { HasNestedLevel: true },
            ReferenceEquals(focused, selected),
            selected is not null && focused is Control);
        switch (_navigation.BackAction(context))
        {
            case OverlayBackAction.CloseSurface:
                return Surfaces.CloseActive();
            case OverlayBackAction.NestedBack:
                return ActiveSubView?.Host is OverlaySubView currentView && currentView.Back();
            case OverlayBackAction.FocusRail:
                selected?.Focus(NavigationMethod.Directional);
                return true;
            case OverlayBackAction.CloseDialog:
                _powerConfirms.Reset();
                return true;
            case OverlayBackAction.LeaveNestedPage:
                // A self-drawing sub-view handles its own deeper levels; at its root it raises
                // CloseRequested, which pops this window's page entry.
                if (ActiveSubView is { Host: OverlaySubView nested })
                {
                    return nested.Back();
                }

                if (AnySubView)
                {
                    LeaveActiveSubView();
                    return true;
                }

                // Every branch above owns its own return focus. This is the fallback for a nested
                // page none of them claimed — a page added later, or a sub-view flag that went out
                // of step with the stack — and it has to restore focus like the rest of them.
                // Popping bare would leave the user at the top of the page they came back to, with
                // no indication of where they had been.
                RestoreRootFocus(_navigation.Pop());
                return true;
            case OverlayBackAction.ReturnHome:
                SelectDestination(OverlayDestination.QuickAccess);
                return true;
            case OverlayBackAction.CloseOverlay:
            default:
                return false;
        }
    }

    /// <summary>
    ///     One selection path for touch, mouse and LB/RB: the strip carries stable
    ///     destination IDs, while this window owns page visibility and semantic focus.
    /// </summary>
    private void OnTabSelectionChanged(object? sender, TabStripSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is null
            || !Enum.IsDefined((OverlayDestination)e.SelectedItem.Tag))
        {
            return;
        }

        ActivateDestination((OverlayDestination)e.SelectedItem.Tag);
    }

    private void SelectDestination(OverlayDestination destination)
    {
        if (!_navigation.IsVisible(destination))
        {
            destination = OverlayDestination.QuickAccess;
        }

        var index = DestinationIndex(destination);
        if (Tabs.SelectedIndex != index)
        {
            Tabs.SelectedIndex = index;
            return;
        }

        ActivateDestination(destination);
    }

    /// <summary>Leaves the current destination, remembering its state, and shows another at its root.</summary>
    private void ActivateDestination(OverlayDestination destination)
    {
        RememberDestinationState(_navigation.Destination);
        LeaveAllNestedPages();
        if (!_navigation.Select(destination))
        {
            return;
        }

        _session.Destination = destination;
        ShowDestination(destination, true);
    }

    private void ShowDestination(OverlayDestination destination, bool restoreFocus)
    {
        // Selecting a destination resets its stack to the root, so the header affordance is
        // resolved here as well as on the enter/leave paths.
        SyncBackAffordance();
        PanelQuickAccess.IsVisible = destination == OverlayDestination.QuickAccess;
        DevicePage.IsVisible = destination == OverlayDestination.Device
                               && _navigation.IsVisible(OverlayDestination.Device);

        // The Device rows are built once per render into a panel that survives destination changes,
        // and until this call arriving here they were rebuilt only on attach and on a device-state
        // event. Selecting the destination resets the navigation stack to the Device root, so
        // without a render the panel kept rows belonging to whatever page was last drawn — showing
        // a section's contents under the root heading, or nothing at all when the attach-time
        // render had happened before the plugin published anything. The user reached an empty
        // "DEVICE CONTROLS" this way while all 16 capabilities were live.
        if (DevicePage.IsVisible)
        {
            RefreshDevicePanel();
        }

        // Quick access and the Device page's widget toggles are not kept current while hidden, so
        // they are drawn each time they show.
        RenderPins();
        RefreshPluginWidgets(true, false);
        RestoreDestinationState(restoreFocus);
        RefreshWorkspace();
        SelectRememberedSection(restoreFocus);
        if (restoreFocus && destination == OverlayDestination.Device && _powerSchemeSelection is { } schemes)
        {
            _ = schemes.RefreshAsync();
        }
    }

    /// <summary>The page a destination shows at its root, or null for one whose rail is all it offers there.</summary>
    private Control? DestinationPanel()
    {
        return _navigation.Destination switch
        {
            OverlayDestination.QuickAccess => PanelQuickAccess,
            OverlayDestination.Device => DevicePage,
            _ => null
        };
    }

    private void RememberDestinationState(OverlayDestination destination)
    {
        var previous = _session.Focus.Recall(destination);
        var semanticKey = CurrentSemanticFocusKey() ?? previous.SemanticKey;
        _session.Focus.Remember(destination, semanticKey, ContentScroller.Offset.Y);
    }

    private string? CurrentSemanticFocusKey()
    {
        return GetTopLevel(this)?.FocusManager.GetFocusedElement()
            is Control { Tag: string key }
            ? key
            : null;
    }

    private void RestoreRootFocus(string? semanticKey)
    {
        var state = _session.Focus.Recall(_navigation.Destination);
        _session.Focus.Remember(
            _navigation.Destination,
            semanticKey ?? state.SemanticKey,
            state.ScrollOffset);
        RestoreDestinationState(true);
    }

    private void RestoreDestinationState(bool focus)
    {
        var state = _session.Focus.Recall(_navigation.Destination);
        ContentScroller.Offset = new Vector(0, state.ScrollOffset);
        if (!focus)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || Surfaces.HasActive || AnySubView)
            {
                return;
            }

            if (SelectedSectionButton is { } sectionButton)
            {
                sectionButton.Focus(NavigationMethod.Directional);
                return;
            }

            if (DestinationPanel() is { } root)
            {
                FocusByKey(root, state.SemanticKey);
            }
        });
    }

    private void LeaveAllNestedPages()
    {
        // Unwound rather than named one by one: a category page can have another page open above
        // it, and the list of every sub-view that had to be closed here went stale the moment a
        // page was added. Each pop runs that page's own OnLeave, innermost first.
        while (AnySubView && _navigation.Depth > 1)
        {
            LeaveActiveSubView();
        }
    }

    /// <summary>Leaves a Device section or the GPU page: the view draws the page beneath it.</summary>
    private void LeaveDevicePage()
    {
        ObserveGlyphInput(false);
        RefreshDevicePanel();
    }

    private static void FocusFirstControl(Control panel)
    {
        FocusSearch.FirstNavigable(panel)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>Focuses the control carrying a semantic key, or the panel's first control without one.</summary>
    private static void FocusByKey(Control panel, string? key)
    {
        if (FocusSearch.Keyed(panel, key) is { } target)
        {
            target.Focus(NavigationMethod.Directional);
        }
        else
        {
            FocusFirstControl(panel);
        }
    }

    /// <summary>
    ///     Opens a nested page from the section it belongs to. A pinned copy on Quick access asks from another
    ///     destination, so the page's section is selected first, as choosing it on the rail does.
    /// </summary>
    /// <param name="page">The nested page.</param>
    private void OpenPage(OverlayPage page)
    {
        var view = SubViews.First(candidate => candidate.Page == page);
        if (_navigation.Destination != view.Destination
            && SubViews.FirstOrDefault(section => section.Title is not null
                                                  && ReferenceEquals(section.Host, view.Parent)) is { } owner
            && !OpenSection(owner.Page))
        {
            return;
        }

        EnterSubView(page);
    }

    /// <summary>Selects a section the way choosing it on its destination's rail does.</summary>
    /// <param name="page">The section's page.</param>
    /// <returns>Whether the section is offered in this window.</returns>
    private bool OpenSection(OverlayPage page)
    {
        var destination = SubViews.First(view => view.Page == page).Destination;
        if (_navigation.Destination != destination)
        {
            SelectDestination(destination);
        }

        var section = _workspaceSections.FirstOrDefault(item => item.Page == page);
        if (section is null)
        {
            return false;
        }

        SelectWorkspaceSection(section, false);
        return true;
    }

    /// <summary>Shows Tools > Notes, which opens on the page the overlay last showed.</summary>
    /// <returns>Whether the Notes section exists in this window.</returns>
    internal bool OpenNotes()
    {
        if (_navigation.Page == OverlayPage.Notes)
        {
            // Already showing: start over from the remembered place rather than keep a stale level.
            NotesHost.OpenLastLocation();
        }
        else if (!OpenSection(OverlayPage.Notes))
        {
            return false;
        }

        FocusFirstControl(NotesHost);
        return true;
    }

    /// <summary>Shows the storage format flow over Tools > Storage, from wherever it was started.</summary>
    private void OpenStorageFormat()
    {
        OpenSection(OverlayPage.SystemStorage);
        EnterSubView(OverlayPage.SteamStorageFormat);
    }

    /// <summary>
    ///     Brings a controller-focused control into the overlay viewport and remembers a root row's
    ///     semantic key. Directional focus navigation does not raise this request on its own, so
    ///     without it the lower keyboard rows could be focused off-screen.
    /// </summary>
    private void OnContentGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is not Control control || control is ScrollViewer)
        {
            return;
        }

        control.BringIntoView();
        if (AnySubView || control.Tag is not string semanticKey)
        {
            return;
        }

        _session.Focus.Remember(
            _navigation.Destination,
            semanticKey,
            ContentScroller.Offset.Y);
    }

    /// <summary>
    ///     One in-place nested page: what it pushes onto the navigation stack, the host it
    ///     reveals, the destination panel it hides while it is up, and any state it owns.
    /// </summary>
    /// <param name="Page">The navigation page; also the identity of the open sub-view.</param>
    /// <param name="Host">The control revealed while the page is open.</param>
    /// <param name="Parent">The page hidden behind it, or null for a section at its destination's root.</param>
    /// <param name="Destination">The destination that panel belongs to.</param>
    /// <param name="OnLeave">
    ///     State the page owns, released before the keyboard surface is told
    ///     to close so nothing re-reads a value the page has already abandoned.
    /// </param>
    /// <param name="Title">The section rail label, or null for a deeper page.</param>
    /// <param name="Icon">The section icon.</param>
    /// <param name="Available">Whether the section is currently offered.</param>
    /// <param name="OnEnter">Draws the page once it is on the stack and shown.</param>
    /// <param name="PinId">The id the section pins to Quick access under, as a card that opens it, or null.</param>
    /// <param name="Description">The pinned card's description.</param>
    private sealed record SubView(
        OverlayPage Page,
        Control Host,
        Control? Parent,
        OverlayDestination Destination,
        Action? OnLeave = null,
        string? Title = null,
        Geometry? Icon = null,
        Func<bool>? Available = null,
        Action? OnEnter = null,
        string? PinId = null,
        string? Description = null);
}

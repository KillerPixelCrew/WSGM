using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WSGM.Controls;
using WSGM.Input;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private readonly PixelPoint? _preferredScreenPoint;
    private readonly List<(OverlaySubView Host, Action Leave)> _subViewCloseHandlers = [];

    private readonly double _uiScale;

    /// <summary>
    ///     The factor RootScale currently applies (1.0 = no transform). The
    ///     sheet's inner layout happens in pre-transform units, so every budget derived
    ///     from the window's width has to divide by this first.
    /// </summary>
    private double _contentScale = 1.0;

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
        // The category pages: each destination root is a menu, and its groups of controls are
        // pages one level down. Nesting is what the stack is for, so a page opened from inside a
        // category names that category as its parent rather than the destination root.
        new SubView(OverlayPage.SteamLibrary, PanelSteamLibrary, PanelSteam, OverlayDestination.Steam),
        new SubView(OverlayPage.SteamLaunchFixes, PanelSteamLaunch, PanelSteam, OverlayDestination.Steam),
        new SubView(OverlayPage.SystemTools, PanelSystemTools, PanelSystem, OverlayDestination.System),
        new SubView(OverlayPage.SystemPerformance, PanelSystemPerformance, PanelSystem,
            OverlayDestination.System),
        new SubView(OverlayPage.SystemStorage, PanelSystemStorage, PanelSystem, OverlayDestination.System),
        new SubView(OverlayPage.SystemDisplay, PanelSystemDisplay, PanelSystem, OverlayDestination.System),
        new SubView(OverlayPage.SystemPlugins, PanelSystemPlugins, PanelSystem, OverlayDestination.System),
        new SubView(OverlayPage.SystemController, PanelSystemController, PanelSystem,
            OverlayDestination.System),
        new SubView(OverlayPage.PowerWake, PanelPowerWake, PanelPower, OverlayDestination.Power),
        new SubView(OverlayPage.PowerTimeouts, PanelPowerTimeouts, PanelPower, OverlayDestination.Power),
        new SubView(OverlayPage.PowerActions, PanelPowerActions, PanelPower, OverlayDestination.Power),
        new SubView(OverlayPage.PowerSession, PanelPowerSession, PanelPower, OverlayDestination.Power),

        new SubView(OverlayPage.SteamStorageFormat, PanelFormat, PanelSteamLibrary, OverlayDestination.Steam,
            () =>
            {
                _pendingTarget = null;
                _formatReturnsToCards = false;
            }),
        new SubView(OverlayPage.SteamLibraryTabs, LibraryTabsHost, PanelSteamLibrary, OverlayDestination.Steam),
        new SubView(OverlayPage.SteamCardManager, CardManagerHost, PanelSteamLibrary, OverlayDestination.Steam),
        new SubView(OverlayPage.SteamLaunchConfiguration, LaunchWrapperHost, PanelSteamLaunch,
            OverlayDestination.Steam,
            () =>
            {
                _pendingLaunchFix = null;
                // Clears the "Asking Steam…" title left on whichever button opened the picker.
                // A pick re-writes it moments later with the real outcome.
                if (DataContext is OverlayViewModel viewModel)
                {
                    InitializeLaunchFixLabels(viewModel);
                }
            }),
        new SubView(OverlayPage.PowerWakeLocks, WakeLockHost, PanelPowerWake, OverlayDestination.Power),
        new SubView(OverlayPage.DeviceColor, DeviceColorHost, PanelDevice, OverlayDestination.Device,
            RefreshDevicePanel)
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
            if (ActiveSurfaceFocusTarget is { } surfaceTarget)
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

            if (FocusSearch.First<Button>(DestinationPanel(), IsFocusableButton) is { } button)
            {
                return button;
            }

            // An empty Quick access root has no row: land on the first tab button so LB/RB
            // and the D-pad still lead somewhere visible. The close pill is header chrome and
            // always present, which the Session row it used to fall back to no longer is.
            return FocusSearch.FirstNavigable(Tabs) ?? CloseButton;
        }
    }

    private void EnterSubView(OverlayPage page)
    {
        var view = SubViews.First(candidate => candidate.Page == page);
        if (!_navigation.Push(page, CurrentSemanticFocusKey()))
        {
            return;
        }

        view.Parent.IsVisible = false;
        view.Host.IsVisible = true;
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
        // Closes any keyboard surface the page opened; without it the keyboard can outlive its
        // sub-view and keep writing back to a now-hidden field.
        SubViewClosed?.Invoke();
        view.Host.IsVisible = false;
        view.Parent.IsVisible = _navigation.Destination == view.Destination;
        SyncBackAffordance();
        if (view.Parent.IsVisible)
        {
            if (!ReferenceEquals(view.Parent, DestinationPanel()))
            {
                var target = returnFocusKey is null
                    ? null
                    : FocusSearch.First<Control>(view.Parent,
                        control => Equals(control.Tag, returnFocusKey) && control.Focusable
                                                                       && control.IsEffectivelyEnabled &&
                                                                       control.IsEffectivelyVisible);
                if (target is not null)
                {
                    target.Focus(NavigationMethod.Directional);
                }
                else
                {
                    FocusFirstControl(view.Parent);
                }
            }
            else
            {
                RestoreRootFocus(returnFocusKey);
            }
        }
    }

    private void ConfigureTabs(bool showDevice)
    {
        if (_powerSchemeSelection is not null && _navigation.NeedsDeviceRoot(showDevice))
        {
            // Keep Core controls reachable when the plugin owning the open section disappears.
            // The normal root transition also releases nested-page resources and restores focus.
            SelectDestination(OverlayDestination.Device);
        }

        if (!showDevice)
        {
            // A coordinator can retract Device while the Glyphs page is still selected. No tab
            // selection event is raised for that removal, so release the high-rate sample observer
            // here before the page and its tiles disappear.
            UpdateGlyphInputObservation(false);
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

        PlacePerformanceSection(deviceAvailable);

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
        return destination switch
        {
            OverlayDestination.QuickAccess => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(),
                Icons.Panel, (int)destination),
            OverlayDestination.Steam => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(),
                Icons.SteamLike, (int)destination),
            OverlayDestination.Device => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(), Icons.Gear,
                (int)destination),
            OverlayDestination.System => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(),
                Icons.Wrench, (int)destination),
            OverlayDestination.Power => new TabStripItem(DestinationLabel(destination).ToUpperInvariant(), Icons.Power,
                (int)destination),
            _ => throw new ArgumentOutOfRangeException(nameof(destination))
        };
    }

    /// <summary>The user-facing name of a destination — the strip label and the header eyebrow.</summary>
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
        if (!HasActiveSurface)
        {
            Tabs.SelectPrevious();
        }
    }

    /// <summary>Selects the next destination (RB). Suppressed while a nested page is open.</summary>
    internal void SelectNextTab()
    {
        if (!HasActiveSurface)
        {
            Tabs.SelectNext();
        }
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
    internal bool TryCancelSubView()
    {
        if (HasActiveSurface)
        {
            return CloseActiveSurface();
        }

        if (_navigation.Depth <= 2 && !_confirmRestart && !_confirmShutdown && !_confirmCloseLauncher
            && !_confirmSignOut && SelectedSectionButton is { } selected
            && GetTopLevel(this)?.FocusManager.GetFocusedElement() is Control focused
            && !ReferenceEquals(focused, selected))
        {
            selected.Focus(NavigationMethod.Directional);
            return true;
        }

        if (_navigation.Depth <= 2 && _navigation.Destination != OverlayDestination.QuickAccess
                                   && ReferenceEquals(GetTopLevel(this)?.FocusManager.GetFocusedElement(),
                                       SelectedSectionButton))
        {
            SelectDestination(OverlayDestination.QuickAccess);
            return true;
        }

        var handled = CancelOpenPage();
        SyncBackAffordance();
        return handled;
    }

    private bool CancelOpenPage()
    {
        var confirmationOpen = _confirmCloseLauncher || _confirmRestart || _confirmShutdown || _confirmSignOut;
        switch (_navigation.BackAction(false, confirmationOpen))
        {
            case OverlayBackAction.CloseDialog:
                ResetConfirms();
                return true;
            case OverlayBackAction.LeaveNestedPage:
                // A self-drawing sub-view handles its own deeper levels; at its root it raises
                // CloseRequested, which pops this window's page entry.
                if (ActiveSubView is { Host: OverlaySubView nested })
                {
                    return nested.Back();
                }

                // The format panel is XAML rather than a sub-view and returns to whichever surface
                // opened it, so it is named rather than left through the ordinary path. Every other
                // XAML page — the category pages included — is just popped.
                if (_navigation.Page is OverlayPage.SteamStorageFormat)
                {
                    LeaveFormatSubViewToOrigin();
                    return true;
                }

                if (AnySubView)
                {
                    LeaveActiveSubView();
                    return true;
                }

                if (DeviceOverlaySectionPages.SectionFor(_navigation.Page) is { } leaving)
                {
                    LeaveDeviceSection(leaving);
                    return true;
                }

                if (_navigation.Page is OverlayPage.DevicePluginSection)
                {
                    LeaveDevicePluginSection();
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
            case OverlayBackAction.ClosePopup:
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

        var destination = (OverlayDestination)e.SelectedItem.Tag;
        RememberDestinationState(_navigation.Destination);
        LeaveAllNestedPages();
        if (!_navigation.Select(destination))
        {
            return;
        }

        _session.Destination = destination;
        ShowDestination(destination, true);
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

        RememberDestinationState(_navigation.Destination);
        LeaveAllNestedPages();
        _navigation.Select(destination);
        _session.Destination = destination;
        ShowDestination(destination, true);
    }

    private void ShowDestination(OverlayDestination destination, bool restoreFocus)
    {
        // Selecting a destination resets its stack to the root, so the header affordance is
        // resolved here as well as on the enter/leave paths.
        SyncBackAffordance();
        PanelQuickAccess.IsVisible = destination == OverlayDestination.QuickAccess;
        PanelSteam.IsVisible = destination == OverlayDestination.Steam;
        PanelDevice.IsVisible = destination == OverlayDestination.Device
                                && _navigation.IsVisible(OverlayDestination.Device);
        PanelSystem.IsVisible = destination == OverlayDestination.System;
        PanelPower.IsVisible = destination == OverlayDestination.Power;

        // The Device rows are built once per render into a panel that survives destination changes,
        // and until this call arriving here they were rebuilt only on attach and on a device-state
        // event. Selecting the destination resets the navigation stack to the Device root, so
        // without a render the panel kept rows belonging to whatever page was last drawn — showing
        // a section's contents under the root heading, or nothing at all when the attach-time
        // render had happened before the plugin published anything. The user reached an empty
        // "DEVICE CONTROLS" this way while all 16 capabilities were live.
        // RefreshDevicePanel calls ConfigureTabs, which calls back here. That terminates today only
        // because ConfigureTabs returns early on the second pass; an explicit guard is what keeps a
        // later change to either of them from turning this into a loop that hangs the UI thread.
        if (PanelDevice.IsVisible && !_showingDestination)
        {
            _showingDestination = true;
            try
            {
                RefreshDevicePanel();
                RefreshPerformancePanel();
            }
            finally
            {
                _showingDestination = false;
            }
        }

        RestoreDestinationState(restoreFocus);
        RefreshWorkspace();
        SelectRememberedSection(restoreFocus);
        if (restoreFocus && destination == OverlayDestination.Device && _powerSchemeSelection is { } schemes)
        {
            _ = schemes.RefreshAsync();
        }
    }

    private Control DestinationPanel()
    {
        return _navigation.Destination switch
        {
            OverlayDestination.Steam => PanelSteam,
            OverlayDestination.Device => PanelDevice,
            OverlayDestination.System => PanelSystem,
            OverlayDestination.Power => PanelPower,
            _ => PanelQuickAccess
        };
    }

    private void RememberDestinationState(OverlayDestination destination)
    {
        var previous = _session.Focus.Recall(destination);
        var semanticKey = GetTopLevel(this)?.FocusManager.GetFocusedElement()
            is Control { Tag: string key }
            ? key
            : previous.SemanticKey;
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
            if (_closed || HasActiveSurface || AnySubView)
            {
                return;
            }

            if (SelectedSectionButton is { } sectionButton)
            {
                sectionButton.Focus(NavigationMethod.Directional);
                return;
            }

            var panel = DestinationPanel();
            if (state.SemanticKey is not null
                && FocusSearch.First<Control>(panel, control => control is
                {
                    Tag: string key,
                    Focusable: true,
                    IsEffectivelyEnabled: true,
                    IsEffectivelyVisible: true
                } && string.Equals(key, state.SemanticKey, StringComparison.Ordinal)) is { } target)
            {
                target.Focus(NavigationMethod.Directional);
                return;
            }

            FocusFirstControl(panel);
        });
    }

    private void LeaveAllNestedPages()
    {
        // Unwound rather than named one by one: a category page can have another page open above
        // it, and the list of every sub-view that had to be closed here went stale the moment a
        // page was added. Each pop runs that page's own OnLeave, innermost first.
        for (var depth = 0; depth < OverlayNavigation.MaximumDepth && AnySubView; depth++)
        {
            LeaveActiveSubView();
        }

        // The Device sections are not sub-views with hosts of their own, so leaving them is only
        // this: dropping the one thing a section page holds beyond its rendered controls.
        UpdateGlyphInputObservation(false);
    }

    private static void FocusFirstControl(Control panel)
    {
        FocusSearch.FirstNavigable(panel)?.Focus(NavigationMethod.Directional);
    }

    // The category menus. Each destination root offers its groups as large tiles and the controls
    // themselves live one level down, so a handheld's few visible rows are a choice rather than the
    // top of a list the controller has to scroll through. Back and B leave a category the same way
    // they leave any other page, through the sub-view stack.
    /// <summary>Opens the category page a tile names in its CommandParameter.</summary>
    private void OnEnterCategory(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: OverlayPage page })
        {
            EnterSubView(page);
        }
    }

    /// <summary>
    ///     One in-place nested page: what it pushes onto the navigation stack, the host it
    ///     reveals, the destination panel it hides while it is up, and any state it owns.
    /// </summary>
    /// <param name="Page">The navigation page; also the identity of the open sub-view.</param>
    /// <param name="Host">The control revealed while the page is open.</param>
    /// <param name="Parent">The destination panel hidden behind it.</param>
    /// <param name="Destination">The destination that panel belongs to.</param>
    /// <param name="OnLeave">
    ///     State the page owns, released before the keyboard surface is told
    ///     to close so nothing re-reads a value the page has already abandoned.
    /// </param>
    private sealed record SubView(
        OverlayPage Page,
        Control Host,
        Control Parent,
        OverlayDestination Destination,
        Action? OnLeave = null);
}

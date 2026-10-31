using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>
///     Owns top-level destination visibility and the nested-page stack without retaining
///     controls, device descriptors, or service generations.
/// </summary>
internal sealed class OverlayNavigation
{
    private readonly List<OverlayRoute> _stack = [];
    private bool _deviceVisible;
    private bool _graphicsVisible;

    /// <summary>Initializes one Quick Access root route.</summary>
    internal OverlayNavigation()
    {
        Select(OverlayDestination.QuickAccess);
    }

    /// <summary>Selected top-level destination.</summary>
    internal OverlayDestination Destination { get; private set; }

    /// <summary>Current top stack route; a destination always retains its root.</summary>
    internal OverlayPage Page => _stack[^1].Page;

    /// <summary>The plugin section id carried by the current page, when it is one.</summary>
    internal string? SectionId => _stack[^1].SectionId;

    /// <summary>Number of routes including the destination root.</summary>
    internal int Depth => _stack.Count;

    /// <summary>Fresh destination list in enum/navigation order after current visibility policy.</summary>
    internal IReadOnlyList<OverlayDestination> VisibleDestinations =>
    [
        .. Enum.GetValues<OverlayDestination>().Where(IsVisible)
    ];

    /// <summary>Determines whether disappearance of plugin controls invalidates the current dynamic section.</summary>
    /// <param name="pluginVisible">Whether device-plugin controls are available.</param>
    /// <returns>True for a vanished plugin section other than the retained core Power and Controller sections.</returns>
    internal bool NeedsDeviceRoot(bool pluginVisible)
    {
        return !pluginVisible && Page == OverlayPage.DevicePluginSection
                              && SectionId != DeviceSections.PowerId
                              && SectionId != DeviceSections.ControllerId;
    }

    /// <summary>Evaluates destination availability without changing navigation.</summary>
    /// <param name="destination">Top-level route to inspect.</param>
    /// <returns>Device requires device/core or graphics availability; other destinations are available.</returns>
    internal bool IsVisible(OverlayDestination destination)
    {
        return destination switch
        {
            OverlayDestination.Device => _deviceVisible || _graphicsVisible,
            _ => true
        };
    }

    /// <summary>Keeps Device available for GPU controls independently of device integration.</summary>
    /// <param name="visible">Whether at least one graphics publisher is running.</param>
    /// <returns>Whether the visibility changed.</returns>
    internal bool SetGraphicsVisible(bool visible)
    {
        if (_graphicsVisible == visible)
        {
            return false;
        }

        var deviceVisible = IsVisible(OverlayDestination.Device);
        _graphicsVisible = visible;
        if (!IsVisible(OverlayDestination.Device) && Destination == OverlayDestination.Device)
        {
            Select(OverlayDestination.QuickAccess);
        }

        return deviceVisible != IsVisible(OverlayDestination.Device);
    }

    /// <summary>Updates device/core availability and returns home if the current destination becomes hidden.</summary>
    /// <param name="visible">Whether device-plugin controls are available.</param>
    /// <param name="coreControlsAvailable">Whether core Windows controls keep Device reachable without a plugin.</param>
    /// <returns>True when the combined device/core availability flag changes.</returns>
    internal bool SetDeviceVisible(bool visible, bool coreControlsAvailable = false)
    {
        visible |= coreControlsAvailable;
        if (_deviceVisible == visible)
        {
            return false;
        }

        _deviceVisible = visible;
        if (!IsVisible(OverlayDestination.Device) && Destination == OverlayDestination.Device)
        {
            Select(OverlayDestination.QuickAccess);
        }

        return true;
    }

    /// <summary>Selects an available destination and replaces its nested stack with the root route.</summary>
    /// <param name="destination">Requested destination.</param>
    /// <returns>True when selected, including reselecting the current destination; false when hidden.</returns>
    internal bool Select(OverlayDestination destination)
    {
        if (!IsVisible(destination))
        {
            Log.Warn($"Overlay nav: destination {destination} refused (not visible).");
            return false;
        }

        Destination = destination;
        _stack.Clear();
        _stack.Add(new OverlayRoute(RootPage(destination), null));
        Log.Debug($"Overlay nav: destination {destination}, page {Page}.");
        return true;
    }

    /// <summary>Adds a nested route only when it belongs to the selected destination.</summary>
    /// <param name="page">Requested nested page.</param>
    /// <param name="returnFocusKey">Caller control to focus when this route is popped.</param>
    /// <param name="sectionId">Optional dynamic plugin section identity.</param>
    /// <returns>True when pushed; false for a cross-destination route.</returns>
    internal bool Push(OverlayPage page, string? returnFocusKey, string? sectionId = null)
    {
        if (DestinationFor(page) != Destination)
        {
            Log.Warn($"Overlay nav: push {page} refused from {Destination}/{Page} "
                     + $"(depth={_stack.Count}, pageDestination={DestinationFor(page)}).");
            return false;
        }

        _stack.Add(new OverlayRoute(page, returnFocusKey, sectionId));
        Log.Info($"Overlay nav: pushed {page} (depth={_stack.Count}).");
        return true;
    }

    /// <summary>Pops one nested route; the root remains mounted.</summary>
    /// <returns>The popped route's return-focus key, or null at the root or when no key was supplied.</returns>
    internal string? Pop()
    {
        if (_stack.Count <= 1)
        {
            return null;
        }

        var returnFocusKey = _stack[^1].ReturnFocusKey;
        _stack.RemoveAt(_stack.Count - 1);
        Log.Info($"Overlay nav: popped to {Page} (depth={_stack.Count}).");
        return returnFocusKey;
    }

    /// <summary>Chooses one Back action without mutating the stack or UI.</summary>
    /// <param name="context">Current dialog/surface, nested-level and rail state.</param>
    /// <returns>The highest-priority close, nested-navigation, rail or destination action.</returns>
    internal OverlayBackAction BackAction(OverlayBackContext context)
    {
        if (context.SurfaceOpen)
        {
            return OverlayBackAction.CloseSurface;
        }

        if (context.NestedLevel)
        {
            return OverlayBackAction.NestedBack;
        }

        if (context.DialogOpen)
        {
            return OverlayBackAction.CloseDialog;
        }

        if (_stack.Count <= 2 && context.RailCanTakeFocus && !context.FocusInRail)
        {
            return OverlayBackAction.FocusRail;
        }

        if (_stack.Count <= 2 && context.FocusInRail && Destination != OverlayDestination.QuickAccess)
        {
            return OverlayBackAction.ReturnHome;
        }

        if (_stack.Count > 1)
        {
            return OverlayBackAction.LeaveNestedPage;
        }

        // Quick access is the sheet's home: Back from any other root returns there, and Back
        // from it closes the sheet.
        return Destination == OverlayDestination.QuickAccess
            ? OverlayBackAction.CloseOverlay
            : OverlayBackAction.ReturnHome;
    }

    private static OverlayPage RootPage(OverlayDestination destination)
    {
        return destination switch
        {
            OverlayDestination.QuickAccess => OverlayPage.QuickAccess,
            OverlayDestination.Steam => OverlayPage.Steam,
            OverlayDestination.Device => OverlayPage.Device,
            OverlayDestination.System => OverlayPage.System,
            OverlayDestination.Power => OverlayPage.Power,
            _ => throw new ArgumentOutOfRangeException(nameof(destination))
        };
    }

    private static OverlayDestination DestinationFor(OverlayPage page)
    {
        return page switch
        {
            OverlayPage.QuickAccess => OverlayDestination.QuickAccess,
            OverlayPage.Steam or OverlayPage.SteamLibrary or OverlayPage.SteamLaunchFixes
                or OverlayPage.SteamLibraryTabs or OverlayPage.SteamCardManager
                or OverlayPage.SteamLaunchConfiguration or OverlayPage.SteamGameTools
                => OverlayDestination.Steam,
            OverlayPage.Device or OverlayPage.DeviceOverview or OverlayPage.DeviceGpu
                or OverlayPage.DevicePowerAndThermals or OverlayPage.DeviceControllerAndMotion
                or OverlayPage.DeviceOem or OverlayPage.DeviceLightingAndFeatures
                or OverlayPage.DeviceColor or OverlayPage.DeviceDiagnostics
                or OverlayPage.DevicePluginSection
                => OverlayDestination.Device,
            OverlayPage.System or OverlayPage.SystemTools or OverlayPage.SystemPerformance
                or OverlayPage.SteamStorageFormat
                or OverlayPage.SystemStorage or OverlayPage.SystemDisplay
                or OverlayPage.SystemPlugins or OverlayPage.SystemThemes or OverlayPage.SystemArtwork
                or OverlayPage.SteamGameLibrary or OverlayPage.SystemAnimations
                or OverlayPage.EmulatorManager
                or OverlayPage.Programs
                or OverlayPage.Notes or OverlayPage.SystemBrowser or OverlayPage.Screensavers
                or OverlayPage.SystemSounds
                or OverlayPage.SystemController
                or OverlayPage.SystemAbout
                => OverlayDestination.System,
            OverlayPage.Power or OverlayPage.PowerWake or OverlayPage.PowerTimeouts
                or OverlayPage.PowerActions or OverlayPage.PowerSession
                or OverlayPage.PowerWakeLocks => OverlayDestination.Power,
            _ => throw new ArgumentOutOfRangeException(nameof(page))
        };
    }
}

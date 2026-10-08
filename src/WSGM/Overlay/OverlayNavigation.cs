using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>Stable top-level destinations in the quick access sheet, in strip order.</summary>
internal enum OverlayDestination
{
    /// <summary>The pinned rows — the sheet's home and the Back target of every other root.</summary>
    QuickAccess,
    Steam,
    Device,

    /// <summary>System and storage tools (labelled "Tools").</summary>
    System,

    /// <summary>Wake, idle timeouts and the session-ending actions.</summary>
    Power
}

/// <summary>Stable page identifiers used by the in-overlay navigation stack.</summary>
internal enum OverlayPage
{
    QuickAccess,
    Steam,

    /// <summary>The Steam library category page.</summary>
    SteamLibrary,

    /// <summary>The per-game launch fixes category page.</summary>
    SteamLaunchFixes,
    SteamLibraryTabs,
    SteamCardManager,

    /// <summary>The Game Library: bring other launchers' games into Steam.</summary>
    SteamGameLibrary,
    SteamLaunchConfiguration,
    SteamStorageFormat,
    Device,
    DeviceOverview,

    /// <summary>Graphics controls supplied by common GPU plugins, independent of the device package.</summary>
    DeviceGpu,
    DeviceProfiles,
    DevicePowerAndThermals,
    DeviceControllerAndMotion,
    DeviceOem,
    DeviceLightingAndFeatures,
    DeviceColor,
    DeviceDiagnostics,

    /// <summary>One plugin-declared Device section; the route carries which one.</summary>
    DevicePluginSection,

    System,

    /// <summary>WSGM Settings, the Windows Task Manager and the UAC prompt policy.</summary>
    SystemTools,

    /// <summary>The frame limit, overlay and per-application profile rows, when Device is off.</summary>
    SystemPerformance,

    /// <summary>Removable storage actions.</summary>
    SystemStorage,

    /// <summary>Panel brightness and the display routes.</summary>
    SystemDisplay,

    /// <summary>Rows published by the enabled common plugins.</summary>
    SystemPlugins,

    /// <summary>The Steam themes: browse, install and manage CSS Loader themes.</summary>
    SystemThemes,
    SystemArtwork,

    /// <summary>The boot movie: browse, download and choose what Big Picture starts with.</summary>
    SystemAnimations,

    /// <summary>Steam UI sound packs and previews.</summary>
    SystemSounds,

    /// <summary>The on-screen keyboard and the Steam Input handoff.</summary>
    SystemController,

    /// <summary>Version, licence and the people and projects WSGM thanks.</summary>
    SystemAbout,
    Power,

    /// <summary>Keep Awake, the wake-lock list and the sign-in after standby.</summary>
    PowerWake,

    /// <summary>The display and standby idle timeouts.</summary>
    PowerTimeouts,

    /// <summary>Standby, hibernate, restart and shut down.</summary>
    PowerActions,

    /// <summary>
    ///     Steam start/focus, desktop, Big Picture and Steam exit. A root tab of its own until
    ///     2026-09-11: four buttons did not justify one, and they are lifecycle transitions, which
    ///     is what the rest of Power is.
    /// </summary>
    PowerSession,
    PowerWakeLocks,

    /// <summary>The standalone emulator downloader and updater under Tools.</summary>
    EmulatorManager
}

/// <summary>The single action selected by Back/B after higher-priority UI has been considered.</summary>
internal enum OverlayBackAction
{
    ClosePopup,
    CloseDialog,
    CloseSurface,
    NestedBack,
    FocusRail,
    LeaveNestedPage,

    /// <summary>Return to the Quick access root from another destination's root.</summary>
    ReturnHome,
    CloseOverlay
}

/// <summary>Snapshot of higher-priority UI consumed by the pure Back policy.</summary>
/// <param name="PopupOpen">A dropdown or popup currently has priority.</param>
/// <param name="DialogOpen">A dialog currently has priority.</param>
/// <param name="SurfaceOpen">An in-window utility or keyboard is active.</param>
/// <param name="NestedLevel">The current service view can consume one nested Back.</param>
/// <param name="FocusInRail">Focus already rests in the persistent section rail.</param>
/// <param name="RailCanTakeFocus">The current page has a section rail to return focus to.</param>
internal readonly record struct OverlayBackContext(
    bool PopupOpen,
    bool DialogOpen,
    bool SurfaceOpen = false,
    bool NestedLevel = false,
    bool FocusInRail = false,
    bool RailCanTakeFocus = false);

/// <summary>A navigation-stack entry with a semantic focus target for its caller.</summary>
/// <param name="Page">Stable page identifier.</param>
/// <param name="ReturnFocusKey">Optional semantic control key restored when returning from this route.</param>
/// <param name="SectionId">Dynamic plugin/section identity where the page route requires one.</param>
internal readonly record struct OverlayRoute(
    OverlayPage Page,
    string? ReturnFocusKey,
    string? SectionId = null);

/// <summary>Stable device section identity with distinct focus and persisted-pin encodings.</summary>
/// <param name="Id">Unprefixed shared device-section identifier.</param>
internal readonly record struct SectionKey(string Id)
{
    private const string FocusPrefix = "section.device.";
    private const string PinPrefix = "device.section.";

    /// <summary>Semantic focus key for the source section heading.</summary>
    internal string FocusKey => FocusPrefix + Id;

    /// <summary>Persisted Quick Access pin key for this section.</summary>
    internal string PinKey => PinPrefix + Id;

    /// <summary>Decodes a recognized focus or pin key.</summary>
    /// <param name="key">A section.device. or device.section. prefixed key.</param>
    /// <returns>The unprefixed section identity; unknown prefixes throw ArgumentException.</returns>
    internal static SectionKey FromKey(string key)
    {
        if (key.StartsWith(FocusPrefix, StringComparison.Ordinal))
        {
            return new SectionKey(key[FocusPrefix.Length..]);
        }

        if (key.StartsWith(PinPrefix, StringComparison.Ordinal))
        {
            return new SectionKey(key[PinPrefix.Length..]);
        }

        throw new ArgumentException("Unknown device section key.", nameof(key));
    }
}

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
    /// <param name="context">Current popup/dialog/surface, nested-level and rail state.</param>
    /// <returns>The highest-priority close, nested-navigation, rail or destination action.</returns>
    internal OverlayBackAction BackAction(OverlayBackContext context)
    {
        if (context.PopupOpen)
        {
            return OverlayBackAction.ClosePopup;
        }

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
                or OverlayPage.SteamLaunchConfiguration
                => OverlayDestination.Steam,
            OverlayPage.Device or OverlayPage.DeviceOverview or OverlayPage.DeviceGpu or OverlayPage.DeviceProfiles
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

/// <summary>Semantic focus and scroll state retained without keeping a page or control alive.</summary>
/// <param name="SemanticKey">Stable control identity, or null when no row was focused.</param>
/// <param name="ScrollOffset">Vertical scroll position in device-independent pixels.</param>
internal readonly record struct OverlayFocusState(string? SemanticKey, double ScrollOffset);

/// <summary>Stores bounded destination-local focus state across overlay window recreation.</summary>
internal sealed class OverlayFocusMemory
{
    private readonly Dictionary<OverlayDestination, OverlayFocusState> _states = new();

    /// <summary>Replaces one destination's semantic focus and scroll memory.</summary>
    /// <param name="destination">Destination whose state is saved.</param>
    /// <param name="semanticKey">Control identity; blank values become null.</param>
    /// <param name="scrollOffset">Vertical DIP offset; negative values clamp to zero.</param>
    internal void Remember(OverlayDestination destination, string? semanticKey, double scrollOffset)
    {
        _states[destination] = new OverlayFocusState(
            string.IsNullOrWhiteSpace(semanticKey) ? null : semanticKey,
            Math.Max(0, scrollOffset));
    }

    /// <summary>Reads remembered destination state without retaining controls.</summary>
    /// <param name="destination">Destination being reopened.</param>
    /// <returns>Saved state, or null focus and zero scroll when never recorded.</returns>
    internal OverlayFocusState Recall(OverlayDestination destination)
    {
        return _states.TryGetValue(destination, out var state)
            ? state
            : new OverlayFocusState(null, 0);
    }
}

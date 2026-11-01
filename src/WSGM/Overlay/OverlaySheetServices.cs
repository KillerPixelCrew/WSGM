using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The session services a controller's sheets show, and how each new sheet is connected to them: the
///     brightness, manual power mode, Windows energy plan, processor cores and power assignments, and which
///     Steam and content features the sheet offers.
/// </summary>
/// <remarks>
///     The services are borrowed. Each sheet gets its own selections over them, read when it opens and
///     disposed when it closes; a preview's selections read Windows but never write it.
/// </remarks>
internal sealed class OverlaySheetServices
{
    private readonly NativeQamHybridCoreService _hybridCores;
    private readonly DevicePowerAssignments? _powerAssignments;
    private readonly DevicePowerPresets? _powerPresets;
    private readonly NativeQamPowerProfileService _powerProfiles;
    private readonly bool _previewOnly;

    /// <summary>Takes the session's services, or a preview's own Windows power readers where it has none.</summary>
    /// <param name="sources">The session's services; a null one hides or empties its page.</param>
    /// <param name="power">The Windows power policy owners.</param>
    /// <param name="powerPresets">The device's power presets, or null without device integration.</param>
    /// <param name="powerAssignments">The session's AC and battery preset assignments, or null.</param>
    /// <param name="previewOnly">Whether the sheets are previews that never change the machine.</param>
    internal OverlaySheetServices(OverlaySources sources, WindowsPowerPolicy power,
        DevicePowerPresets? powerPresets, DevicePowerAssignments? powerAssignments, bool previewOnly)
    {
        Sources = sources;
        _powerProfiles = sources.PowerProfiles ?? new NativeQamPowerProfileService(power.Schemes);
        _hybridCores = sources.HybridCores ?? new NativeQamHybridCoreService(power.HybridCores);
        _powerPresets = powerPresets;
        _powerAssignments = powerAssignments;
        _previewOnly = previewOnly;
    }

    /// <summary>The session's services the sheet's pages attach.</summary>
    internal OverlaySources Sources { get; }

    /// <summary>
    ///     Hides each CEF feature's sidebar button when that feature is off, so
    ///     a disabled integration has no entry point. With CEF off entirely the
    ///     launch-wrapper buttons fall back to copying the command instead of
    ///     disappearing, which keeps them useful.
    /// </summary>
    /// <param name="vm">The panel's view model.</param>
    /// <param name="config">The configuration to read the gates from.</param>
    internal void ApplyVisibility(OverlayViewModel vm, AppConfig config)
    {
        vm.ShowGameLibrary = Sources.GameLibrary is not null;
        vm.ShowEmulators = Sources.Emulators is not null;
        vm.ShowArtwork = config.Cef.Enabled && Sources.Artwork is not null;
        // The install folder and shortcut target come from Steam's frontend on every scan.
        vm.ShowGameTools = config.Cef.Enabled && Sources.GameTools is not null;
        vm.ShowThemes = config.Cef.Enabled && Sources.Themes is not null;
        vm.ShowAnimations = config.Cef.Enabled && Sources.Animations is not null;
        vm.ShowSounds = Sources.Sounds is not null;
        vm.ShowLibraryTabs = config.Cef is { Enabled: true, LibraryTabs: true };
        vm.ShowCardManager = config.Cef is { Enabled: true, CardManager: true };
        vm.ShowSdCard = config.Cef is { Enabled: true, SdFormat: true };
        vm.ConfigureLaunchOptionsLive = config.Cef.Enabled;
    }

    /// <summary>Connects a new sheet to the brightness, manual power mode and Windows power selections.</summary>
    /// <param name="overlay">The sheet.</param>
    /// <returns>The sheet's energy plan selection, whose changes the idle timeouts follow.</returns>
    internal ChoiceRowSelection AttachPower(OverlayWindow overlay)
    {
        if (Sources.Brightness is { } brightness)
        {
            overlay.AttachBrightness(brightness,
                _previewOnly ? DisplayModeAccess.Unavailable : Sources.DisplayModes ?? DisplayModeAccess.Unavailable);
        }

        if (Sources.ManualTdp is { } manual)
        {
            overlay.AttachManualTdp(manual);
        }

        var powerSchemes = new ChoiceRowSelection("system.power-profile.choice", "Windows power profile",
            _powerProfiles.ReadAsync, _powerProfiles.SetPowerProfileAsync,
            _previewOnly || Sources.PowerProfiles is null);
        overlay.AttachPowerSchemes(powerSchemes);
        // Read on every open rather than cached for the session: activating a power scheme can
        // carry a different core preference with it, so a value read once would go stale silently.
        var hybridCores = new ChoiceRowSelection("system.hybrid-cores.choice", "Processor core preference",
            _hybridCores.ReadAsync, _hybridCores.SetHybridCoresAsync, _previewOnly);
        overlay.AttachHybridCores(hybridCores);
        if (_powerPresets is not null)
        {
            var presets = new DevicePowerPresetSelection(_powerPresets, _previewOnly, _powerAssignments);
            overlay.AttachPowerPresets(presets);
        }

        return powerSchemes;
    }
}

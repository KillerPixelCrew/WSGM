using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibHandheld.Contracts;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Install;
using WSGM.Shell;

namespace WSGM.Settings;

/// <summary>UI-thread editing model for WSGM configuration with explicit machine-service dependencies.</summary>
/// <remarks>
///     Bound state stays on the Avalonia dispatcher. Saves capture a detached request, merge owned edits
///     onto fresh configuration on a worker, then acknowledge only that request's baseline. The model
///     does not own session services; closing the window must end its imports and update work.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly SettingsServices _services;
    private readonly ConfigStore? _store;

    /// <summary>Builds the view model over an already loaded configuration and explicit services.</summary>
    /// <param name="config">
    ///     The configuration this view model edits. It is taken over, not copied: the save path loads
    ///     fresh configuration and merges before persisting anyway.
    /// </param>
    /// <param name="definition">Cached exact handheld metadata; null leaves device-profile authoring unavailable.</param>
    /// <param name="services">Every machine read and write the window uses; a test supplies inert ones.</param>
    /// <param name="store">The persistence owner, for the log folder and the update check; null in tests.</param>
    internal SettingsViewModel(
        AppConfig config,
        SettingsServices services,
        HandheldDefinition? definition = null,
        ConfigStore? store = null)
    {
        _store = store;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        InstalledPackages.CollectionChanged += (_, _) => Raise(nameof(HasInstalledPackages));
        AvailablePackages.CollectionChanged += (_, _) => Raise(nameof(HasAvailablePackages));
        UnavailablePackages.CollectionChanged += (_, _) => Raise(nameof(HasUnavailablePackages));
        SaveCommand = new AsyncRelayCommand(SaveWithStatusAsync);
        OpenLogLocationCommand = new RelayCommand(OpenLogLocation);
        TakeOverSteamAutostartCommand = new AsyncRelayCommand(TakeOverSteamAutostartAsync);
        TakeOverOtherManagersCommand = new AsyncRelayCommand(TakeOverOtherManagersAsync);
        GameLayout = new DisplayLayoutEditor(RefreshLaunchSummary);
        DesktopLayout = new DisplayLayoutEditor(RefreshLaunchSummary);
        GameAudioProfile = new AudioProfileEditor(RefreshLaunchSummary, _services.ReadAudio);
        DesktopAudioProfile = new AudioProfileEditor(RefreshLaunchSummary, _services.ReadAudio);
        ActionLists =
        [
            new PluginActionListEditor("Entering Game Mode", RefreshLaunchSummary),
            new PluginActionListEditor("Leaving Game Mode", RefreshLaunchSummary),
            new PluginActionListEditor("Desktop startup", RefreshLaunchSummary),
            new PluginActionListEditor("Desktop wake", RefreshLaunchSummary)
        ];
        AddActionStepCommand = new RelayCommand<PluginActionListEditor>(list => list?.Add());
        RemoveActionStepCommand = new RelayCommand<PluginActionStepEditorRow>(RemoveActionStep);
        MoveActionStepUpCommand = new RelayCommand<PluginActionStepEditorRow>(row => MoveActionStep(row, -1));
        MoveActionStepDownCommand = new RelayCommand<PluginActionStepEditorRow>(row => MoveActionStep(row, +1));
        ForgetDisplayCommand = new RelayCommand<DisplayLayoutEditorRow>(ForgetDisplay);
        RefreshDisplaysCommand = new AsyncRelayCommand(RefreshDisplaysAsync);
        CopyCurrentLayoutCommand = new AsyncRelayCommand(CopyCurrentLayoutAsync);
        EditGameLayoutCommand = new RelayCommand(() => EditingDesktopLayout = false);
        EditDesktopLayoutCommand = new RelayCommand(() => EditingDesktopLayout = true);
        RemoveAppCommand = new RelayCommand<StartupAppRow>(row =>
        {
            if (row is not null)
            {
                StartupApps.Remove(row);
            }
        });
        MoveUpCommand = new RelayCommand<StartupAppRow>(row => MoveStartupApp(row, -1));
        MoveDownCommand = new RelayCommand<StartupAppRow>(row => MoveStartupApp(row, +1));
        MoveArtworkTabUpCommand = new RelayCommand<ArtworkTabRow>(row => MoveArtworkTab(row, -1));
        MoveArtworkTabDownCommand = new RelayCommand<ArtworkTabRow>(row => MoveArtworkTab(row, +1));

        // Normalize so an injected bare AppConfig gets the same non-null nested
        // sections (and clamped splash numbers) the load path guarantees.
        var normalized = AppConfigRules.Normalize(config);
        _config = normalized.Value;
        foreach (var diagnostic in normalized.Diagnostics)
        {
            Log.Warn(diagnostic);
        }

        SavedAccentColor = _config.AccentColor;
        RecordSharedBaseline(_config);
        LoadDeviceProfiles(definition);
        LoadGraphicsDrivers();

        SteamAutoRelaunch = _config.SteamAutoRelaunch;
        SteamLaunchUnelevated = _config.SteamLaunchUnelevated;
        StartupDelayMs = _config.StartupDelayMs;
        StaggerDelayMs = _config.StaggerDelayMs;
        BootSplashEnabled = _config.BootSplashEnabled;
        StartAtSignIn = _config.StartAtSignIn;
        StartModeIndex = (int)_config.StartMode;
        SteamAutostartTakeoverAccepted = _config.SteamAutostartTakeoverAccepted;
        // Described from what was recorded, never by scanning here: reading the task scheduler is
        // slow enough that it does not belong on the path that opens this window.
        SteamAutostartStatusText = !_config.SteamAutostartTakeoverAccepted
            ? "Windows may start Steam before WSGM does, which costs Steam Input its reach over elevated windows. Check and take over."
            : _config.SteamAutostartDisabled.Count == 0
                ? "WSGM starts Steam. No Windows startup entry for Steam was turned off."
                : $"WSGM starts Steam. {_config.SteamAutostartDisabled.Count} Windows startup entry/entries are turned off and are restored when WSGM is uninstalled.";
        OtherManagersTakeoverAccepted = _config.OtherManagersTakeoverAccepted;
        OtherManagersStatusText = DescribeOtherManagers(_config);
        SteamInputLeaseEnabled = _config.SteamInputLeaseEnabled;
        SteamInputManagementEnabled = _config.SteamInputManagementEnabled;
        ArtworkSteamGridDbApiKey = _config.Artwork.SteamGridDbApiKey;
        ArtworkScreenscraperEnabled = _config.Artwork.ScreenscraperEnabled;
        ArtworkScreenscraperUser = _config.Artwork.ScreenscraperUser;
        ArtworkScreenscraperPassword = _config.Artwork.ScreenscraperUserPassword;
        GameLibraryDefaultModeIndex = _config.GameLibrary.DefaultMode is ImportMode.ControllerOnly ? 1 : 0;
        GameLibraryImportUnroutable = _config.GameLibrary.ImportUnroutable;
        GameLibraryArtworkPreferenceIndex =
            _config.GameLibrary.ArtworkPreference is ArtworkPreference.Providers ? 1 : 0;
        LoadArtworkTabs();
        DeviceIntegrationEnabled = _config.DeviceIntegration.Enabled;
        DeviceControllerManagementEnabled = _config.DeviceIntegration.ControllerManagementEnabled;
        DeviceKeepGuideChordEdits = _config.DeviceIntegration.KeepGuideChordEdits;
        DeviceControllerTargetIndex =
            (int)(_config.Profiles.Global.ControllerTarget ?? ProfileFields.DefaultControllerTarget);
        DeviceAutoTdpEnabled = _config.DeviceIntegration.AutoTdpEnabled;
        DeviceGlyphSelectionIndex = (int)_config.DeviceIntegration.GlyphSelection;
        PerformanceEnabled = _config.Performance.Enabled;
        FrameLimitStrategyIndex = (int)_config.Performance.FrameLimitStrategy;
        OsdCustomOrder = _config.Performance.OsdCustomOrder;
        OsdCustomTimeIndex = Math.Clamp(_config.Performance.OsdCustomTime, 0, 2);
        OsdCustomFpsIndex = Math.Clamp(_config.Performance.OsdCustomFps, 0, 2);
        OsdCustomCpuIndex = Math.Clamp(_config.Performance.OsdCustomCpu, 0, 2);
        OsdCustomRamIndex = Math.Clamp(_config.Performance.OsdCustomRam, 0, 2);
        OsdCustomGpuIndex = Math.Clamp(_config.Performance.OsdCustomGpu, 0, 2);
        OsdCustomVramIndex = Math.Clamp(_config.Performance.OsdCustomVram, 0, 2);
        OsdCustomBatteryIndex = Math.Clamp(_config.Performance.OsdCustomBattery, 0, 2);
        CefEnabled = _config.Cef.Enabled;
        CefLibraryTabs = _config.Cef.LibraryTabs;
        CefCardManager = _config.Cef.CardManager;
        CefSdFormat = _config.Cef.SdFormat;
        SteamStorageFormat = _config.SteamStorageFormatEnabled;
        CefWifiIndicator = _config.Cef.WifiIndicator;
        CefNativeQuickAccess = _config.Cef.NativeQuickAccess;
        CefDownloadKeepAwake = _config.Cef.DownloadKeepAwake;
        CefDownloadQueueSort = _config.Cef.DownloadQueueSort;
        CefConnectedLibraryCarousel = _config.Cef.ConnectedLibraryCarousel;
        CefCarouselShowUninstalled = _config.Cef.CarouselShowUninstalled;
        MuteWhileDisplayOff = _config.MuteWhileDisplayOff;
        CheckForUpdates = _config.CheckForUpdates;
        LoadUpdateState();
        ResuspendUnexplainedWakes = _config.ResuspendUnexplainedWakes;
        var standby = _services.ReadStandby();
        ModernStandbyStatusText = standby.ArmedWakeSources.Count == 0
            ? standby.Summary
            : $"{standby.Summary} Allowed to wake it: {string.Join(", ", standby.ArmedWakeSources)}.";
        VerboseLogging = _config.LogVerbosity == LogVerbosity.Verbose;
        AutoTdpTrace = _config.AutoTdpTraceEnabled;
        _hotkey = _config.Hotkey;
        _chord = _config.GamepadChord;
        GestureBottom = _config.Gestures.BottomEdge;
        GestureTop = _config.Gestures.TopEdge;
        GestureLeftSteamMenu = _config.Gestures.LeftEdgeSteamMenu;
        GestureRightSteamQuickAccess = _config.Gestures.RightEdgeSteamQuickAccess;
        GlyphStyleIndex = (int)_config.GlyphStyle;
        AccentColorHex = _config.AccentColor;
        OverlayBlurRadius = _config.OverlayBlurRadius;
        SteamCefPluginWarningAccepted = _config.SteamCefPluginWarningAccepted;
        LoadSplash(_config.Splash);

        foreach (var app in _config.StartupApps)
        {
            StartupApps.Add(new StartupAppRow
            {
                Path = app.Path,
                Args = app.Args,
                Enabled = app.Enabled,
                Elevated = app.Elevated,
                AutoRelaunch = app.AutoRelaunch
            });
        }

        LoadLaunchConfiguration(_config.GameModeLaunch);

        BuildStartupSuggestions();
    }

    /// <summary>Borrowed persistence owner; throws when the model was composed without one.</summary>
    internal ConfigStore Store =>
        _store ?? throw new InvalidOperationException("Configuration persistence was not supplied.");

    /// <summary>Whether configuration persistence was supplied; a test model has none.</summary>
    internal bool HasStore => _store is not null;

    /// <summary>
    ///     Gets or sets the transient status line shown in the window's
    ///     bottom strip: last-save time on success, otherwise the failure text.
    /// </summary>
    public string StatusText
    {
        get;
        set => SetField(ref field, value, nameof(StatusText));
    } = "";

    /// <summary>Builds the production settings model over configuration already read off the UI thread.</summary>
    /// <param name="read">
    ///     The configuration read this view model edits. A corrupt or unreadable file opens on the defaults
    ///     with the problem in the status strip, and a save then refuses through the store's strict write.
    /// </param>
    /// <param name="store">The persistence owner supplied by the process or resident session.</param>
    /// <param name="steamInputShim">The process's Steam Input shim, applied after a save and described on the Steam page.</param>
    /// <param name="readPluginActions">
    ///     The actions the running plugins declare: the resident session's own source, or an empty list for a
    ///     standalone Settings process, which then shows saved steps read-only.
    /// </param>
    /// <param name="definition">Exact handheld metadata captured by the caller before constructing the UI.</param>
    /// <returns>A UI-thread model with explicit production services and any configuration-read problem retained for display.</returns>
    internal static SettingsViewModel FromLoadedConfig(ConfigReadResult read, ConfigStore store,
        SteamInputShim steamInputShim, Func<IReadOnlyList<PluginActionOption>> readPluginActions,
        HandheldDefinition? definition = null)
    {
        var viewModel = new SettingsViewModel(read.Config ?? new AppConfig(),
            SettingsServices.Windows(store, steamInputShim, readPluginActions), definition, store);
        Log.Observe(viewModel.LoadPluginPackagesAsync(), "Settings plugin packages");
        viewModel.ShowConfigReadProblem(read);
        return viewModel;
    }

    /// <summary>Puts a failed configuration read in the status strip, so defaults are never shown as saved values.</summary>
    /// <param name="read">The read the window opened on.</param>
    internal void ShowConfigReadProblem(ConfigReadResult read)
    {
        var reason = read.Error?.Message is { Length: > 0 } message ? $" ({message})" : "";
        StatusText = read.Outcome switch
        {
            ConfigReadOutcome.Corrupt =>
                $"config.json is damaged{reason}, so these are the defaults. Saving is refused until the file is repaired or removed.",
            ConfigReadOutcome.Unreadable =>
                $"config.json could not be read{reason}, so these are the defaults. Saving is refused until it can be read; reopen Settings to try again.",
            _ => StatusText
        };
    }

    /// <summary>Required machine boundaries; synchronous native/file delegates must run on the caller's designated worker.</summary>
    /// <param name="CaptureDisplays">Reads the current Windows display arrangement.</param>
    /// <param name="ReadDisplayFacts">Reads supported facts for a stable target identity.</param>
    /// <param name="ReadPluginActions">Reads currently declared plugin actions.</param>
    /// <param name="DetectStartupApps">Enumerates launch suggestions without changing startup policy.</param>
    /// <param name="BeginImportSession">Acquires staged splash import lifetime.</param>
    /// <param name="EndImportSession">Releases one staged import lifetime.</param>
    /// <param name="Persist">Merges a detached save request on a worker and returns the committed result.</param>
    /// <param name="ReconcileSteamInputShim">Applies saved shim intent after the configuration transaction.</param>
    /// <param name="DescribeSteamInputShim">Describes current shim installation state.</param>
    /// <param name="Report">Reports operation failures without replacing product policy.</param>
    /// <param name="ReadStandby">Reads Modern Standby support.</param>
    /// <param name="ScanSteamAutostart">Lists startup owners without changing them.</param>
    /// <param name="ApplySteamAutostart">Applies explicitly accepted startup takeover to the supplied entries.</param>
    /// <param name="CheckUpdates">Checks for a release with cooperative cancellation.</param>
    /// <param name="DownloadUpdate">Downloads an explicitly selected release with progress and cancellation.</param>
    /// <param name="RunSetup">Starts an explicitly confirmed setup/update action.</param>
    /// <param name="ReadUpdateFailure">Reads the last recorded update failure.</param>
    /// <param name="ReadPackages">Reads and validates installed and bundled package metadata.</param>
    /// <param name="ActOnPackage">Performs the selected package install/remove action.</param>
    /// <param name="RepairAvailable">Reports whether setup repair is available.</param>
    /// <param name="StartRepair">Starts explicit setup repair.</param>
    /// <param name="ReadAudio">Reads endpoint capabilities, optionally for one selected playback endpoint.</param>
    /// <param name="ReadUpdates">Reads the latest saved update-check state.</param>
    /// <param name="DetectOtherManagers">Detects conflicting manager installations/startup owners.</param>
    /// <param name="ApplyOtherManagers">Applies the user's explicit takeover choice.</param>
    /// <param name="LoadPersisted">Strictly reloads persisted configuration for merge/check workflows.</param>
    internal sealed record SettingsServices(
        Func<DisplayArrangement> CaptureDisplays,
        Func<DisplayTargetIdentity, DisplayCatalogFacts?> ReadDisplayFacts,
        Func<IReadOnlyList<PluginActionOption>> ReadPluginActions,
        Func<IEnumerable<(string Label, string Path, bool Elevated)>> DetectStartupApps,
        Action BeginImportSession,
        Action EndImportSession,
        Func<SaveRequest, Task<SaveResult>> Persist,
        Action<AppConfig> ReconcileSteamInputShim,
        Func<string> DescribeSteamInputShim,
        Action<string, Exception?> Report,
        Func<ModernStandbyReport> ReadStandby,
        Func<IReadOnlyList<SteamAutostartSource>> ScanSteamAutostart,
        Func<IReadOnlyList<SteamAutostartSource>, SteamAutostartTakeoverResult> ApplySteamAutostart,
        Func<CancellationToken, Task<UpdateState>> CheckUpdates,
        Func<UpdateRelease, IProgress<double>, CancellationToken, Task<string>> DownloadUpdate,
        Action<string> RunSetup,
        Func<string?> ReadUpdateFailure,
        Func<PluginPackagePage> ReadPackages,
        Func<PluginPackageRowState, BundleManifest?, Task<string>> ActOnPackage,
        Func<bool> RepairAvailable,
        Action StartRepair,
        Func<string?, AudioDiscovery> ReadAudio,
        Func<UpdateState> ReadUpdates,
        Func<IReadOnlyList<DetectedManager>> DetectOtherManagers,
        Func<IReadOnlyList<DetectedManager>, OtherManagersResult> ApplyOtherManagers,
        Func<AppConfig> LoadPersisted)
    {
        /// <summary>Composes production delegates around the caller's persistence and shim owners.</summary>
        /// <param name="store">Borrowed configuration store.</param>
        /// <param name="steamInputShim">Borrowed process shim reconciler.</param>
        /// <param name="readPluginActions">Resident action catalog or an empty standalone catalog.</param>
        /// <returns>Explicit service dependencies; none are invoked to run setup or install packages during composition.</returns>
        internal static SettingsServices Windows(ConfigStore store, SteamInputShim steamInputShim,
            Func<IReadOnlyList<PluginActionOption>> readPluginActions)
        {
            return new SettingsServices(
                () => OperatingSystem.IsWindows()
                    ? DisplayLayouts.Observe()
                    : new DisplayArrangement([], "", DateTimeOffset.UtcNow),
                static target => OperatingSystem.IsWindows() ? DisplayCatalogFacts.Read(target) : null,
                readPluginActions,
                KnownStartupApps.Detected,
                SplashTheme.BeginImportSession, SplashTheme.EndImportSession,
                request => Task.Run(() => PersistSave(request, store)),
                // Follows persisted intent, on the post-save worker and outside the writer transaction,
                // whose timeout is sized for one small JSON write, not for file copies into Program Files.
                config => SteamInputManagement.Apply(steamInputShim, config, "settings-save"),
                () => SteamInputManagement.Describe(steamInputShim),
                (message, error) =>
                {
                    if (error is null)
                    {
                        Log.Info(message);
                    }
                    else
                    {
                        Log.Error(message, error);
                    }
                },
                // Windows' account of the last standby. Injected so a preview or a test renders a fixed
                // report instead of whatever this machine did last night.
                ModernStandbyDiagnostics.Read,
                () => SteamAutostartService.Scan(),
                sources => SteamAutostartService.Apply(store, sources, true),
                // The update check and download own their client and run off the dispatcher. The
                // window's token stops them when Settings closes.
                cancellationToken => Task.Run(async () =>
                {
                    using var http = UpdateChecker.CreateHttpClient();
                    return await UpdateChecker.CheckAsync(http, store.Context, cancellationToken)
                        .ConfigureAwait(false);
                }, cancellationToken),
                (release, progress, cancellationToken) => Task.Run(async () =>
                {
                    using var http = UpdateChecker.CreateHttpClient();
                    return await UpdateChecker.DownloadAsync(http, release, progress, cancellationToken)
                        .ConfigureAwait(false);
                }, cancellationToken),
                UpdateChecker.RunSetup,
                UpdateFailure.Read,
                ReadPluginPackagePage,
                ActOnPluginPackageAsync,
                static () => File.Exists(InstallLayout.SetupExe),
                StartSetupRepair,
                // Core Audio, off the dispatcher. A test supplies its own so it reads a fixture
                // rather than whatever this machine has plugged in.
                AudioDiscovery.Read,
                // The last update check, from the user's profile.
                () => UpdateChecker.ReadState(UpdateChecker.StatePath(store.Context)),
                () => OtherManagers.Detect(),
                detected => OtherManagers.Apply(store, detected, true),
                () => store.Read().RequireConfig());
        }
    }
}

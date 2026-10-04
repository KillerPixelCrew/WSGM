using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Settings;

/// <summary>Binds persisted shell, startup, input, and display settings to the Settings window.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly SettingsServices _services;
    private readonly ConfigStore? _store;

    // The process's shim whose deployment the Steam page describes. A model built without one
    // (design time, tests) describes a fresh instance, which has seen no Steam.
    private readonly SteamInputShim _steamInputShim;
    internal ConfigStore Store => _store ?? throw new InvalidOperationException("Configuration persistence was not supplied.");

    /// <summary>Creates design-time defaults without reading persisted user configuration.</summary>
    public SettingsViewModel()
        : this(new AppConfig(), null, false)
    {
    }

    internal SettingsViewModel(
        AppConfig config,
        string? installedPluginId,
        bool filterToInstalledPlugin,
        SettingsServices? services = null,
        ConfigStore? store = null,
        SteamInputShim? steamInputShim = null)
    {
        _store = store;
        _steamInputShim = steamInputShim ?? new SteamInputShim();
        _services = services ?? SettingsServices.Windows(store, _steamInputShim);
        _queryDisplaysOnWorker = services is null;
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
        RecordSharedBaseline(_config);
        LoadPluginSettings(_config, installedPluginId, filterToInstalledPlugin);

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

    /// <summary>
    ///     Gets or sets the transient status line shown in the window's
    ///     bottom strip: last-save time on success, otherwise the failure text.
    /// </summary>
    public string StatusText
    {
        get;
        set => SetField(ref field, value, nameof(StatusText));
    } = "";

    /// <summary>Builds the production settings model over configuration already loaded at startup.</summary>
    /// <param name="config">The configuration this view model edits.</param>
    /// <param name="store">The persistence owner supplied by the process or resident session.</param>
    /// <param name="steamInputShim">The process's Steam Input shim, applied after a save and described on the Steam page.</param>
    internal static SettingsViewModel FromLoadedConfig(AppConfig config, ConfigStore store, SteamInputShim steamInputShim)
    {
        var viewModel = new SettingsViewModel(config, ReadInstalledPluginId(), true, store: store,
            steamInputShim: steamInputShim);
        viewModel.LoadCommonPlugins(PluginPackageCatalog.DiscoverInstalled());
        return viewModel;
    }

    internal sealed record SettingsServices(
        Func<DisplayArrangement> CaptureDisplays,
        Func<DisplayTargetIdentity, DisplayCatalogFacts?> ReadDisplayFacts,
        Func<IReadOnlyList<PluginActionOption>> ReadPluginActions,
        Func<IEnumerable<(string Label, string Path, bool Elevated)>> DetectStartupApps,
        Action BeginImportSession,
        Action EndImportSession,
        Func<SaveRequest, Task<SaveResult>> Persist,
        Func<AppConfig, Task> ApplySteamInput,
        Action<string, Exception?> Report,
        Func<ModernStandbyReport> ReadStandby,
        Func<IReadOnlyList<SteamAutostartSource>> ScanSteamAutostart,
        Func<IReadOnlyList<SteamAutostartSource>, SteamAutostartTakeoverResult> ApplySteamAutostart,
        Func<string?, AudioDiscovery>? ReadAudio = null,
        Func<UpdateState>? ReadUpdates = null,
        Func<IReadOnlyList<DetectedManager>>? DetectOtherManagers = null,
        Func<IReadOnlyList<DetectedManager>, OtherManagersResult>? ApplyOtherManagers = null,
        Func<AppConfig>? LoadPersisted = null)
    {
        internal static SettingsServices Windows(ConfigStore? store, SteamInputShim steamInputShim)
        {
            return new SettingsServices(
                () => OperatingSystem.IsWindows()
                    ? DisplayLayouts.Observe()
                    : new DisplayArrangement([], "", DateTimeOffset.UtcNow),
                static target => OperatingSystem.IsWindows() ? ReadWindowsDisplayFacts(target) : null,
                SettingsPluginActions.Read,
                KnownStartupApps.Detected,
                SplashTheme.BeginImportSession, SplashTheme.EndImportSession,
                request => Task.Run(() => PersistSave(request, RequireStore(store))),
                config => Task.Run(() =>
                    ApplySteamInputManagementAfterSave(steamInputShim, config, RequireStore(store))),
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
                sources => SteamAutostartService.Apply(RequireStore(store), sources, true),
                // Core Audio, off the dispatcher. A test supplies its own so it reads a fixture
                // rather than whatever this machine has plugged in.
                AudioDiscovery.Read,
                // The last update check, from the user's profile; a test that omits it sees none.
                () => store is null ? new UpdateState() : UpdateChecker.ReadState(UpdateChecker.StatePath(store.Context)),
                () => OtherManagers.Detect(),
                detected => OtherManagers.Apply(RequireStore(store), detected, true));
        }

        private static ConfigStore RequireStore(ConfigStore? store)
        {
            return store ?? throw new InvalidOperationException("Persistence was not supplied to Windows settings services.");
        }

        /// <summary>
        ///     Asks one connected display what it advertises, so the answers can be remembered and
        ///     offered again after it is unplugged. Every query is optional: a display that refuses one
        ///     of them still contributes the rest.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static DisplayCatalogFacts ReadWindowsDisplayFacts(DisplayTargetIdentity target)
        {
            var modes = DisplayModes.Read(target)?.Supported ?? DisplayEdid.ReadModes(target);
            var hdr = DisplayColor.TryReadHdr(target, out _, out var supported) && supported;
            var maximum = DisplayScaling.TryReadRange(target, out _, out _, out var highest) ? highest : 0;
            return new DisplayCatalogFacts(modes, hdr, maximum);
        }
    }

    /// <summary>
    ///     Builds the view model over an ALREADY LOADED configuration instead of
    ///     reading <c>%LOCALAPPDATA%\WSGM\config.json</c>. Tests must use this overload: the
    ///     loaded production factory receives its persistence explicitly; the designer uses defaults.
    /// </summary>
    /// <param name="config">
    ///     The configuration this view model edits. It is taken over,
    ///     not copied — the save path re-loads and merges before persisting anyway.
    /// </param>
    // ReSharper disable IntroduceOptionalParameters.Global
    internal SettingsViewModel(AppConfig config)
        : this(config, null, false)
    {
    }

    /// <summary>Builds a testable settings model while selecting the named installed plugin.</summary>
    /// <param name="config">The configuration this view model edits.</param>
    /// <param name="installedPluginId">Installed package ID, or null when the slot is empty or invalid.</param>
    internal SettingsViewModel(AppConfig config, string? installedPluginId)
        : this(config, installedPluginId, true)
    {
    }
    // ReSharper restore IntroduceOptionalParameters.Global
}

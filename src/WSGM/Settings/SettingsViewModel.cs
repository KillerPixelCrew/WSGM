using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Input;
using WSGM.Install;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using WSGM.Themes;

namespace WSGM.Settings;

/// <summary>Binds persisted shell, startup, input, and display settings to the Settings window.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>What each artwork tab id is called, in the canonical order.</summary>
    private static readonly Dictionary<string, string> ArtworkTabTitles = new(StringComparer.Ordinal)
    {
        ["grid"] = "Capsule",
        ["wide"] = "Wide capsule",
        ["hero"] = "Hero",
        ["logo"] = "Logo",
        ["icon"] = "Icon",
        ["manage"] = "Manage"
    };

    /// <summary>
    ///     The fields WSGM's settings page in Steam can also write, each with how to read it and how to
    ///     take the saved value over.
    /// </summary>
    /// <remarks>
    ///     The window saves by writing its whole snapshot back, so without this a change made in Steam
    ///     while it was open would be reverted by its next save, as the overlay's AutoTDP switch once
    ///     was. Plugin instances and the device plugin's settings already merge only what was edited.
    /// </remarks>
    private static readonly SharedField[] SharedFields =
    [
        new("Cef.Enabled", config => config.Cef.Enabled, (to, from) => to.Cef.Enabled = from.Cef.Enabled),
        new("Cef.LibraryTabs", config => config.Cef.LibraryTabs,
            (to, from) => to.Cef.LibraryTabs = from.Cef.LibraryTabs),
        new("Cef.CardManager", config => config.Cef.CardManager,
            (to, from) => to.Cef.CardManager = from.Cef.CardManager),
        new("Cef.SdFormat", config => config.Cef.SdFormat, (to, from) => to.Cef.SdFormat = from.Cef.SdFormat),
        new("Cef.ConnectedLibraryCarousel", config => config.Cef.ConnectedLibraryCarousel,
            (to, from) => to.Cef.ConnectedLibraryCarousel = from.Cef.ConnectedLibraryCarousel),
        new("Cef.CarouselShowUninstalled", config => config.Cef.CarouselShowUninstalled,
            (to, from) => to.Cef.CarouselShowUninstalled = from.Cef.CarouselShowUninstalled),
        new("Cef.WifiIndicator", config => config.Cef.WifiIndicator,
            (to, from) => to.Cef.WifiIndicator = from.Cef.WifiIndicator),
        new("Cef.NativeQuickAccess", config => config.Cef.NativeQuickAccess,
            (to, from) => to.Cef.NativeQuickAccess = from.Cef.NativeQuickAccess),
        new("Cef.DownloadKeepAwake", config => config.Cef.DownloadKeepAwake,
            (to, from) => to.Cef.DownloadKeepAwake = from.Cef.DownloadKeepAwake),
        new("Cef.DownloadQueueSort", config => config.Cef.DownloadQueueSort,
            (to, from) => to.Cef.DownloadQueueSort = from.Cef.DownloadQueueSort),
        new("SteamStorageFormatEnabled", config => config.SteamStorageFormatEnabled,
            (to, from) => to.SteamStorageFormatEnabled = from.SteamStorageFormatEnabled),
        new("StartAtSignIn", config => config.StartAtSignIn, (to, from) => to.StartAtSignIn = from.StartAtSignIn),
        new("StartMode", config => config.StartMode, (to, from) => to.StartMode = from.StartMode),
        new("SteamInputLeaseEnabled", config => config.SteamInputLeaseEnabled,
            (to, from) => to.SteamInputLeaseEnabled = from.SteamInputLeaseEnabled),
        new("SteamInputManagementEnabled", config => config.SteamInputManagementEnabled,
            (to, from) => to.SteamInputManagementEnabled = from.SteamInputManagementEnabled)
    ];

    private readonly AppConfig _config;

    /// <summary>Edits made on the plugin page, applied at save. Empty until the user changes one.</summary>
    private readonly Dictionary<string, CapabilityValue> _pluginSettingEdits =
        new(StringComparer.Ordinal);

    private readonly SettingsServices _services;

    /// <summary>
    ///     The shared fields' values this window last loaded or saved: what "the user changed this
    ///     here" is measured against. Moved forward on each save, so a field saved once is not taken
    ///     as edited for the rest of the window's life.
    /// </summary>
    private readonly Dictionary<string, object> _sharedBaseline = new(StringComparer.Ordinal);

    private GamepadChordConfig _chord = new();
    private bool _chordRecording;
    private DisplayLayout? _desktopLayout;
    private List<PluginActionStep> _desktopStartupActions = [];
    private List<PluginActionStep> _desktopWakeActions = [];

    // Set by the property setters, cleared once after the constructor's own seeding, so they mean
    // "the user changed this here" rather than "this window has a value for it".
    private bool _deviceAutoTdpEdited;
    private bool _deviceControllerTargetEdited;
    private bool _deviceGlyphSelectionEdited;

    /// <summary>Whether the profile list was changed and should be written at save.</summary>
    /// <remarks>
    ///     Tracked rather than always written, for the same reason the plugin settings are: a save
    ///     triggered by an unrelated page must not overwrite what another process put there.
    /// </remarks>
    private bool _deviceProfilesEdited;

    private List<PluginActionStep> _enterActions = [];

    private DisplayLayout? _gameLayout;

    // --- Gestures / glyphs ---
    private int _glyphStyleIndex;

    // --- Overlay shortcuts (recorded, not picked from a list) ---
    private HotkeyConfig _hotkey = new();

    private bool _hotkeyRecording;
    private List<PluginActionStep> _leaveActions = [];

    private string _pluginSettingsDevice = string.Empty;
    private string _pluginSettingsPlugin = string.Empty;
    private IReadOnlyList<DisplayTargetIdentity> _present = [];

    private DeviceProfileRowViewModel? _selectedDeviceProfile;

    private int _selectedSuggestionIndex;
    private List<(string Path, bool Elevated)> _startupSuggestionTargets = [];
    private DisplayTargetIdentity? _waitForDisplay;

    /// <summary>Loads the current configuration and discovers locally installed startup suggestions.</summary>
    public SettingsViewModel()
        : this(ConfigStore.Load(), ReadInstalledPluginId(), true)
    {
        LoadCommonPlugins(PluginPackageCatalog.DiscoverInstalled());
    }

    internal SettingsViewModel(
        AppConfig config,
        string? installedPluginId,
        bool filterToInstalledPlugin,
        SettingsServices? services = null)
    {
        _services = services ?? SettingsServices.Windows();
        _queryDisplaysOnWorker = services is null;
        InstalledPackages.CollectionChanged += (_, _) => Raise(nameof(HasInstalledPackages));
        AvailablePackages.CollectionChanged += (_, _) => Raise(nameof(HasAvailablePackages));
        UnavailablePackages.CollectionChanged += (_, _) => Raise(nameof(HasUnavailablePackages));
        SaveCommand = new AsyncRelayCommand(SaveWithStatusAsync);
        OpenLogLocationCommand = new RelayCommand(OpenLogLocation);
        TakeOverSteamAutostartCommand = new AsyncRelayCommand(TakeOverSteamAutostartAsync);
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
        RebindDisplayCommand = new RelayCommand<DisplayLayoutEditorRow>(RebindDisplay);
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
        _config = ConfigStore.Normalize(config);
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
        SteamInputLeaseEnabled = _config.SteamInputLeaseEnabled;
        SteamInputManagementEnabled = _config.SteamInputManagementEnabled;
        ArtworkSteamGridDbApiKey = _config.Artwork.SteamGridDbApiKey;
        ArtworkScreenscraperEnabled = _config.Artwork.ScreenscraperEnabled;
        ArtworkScreenscraperUser = _config.Artwork.ScreenscraperUser;
        ArtworkScreenscraperPassword = _config.Artwork.ScreenscraperUserPassword;
        GameLibraryDefaultModeIndex = _config.GameLibrary.DefaultMode is ImportMode.ControllerOnly ? 1 : 0;
        GameLibraryImportUnroutable = _config.GameLibrary.ImportUnroutable;
        LoadArtworkTabs();
        DeviceIntegrationEnabled = _config.DeviceIntegration.Enabled;
        DeviceControllerManagementEnabled = _config.DeviceIntegration.ControllerManagementEnabled;
        DeviceMotionOnDemand = _config.DeviceIntegration.MotionStream is MotionStreamMode.OnDemand;
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

        // Seeding the properties above set these; only what the user does from here counts as an
        // edit. Without the distinction, a save of any unrelated setting wrote this window's
        // startup snapshot of AutoTDP, the controller target and the glyph policy over whatever the
        // running shell had persisted while the window was open.
        _deviceAutoTdpEdited = false;
        _deviceControllerTargetEdited = false;
        _deviceGlyphSelectionEdited = false;
    }

    /// <summary>
    ///     Gets whether an asynchronous save is currently persisting its captured
    ///     settings snapshot. The window disables every editor for this interval so it
    ///     cannot acknowledge changes that were made after the snapshot was taken.
    /// </summary>
    public bool IsSaving
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(IsSaving));
    }

    /// <summary>Installed common integrations and configured instances, independent of Device integration.</summary>
    public ObservableCollection<CommonPluginInstanceRow> CommonPlugins { get; } = [];

    /// <summary>Metadata discovery failures; discovery never executes plugin code.</summary>
    public string CommonPluginDiscoveryError { get; private set; } = "";

    /// <summary>Package files in the Plugins folder.</summary>
    public ObservableCollection<PluginPackageRow> InstalledPackages { get; } = [];

    /// <summary>Plugins the installed release bundles that this machine can install.</summary>
    public ObservableCollection<PluginPackageRow> AvailablePackages { get; } = [];

    /// <summary>Bundled plugins for other hardware, and community plugins this release could not build.</summary>
    public ObservableCollection<PluginPackageRow> UnavailablePackages { get; } = [];

    /// <summary>Whether anything is installed.</summary>
    public bool HasInstalledPackages => InstalledPackages.Count > 0;

    /// <summary>Whether the release offers anything to install.</summary>
    public bool HasAvailablePackages => AvailablePackages.Count > 0;

    /// <summary>Whether the release bundles plugins this machine cannot use.</summary>
    public bool HasUnavailablePackages => UnavailablePackages.Count > 0;

    /// <summary>Whether the installed setup is present to run Repair.</summary>
    public bool CanRepair => File.Exists(InstallLayout.SetupExe);

    /// <summary>Runs the installed setup's repair, which installs what the plugins need.</summary>
    public RelayCommand RepairCommand => field ??= new RelayCommand(StartRepair);

    // --- Commands (bound by the Settings pages; bodies stay on the named methods) ---
    /// <summary>
    ///     Gets the command that merges and persists the edited settings,
    ///     reporting the outcome (including the last-save time) via <see cref="StatusText" />.
    /// </summary>
    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>Gets the command that reveals wsgm.log in Explorer.</summary>
    public RelayCommand OpenLogLocationCommand { get; }

    /// <summary>
    ///     Gets the command that re-checks Windows' Steam startup entries and turns off any
    ///     that came back. This configures how WSGM starts Steam, which is WSGM's own behavior; the
    ///     exception for touching an external setting is recorded in <c>docs\decisions.md</c>.
    /// </summary>
    public AsyncRelayCommand TakeOverSteamAutostartCommand { get; }

    /// <summary>Gets the command that removes a display from the remembered catalog.</summary>
    public RelayCommand<DisplayLayoutEditorRow> ForgetDisplayCommand { get; }

    /// <summary>Gets the command that points a migrated row at a connected display.</summary>
    public RelayCommand<DisplayLayoutEditorRow> RebindDisplayCommand { get; }

    /// <summary>Gets the command that appends the selected action to one list.</summary>
    public RelayCommand<PluginActionListEditor> AddActionStepCommand { get; }

    /// <summary>Gets the command that removes one action step.</summary>
    public RelayCommand<PluginActionStepEditorRow> RemoveActionStepCommand { get; }

    /// <summary>Gets the command that runs one step earlier.</summary>
    public RelayCommand<PluginActionStepEditorRow> MoveActionStepUpCommand { get; }

    /// <summary>Gets the command that runs one step later.</summary>
    public RelayCommand<PluginActionStepEditorRow> MoveActionStepDownCommand { get; }

    /// <summary>
    ///     The connected display a rebind will use, chosen from
    ///     <see cref="RebindChoices" />.
    /// </summary>
    public int RebindChoiceIndex
    {
        get;
        set => SetField(ref field, value, nameof(RebindChoiceIndex));
    }

    /// <summary>The displays a migrated row can be pointed at: the ones seen most recently.</summary>
    public ObservableCollection<string> RebindChoices { get; } = [];

    /// <summary>Gets the command that removes one startup-program row.</summary>
    public RelayCommand<StartupAppRow> RemoveAppCommand { get; }

    /// <summary>Gets the command that moves one startup-program row up.</summary>
    public RelayCommand<StartupAppRow> MoveUpCommand { get; }

    /// <summary>Gets the command that moves one startup-program row down.</summary>
    public RelayCommand<StartupAppRow> MoveDownCommand { get; }

    /// <summary>Gets the command that moves an artwork tab earlier in the strip.</summary>
    public RelayCommand<ArtworkTabRow> MoveArtworkTabUpCommand { get; }

    /// <summary>Gets the command that moves an artwork tab later in the strip.</summary>
    public RelayCommand<ArtworkTabRow> MoveArtworkTabDownCommand { get; }

    /// <summary>Edits the Game Mode layout.</summary>
    public DisplayLayoutEditor GameLayout { get; }

    /// <summary>Edits the Desktop layout leaving Game Mode restores.</summary>
    public DisplayLayoutEditor DesktopLayout { get; }

    /// <summary>Edits saved audio preferences applied when entering Game Mode.</summary>
    public AudioProfileEditor GameAudioProfile { get; }

    /// <summary>Edits saved audio preferences applied when returning to Desktop.</summary>
    public AudioProfileEditor DesktopAudioProfile { get; }

    /// <summary>The four action lists, in the order the page shows them.</summary>
    public IReadOnlyList<PluginActionListEditor> ActionLists { get; }

    /// <summary>Displays that can be chosen in the layout editor, present or remembered.</summary>
    public ObservableCollection<KnownDisplay> KnownDisplays { get; } = [];

    /// <summary>Sections the installed plugin declares, in render order.</summary>
    public ObservableCollection<PluginSettingSectionViewModel> PluginSettingSections { get; } = [];

    /// <summary>Whether the plugin settings page has anything to draw.</summary>
    public bool PluginSettingsAvailable => PluginSettingSections.Count > 0;

    /// <summary>Authored fan and lighting profiles for the installed device.</summary>
    public ObservableCollection<DeviceProfileRowViewModel> DeviceProfiles { get; } = [];

    /// <summary>Gets or sets the profile the curve editor is showing.</summary>
    public DeviceProfileRowViewModel? SelectedDeviceProfile
    {
        get => _selectedDeviceProfile;
        set
        {
            if (ReferenceEquals(_selectedDeviceProfile, value))
            {
                return;
            }

            if (_selectedDeviceProfile is not null)
            {
                _selectedDeviceProfile.PropertyChanged -= OnSelectedDeviceProfileChanged;
            }

            _selectedDeviceProfile = value;
            if (_selectedDeviceProfile is not null)
            {
                _selectedDeviceProfile.PropertyChanged += OnSelectedDeviceProfileChanged;
            }

            Raise(nameof(SelectedDeviceProfile));
            Raise(nameof(HasSelectedDeviceProfile));
        }
    }

    /// <summary>Whether a profile is selected and the editor has something to draw.</summary>
    public bool HasSelectedDeviceProfile => _selectedDeviceProfile is not null;

    /// <summary>
    ///     Why the plugin settings page is empty.
    /// </summary>
    /// <remarks>
    ///     Shown instead of a blank page. A plugin that declares no settings and a machine with no
    ///     plugin at all look identical otherwise, and the user cannot tell whether something failed.
    /// </remarks>
    public string PluginSettingsEmptyReason
    {
        get;
        set => SetField(ref field, value, nameof(PluginSettingsEmptyReason));
    } = "No device plugin is installed, so there are no plugin settings to show.";

    /// <summary>Selected <see cref="GameModeLaunchKind" /> index.</summary>
    public int GameModeLaunchKindIndex
    {
        get;
        set
        {
            field = value;
            Raise(nameof(GameModeLaunchKindIndex));
            Raise(nameof(ShowCustomLaunch));
            Raise(nameof(ShowLayoutEditor));
            if (_launchLoaded && ShowCustomLaunch && GameLayout is { HasActiveDisplays: false, CanUndo: false })
            {
                SeedDisplayLayout(GameLayout, true);
            }

            if (_launchLoaded)
            {
                RefreshLaunchSummary();
            }
        }
    }

    /// <summary>Whether the layout and wait fields apply to the selected launch kind.</summary>
    public bool ShowCustomLaunch => GameModeLaunchKindIndex == (int)GameModeLaunchKind.Custom;

    /// <summary>Selected <see cref="GameModeReturn" /> index.</summary>
    public int GameModeReturnIndex
    {
        get;
        set
        {
            field = value;
            Raise(nameof(GameModeReturnIndex));
            Raise(nameof(ShowDesktopLayout));
            Raise(nameof(ShowLayoutEditor));
            if (_launchLoaded && ShowDesktopLayout && DesktopLayout is { HasActiveDisplays: false, CanUndo: false })
            {
                SeedDisplayLayout(DesktopLayout, false);
            }

            if (_launchLoaded)
            {
                RefreshLaunchSummary();
            }
        }
    }

    /// <summary>Whether a Desktop layout is configured rather than captured at entry.</summary>
    public bool ShowDesktopLayout => GameModeReturnIndex == (int)GameModeReturn.DesktopLayout;

    /// <summary>What the saved layouts describe, including anything needing confirmation.</summary>
    public string LaunchSummaryText
    {
        get;
        private set => SetField(ref field, value, nameof(LaunchSummaryText));
    } = "";

    /// <summary>Index into <see cref="WaitForDisplayChoices" />; zero means no wait.</summary>
    public int WaitForDisplayIndex
    {
        get;
        set => SetField(ref field, value, nameof(WaitForDisplayIndex));
    }

    /// <summary>"No display wait" followed by one entry per remembered display.</summary>
    public ObservableCollection<string> WaitForDisplayChoices { get; } = [];

    /// <summary>
    ///     Gets or sets the transient status line shown in the window's
    ///     bottom strip: last-save time on success, otherwise the failure text.
    /// </summary>
    public string StatusText
    {
        get;
        set => SetField(ref field, value, nameof(StatusText));
    } = "";

    /// <summary>
    ///     Gets the compact logon-service state for the status strip,
    ///     derived from the same flag the boot manifest is projected from.
    /// </summary>
    public string ServiceStateText => StartAtSignIn
        ? $"Sign-in start: {(StartModeIndex == (int)SessionStartMode.Desktop ? "Desktop" : "Game")}"
        : "Sign-in start: off";

#pragma warning disable CA1822
    /// <summary>Gets the compact shell state for the status strip.</summary>
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public string ShellStateText => "Shell: Explorer";
#pragma warning restore CA1822

    /// <summary>Gets what Windows would still start Steam from, refreshed on demand.</summary>
    public string SteamAutostartStatusText
    {
        get;
        private set => SetField(ref field, value, nameof(SteamAutostartStatusText));
    }

    // --- Startup app suggestions ---
    /// <summary>
    ///     Common handheld companions found on this PC, offered as one-click adds
    ///     instead of making the user hunt for exe paths.
    /// </summary>
    public List<string> StartupSuggestions { get; private set; } = [];

    /// <summary>Gets or sets the selected discovered startup-app suggestion.</summary>
    public int SelectedSuggestionIndex
    {
        get => _selectedSuggestionIndex;
        set => SetField(ref _selectedSuggestionIndex, value, nameof(SelectedSuggestionIndex));
    }

    // --- Sign-in behavior ---

    /// <summary>
    ///     Gets or sets whether the logon service starts WSGM at sign-in.
    ///     Persisted via Save; the boot manifest is rewritten there.
    /// </summary>
    public bool StartAtSignIn
    {
        get;
        set
        {
            field = value;
            Raise(nameof(StartAtSignIn));
            Raise(nameof(ServiceStateText));
            Raise(nameof(ShellStatusText));
        }
    }

    /// <summary>
    ///     Gets or sets whether WSGM may own how Steam starts, turning Windows' own Steam
    ///     startup entries off. Persisted via Save; the takeover itself runs after the save, outside
    ///     the config lock, because it may need an elevation prompt.
    /// </summary>
    public bool SteamAutostartTakeoverAccepted
    {
        get;
        set => SetField(ref field, value, nameof(SteamAutostartTakeoverAccepted));
    }

    /// <summary>
    ///     Gets or sets the session mode a start produces, as the selector's index:
    ///     0 = Desktop, 1 = Game. Independent of <see cref="StartAtSignIn" />.
    /// </summary>
    public int StartModeIndex
    {
        get;
        set
        {
            field = value;
            Raise(nameof(StartModeIndex));
            Raise(nameof(ServiceStateText));
            Raise(nameof(ShellStatusText));
        }
    }

    /// <summary>
    ///     Gets or sets whether WSGM leases the controller away from Steam
    ///     Input while its focused surfaces are open. Off = Steam is never touched.
    /// </summary>
    public bool SteamInputLeaseEnabled
    {
        get;
        set => SetField(ref field, value, nameof(SteamInputLeaseEnabled));
    }

    /// <summary>
    ///     Gets or sets whether WSGM deploys its Steam Input shim into Steam's
    ///     own install directory, so Steam loads it and WSGM never injects.
    /// </summary>
    public bool SteamInputManagementEnabled
    {
        get;
        set => SetField(ref field, value, nameof(SteamInputManagementEnabled));
    }

    /// <summary>Gets or sets the user's own SteamGridDB key. Empty leaves that source unsearched.</summary>
    public string ArtworkSteamGridDbApiKey
    {
        get;
        set => SetField(ref field, value, nameof(ArtworkSteamGridDbApiKey));
    } = "";

    /// <summary>Gets or sets whether Screenscraper.fr is searched alongside SteamGridDB.</summary>
    public bool ArtworkScreenscraperEnabled
    {
        get;
        set => SetField(ref field, value, nameof(ArtworkScreenscraperEnabled));
    }

    /// <summary>Gets or sets the optional Screenscraper account, which raises its own daily quota.</summary>
    public string ArtworkScreenscraperUser
    {
        get;
        set => SetField(ref field, value, nameof(ArtworkScreenscraperUser));
    } = "";

    /// <summary>Gets or sets the Screenscraper account's password.</summary>
    public string ArtworkScreenscraperPassword
    {
        get;
        set => SetField(ref field, value, nameof(ArtworkScreenscraperPassword));
    } = "";

    /// <summary>Gets the artwork page's tabs, in the order they are offered.</summary>
    /// <remarks>
    ///     The collection's own order is the stored tab order, so the move commands are the whole
    ///     reordering edit and nothing else has to be kept in step.
    /// </remarks>
    public ObservableCollection<ArtworkTabRow> ArtworkTabs { get; } = [];

    /// <summary>Gets the launch modes a new Game Library title can start on, in index order.</summary>
    public IReadOnlyList<string> GameLibraryModes { get; } = ["Steam overlay", "Controller only"];

    /// <summary>Gets or sets which mode a newly found single-player title starts on.</summary>
    public int GameLibraryDefaultModeIndex
    {
        get;
        set => SetField(ref field, value, nameof(GameLibraryDefaultModeIndex));
    }

    /// <summary>Gets or sets whether titles with no validated launch route are offered.</summary>
    public bool GameLibraryImportUnroutable
    {
        get;
        set => SetField(ref field, value, nameof(GameLibraryImportUnroutable));
    }

    /// <summary>Gets or sets which tab the artwork page opens on, as an index into the strip.</summary>
    public int ArtworkDefaultTabIndex
    {
        get;
        set => SetField(ref field, value, nameof(ArtworkDefaultTabIndex));
    }

    /// <summary>Gets or sets the optional production Device Integration master switch.</summary>
    public bool DeviceIntegrationEnabled
    {
        get;
        set
        {
            field = value;
            Raise(nameof(DeviceIntegrationEnabled));
        }
    }

    /// <summary>Gets or sets the remembered controller-management child preference.</summary>
    public bool DeviceControllerManagementEnabled
    {
        get;
        set
        {
            field = value;
            Raise(nameof(DeviceControllerManagementEnabled));
        }
    }

    /// <summary>Gets or sets whether the plugin's motion stream stops while no application runs.</summary>
    /// <remarks>
    ///     Maps <see cref="MotionStreamMode.OnDemand" /> onto a switch. Only this window edits the mode,
    ///     so it is written on every save like <see cref="DeviceControllerManagementEnabled" />.
    /// </remarks>
    public bool DeviceMotionOnDemand
    {
        get;
        set
        {
            field = value;
            Raise(nameof(DeviceMotionOnDemand));
        }
    }

    /// <summary>Gets or sets whether guide button chord edits are kept for a Steam Deck target.</summary>
    /// <remarks>Only this window edits it, so it is written on every save.</remarks>
    public bool DeviceKeepGuideChordEdits
    {
        get;
        set
        {
            field = value;
            Raise(nameof(DeviceKeepGuideChordEdits));
        }
    }

    /// <summary>Gets or sets whether AutoTDP controls the primary power limit.</summary>
    /// <remarks>
    ///     One of the three device settings the running shell also owns: the overlay and the native
    ///     quick-access menu persist all of them while this window is open. Each records whether it was
    ///     edited here, because a save merges over a fresh load and an untouched snapshot would
    ///     otherwise revert whatever the running session had changed. See <see cref="DeviceEditsMade" />.
    /// </remarks>
    public bool DeviceAutoTdpEnabled
    {
        get;
        set
        {
            field = value;
            _deviceAutoTdpEdited = true;
            Raise(nameof(DeviceAutoTdpEnabled));
        }
    }

    /// <summary>Selected global managed-controller target index.</summary>
    public int DeviceControllerTargetIndex
    {
        get;
        set
        {
            field = value;
            _deviceControllerTargetEdited = true;
            Raise(nameof(DeviceControllerTargetIndex));
        }
    }

    /// <summary>Selected physical glyph-policy index.</summary>
    public int DeviceGlyphSelectionIndex
    {
        get;
        set
        {
            field = value;
            _deviceGlyphSelectionEdited = true;
            Raise(nameof(DeviceGlyphSelectionIndex));
        }
    }

    /// <summary>Which runtime-owned device settings this window actually edited.</summary>
    /// <remarks>
    ///     Exposed for tests: the merge behaviour it drives is the whole point of the flags, and it
    ///     cannot be observed from the saved configuration without a real config file.
    /// </remarks>
    internal (bool AutoTdp, bool ControllerTarget, bool GlyphSelection) DeviceEditsMade =>
        (_deviceAutoTdpEdited, _deviceControllerTargetEdited, _deviceGlyphSelectionEdited);

    /// <summary>Read-only status reported by the authoritative shell coordinator.</summary>
    public string DeviceOwnerStatusText
    {
        get;
        private set => SetField(ref field, value, nameof(DeviceOwnerStatusText));
    } = "No running device coordinator detected.";

#pragma warning disable CA1822
    /// <summary>
    ///     Gets a plain-language description of the shim deployment, naming the
    ///     file so a pasted screenshot is diagnostic on its own.
    /// </summary>
    public string SteamInputShimStatusText => SteamInputManagement.Describe(SteamInputShim.LastStatus);
#pragma warning restore CA1822

    /// <summary>Gets or sets the shared RTSS performance integration master switch.</summary>
    public bool PerformanceEnabled
    {
        get;
        set => SetField(ref field, value, nameof(PerformanceEnabled));
    }

    /// <summary>The three detail options shared by every Custom-overlay widget selector.</summary>
    public List<string> OsdCustomLevels { get; } = ["Hidden", "Minimal", "Full"];

    /// <summary>Custom overlay (level 4) widget order, comma-separated widget names.</summary>
    public string OsdCustomOrder
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomOrder));
    }

    /// <summary>Clock detail for the Custom overlay.</summary>
    public int OsdCustomTimeIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomTimeIndex));
    }

    /// <summary>Framerate detail for the Custom overlay.</summary>
    public int OsdCustomFpsIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomFpsIndex));
    }

    /// <summary>CPU detail for the Custom overlay.</summary>
    public int OsdCustomCpuIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomCpuIndex));
    }

    /// <summary>Memory detail for the Custom overlay.</summary>
    public int OsdCustomRamIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomRamIndex));
    }

    /// <summary>GPU detail for the Custom overlay.</summary>
    public int OsdCustomGpuIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomGpuIndex));
    } = 2;

    /// <summary>Video-memory detail for the Custom overlay.</summary>
    public int OsdCustomVramIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomVramIndex));
    } = 2;

    /// <summary>Battery detail for the Custom overlay.</summary>
    public int OsdCustomBatteryIndex
    {
        get;
        set => SetField(ref field, value, nameof(OsdCustomBatteryIndex));
    } = 2;

    /// <summary>Gets or sets how a frame cap is paired with the panel's refresh rate.</summary>
    /// <remarks>
    ///     Index into <see cref="FrameLimitStrategy" />, in declaration order, so the combo box needs no
    ///     converter. It decides both what the cap does to the display and which caps are offered at
    ///     all: uncoupled offers a free range, and the two coupled strategies offer only caps that
    ///     divide a real mode exactly.
    /// </remarks>
    public int FrameLimitStrategyIndex
    {
        get;
        set => SetField(ref field, value, nameof(FrameLimitStrategyIndex));
    }

    /// <summary>
    ///     Gets or sets the master Steam CEF integration switch. Off closes the
    ///     debug port, injects nothing, and hides the sub-toggles below and the overlay
    ///     feature buttons.
    /// </summary>
    public bool CefEnabled
    {
        get;
        set => SetField(ref field, value, nameof(CefEnabled));
    } = true;

    /// <summary>Gets or sets the injected library filter tabs, tab order, and native-tab hiding.</summary>
    public bool CefLibraryTabs
    {
        get;
        set => SetField(ref field, value, nameof(CefLibraryTabs));
    } = true;

    /// <summary>Gets or sets the SD-card library manager (card tabs, badges, live labels).</summary>
    public bool CefCardManager
    {
        get;
        set => SetField(ref field, value, nameof(CefCardManager));
    } = true;

    /// <summary>Gets or sets Format SD Card + live library registration.</summary>
    public bool CefSdFormat
    {
        get;
        set => SetField(ref field, value, nameof(CefSdFormat));
    } = true;

    /// <summary>Gets or sets whether Steam's own storage pages may erase a drive through WSGM.</summary>
    /// <remarks>
    ///     An opt-out, separate from <see cref="CefSdFormat" />: that one is WSGM's own guided flow,
    ///     this one lets Steam's Format Drive modal start the same erase. The refusal Steam shows when
    ///     this is off is a generic result code, so the switch has to be where the user can find it —
    ///     which it was not, for a day.
    /// </remarks>
    public bool SteamStorageFormat
    {
        get;
        set => SetField(ref field, value, nameof(SteamStorageFormat));
    }

    /// <summary>Gets or sets the Big Picture Wi-Fi indicator.</summary>
    public bool CefWifiIndicator
    {
        get;
        set => SetField(ref field, value, nameof(CefWifiIndicator));
    } = true;

    /// <summary>Gets or sets the fingerprint-gated native Steam Quick Access bootstrap.</summary>
    public bool CefNativeQuickAccess
    {
        get;
        set => SetField(ref field, value, nameof(CefNativeQuickAccess));
    } = true;

    /// <summary>
    ///     Gets or sets the automatic download wake lock (keep the device awake
    ///     while Steam reports an active download).
    /// </summary>
    public bool CefDownloadKeepAwake
    {
        get;
        set => SetField(ref field, value, nameof(CefDownloadKeepAwake));
    } = true;

    /// <summary>
    ///     Gets or sets the Name/Size/Type sort buttons injected into Big
    ///     Picture's download-queue header.
    /// </summary>
    public bool CefDownloadQueueSort
    {
        get;
        set => SetField(ref field, value, nameof(CefDownloadQueueSort));
    } = true;

    /// <summary>
    ///     Gets or sets whether Big Picture Home's carousel lists the games on the
    ///     libraries attached right now.
    /// </summary>
    public bool CefConnectedLibraryCarousel
    {
        get;
        set => SetField(ref field, value, nameof(CefConnectedLibraryCarousel));
    } = true;

    /// <summary>
    ///     Gets or sets whether that carousel also lists owned games that are not
    ///     installed, greyed.
    /// </summary>
    public bool CefCarouselShowUninstalled
    {
        get;
        set => SetField(ref field, value, nameof(CefCarouselShowUninstalled));
    }

    /// <summary>Gets or sets muting system audio while the screen is off.</summary>
    public bool MuteWhileDisplayOff
    {
        get;
        set => SetField(ref field, value, nameof(MuteWhileDisplayOff));
    }

    /// <summary>Gets or sets suspending again after a standby wake nothing accounts for.</summary>
    public bool ResuspendUnexplainedWakes
    {
        get;
        set => SetField(ref field, value, nameof(ResuspendUnexplainedWakes));
    }

    /// <summary>Gets Windows' own account of the last standby, for the settings surface.</summary>
    /// <remarks>
    ///     Read once when the page loads rather than polled: it describes the last resume, and nothing
    ///     about it changes while the settings window is open. Windows exposes no documented call for
    ///     what woke the machine, so this never names a cause.
    /// </remarks>
    public string ModernStandbyStatusText
    {
        get;
        private set => SetField(ref field, value, nameof(ModernStandbyStatusText));
    } = "";

    /// <summary>Gets or sets whether the log records debug detail.</summary>
    public bool VerboseLogging
    {
        get;
        set => SetField(ref field, value, nameof(VerboseLogging));
    }

    /// <summary>Gets or sets whether AutoTDP writes a CSV trace of its decisions.</summary>
    public bool AutoTdpTrace
    {
        get;
        set => SetField(ref field, value, nameof(AutoTdpTrace));
    }

    /// <summary>Gets a user-facing explanation of the current sign-in behavior.</summary>
    public string ShellStatusText => !StartAtSignIn
        ? "WSGM does not start at sign-in. Start it from the Start Menu; Explorer stays your Windows shell."
        : StartModeIndex == (int)SessionStartMode.Desktop
            ? "WSGM starts at sign-in and stays in desktop mode until you enter game mode. Explorer stays your Windows shell."
            : "Game mode starts at sign-in through the WSGM logon service. Explorer stays your Windows shell.";

    // --- UAC prompt level ---
#pragma warning disable CA1822
    /// <summary>Gets whether UAC consent prompts are disabled for the machine.</summary>
    public bool UacPromptsDisabled => UacSettings.Read().PromptsDisabled;
#pragma warning restore CA1822

    /// <summary>Gets a user-facing explanation of the current UAC prompt policy.</summary>
    public string UacStatusText => UacPromptsDisabled
        ? "UAC prompts are OFF — elevated apps start silently. Windows still runs with UAC enabled, but anything that asks for administrator rights gets them without asking you."
        : "UAC prompts are ON (Windows default). Each elevated launch shows a consent dialog, which interrupts boot-to-game on a handheld.";

    // --- Lock on wake ---
#pragma warning disable CA1822
    /// <summary>Gets whether Windows will skip a sign-in prompt after display sleep.</summary>
    public bool LockOnWakeDisabled => LockScreenSettings.SignInOnWakeDisabled();
#pragma warning restore CA1822

    /// <summary>Gets a user-facing explanation of the wake sign-in policy.</summary>
    public string LockOnWakeStatusText => LockOnWakeDisabled
        ? "Waking the device goes straight back to your game — no sign-in screen."
        : "Windows currently asks you to sign in again after the screen sleeps (Windows default).";

    // --- Steam (the only launcher; located via registry, nothing to configure) ---
#pragma warning disable CA1822
    /// <summary>Gets Steam discovery status because game mode requires Steam.</summary>
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public string SteamStatusText => Steam.ExePath is { } exe
        ? $"Detected: {exe}"
        : "Steam was not found on this PC. Install Steam first — WSGM is Steam-exclusive.";
#pragma warning restore CA1822

    /// <summary>Gets or sets whether the Steam monitor restarts Steam after an unexpected exit.</summary>
    public bool SteamAutoRelaunch
    {
        get;
        set => SetField(ref field, value, nameof(SteamAutoRelaunch));
    }

    /// <summary>Whether the complete Steam client starts at medium integrity.</summary>
    public bool SteamLaunchUnelevated
    {
        get;
        set => SetField(ref field, value, nameof(SteamLaunchUnelevated));
    }

    // --- Startup apps ---
    /// <summary>Gets the ordered startup programs shown in the settings editor.</summary>
    public ObservableCollection<StartupAppRow> StartupApps { get; } = [];

    /// <summary>Gets or sets the initial delay before launching configured startup programs.</summary>
    public int StartupDelayMs
    {
        get;
        set => SetField(ref field, value, nameof(StartupDelayMs));
    }

    /// <summary>Gets or sets the delay between successive configured startup programs.</summary>
    public int StaggerDelayMs
    {
        get;
        set => SetField(ref field, value, nameof(StaggerDelayMs));
    }

    /// <summary>Gets or sets whether a splash window is shown while game mode starts.</summary>
    public bool BootSplashEnabled
    {
        get;
        set => SetField(ref field, value, nameof(BootSplashEnabled));
    }

    /// <summary>Gets the current keyboard shortcut or the key-recording prompt.</summary>
    public string HotkeyText => _hotkeyRecording ? "Press keys…" : KeyRecorder.Describe(_hotkey);

    /// <summary>Gets the current controller chord or the button-recording prompt.</summary>
    public string ChordText => _chordRecording
        ? "Press buttons…"
        : _chord.Enabled && _chord.Buttons != 0
            ? GamepadService.Describe((GamepadButtons)_chord.Buttons, _chord.Hold)
            : "None";

    /// <summary>Gets or sets whether a bottom-edge swipe opens quick access on its Open apps strip (game mode).</summary>
    public bool GestureBottom
    {
        get;
        set => SetField(ref field, value, nameof(GestureBottom));
    }

    /// <summary>Gets or sets whether a top-edge swipe opens quick access.</summary>
    public bool GestureTop
    {
        get;
        set => SetField(ref field, value, nameof(GestureTop));
    }

    /// <summary>Gets or sets whether a left-edge swipe opens Steam's Big Picture menu.</summary>
    public bool GestureLeftSteamMenu
    {
        get;
        set => SetField(ref field, value, nameof(GestureLeftSteamMenu));
    }

    /// <summary>Gets or sets whether a right-edge swipe opens Steam's Big Picture quick-access menu.</summary>
    public bool GestureRightSteamQuickAccess
    {
        get;
        set => SetField(ref field, value, nameof(GestureRightSteamQuickAccess));
    }

    /// <summary>Gets or sets the selected controller-glyph family index.</summary>
    public int GlyphStyleIndex
    {
        get => _glyphStyleIndex;
        set
        {
            _glyphStyleIndex = value;
            Raise(nameof(GlyphStyleIndex));
            Raise(nameof(GlyphStyle));
        }
    }

    /// <summary>
    ///     Gets the selected glyph family as its enum value — what the
    ///     status strip's A/B glyph icons bind to.
    /// </summary>
    public GlyphStyle GlyphStyle => (GlyphStyle)Math.Clamp(_glyphStyleIndex, 0, 2);

    /// <summary>Gets the controller-glyph family names presented by the settings selector.</summary>
    public List<string> GlyphStyles { get; } = ["Xbox", "PlayStation", "Nintendo"];

    /// <summary>Gaussian blur of the Overlay background in physical pixels.</summary>
    public double OverlayBlurRadius
    {
        get;
        set
        {
            if (SetFieldIfChanged(ref field, value, nameof(OverlayBlurRadius)))
            {
                Raise(nameof(OverlayBlurLabel));
            }
        }
    } = 8;

    /// <summary>Text beside the Overlay blur slider.</summary>
    public string OverlayBlurLabel => $"{OverlayBlurRadius:0} px";

    // --- Appearance: accent color ---

    /// <summary>
    ///     Gets or sets the UI accent color as a hex string (e.g. "#FF9D3D").
    ///     An unparsable value falls back to the default accent when applied.
    /// </summary>
    public string AccentColorHex
    {
        get;
        set => SetField(ref field, value, nameof(AccentColorHex));
    } = AccentPalette.DefaultAccent;

    // --- Appearance: boot splash ---
    // The editor binds the SplashConfig instance directly ({Binding Splash.X}).
    // Only members with a dependent consumer keep an INPC wrapper here: the four
    // colors repaint their swatch previews on every keystroke, and the two image
    // paths drive the Appearance page's thumbnail refresh.

    /// <summary>
    ///     The splash section being edited. Replaced wholesale by
    ///     <see cref="LoadSplash" /> (startup, preset apply, theme import), which raises
    ///     this property so every nested binding re-evaluates.
    /// </summary>
    public SplashConfig Splash { get; private set; } = new();

    /// <summary>Editable placement of the splash text stack.</summary>
    public SplashPlacementEditor TextPlacement { get; } = new();

    /// <summary>Editable placement of the splash spinner.</summary>
    public SplashPlacementEditor SpinnerPlacement { get; } = new();

    /// <summary>Editable placement of the splash logo.</summary>
    public SplashPlacementEditor LogoPlacement { get; } = new();

    /// <summary>Spinner styles offered by the settings selector.</summary>
    public static SplashSpinnerStyle[] SpinnerStyleValues { get; } = Enum.GetValues<SplashSpinnerStyle>();

    /// <summary>Sweep-line edges offered by the settings selector.</summary>
    // ReSharper disable once CollectionNeverQueried.Global
    public static SweepEdge[] SweepEdgeValues { get; } = Enum.GetValues<SweepEdge>();

    /// <summary>Placement modes offered for the spinner and logo.</summary>
    public static SplashPlacementMode[] PlacementModeValues { get; } = Enum.GetValues<SplashPlacementMode>();

    /// <summary>
    ///     Placement modes offered for the text element itself, which cannot
    ///     ride its own stack.
    /// </summary>
    public static SplashPlacementMode[] TextPlacementModeValues { get; } =
        [SplashPlacementMode.Anchor, SplashPlacementMode.Absolute];

    /// <summary>Nine-grid anchors offered by the settings selectors.</summary>
    public static SplashPlacementAnchor[] PlacementAnchorValues { get; } = Enum.GetValues<SplashPlacementAnchor>();

    /// <summary>Gets or sets the splash title color as a hex string.</summary>
    public string SplashTextColorHex
    {
        get => Splash.TextColor;
        set
        {
            Splash.TextColor = value;
            Raise(nameof(SplashTextColorHex));
        }
    }

    /// <summary>Gets or sets the splash caption color as a hex string.</summary>
    public string SplashCaptionColorHex
    {
        get => Splash.CaptionColor;
        set
        {
            Splash.CaptionColor = value;
            Raise(nameof(SplashCaptionColorHex));
        }
    }

    /// <summary>Gets or sets the spinner color as a hex string.</summary>
    public string SplashSpinnerColorHex
    {
        get => Splash.SpinnerColor;
        set
        {
            Splash.SpinnerColor = value;
            Raise(nameof(SplashSpinnerColorHex));
        }
    }

    /// <summary>Gets or sets the splash background fill color as a hex string.</summary>
    public string SplashBackgroundColorHex
    {
        get => Splash.BackgroundColor;
        set
        {
            Splash.BackgroundColor = value;
            Raise(nameof(SplashBackgroundColorHex));
        }
    }

    /// <summary>Gets or sets the splash logo image path; empty = no logo.</summary>
    public string SplashLogoPath
    {
        get => Splash.LogoImagePath;
        set
        {
            Splash.LogoImagePath = value;
            Raise(nameof(SplashLogoPath));
        }
    }

    /// <summary>Gets or sets the splash background image path; empty = solid color.</summary>
    public string SplashBackgroundImagePath
    {
        get => Splash.BackgroundImagePath;
        set
        {
            Splash.BackgroundImagePath = value;
            Raise(nameof(SplashBackgroundImagePath));
        }
    }

    /// <summary>Whether both layouts currently describe a desktop Windows would accept.</summary>
    public bool CanSaveLayouts =>
        (!ShowCustomLaunch || GameLayout is { HasActiveDisplays: true, HasValidationError: false })
        && (!ShowDesktopLayout || DesktopLayout is { HasActiveDisplays: true, HasValidationError: false })
        && !ActionLists.Any(list => list.HasValidationError);

    /// <summary>Builds the production settings model over configuration already loaded at startup.</summary>
    internal static SettingsViewModel FromLoadedConfig(AppConfig config)
    {
        var viewModel = new SettingsViewModel(config, ReadInstalledPluginId(), true);
        viewModel.LoadCommonPlugins(PluginPackageCatalog.DiscoverInstalled());
        return viewModel;
    }

    private void StartRepair()
    {
        try
        {
            Process.Start(new ProcessStartInfo(InstallLayout.SetupExe, "/repair") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            Log.Warn("Plugins: starting setup's repair failed: " + ex.Message);
        }
    }

    /// <summary>Reads the Plugins page: installed files, and what the installed release bundles.</summary>
    /// <param name="catalog">The installed packages.</param>
    private void LoadPluginPackages(PluginPackageCatalog catalog)
    {
        BundleManifest? bundle = null;
        PluginOffers? offers = null;
        try
        {
            bundle = BundleManifest.TryRead(InstallLayout.InstalledBundle);
            if (bundle is not null)
            {
                string[] installed =
                [
                    .. catalog.Common.Select(package => package.Manifest.Id),
                    .. catalog.Device.InstalledPackage?.Manifest is { } device ? [device.Id] : Array.Empty<string>()
                ];
                offers = PluginOffers.Compute(bundle, DeviceMachineIdentity.Collect(), installed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("Plugins: the installed bundle could not be read: " + ex.Message);
        }

        InstalledPackages.Clear();
        AvailablePackages.Clear();
        UnavailablePackages.Clear();
        foreach (var state in PluginPackageManager.Rows(catalog, bundle, InstallLayout.SetupPackages, offers))
        {
            PluginPackageRow row = new(state, action => ActOnPackageAsync(action, bundle));
            (state.Section switch
            {
                PluginPackageSection.Installed => InstalledPackages,
                PluginPackageSection.Available => AvailablePackages,
                _ => UnavailablePackages
            }).Add(row);
        }
    }

    private static Task<string> ActOnPackageAsync(PluginPackageRowState row, BundleManifest? bundle)
    {
        return Task.Run(() =>
        {
            try
            {
                return row.Action switch
                {
                    PluginPackageAction.Install when bundle is not null => PluginPackageManager.Install(
                        row.PackagePath, bundle, InstallLayout.Plugins),
                    PluginPackageAction.Remove => PluginPackageManager.Remove(row.PackagePath, InstallLayout.Plugins),
                    _ => ""
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Plugins: {row.Action} {row.Id} failed: {ex.Message}");
                return ex is UnauthorizedAccessException
                    ? "Changing the Plugins folder needs administrator rights."
                    : "That did not work: " + ex.Message;
            }
        });
    }

    private void LoadCommonPlugins(PluginPackageCatalog catalog)
    {
        LoadPluginPackages(catalog);
        CommonPlugins.Clear();
        foreach (var package in catalog.Common)
        {
            var configured = _config.PluginInstances.Where(entry => entry.PluginId == package.Manifest.Id).ToArray();
            if (configured.Length == 0)
            {
                CommonPlugins.Add(new CommonPluginInstanceRow(package.Manifest.Id, "default", package.Manifest.Name,
                    false, true));
            }

            foreach (var instance in configured)
            {
                CommonPlugins.Add(new CommonPluginInstanceRow(instance.PluginId, instance.InstanceId,
                    package.Manifest.Name, instance.Enabled, true));
            }
        }

        foreach (var instance in _config.PluginInstances.Where(entry =>
                     catalog.Common.All(package => package.Manifest.Id != entry.PluginId)))
        {
            CommonPlugins.Add(new CommonPluginInstanceRow(instance.PluginId, instance.InstanceId, instance.PluginId,
                instance.Enabled, false));
        }

        CommonPluginDiscoveryError = string.Join(Environment.NewLine, catalog.Errors);
        Raise(nameof(CommonPluginDiscoveryError));
    }

    private void OnSelectedDeviceProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        _deviceProfilesEdited = true;
    }

    /// <summary>Adds an empty fan curve the user can then shape.</summary>
    /// <param name="capabilityId">The capability the new profile authors.</param>
    /// <param name="color">Whether to author a colour rather than a curve.</param>
    /// <remarks>
    ///     Seeded with two points at the ends rather than none. A curve needs at least two to be valid,
    ///     and an editor opening on an empty plot gives the user nothing to grab.
    /// </remarks>
    internal void AddDeviceProfile(string capabilityId, bool color = false)
    {
        var id = $"profile-{Guid.NewGuid():N}"[..16];
        DeviceProfileRowViewModel row = new(new DeviceAuthoredProfile
        {
            ProfileId = id,
            Name = $"Profile {DeviceProfiles.Count + 1}",
            CapabilityId = capabilityId,
            // One or the other, never both: the capability being authored decides which, and a
            // profile carrying an unused half would let a capability change silently resurrect a
            // value the user set for something else.
            Curve = color
                ? []
                :
                [
                    new AuthoredCurvePoint { Input = 0, Output = 0 },
                    new AuthoredCurvePoint { Input = 100, Output = 100 }
                ],
            Color = color ? 0xFF9D3D : null
        });
        DeviceProfiles.Add(row);
        SelectedDeviceProfile = row;
        _deviceProfilesEdited = true;
    }

    /// <summary>Removes the selected profile.</summary>
    internal void RemoveSelectedDeviceProfile()
    {
        if (_selectedDeviceProfile is not { } row)
        {
            return;
        }

        var index = DeviceProfiles.IndexOf(row);
        DeviceProfiles.Remove(row);
        _deviceProfilesEdited = true;
        SelectedDeviceProfile = DeviceProfiles.Count == 0
            ? null
            : DeviceProfiles[Math.Min(index, DeviceProfiles.Count - 1)];
    }

    /// <summary>Records that a profile changed.</summary>
    internal void NoteDeviceProfileEdited()
    {
        _deviceProfilesEdited = true;
    }

    /// <summary>Replaces the plugin settings page content.</summary>
    /// <param name="view">The projected sections and their settings, in draw order.</param>
    /// <param name="onEdited">Called with the setting id and new value after each edit.</param>
    /// <remarks>
    ///     Rebuilt wholesale rather than reconciled in place: the manifest changes only when a plugin is
    ///     installed or updated, so the simple path is also the correct one, and a partial reconcile
    ///     would have to answer what happens to a row whose declared kind changed underneath it.
    ///     <para>
    ///         Section ids are kept on the section view models so the window's focus and scroll restoration
    ///         still has a stable key after a rebuild.
    ///     </para>
    /// </remarks>
    internal void SetPluginSettings(
        PluginSettingsView view,
        Action<string, CapabilityValue> onEdited)
    {
        ArgumentNullException.ThrowIfNull(onEdited);
        PluginSettingSections.Clear();
        foreach (var section in view.Sections)
        {
            if (!view.Settings.TryGetValue(
                    section.SectionId,
                    out var settings))
            {
                continue;
            }

            List<PluginSettingRowViewModel> rows = [];
            foreach (var setting in settings)
            {
                PluginSettingRowViewModel model = new(setting.Descriptor, setting.Value);
                model.Edited += onEdited;
                rows.Add(model);
            }

            PluginSettingSections.Add(new PluginSettingSectionViewModel(
                section.SectionId,
                SectionTitle(section),
                rows));
        }

        Raise(nameof(PluginSettingsAvailable));
    }

    /// <summary>
    ///     Builds the plugin settings page from the most recently published declaration.
    /// </summary>
    /// <param name="config">The configuration to read the cache and the stored values from.</param>
    /// <param name="installedPluginId">Installed package ID, when discovery found one package.</param>
    /// <param name="filterToInstalledPlugin">Whether declarations from other package IDs are excluded.</param>
    /// <remarks>
    ///     Settings does not activate device hardware, so the cached declaration is the only description
    ///     of the plugin's settings available here. Stored values are still reconciled against it,
    ///     because an older declaration can describe bounds the stored values no longer fit.
    ///     <para>
    ///         Exactly one scope is drawn — the one matching the installed plugin — and the reason is
    ///         reported when none does, since a blank page cannot distinguish "no plugin" from "the page
    ///         failed".
    ///     </para>
    /// </remarks>
    private void LoadPluginSettings(
        AppConfig config,
        string? installedPluginId,
        bool filterToInstalledPlugin)
    {
        ArgumentNullException.ThrowIfNull(config);
        var candidates = config.DeviceIntegration.PluginSettings
            .Where(candidate => candidate.Declaration is not null);
        if (filterToInstalledPlugin)
        {
            candidates = installedPluginId is null
                ? []
                : candidates.Where(candidate => string.Equals(
                    candidate.PluginId,
                    installedPluginId,
                    StringComparison.Ordinal));
        }

        var scope = candidates.LastOrDefault();
        if (scope?.Declaration is not { } declaration)
        {
            PluginSettingSections.Clear();
            PluginSettingsEmptyReason =
                "No device plugin has published settings yet. Start WSGM's shell once with the "
                + "plugin installed, then reopen Settings.";
            Raise(nameof(PluginSettingsAvailable));
            return;
        }

        var resolution = PluginSettingsResolver.Resolve(
            declaration,
            scope.Values);
        foreach (var rejected in resolution.Values
                     .Where(value => value.Origin is PluginSettingOrigin.Rejected))
        {
            // The stored value and the declared bound, together: a rejection reported without both
            // cannot be acted on from a user's log.
            Log.Warn(
                $"Plugin setting '{rejected.SettingId}' fell back to its default: {rejected.Reason}");
        }

        _pluginSettingsDevice = scope.DeviceDefinitionId;
        _pluginSettingsPlugin = scope.PluginId;
        _pluginSettingEdits.Clear();
        LoadDeviceProfiles(scope);
        SetPluginSettings(
            PluginSettingsCoordinator.Project(declaration, resolution),
            (settingId, value) => _pluginSettingEdits[settingId] = value);

        if (PluginSettingSections.Count == 0)
        {
            PluginSettingsEmptyReason =
                "The installed device plugin declares no settings.";
        }
    }

    private static string? ReadInstalledPluginId()
    {
        return PluginPackageCatalog.InstalledDevicePluginId();
    }

    private void LoadDeviceProfiles(PluginSettingsScope scope)
    {
        DeviceProfiles.Clear();
        foreach (var profile in scope.Profiles)
        {
            DeviceProfiles.Add(new DeviceProfileRowViewModel(profile));
        }

        SelectedDeviceProfile = DeviceProfiles.FirstOrDefault();
        _deviceProfilesEdited = false;
    }

    /// <summary>Writes the authored profiles into the configuration being saved.</summary>
    /// <param name="config">The freshly loaded configuration the save is applied to.</param>
    /// <remarks>
    ///     The whole list is replaced, not merged, because authoring is Settings-only (D22b) and this
    ///     window holds the complete set — but only when the user actually changed something, so an
    ///     unrelated save never overwrites profiles another process wrote.
    /// </remarks>
    internal void ApplyDeviceProfilesTo(AppConfig config)
    {
        if (!_deviceProfilesEdited
            || _pluginSettingsDevice.Length == 0
            || _pluginSettingsPlugin.Length == 0)
        {
            return;
        }

        FindOrAddScope(config).Profiles = [.. DeviceProfiles.Select(row => row.ToStored())];
    }

    /// <summary>
    ///     Finds this window's plugin-settings scope in the configuration being
    ///     saved, adding it when a fresh load does not carry one yet.
    /// </summary>
    /// <param name="config">The freshly loaded configuration the save is applied to.</param>
    private PluginSettingsScope FindOrAddScope(AppConfig config)
    {
        var scopes = config.DeviceIntegration.PluginSettings;
        var scope = scopes.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceDefinitionId, _pluginSettingsDevice, StringComparison.Ordinal)
            && string.Equals(candidate.PluginId, _pluginSettingsPlugin, StringComparison.Ordinal));
        if (scope is not null)
        {
            return scope;
        }

        scope = new PluginSettingsScope
        {
            DeviceDefinitionId = _pluginSettingsDevice,
            PluginId = _pluginSettingsPlugin
        };
        scopes.Add(scope);
        return scope;
    }

    /// <summary>Writes the edited plugin settings into the configuration being saved.</summary>
    /// <param name="config">The freshly loaded configuration the save is applied to.</param>
    /// <remarks>
    ///     Edits are recorded rather than written onto the configuration the page was built from,
    ///     because the save re-reads configuration from disk and applies the view model onto THAT
    ///     object — anything written to the loaded copy is discarded. It also means a setting the user
    ///     never touched is left exactly as another process wrote it, instead of being rewritten with
    ///     whatever this window happened to load.
    /// </remarks>
    internal void ApplyPluginSettingsTo(AppConfig config)
    {
        if (_pluginSettingEdits.Count == 0
            || _pluginSettingsDevice.Length == 0
            || _pluginSettingsPlugin.Length == 0)
        {
            return;
        }

        var scope = FindOrAddScope(config);
        foreach (var (settingId, value) in _pluginSettingEdits)
        {
            PluginSettingsResolver.Store(scope, settingId, value);
        }
    }

    /// <remarks>
    ///     A custom title is plugin-supplied plain text, already bounded and validated by
    ///     <see cref="PluginSettingSection" />; it is rendered as text and never as markup. A keyed title
    ///     is WSGM's, which is the entire reason the key exists.
    /// </remarks>
    private static string SectionTitle(PluginSettingSection section)
    {
        return section.Key is SettingSectionKey.Custom
            ? (section.CustomTitle ?? section.SectionId).ToUpperInvariant()
            : section.Key.ToString().ToUpperInvariant();
    }

    /// <summary>
    ///     Reads startup sources on a worker. The synchronous Windows adapter waits for an
    ///     asynchronous console command and must never run under the UI synchronization context.
    /// </summary>
    internal async Task<IReadOnlyList<SteamAutostartSource>> ScanSteamAutostartAsync()
    {
        try
        {
            return await Task.Run(_services.ScanSteamAutostart);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Report("Steam autostart scan failed", ex);
            throw;
        }
    }

    /// <summary>Re-scans and, with the takeover accepted, disables what came back.</summary>
    private async Task TakeOverSteamAutostartAsync()
    {
        try
        {
            SteamAutostartStatusText = "Checking how Windows starts Steam…";
            var enabled = (await ScanSteamAutostartAsync()).Where(source => source.Enabled).ToArray();
            if (enabled.Length == 0)
            {
                SteamAutostartStatusText = "WSGM starts Steam; Windows has no Steam startup entry of its own.";
                return;
            }

            if (!SteamAutostartTakeoverAccepted)
            {
                SteamAutostartStatusText = $"Windows starts Steam from {enabled.Length} place(s). "
                                           + "Turn this on and save to let WSGM own that start.";
                SteamAutostartTakeoverAccepted = true;
                return;
            }

            var result = await Task.Run(() => _services.ApplySteamAutostart(enabled));
            SteamAutostartStatusText = result.Complete
                ? $"Turned off {result.Disabled.Count} Steam startup entry/entries; WSGM starts Steam."
                : "Some Steam startup entries are still enabled: "
                  + string.Join(", ", result.Pending.Concat(result.NeedsElevation).Select(source => source.Describe()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Report("Steam autostart takeover failed", ex);
            SteamAutostartStatusText = $"Could not read Windows' startup entries: {ex.Message}";
        }
    }

    private void OpenLogLocation()
    {
        try
        {
            var log = Path.Combine(Log.Directory, "wsgm.log");
            // Game mode has no Explorer in the session, and WSGM is normally elevated:
            // starting explorer.exe here would either break UWP for the session (an
            // elevated Explorer; see docs\elevation.md) or bring its taskbar up next to WSGM's
            // own tray host. Show the path instead; the user can open it in desktop mode.
            if (!ExplorerControl.IsDesktopShellRunning())
            {
                Log.Info(
                    $"Open log location: no Explorer in this session — showing the path instead ({Log.Directory}).");
                StatusText = $"Log folder: {Log.Directory} (open it in desktop mode)";
                return;
            }

            // Absolute system path: a relative name would resolve via the process
            // working directory, which is the user-writable install dir.
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var explorer = Path.Combine(windir, "explorer.exe");
            // Select the file when it exists so the user lands right on it;
            // otherwise just open the folder.
            var psi = File.Exists(log)
                ? new ProcessStartInfo(explorer, $"/select,\"{log}\"")
                : new ProcessStartInfo(Log.Directory);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = windir;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the log location: {ex.Message}");
            StatusText = $"Could not open the log location: {ex.Message}";
        }
    }

    private void BuildStartupSuggestions()
    {
        var names = new List<string>();
        var targets = new List<(string, bool)>();

        foreach (var (label, path, elevated) in _services.DetectStartupApps())
        {
            names.Add(label);
            targets.Add((path, elevated));
        }

        names.Add("Choose a program…");
        targets.Add(("", false));

        StartupSuggestions = names;
        _startupSuggestionTargets = targets;
        _selectedSuggestionIndex = 0;
    }

    /// <summary>Adds the selected discovered program when it has a concrete executable path.</summary>
    /// <returns><see langword="true" /> when a startup row was added; otherwise the caller should open a file picker.</returns>
    public bool AddSelectedStartupApp()
    {
        if (_selectedSuggestionIndex < 0 || _selectedSuggestionIndex >= _startupSuggestionTargets.Count)
        {
            return false;
        }

        var (path, elevated) = _startupSuggestionTargets[_selectedSuggestionIndex];
        if (string.IsNullOrEmpty(path))
        {
            return false; // caller opens the file picker
        }

        StartupApps.Add(new StartupAppRow { Path = path, Elevated = elevated, Enabled = true });
        return true;
    }

    /// <summary>Refreshes the read-only owner snapshot without creating a device cycle.</summary>
    public async Task RefreshDeviceOwnerStatusAsync()
    {
        try
        {
            var snapshot =
                await DeviceCoordinatorDiagnosticsClient.TryReadAsync(
                    (uint)WindowFinder.CurrentSessionId,
                    TimeSpan.FromMilliseconds(750));
            DeviceOwnerStatusText = snapshot is null
                ? "No running device coordinator detected. Saved changes apply at the next shell start."
                : $"{snapshot.State} · {snapshot.InstalledPackage?.PackageId ?? "no package"} · "
                  + $"{snapshot.HealthyCapabilityCount}/{snapshot.CapabilityCount} healthy · "
                  + $"cycle {snapshot.CycleGeneration}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Device owner status refresh failed: {ex.Message}");
            DeviceOwnerStatusText = $"Could not read the running device owner: {ex.Message}";
        }
    }

    /// <summary>
    ///     Toggles the machine UAC prompt level, off-thread: the elevated
    ///     one-shot blocks for as long as the consent prompt is on screen — up to a
    ///     minute if the user leaves it sitting — and in game mode the frozen window is
    ///     the one holding the Steam Input lease, so the pad looks dead too and it reads
    ///     as a hang.
    ///     <para>
    ///         Call from the UI thread: the continuation resumes there, so the property
    ///         change notifications stay UI-thread owned.
    ///     </para>
    /// </summary>
    /// <param name="disable">Whether to suppress consent prompts.</param>
    /// <returns><see langword="true" /> when Windows accepted the policy change.</returns>
    public async Task<bool> SetUacPromptsAsync(bool disable)
    {
        var ok = await Task.Run(() => UacSettings.RequestChange(disable)).ConfigureAwait(true);
        Raise(nameof(UacPromptsDisabled));
        Raise(nameof(UacStatusText));
        return ok;
    }

    /// <summary>
    ///     Changes the Windows wake sign-in policy through the elevated helper,
    ///     off-thread — see <see cref="SetUacPromptsAsync" /> for why a synchronous form
    ///     would freeze the window. Call from the UI thread so the notifications resume
    ///     there.
    /// </summary>
    /// <param name="disable">Whether to bypass the sign-in prompt after display sleep.</param>
    /// <returns><see langword="true" /> when Windows accepted the policy change.</returns>
    public async Task<bool> SetLockOnWakeAsync(bool disable)
    {
        var ok = await Task.Run(() => LockScreenSettings.RequestChange(disable)).ConfigureAwait(true);
        Raise(nameof(LockOnWakeDisabled));
        Raise(nameof(LockOnWakeStatusText));
        return ok;
    }

    /// <summary>Builds the tab rows from the stored order and visibility.</summary>
    private void LoadArtworkTabs()
    {
        var titles = ArtworkTabTitles;
        var stored = _config.Artwork.TabOrder
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(titles.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // ConfigStore repairs a stored order that is not a permutation, but Settings can be opened
        // against anything on disk, so the canonical list still fills in whatever is missing.
        stored.AddRange(ArtworkConfig.DefaultTabOrder.Split(',').Where(id => !stored.Contains(id)));

        ArtworkTabs.Clear();
        foreach (var id in stored)
        {
            ArtworkTabs.Add(new ArtworkTabRow(id, titles[id], IsArtworkTabVisible(id)));
        }

        var index = ArtworkTabs.ToList()
            .FindIndex(row => string.Equals(row.Id, _config.Artwork.DefaultTab, StringComparison.Ordinal));
        ArtworkDefaultTabIndex = index < 0 ? 0 : index;
    }

    /// <summary>Whether the stored configuration offers one tab.</summary>
    /// <param name="id">The tab id.</param>
    private bool IsArtworkTabVisible(string id)
    {
        return id switch
        {
            "grid" => _config.Artwork.ShowGrid,
            "wide" => _config.Artwork.ShowWide,
            "hero" => _config.Artwork.ShowHero,
            "logo" => _config.Artwork.ShowLogo,
            "icon" => _config.Artwork.ShowIcon,
            "manage" => _config.Artwork.ShowManage,
            _ => true
        };
    }

    /// <summary>Whether the edited rows offer one tab.</summary>
    /// <param name="id">The tab id.</param>
    private bool IsArtworkTabChecked(string id)
    {
        return ArtworkTabs.FirstOrDefault(row => string.Equals(row.Id, id, StringComparison.Ordinal))
            ?.Visible ?? true;
    }

    /// <summary>Moves an artwork tab by one position when the target remains in range.</summary>
    /// <param name="row">The row to move, or null (a no-op).</param>
    /// <param name="delta">The signed number of positions to move the row.</param>
    private void MoveArtworkTab(ArtworkTabRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        var index = ArtworkTabs.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ArtworkTabs.Count)
        {
            return;
        }

        // The default follows the tab it names rather than the position, which is what a user
        // reordering the strip means by it.
        var wanted = ArtworkDefaultTabIndex >= 0 && ArtworkDefaultTabIndex < ArtworkTabs.Count
            ? ArtworkTabs[ArtworkDefaultTabIndex]
            : null;
        ArtworkTabs.Move(index, target);
        if (wanted is not null)
        {
            ArtworkDefaultTabIndex = ArtworkTabs.IndexOf(wanted);
        }
    }

    /// <summary>Moves a startup-program row by one position when the target remains in range.</summary>
    /// <param name="row">The row to move, or null (a no-op).</param>
    /// <param name="delta">The signed number of positions to move the row.</param>
    private void MoveStartupApp(StartupAppRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        var index = StartupApps.IndexOf(row);
        var target = index + delta;
        if (index >= 0 && target >= 0 && target < StartupApps.Count)
        {
            StartupApps.Move(index, target);
        }
    }

    /// <summary>Starts or stops keyboard-shortcut recording.</summary>
    /// <param name="recording">Whether the next eligible key combination should be captured.</param>
    public void SetHotkeyRecording(bool recording)
    {
        _hotkeyRecording = recording;
        Raise(nameof(HotkeyText));
    }

    /// <summary>Starts or stops controller-chord recording.</summary>
    /// <param name="recording">Whether the next eligible controller chord should be captured.</param>
    public void SetChordRecording(bool recording)
    {
        _chordRecording = recording;
        Raise(nameof(ChordText));
    }

    /// <summary>Stores a recorded keyboard shortcut, already in configuration shape.</summary>
    /// <param name="hotkey">The captured shortcut, or <see cref="KeyRecorder.Cleared" />.</param>
    public void ApplyRecordedHotkey(HotkeyConfig hotkey)
    {
        _hotkey = hotkey;
        SetHotkeyRecording(false);
    }

    /// <summary>Stores a recorded controller chord. No buttons clears it.</summary>
    /// <param name="buttons">The buttons captured from one controller.</param>
    /// <param name="hold">Whether the chord activates on a hold rather than an edge.</param>
    public void ApplyRecordedChord(GamepadButtons buttons, bool hold)
    {
        _chord = new GamepadChordConfig
        {
            Enabled = buttons != 0,
            Buttons = (int)buttons,
            Hold = hold
        };
        SetChordRecording(false);
    }

    /// <summary>Clears the keyboard shortcut.</summary>
    public void ClearHotkey()
    {
        ApplyRecordedHotkey(KeyRecorder.Cleared());
    }

    /// <summary>Clears the controller chord.</summary>
    public void ClearChord()
    {
        ApplyRecordedChord(0, false);
    }

    /// <summary>
    ///     Builds the splash section handed to Save, the preview window, and
    ///     theme export: an isolated copy of the edited section, so the save path's
    ///     asset staging can rewrite its image paths without touching the editor.
    /// </summary>
    internal SplashConfig BuildSplashConfig()
    {
        var splash = ConfigStore.CloneJson(Splash, ConfigJsonContext.Default.SplashConfig);
        // "With text" is a spinner/logo-only mode; the text element itself anchors.
        // Normalize accepts WithText on every placement, so an imported theme can
        // still carry it on the text placement — this is where it is coerced.
        if (splash.TextPlacement.Mode == SplashPlacementMode.WithText)
        {
            splash.TextPlacement.Mode = SplashPlacementMode.Anchor;
        }

        return splash;
    }

    /// <summary>
    ///     Loads the splash editor from a splash section — used at startup, on
    ///     preset apply, and after theme import. The section is copied and normalized,
    ///     so later edits cannot mutate the caller's instance and an imported value can
    ///     never carry an out-of-range enum into the editor.
    /// </summary>
    internal void LoadSplash(SplashConfig splash)
    {
        Splash = ConfigStore.NormalizeSplash(
            ConfigStore.CloneJson(splash, ConfigJsonContext.Default.SplashConfig));
        TextPlacement.Load(Splash.TextPlacement);
        SpinnerPlacement.Load(Splash.SpinnerPlacement);
        LogoPlacement.Load(Splash.LogoPlacement);
        Raise(nameof(Splash));
        Raise(nameof(SplashTextColorHex));
        Raise(nameof(SplashCaptionColorHex));
        Raise(nameof(SplashSpinnerColorHex));
        Raise(nameof(SplashBackgroundColorHex));
        Raise(nameof(SplashLogoPath));
        Raise(nameof(SplashBackgroundImagePath));
    }

    // --- Save ---
    private void ApplyTo(AppConfig config)
    {
        ApplyTo(config, BuildSplashConfig());
    }

    /// <summary>
    ///     Applies the UI-owned fields over <paramref name="config" />, taking the
    ///     splash section from <paramref name="splash" /> instead of rebuilding it — the save
    ///     path prepares (and thereby path-rewrites) its splash section BEFORE it takes the
    ///     config lock, and rebuilding here would throw that rewrite away.
    /// </summary>
    private void ApplyTo(AppConfig config, SplashConfig splash)
    {
        config.SteamAutoRelaunch = SteamAutoRelaunch;
        config.SteamLaunchUnelevated = SteamLaunchUnelevated;
        config.StartupDelayMs = StartupDelayMs;
        config.StaggerDelayMs = StaggerDelayMs;
        config.BootSplashEnabled = BootSplashEnabled;
        config.StartAtSignIn = StartAtSignIn;
        config.StartMode = (SessionStartMode)Math.Clamp(StartModeIndex, 0, 1);
        config.SteamAutostartTakeoverAccepted = SteamAutostartTakeoverAccepted;
        ApplyLaunchTo(config.GameModeLaunch);
        config.SteamInputLeaseEnabled = SteamInputLeaseEnabled;
        config.SteamInputManagementEnabled = SteamInputManagementEnabled;
        // The tab strip's order is the row order, and its default follows the tab it names.
        config.Artwork.SteamGridDbApiKey = ArtworkSteamGridDbApiKey;
        config.Artwork.ScreenscraperEnabled = ArtworkScreenscraperEnabled;
        config.Artwork.ScreenscraperUser = ArtworkScreenscraperUser;
        config.Artwork.ScreenscraperUserPassword = ArtworkScreenscraperPassword;
        config.Artwork.TabOrder = string.Join(',', ArtworkTabs.Select(row => row.Id));
        config.Artwork.DefaultTab = ArtworkDefaultTabIndex >= 0 && ArtworkDefaultTabIndex < ArtworkTabs.Count
            ? ArtworkTabs[ArtworkDefaultTabIndex].Id
            : ArtworkConfig.DefaultTabOrder.Split(',')[0];
        config.Artwork.ShowGrid = IsArtworkTabChecked("grid");
        config.Artwork.ShowWide = IsArtworkTabChecked("wide");
        config.Artwork.ShowHero = IsArtworkTabChecked("hero");
        config.Artwork.ShowLogo = IsArtworkTabChecked("logo");
        config.Artwork.ShowIcon = IsArtworkTabChecked("icon");
        config.Artwork.ShowManage = IsArtworkTabChecked("manage");
        config.GameLibrary.DefaultMode = GameLibraryDefaultModeIndex == 1
            ? ImportMode.ControllerOnly
            : ImportMode.SteamIntegration;
        config.GameLibrary.ImportUnroutable = GameLibraryImportUnroutable;
        config.DeviceIntegration.Enabled = DeviceIntegrationEnabled;
        config.DeviceIntegration.ControllerManagementEnabled = DeviceControllerManagementEnabled;
        config.DeviceIntegration.MotionStream = DeviceMotionOnDemand
            ? MotionStreamMode.OnDemand
            : MotionStreamMode.Always;
        config.DeviceIntegration.KeepGuideChordEdits = DeviceKeepGuideChordEdits;
        // Same rule as the three below, for the same reason: only settings this window actually
        // edited are written, so a running shell's own stores are not reverted by an unrelated save.
        ApplyPluginSettingsTo(config);
        ApplyDeviceProfilesTo(config);
        // Only when this window actually changed them. All three are also owned by the running
        // shell — the overlay and the native quick-access menu persist AutoTDP, the controller
        // target and the glyph policy while Settings is open — so writing an unedited snapshot over
        // the fresh load silently reverted the active policy on the next unrelated save.
        if (_deviceAutoTdpEdited)
        {
            config.DeviceIntegration.AutoTdpEnabled = DeviceAutoTdpEnabled;
        }

        if (_deviceControllerTargetEdited)
        {
            config.Profiles.Global.ControllerTarget = (ManagedControllerTarget)Math.Clamp(
                DeviceControllerTargetIndex,
                0,
                Enum.GetValues<ManagedControllerTarget>().Length - 1);
        }

        if (_deviceGlyphSelectionEdited)
        {
            config.DeviceIntegration.GlyphSelection = (DeviceGlyphSelection)Math.Clamp(
                DeviceGlyphSelectionIndex,
                0,
                Enum.GetValues<DeviceGlyphSelection>().Length - 1);
        }

        config.Performance.Enabled = PerformanceEnabled;
        config.Performance.FrameLimitStrategy = (FrameLimitStrategy)Math.Clamp(
            FrameLimitStrategyIndex,
            0,
            Enum.GetValues<FrameLimitStrategy>().Length - 1);
        config.Performance.OsdCustomOrder = OsdCustomOrder;
        config.Performance.OsdCustomTime = Math.Clamp(OsdCustomTimeIndex, 0, 2);
        config.Performance.OsdCustomFps = Math.Clamp(OsdCustomFpsIndex, 0, 2);
        config.Performance.OsdCustomCpu = Math.Clamp(OsdCustomCpuIndex, 0, 2);
        config.Performance.OsdCustomRam = Math.Clamp(OsdCustomRamIndex, 0, 2);
        config.Performance.OsdCustomGpu = Math.Clamp(OsdCustomGpuIndex, 0, 2);
        config.Performance.OsdCustomVram = Math.Clamp(OsdCustomVramIndex, 0, 2);
        config.Performance.OsdCustomBattery = Math.Clamp(OsdCustomBatteryIndex, 0, 2);
        config.Cef.Enabled = CefEnabled;
        config.Cef.LibraryTabs = CefLibraryTabs;
        config.Cef.CardManager = CefCardManager;
        config.Cef.SdFormat = CefSdFormat;
        config.SteamStorageFormatEnabled = SteamStorageFormat;
        config.Cef.WifiIndicator = CefWifiIndicator;
        config.Cef.NativeQuickAccess = CefNativeQuickAccess;
        config.Cef.DownloadKeepAwake = CefDownloadKeepAwake;
        config.Cef.DownloadQueueSort = CefDownloadQueueSort;
        config.Cef.ConnectedLibraryCarousel = CefConnectedLibraryCarousel;
        config.Cef.CarouselShowUninstalled = CefCarouselShowUninstalled;
        config.MuteWhileDisplayOff = MuteWhileDisplayOff;
        config.CheckForUpdates = CheckForUpdates;
        config.ResuspendUnexplainedWakes = ResuspendUnexplainedWakes;
        config.LogVerbosity = VerboseLogging ? LogVerbosity.Verbose : LogVerbosity.Normal;
        config.AutoTdpTraceEnabled = AutoTdpTrace;
        config.Hotkey = _hotkey;
        config.GamepadChord = _chord;
        config.Gestures.BottomEdge = GestureBottom;
        config.Gestures.TopEdge = GestureTop;
        config.Gestures.LeftEdgeSteamMenu = GestureLeftSteamMenu;
        config.Gestures.RightEdgeSteamQuickAccess = GestureRightSteamQuickAccess;
        config.GlyphStyle = GlyphStyle;
        config.AccentColor = AccentColorHex;
        config.OverlayBlurRadius = OverlayBlurRadius;
        config.Splash = splash;
        config.StartupApps =
        [
            .. StartupApps
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .Select(r => new StartupAppConfig
                {
                    Path = r.Path.Trim(),
                    Args = r.Args.Trim(),
                    Enabled = r.Enabled,
                    Elevated = r.Elevated,
                    AutoRelaunch = r.AutoRelaunch
                })
        ];
    }

    /// <summary>Seeds the launch fields from a stored configuration.</summary>
    /// <param name="launch">The stored configuration.</param>
    private void LoadLaunchConfiguration(GameModeLaunchConfiguration launch)
    {
        GameModeLaunchKindIndex = (int)launch.Kind;
        GameModeReturnIndex = (int)launch.Return;
        _gameLayout = launch.GameLayout;
        _desktopLayout = launch.DesktopLayout;
        GameAudioProfile.Load(launch.GameAudio);
        DesktopAudioProfile.Load(launch.DesktopAudio);
        _waitForDisplay = launch.WaitForDisplay;
        _enterActions = launch.EnterActions;
        _leaveActions = launch.LeaveActions;
        _desktopStartupActions = launch.DesktopStartupActions;
        _desktopWakeActions = launch.DesktopWakeActions;

        KnownDisplays.Clear();
        foreach (var display in launch.KnownDisplays)
        {
            KnownDisplays.Add(display);
        }

        // Injected readers provide the initial fixture observation here. Production discovery
        // starts on a worker after the Settings window opens.
        if (!_queryDisplaysOnWorker)
        {
            try
            {
                _observedDisplays = _services.CaptureDisplays();
                MergeCatalog(_observedDisplays);
            }
            catch (Exception ex)
            {
                DisplayDiscoveryText = "Displays could not be read. Refresh the display list to try again.";
                _services.Report("Could not read the current displays for Settings", ex);
            }
        }

        RefreshLaunchRows();
        _launchLoaded = true;
    }

    /// <summary>Rebuilds every rendered row from the fields the editor owns.</summary>
    private void RefreshLaunchRows()
    {
        GameLayout.Load(KnownDisplays, _present, _gameLayout);
        DesktopLayout.Load(KnownDisplays, _present, _desktopLayout);

        var options = ReadPluginActions();
        ActionLists[0].Load(_enterActions, options);
        ActionLists[1].Load(_leaveActions, options);
        ActionLists[2].Load(_desktopStartupActions, options);
        ActionLists[3].Load(_desktopWakeActions, options);

        RefreshDisplayChoices();
    }

    private void RefreshDisplayChoices()
    {
        WaitForDisplayChoices.Clear();
        WaitForDisplayChoices.Add("No display wait");
        foreach (var display in KnownDisplays)
        {
            WaitForDisplayChoices.Add(display.Target?.FriendlyName ?? "Unnamed display");
        }

        WaitForDisplayIndex = _waitForDisplay is null
            ? 0
            : Math.Max(0,
                KnownDisplays.ToList().FindIndex(display => display.Target?.Matches(_waitForDisplay) == true) + 1);

        RebindChoices.Clear();
        foreach (var target in _present)
        {
            RebindChoices.Add(target.FriendlyName.Length > 0 ? target.FriendlyName : "Unnamed display");
        }

        RebindChoiceIndex = RebindChoices.Count > 0 ? 0 : -1;
        RefreshLaunchSummary();
    }

    /// <summary>
    ///     Restates what the two layouts describe. Called on every edit, so the page says
    ///     whether the layout can be saved while it is being changed rather than only on Save.
    /// </summary>
    private void RefreshLaunchSummary()
    {
        var active = GameLayout.Rows.Count(row => row.Active);
        LaunchSummaryText = GameModeLaunchKindIndex == (int)GameModeLaunchKind.Default
            ? "Game Mode starts on the display Windows calls primary and adjusts scaling only."
            : active == 0
                ? "Enable at least one display for Game Mode."
                : GameLayout.ValidationText.Length > 0
                    ? "Game Mode layout: " + GameLayout.ValidationText
                    : ShowDesktopLayout && DesktopLayout.ValidationText.Length > 0
                        ? "Desktop layout: " + DesktopLayout.ValidationText
                        : $"{active} display(s) in the Game Mode layout.";
        Raise(nameof(CanSaveLayouts));
    }

    private void ForgetDisplay(DisplayLayoutEditorRow? row)
    {
        if (row is null)
        {
            return;
        }

        foreach (var editor in new[] { GameLayout, DesktopLayout })
        {
            foreach (var other in editor.Rows.Where(candidate => candidate.Display == row.Display).ToList())
            {
                editor.Forget(other);
            }
        }

        KnownDisplays.Remove(row.Display);
        if (row.Target is { } forgotten)
        {
            _forgottenDisplays.Add(forgotten);
        }

        if (_waitForDisplay is { } wait && row.Target?.Matches(wait) == true)
        {
            _waitForDisplay = null;
        }

        RefreshDisplayChoices();
        RefreshLaunchSummary();
    }

    private void RebindDisplay(DisplayLayoutEditorRow? row)
    {
        if (row is null || RebindChoiceIndex < 0 || RebindChoiceIndex >= _present.Count)
        {
            return;
        }

        var target = _present[RebindChoiceIndex];
        if (!row.NeedsRebind)
        {
            StatusText = "That row already names a display.";
            return;
        }

        // The chosen display usually already has its own catalog row, because it is connected and
        // Settings observed it on open. Merging is what the user means: the migrated row carries
        // the values, the real row carries the identity, and two rows for one monitor could never
        // both be applied.
        var editor = DesktopLayout.Rows.Contains(row) ? DesktopLayout : GameLayout;
        var existing =
            editor.Rows.FirstOrDefault(candidate => candidate != row && candidate.Target?.Matches(target) == true);
        if (existing is not null)
        {
            existing.Active = row.Active;
            existing.Mode = existing.Modes.FirstOrDefault(mode => mode.Equals(row.Mode)) ?? row.Mode;
            existing.X = row.X;
            existing.Y = row.Y;
            existing.DpiPercent = row.DpiPercent;
            existing.HdrEnabled = row.HdrEnabled;
            existing.IsPrimary = row.IsPrimary;
            ForgetDisplay(row);
        }
        else
        {
            row.Rebind(target);
            if (!KnownDisplays.Contains(row.Display))
            {
                KnownDisplays.Add(row.Display);
            }
        }

        StatusText = $"{target.FriendlyName} bound. Save to keep it.";
        RefreshLaunchSummary();
    }

    private IReadOnlyList<PluginActionOption> ReadPluginActions()
    {
        try
        {
            return _services.ReadPluginActions();
        }
        catch (Exception ex)
        {
            _services.Report("Could not read the running plugin actions for Settings", ex);
            return [];
        }
    }

    private void RemoveActionStep(PluginActionStepEditorRow? row)
    {
        if (row is null)
        {
            return;
        }

        foreach (var list in ActionLists.Where(list => list.Rows.Contains(row)))
        {
            list.Remove(row);
        }
    }

    private void MoveActionStep(PluginActionStepEditorRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        foreach (var list in ActionLists.Where(list => list.Rows.Contains(row)))
        {
            list.Move(row, delta);
        }
    }

    /// <summary>
    ///     Adds what this observation knows about each display to the remembered catalog, so a
    ///     display stays configurable after it is unplugged.
    /// </summary>
    private void MergeCatalog(DisplayArrangement arrangement,
        IReadOnlyDictionary<string, DisplayCatalogFacts?>? factsByDisplay = null)
    {
        _present = [.. arrangement.Targets.Where(target => target.Available).Select(target => target.Target)];
        foreach (var observed in arrangement.Targets)
        {
            if (!observed.Available)
            {
                continue;
            }

            var existing = KnownDisplays.FirstOrDefault(display => display.Target?.Matches(observed.Target) == true);
            if (existing is null)
            {
                existing = new KnownDisplay { Target = observed.Target };
                KnownDisplays.Add(existing);
            }

            existing.Target = observed.Target;
            existing.LastSeen = arrangement.CapturedAt;
            // Disabled sources still expose monitor EDID. Retain the broader driver-mode list
            // remembered while active rather than replacing it with descriptor-only timings.
            if ((factsByDisplay is null
                    ? _services.ReadDisplayFacts(observed.Target)
                    : factsByDisplay.GetValueOrDefault(observed.Target.DevicePath)) is { } facts)
            {
                if (facts.Modes.Count > 0)
                {
                    existing.Modes = observed.Active
                        ? [.. facts.Modes]
                        : [.. existing.Modes.Concat(facts.Modes).Distinct()];
                }

                existing.HdrSupported |= facts.HdrSupported;
                existing.MaximumDpiPercent = Math.Max(existing.MaximumDpiPercent, facts.MaximumDpiPercent);
            }

            if (observed.Current?.Hdr is not null)
            {
                existing.HdrSupported = true;
            }

            if (observed.Current is not { } current)
            {
                continue;
            }

            // The mode it is running is worth keeping even when enumeration failed, so a
            // remembered display always offers at least what it was last seen doing.
            DisplayMode running = new(current.Width, current.Height,
                (int)Math.Round(current.Refresh.Hertz));
            if (running is { Width: > 0, Height: > 0 } && !existing.Modes.Contains(running))
            {
                existing.Modes.Add(running);
            }
        }
    }

    /// <summary>
    ///     Captures the launch fields and remembered displays from the editor. Persistence
    ///     merges concurrent discoveries while honoring explicit Forget actions.
    /// </summary>
    /// <param name="launch">The section to write into.</param>
    private void ApplyLaunchTo(GameModeLaunchConfiguration launch)
    {
        launch.Kind = (GameModeLaunchKind)Math.Clamp(GameModeLaunchKindIndex, 0, 1);
        launch.Return = (GameModeReturn)Math.Clamp(GameModeReturnIndex, 0, 1);
        launch.GameLayout = GameLayout.Build();
        launch.DesktopLayout = DesktopLayout.Build();
        launch.GameAudio = GameAudioProfile.Build();
        launch.DesktopAudio = DesktopAudioProfile.Build();
        launch.WaitForDisplay = WaitForDisplayIndex > 0 && WaitForDisplayIndex <= KnownDisplays.Count
            ? KnownDisplays[WaitForDisplayIndex - 1].Target
            : null;
        launch.EnterActions = ActionLists[0].Build();
        launch.LeaveActions = ActionLists[1].Build();
        launch.DesktopStartupActions = ActionLists[2].Build();
        launch.DesktopWakeActions = ActionLists[3].Build();
        launch.KnownDisplays = [.. KnownDisplays];
    }

    /// <summary>Captures every UI-owned value into an isolated graph on the UI thread.</summary>
    internal SaveRequest CaptureSaveRequest()
    {
        var splash = BuildSplashConfig();
        var values = ConfigStore.CloneJson(_config, ConfigJsonContext.Default.AppConfig);
        ApplyTo(values, splash);
        // ApplyTo intentionally reuses several bound objects. One final contract copy
        // makes the worker independent from edits made while the save is running.
        values = ConfigStore.CloneJson(values, ConfigJsonContext.Default.AppConfig);
        splash = values.Splash;
        return new SaveRequest(
            values,
            splash,
            new Dictionary<string, CapabilityValue>(_pluginSettingEdits, StringComparer.Ordinal),
            _deviceProfilesEdited ? [.. DeviceProfiles.Select(static profile => profile.ToStored())] : null,
            _pluginSettingsDevice,
            _pluginSettingsPlugin,
            _deviceAutoTdpEdited,
            _deviceControllerTargetEdited,
            _deviceGlyphSelectionEdited)
        {
            SharedEdits =
            [
                .. SharedFields.Where(field => !Equals(field.Read(values), _sharedBaseline[field.Name]))
                    .Select(field => field.Name)
            ],
            SharedValues = SharedFields.ToDictionary(field => field.Name, field => field.Read(values),
                StringComparer.Ordinal),
            ForgottenDisplays = [.. _forgottenDisplays],
            CommonPluginEdits = [.. CommonPlugins.Where(row => row.Edited).Select(row => row.Capture())]
        };
    }

    private async Task SaveWithStatusAsync()
    {
        if (!CanSaveLayouts)
        {
            StatusText = "Fix the display layout or session action errors before saving.";
            return;
        }

        IsSaving = true;
        StatusText = "Saving…";
        var importLease = false;
        try
        {
            // The Settings window itself owns one import session, but it may close
            // while this asynchronous save is copying a staged theme. Take a second
            // counted lease so window cleanup cannot delete the source mid-copy.
            _services.BeginImportSession();
            importLease = true;
            var request = CaptureSaveRequest();
            var result = await _services.Persist(request);
            AdvanceSharedBaseline(request);
            CompletePersistedSave(result);
            await _services.ApplySteamInput(result.Config);
            Raise(nameof(SteamInputShimStatusText));
            StatusText = $"Saved {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Report("Saving settings failed", ex);
            StatusText = $"Save failed: {ex.Message}";
        }
        finally
        {
            if (importLease)
            {
                _services.EndImportSession();
            }

            IsSaving = false;
        }
    }

    /// <summary>Applies an immutable UI-thread snapshot while retaining runtime-owned state.</summary>
    internal static AppConfig ApplyCapturedValues(
        AppConfig fresh,
        SaveRequest request,
        SplashConfig preparedSplash)
    {
        var config = request.Values;

        // CaptureSaveRequest applies every Settings-owned value once, on the UI thread. Start with
        // that complete snapshot here, then restore the state that other runtime surfaces may have
        // changed while the window was open.
        config.PluginConfigurations = fresh.PluginConfigurations;
        config.PluginInstances = fresh.PluginInstances;
        config.KeepEjectedCardTabs = fresh.KeepEjectedCardTabs;
        config.CardLibraries = fresh.CardLibraries;
        config.ForgottenInsertedCardIds = fresh.ForgottenInsertedCardIds;
        config.CustomTabs = fresh.CustomTabs;
        config.LibraryTabOrder = fresh.LibraryTabOrder;
        config.HiddenNativeTabs = fresh.HiddenNativeTabs;
        config.KnownNativeTabs = fresh.KnownNativeTabs;

        // Settings is the only editor of every artwork field, so the captured values are written
        // whole. Starting from the shell's instance rather than the snapshot's keeps any field
        // added later from being reverted here by accident.
        var artwork = fresh.Artwork;
        artwork.SteamGridDbApiKey = config.Artwork.SteamGridDbApiKey;
        artwork.ScreenscraperEnabled = config.Artwork.ScreenscraperEnabled;
        artwork.ScreenscraperUser = config.Artwork.ScreenscraperUser;
        artwork.ScreenscraperUserPassword = config.Artwork.ScreenscraperUserPassword;
        artwork.TabOrder = config.Artwork.TabOrder;
        artwork.DefaultTab = config.Artwork.DefaultTab;
        artwork.ShowGrid = config.Artwork.ShowGrid;
        artwork.ShowWide = config.Artwork.ShowWide;
        artwork.ShowHero = config.Artwork.ShowHero;
        artwork.ShowLogo = config.Artwork.ShowLogo;
        artwork.ShowIcon = config.Artwork.ShowIcon;
        artwork.ShowManage = config.Artwork.ShowManage;
        config.Artwork = artwork;
        config.LaunchWrappers = fresh.LaunchWrappers;
        config.SteamDelayMs = fresh.SteamDelayMs;
        config.SteamAutostartDisabled = fresh.SteamAutostartDisabled;
        config.ExplorerLogonSettleMs = fresh.ExplorerLogonSettleMs;
        config.QuickAccessPins = fresh.QuickAccessPins;
        config.PluginWidgetPins = fresh.PluginWidgetPins;
        config.LastSelectedPowerSchemeId = fresh.LastSelectedPowerSchemeId;
        config.SavedDisplayScaleEntries = fresh.SavedDisplayScaleEntries;
        config.GameModeLaunchRecovery = fresh.GameModeLaunchRecovery;
        config.PreviousShellValue = fresh.PreviousShellValue;
        config.PreviousShellSnapshotCaptured = fresh.PreviousShellSnapshotCaptured;
        config.PreviousShellValueExists = fresh.PreviousShellValueExists;
        config.PreviousShellValueKind = fresh.PreviousShellValueKind;
        config.PreviousStartupToGamingHomeValue = fresh.PreviousStartupToGamingHomeValue;
        config.PreviousStartupToGamingHomeSnapshotCaptured = fresh.PreviousStartupToGamingHomeSnapshotCaptured;
        config.PreviousStartupToGamingHomeValueExists = fresh.PreviousStartupToGamingHomeValueExists;
        config.PreviousStartupToGamingHomeValueKind = fresh.PreviousStartupToGamingHomeValueKind;
        config.PreviousUacSnapshotCaptured = fresh.PreviousUacSnapshotCaptured;
        config.PreviousUacConsentPrompt = fresh.PreviousUacConsentPrompt;
        config.PreviousUacSecureDesktop = fresh.PreviousUacSecureDesktop;
        config.PreviousLockOnWakeSnapshotCaptured = fresh.PreviousLockOnWakeSnapshotCaptured;
        config.PreviousNoLockScreen = fresh.PreviousNoLockScreen;
        config.PreviousConsoleLockSchemeValues = fresh.PreviousConsoleLockSchemeValues;
        config.PreviousConsoleLockPolicyKeyExisted = fresh.PreviousConsoleLockPolicyKeyExisted;
        config.PreviousConsoleLockPolicyAc = fresh.PreviousConsoleLockPolicyAc;
        config.PreviousConsoleLockPolicyDc = fresh.PreviousConsoleLockPolicyDc;

        // WSGM's page in Steam writes these too, while this window may be open. A field the user did
        // not change here keeps whatever is saved now, rather than the value this window loaded.
        foreach (var field in SharedFields.Where(field => !request.SharedEdits.Contains(field.Name)))
        {
            field.Copy(config, fresh);
        }

        foreach (var edit in request.CommonPluginEdits)
        {
            config.PluginInstances.RemoveAll(entry =>
                entry.PluginId == edit.PluginId && entry.InstanceId == edit.InstanceId);
            config.PluginInstances.Add(new CommonPluginInstanceConfig
                { PluginId = edit.PluginId, InstanceId = edit.InstanceId, Enabled = edit.Enabled });
        }

        // Preserve display facts discovered since the editor opened without resurrecting displays
        // the user explicitly forgot.
        foreach (var discovered in fresh.GameModeLaunch.KnownDisplays)
        {
            if (discovered.Target is not { } identity ||
                request.ForgottenDisplays.Any(target => target.Matches(identity)))
            {
                continue;
            }

            var edited = config.GameModeLaunch.KnownDisplays.FirstOrDefault(display =>
                display.Target?.Matches(identity) is true);
            if (edited is null)
            {
                config.GameModeLaunch.KnownDisplays.Add(discovered);
            }
            else
            {
                edited.Modes = [.. edited.Modes.Concat(discovered.Modes).Distinct()];
                edited.HdrSupported |= discovered.HdrSupported;
                edited.MaximumDpiPercent = Math.Max(edited.MaximumDpiPercent, discovered.MaximumDpiPercent);
            }
        }

        var editedDevice = config.DeviceIntegration;
        var editedGlobalTarget = config.Profiles.Global.ControllerTarget;
        config.DeviceIntegration = fresh.DeviceIntegration;
        // Profiles belong to the running shell, which saves them from the overlay and Steam while
        // Settings is open. Only the Global controller target is edited here.
        config.Profiles = fresh.Profiles;
        config.DeviceIntegration.Enabled = editedDevice.Enabled;
        config.DeviceIntegration.ControllerManagementEnabled = editedDevice.ControllerManagementEnabled;
        config.DeviceIntegration.MotionStream = editedDevice.MotionStream;
        config.DeviceIntegration.KeepGuideChordEdits = editedDevice.KeepGuideChordEdits;
        if (request.AutoTdpEdited)
        {
            config.DeviceIntegration.AutoTdpEnabled = editedDevice.AutoTdpEnabled;
        }

        if (request.ControllerTargetEdited)
        {
            config.Profiles.Global.ControllerTarget = editedGlobalTarget;
        }

        if (request.GlyphSelectionEdited)
        {
            config.DeviceIntegration.GlyphSelection = editedDevice.GlyphSelection;
        }

        if ((request.PluginEdits.Count > 0 || request.DeviceProfiles is not null)
            && request.PluginDevice.Length > 0
            && request.PluginId.Length > 0)
        {
            var scope = FindOrAddSaveScope(config, request.PluginDevice, request.PluginId);
            foreach (var (settingId, value) in request.PluginEdits)
            {
                var entry = scope.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.SettingId, settingId, StringComparison.Ordinal));
                if (entry is null)
                {
                    entry = new PluginSettingValue { SettingId = settingId };
                    scope.Values.Add(entry);
                }

                entry.Boolean = value.Kind is CapabilityValueKind.Boolean ? value.BooleanValue : null;
                entry.Integer = value.Kind is CapabilityValueKind.Integer ? value.IntegerValue : null;
                entry.Choice = value.Kind is CapabilityValueKind.Choice ? value.ChoiceValue : null;
                entry.Color = value.Kind is CapabilityValueKind.Color ? value.ColorValue : null;
                entry.Text = value.Kind is CapabilityValueKind.Text ? value.TextValue : null;
            }

            if (request.DeviceProfiles is not null)
            {
                // A deleted profile is also removed from every layer that selected it, so that layer
                // falls back to the one below instead of naming nothing.
                foreach (var removed in scope.Profiles.Select(profile => profile.ProfileId)
                             .Except(request.DeviceProfiles.Select(profile => profile.ProfileId),
                                 StringComparer.Ordinal).ToArray())
                {
                    ProfileEdits.RemoveFanCurveReferences(config.Profiles, removed);
                }

                scope.Profiles = [.. request.DeviceProfiles];
            }
        }

        config.Splash = preparedSplash;
        return config;
    }

    private static PluginSettingsScope FindOrAddSaveScope(
        AppConfig config,
        string deviceDefinitionId,
        string pluginId)
    {
        var scope = config.DeviceIntegration.PluginSettings.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceDefinitionId, deviceDefinitionId, StringComparison.Ordinal)
            && string.Equals(candidate.PluginId, pluginId, StringComparison.Ordinal));
        if (scope is not null)
        {
            return scope;
        }

        scope = new PluginSettingsScope
        {
            DeviceDefinitionId = deviceDefinitionId,
            PluginId = pluginId
        };
        config.DeviceIntegration.PluginSettings.Add(scope);
        return scope;
    }

    private static SaveResult PersistSave(SaveRequest request)
    {
        // Copy the picked splash images into the stable per-user splash directory
        // FIRST, and deliberately OUTSIDE the cross-process config lock. Two-phase on
        // purpose: the copies are staged as uniquely named sidecars and only replace
        // the live files after the config write succeeded — a failed save must never
        // leave the still-persisted OLD config pointing at already-replaced images.
        //
        // Why outside the lock: a picked or imported image can be tens of megabytes,
        // while ConfigStore's mutex timeout is 2 s, sized for one small JSON write.
        // Holding the lock across the copy would time every other WSGM process out
        // (the shell's config FileSystemWatcher → Load, the elevated one-shots) and
        // print "Config mutex timed out — proceeding without cross-process lock" on
        // the primary remote-diagnosis surface, which is both log noise and real
        // unserialized access. Staging is safe unlocked because it touches no live
        // file and every sidecar name carries its own GUID (see SplashAssets), so two
        // concurrent savers can no longer collide while staging.
        var splash = request.Splash;
        using var splashAssets = SplashAssets.Prepare(splash);

        AppConfig config;
        IReadOnlyList<string> failedSlots;
        string? failure;
        // The lock now covers exactly four fast operations, and nothing else:
        //   Mutate → Commit → (repair Save) → boot-manifest write.
        // That is sufficient because
        //   (a) Mutate IS the read-modify-write this merge exists for — another
        //       process must not persist between our read and our write, and its
        //       strict load makes an unreadable config.json abort the save instead of
        //       replacing the registry recovery snapshots with defaults;
        //   (b) Save and Commit stay in ONE scope, so a concurrent saver can never
        //       interleave between the config write and the image promotion it
        //       describes: whoever holds the lock last leaves config.json and the
        //       live images agreeing (the round-3 invariant);
        //   (c) boot.json is a projection of the config we just persisted, so it is
        //       written before another saver can change config.json underneath it.
        // LoadForMutation and Save re-acquire the same named mutex inside this scope. The
        // thread-local lock depth balances those nested acquisitions while the outer hold survives.
        using (ConfigStore.AcquireLock())
        {
            // Captured BEFORE ApplyTo overwrites them: if a staged copy cannot be
            // promoted the persisted config has to go back to the path whose file is
            // actually there.
            var previousLogoPath = "";
            var previousBackgroundPath = "";
            // Any throw from here to Commit leaves the transaction uncommitted, and the
            // enclosing `using` rolls it back: the live splash assets stay untouched.
            var fresh = ConfigStore.LoadForMutation();
            previousLogoPath = fresh.Splash.LogoImagePath;
            previousBackgroundPath = fresh.Splash.BackgroundImagePath;
            config = ApplyCapturedValues(fresh, request, splash);
            ConfigStore.Save(config);
            failedSlots = splashAssets.Commit();
            // A slot that could not be promoted (locked file, AV hold, permissions)
            // leaves the just-persisted path pointing at an image that was never
            // written; a slot whose STAGING already failed leaves it pointing at the
            // user's volatile pick (Downloads, a removable drive) instead of a copy
            // WSGM owns. Commit reports both: repair the persisted state, then fail
            // the save — a save that did neither must never log "Settings saved."
            // (A staging failure is therefore written once and immediately corrected,
            // both inside this lock, rather than getting its own earlier repair pass:
            // one reported-failure path is worth more than one avoided write.)
            failure = RestoreSlotsThatFailedToPromote(
                config, failedSlots, previousLogoPath, previousBackgroundPath, ConfigStore.Save);
            // Keep the logon service's view in sync — every save may change the
            // enabled flag or the elevation inputs (elevated startup apps).
            BootManifestWriter.WriteCurrent(config);
        }

        return new SaveResult(config, failedSlots, failure);
    }

    private void CompletePersistedSave(SaveResult result)
    {
        foreach (var row in CommonPlugins)
        {
            row.AcceptSaved();
        }

        AdoptMaterializedPaths(result.Config.Splash, result.FailedSlots);
        // Re-color the running UI live; Application.Current is null in unit tests.
        if (Application.Current is { } app)
        {
            AccentPalette.Apply(app, AccentPalette.Parse(result.Config.AccentColor));
        }

        if (result.Failure is not null)
        {
            // Everything else was persisted and applied — but the save did not do what
            // it said, so SaveCommand must report "Save failed", never "Saved".
            throw new IOException(result.Failure);
        }

        _services.Report("Settings saved.", null);
    }

    /// <summary>
    ///     Brings Steam's directory in line with the setting that was just
    ///     persisted.
    /// </summary>
    /// <remarks>
    ///     Deployment follows persisted intent and never precedes it: a save that failed
    ///     must not leave Steam's directory describing a setting nobody wrote. It also
    ///     runs outside <c>ConfigStore.AcquireLock</c> - that lock's timeout is sized for
    ///     one small JSON write, not for file copies into Program Files.
    /// </remarks>
    private static void ApplySteamInputManagementAfterSave(AppConfig config)
    {
        SteamInputManagement.Apply(config, "settings-save");
        ApplySteamAutostartAfterSave(config);
    }

    /// <summary>
    ///     Turns Windows' own Steam startup entries off once the takeover has been persisted.
    ///     Like the shim deployment, it follows persisted intent and runs outside the config lock: a
    ///     machine-scope entry needs an elevation prompt, which has no business inside it.
    /// </summary>
    /// <param name="config">The configuration that was just written.</param>
    private static void ApplySteamAutostartAfterSave(AppConfig config)
    {
        if (!config.SteamAutostartTakeoverAccepted)
        {
            return;
        }

        try
        {
            var enabled = SteamAutostartService.Scan().Where(source => source.Enabled).ToArray();
            if (enabled.Length == 0)
            {
                return;
            }

            var result = SteamAutostartService.Apply(enabled, true);
            if (!result.Complete)
            {
                Log.Warn("Steam autostart takeover incomplete: Windows may still start Steam itself.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Steam autostart takeover failed: {ex.Message}");
        }
    }

    private static bool Failed(IReadOnlyList<string> failedSlots, string slot)
    {
        return failedSlots.Contains(slot, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Syncs the editor back to the materialized copies — only once they ARE
    ///     the live files: keeping the originally picked paths would re-copy on every save
    ///     and, if the source vanished, clobber the stable copy's path with a dead one on
    ///     the next save.
    ///     <para>
    ///         A FAILED slot is skipped on purpose. Whether the sidecar could not be
    ///         staged (unreadable source, uncreatable target) or not promoted (locked live
    ///         file), config.json keeps the conservative PREVIOUS path while the view model
    ///         keeps the user's PICK, so pressing Save again after fixing the file actually
    ///         retries that image instead of silently re-saving the old one.
    ///     </para>
    /// </summary>
    /// <param name="persisted">
    ///     The splash section as it was just persisted (its paths
    ///     are the materialized ones for every slot that went live).
    /// </param>
    /// <param name="failedSlots">The slot names reported by the splash-asset commit.</param>
    internal void AdoptMaterializedPaths(SplashConfig persisted, IReadOnlyList<string> failedSlots)
    {
        if (!Failed(failedSlots, SplashAssets.LogoSlot))
        {
            SplashLogoPath = persisted.LogoImagePath;
        }

        if (!Failed(failedSlots, SplashAssets.BackgroundSlot))
        {
            SplashBackgroundImagePath = persisted.BackgroundImagePath;
        }
    }

    /// <summary>
    ///     Puts the previously persisted image path back for every slot that did
    ///     not end up as a live copy — staging failed, or the staged copy could not be
    ///     promoted — so the persisted state always names an image WSGM owns and that
    ///     exists. Pure: it only mutates <paramref name="config" /> and builds the
    ///     message — the caller performs the write (see
    ///     <see cref="RestoreSlotsThatFailedToPromote" />), so this step is testable without
    ///     going anywhere near the real per-user config file.
    /// </summary>
    /// <param name="config">The just-saved configuration, repaired in place.</param>
    /// <param name="failedSlots">The slot names reported by the splash-asset commit.</param>
    /// <param name="previousLogoPath">The logo path persisted before this save.</param>
    /// <param name="previousBackgroundPath">The background path persisted before this save.</param>
    /// <returns>The message to fail the save with, or null when every slot committed.</returns>
    internal static string? RepairSlotsThatFailedToPromote(
        AppConfig config,
        IReadOnlyList<string> failedSlots,
        string previousLogoPath,
        string previousBackgroundPath)
    {
        if (failedSlots.Count == 0)
        {
            return null;
        }

        foreach (var slot in failedSlots)
        {
            if (string.Equals(slot, SplashAssets.LogoSlot, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(
                    $"Splash logo image could not be updated — keeping the previously saved '{previousLogoPath}'.");
                config.Splash.LogoImagePath = previousLogoPath;
            }
            else if (string.Equals(slot, SplashAssets.BackgroundSlot, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(
                    $"Splash background image could not be updated — keeping the previously saved '{previousBackgroundPath}'.");
                config.Splash.BackgroundImagePath = previousBackgroundPath;
            }
        }

        // One message for both halves of the transaction (see SplashAssets.Commit):
        // the copy into WSGM's splash folder failed, or the finished copy could not
        // replace the live file. The user's action is the same either way.
        return $"splash image not updated ({string.Join(", ", failedSlots)}) — "
               + "the picked image could not be copied into WSGM's splash folder, or the live file "
               + "is in use or not writable. The previous image is still configured, and your pick "
               + "is kept: fix the file and press Save again to retry.";
    }

    /// <summary>
    ///     Repairs the config for every slot whose staged copy could not be
    ///     promoted and re-persists it through <paramref name="save" />.
    /// </summary>
    /// <param name="config">The just-saved configuration, repaired in place.</param>
    /// <param name="failedSlots">The slot names reported by the splash-asset commit.</param>
    /// <param name="previousLogoPath">The logo path persisted before this save.</param>
    /// <param name="previousBackgroundPath">The background path persisted before this save.</param>
    /// <param name="save">Writes the repaired configuration (ConfigStore.Save in production).</param>
    /// <returns>
    ///     The message to fail the save with, or null when every slot committed.
    ///     A failing repair write does NOT replace it: the promotion failure is the cause
    ///     the user has to act on, and letting the secondary write's exception escape would
    ///     mask it — so that one is logged instead.
    /// </returns>
    internal static string? RestoreSlotsThatFailedToPromote(
        AppConfig config,
        IReadOnlyList<string> failedSlots,
        string previousLogoPath,
        string previousBackgroundPath,
        Action<AppConfig> save)
    {
        var failure = RepairSlotsThatFailedToPromote(
            config, failedSlots, previousLogoPath, previousBackgroundPath);
        if (failure is null)
        {
            return null;
        }

        try
        {
            // Still inside the caller's config lock.
            save(config);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't re-save the config after a failed splash image promotion", ex);
        }

        return failure;
    }

    /// <summary>
    ///     Builds an isolated configuration snapshot for the window's local
    ///     overlay/taskbar preview surfaces, carrying every unsaved edit.
    /// </summary>
    /// <returns>A copy that will not change when this view model is later saved.</returns>
    public AppConfig SnapshotForPreview()
    {
        var snapshot = ConfigStore.CloneJson(_config, ConfigJsonContext.Default.AppConfig);
        ApplyTo(snapshot);
        // A real copy through the production JSON contract: the preview's
        // OverlayController must not see later Save() mutations of the live
        // _config outside its ApplyConfig wholesale-replace contract.
        return ConfigStore.CloneJson(snapshot, ConfigJsonContext.Default.AppConfig);
    }

    /// <summary>Moves the baseline to what this window just saved.</summary>
    /// <param name="request">The save that was persisted.</param>
    /// <remarks>
    ///     The window's own values, not the merged result. For a field the user did not change here, the
    ///     merged result holds what another surface saved while this window kept showing its own; taking
    ///     that as the baseline would make the unchanged field look edited on the next save, which would
    ///     then write the window's stale value over the other surface's change.
    /// </remarks>
    internal void AdvanceSharedBaseline(SaveRequest request)
    {
        foreach (var (name, value) in request.SharedValues)
        {
            _sharedBaseline[name] = value;
        }
    }

    private void RecordSharedBaseline(AppConfig config)
    {
        foreach (var field in SharedFields)
        {
            _sharedBaseline[field.Name] = field.Read(config);
        }
    }

    /// <summary>One action a running plugin instance offers, for the action lists.</summary>
    /// <param name="Identity">The plugin instance.</param>
    /// <param name="Action">The declared action.</param>
    /// <param name="Label">How to name it in a picker.</param>
    public sealed record PluginActionOption(
        PluginInstanceIdentity Identity,
        PluginAction Action,
        string Label)
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return Label;
        }
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
        Func<UpdateState>? ReadUpdates = null)
    {
        internal static SettingsServices Windows()
        {
            return new SettingsServices(
                () => OperatingSystem.IsWindows()
                    ? DisplayLayouts.Observe()
                    : new DisplayArrangement([], "", DateTimeOffset.UtcNow),
                static target => OperatingSystem.IsWindows() ? ReadWindowsDisplayFacts(target) : null,
                SettingsPluginActions.Read,
                KnownStartupApps.Detected,
                SplashTheme.BeginImportSession, SplashTheme.EndImportSession,
                request => Task.Run(() => PersistSave(request)),
                config => Task.Run(() => ApplySteamInputManagementAfterSave(config)),
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
                sources => SteamAutostartService.Apply(sources, true),
                // Core Audio, off the dispatcher. A test supplies its own so it reads a fixture
                // rather than whatever this machine has plugged in.
                AudioDiscovery.Read,
                // The last update check, from the user's profile; a test that omits it sees none.
                () => UpdateChecker.ReadState());
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

    /// <summary>One field another surface can write while this window is open.</summary>
    /// <param name="Name">Its name in the save request.</param>
    /// <param name="Read">Reads it, boxed so fields of any kind compare alike.</param>
    /// <param name="Copy">Copies it from the saved configuration to the one being written.</param>
    private sealed record SharedField(string Name, Func<AppConfig, object> Read, Action<AppConfig, AppConfig> Copy);

    internal sealed record SaveRequest(
        AppConfig Values,
        SplashConfig Splash,
        IReadOnlyDictionary<string, CapabilityValue> PluginEdits,
        IReadOnlyList<DeviceAuthoredProfile>? DeviceProfiles,
        string PluginDevice,
        string PluginId,
        bool AutoTdpEdited,
        bool ControllerTargetEdited,
        bool GlyphSelectionEdited)
    {
        internal IReadOnlyList<DisplayTargetIdentity> ForgottenDisplays { get; init; } = [];

        /// <summary>The shared fields the user changed in this window; the rest keep the saved value.</summary>
        internal IReadOnlyList<string> SharedEdits { get; init; } = [];

        /// <summary>This window's value of every shared field, the baseline once the save succeeds.</summary>
        internal IReadOnlyDictionary<string, object> SharedValues { get; init; } =
            new Dictionary<string, object>(StringComparer.Ordinal);

        internal IReadOnlyList<CommonPluginInstanceConfig> CommonPluginEdits { get; init; } = [];
    }

    internal sealed record SaveResult(
        AppConfig Config,
        IReadOnlyList<string> FailedSlots,
        string? Failure);

    /// <summary>
    ///     Builds the view model over an ALREADY LOADED configuration instead of
    ///     reading <c>%LOCALAPPDATA%\WSGM\config.json</c>. Tests must use this overload: the
    ///     parameterless constructor's <see cref="ConfigStore.Load" /> reads the developer's
    ///     real config, and its corrupt-file branch writes <c>config.bad.json</c> next to it,
    ///     so merely constructing the view model touches the real per-user directory.
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

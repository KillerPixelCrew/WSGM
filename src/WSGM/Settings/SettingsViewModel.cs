using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Input;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using WSGM.Themes;

namespace WSGM.Settings;

/// <summary>Editable settings for one program launched after the shell starts.</summary>
public sealed class StartupAppRow : INotifyPropertyChanged
{
    /// <summary>Raised after an editable startup-app field changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    private string _path = "";
    private string _args = "";
    private bool _enabled = true;
    private bool _elevated;
    private bool _autoRelaunch;

    /// <summary>Gets or sets the executable or protocol to launch.</summary>
    public string Path { get => _path; set { _path = value; Raise(nameof(Path)); } }

    /// <summary>Gets or sets the command-line arguments passed to the program.</summary>
    public string Args { get => _args; set { _args = value; Raise(nameof(Args)); } }

    /// <summary>Gets or sets whether this program participates in startup.</summary>
    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }

    /// <summary>Gets or sets whether the program needs an elevated launch.</summary>
    public bool Elevated { get => _elevated; set { _elevated = value; Raise(nameof(Elevated)); } }

    /// <summary>Gets or sets whether the program is watched and restarted when it exits.</summary>
    public bool AutoRelaunch { get => _autoRelaunch; set { _autoRelaunch = value; Raise(nameof(AutoRelaunch)); } }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Binds persisted shell, startup, input, and display settings to the Settings window.</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    /// <summary>Raised after a settings value or dependent display value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly AppConfig _config;
    private readonly SettingsServices _services;

    /// <summary>One action a running plugin instance offers, for the action lists.</summary>
    /// <param name="Identity">The plugin instance.</param>
    /// <param name="Action">The declared action.</param>
    /// <param name="Label">How to name it in a picker.</param>
    public sealed record PluginActionOption(
        PluginInstanceIdentity Identity, PluginAction Action, string Label)
    {
        /// <inheritdoc />
        public override string ToString() => Label;
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
        Func<ModernStandbyReport> ReadStandby)
    {
        internal static SettingsServices Windows(SettingsViewModel owner) => new(
            () => OperatingSystem.IsWindows()
                ? DisplayLayouts.Observe()
                : new([], "", DateTimeOffset.UtcNow),
            static target => OperatingSystem.IsWindows() ? ReadWindowsDisplayFacts(target) : null,
            SettingsPluginActions.Read,
            KnownStartupApps.Detected,
            SplashTheme.BeginImportSession, SplashTheme.EndImportSession,
            request => Task.Run(() => PersistSave(request)),
            config => Task.Run(() => owner.ApplySteamInputManagementAfterSave(config)),
            (message, error) => { if (error is null) { Log.Info(message); } else { Log.Error(message, error); } },
            // Windows' account of the last standby. Injected so a preview or a test renders a fixed
            // report instead of whatever this machine did last night.
            ModernStandbyDiagnostics.Read);

        /// <summary>Asks one active display what it supports, so the answers can be remembered and
        /// offered again after it is unplugged. Every query is optional: a display that refuses one
        /// of them still contributes the rest.</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static DisplayCatalogFacts ReadWindowsDisplayFacts(DisplayTargetIdentity target)
        {
            IReadOnlyList<DisplayMode> modes = DisplayModes.Read(target)?.Supported ?? [];
            bool hdr = DisplayColor.TryReadHdr(target, out _, out bool supported) && supported;
            int maximum = DisplayScaling.TryReadRange(target, out _, out _, out int highest) ? highest : 0;
            return new(modes, hdr, maximum);
        }
    }

    private bool _isSaving;

    /// <summary>Gets whether an asynchronous save is currently persisting its captured
    /// settings snapshot. The window disables every editor for this interval so it
    /// cannot acknowledge changes that were made after the snapshot was taken.</summary>
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (_isSaving == value)
            {
                return;
            }

            _isSaving = value;
            Raise(nameof(IsSaving));
        }
    }

    /// <summary>Loads the current configuration and discovers locally installed startup suggestions.</summary>
    public SettingsViewModel()
        : this(ConfigStore.Load(), ReadInstalledPluginId(), filterToInstalledPlugin: true)
    {
        LoadCommonPlugins(CommonPluginCatalog.Discover(CommonPluginCatalog.InstalledRoot));
    }

    /// <summary>Installed common integrations and configured instances, independent of Device integration.</summary>
    public ObservableCollection<CommonPluginInstanceRow> CommonPlugins { get; } = [];

    /// <summary>Metadata discovery failures; discovery never executes plugin code.</summary>
    public string CommonPluginDiscoveryError { get; private set; } = "";

    internal void LoadCommonPlugins(CommonPluginCatalog catalog)
    {
        CommonPlugins.Clear();
        foreach (var package in catalog.Packages)
        {
            var configured = _config.PluginInstances.Where(entry => entry.PluginId == package.Manifest.Id).ToArray();
            if (configured.Length == 0)
            {
                CommonPlugins.Add(new(package.Manifest.Id, "default", package.Manifest.Name, false, true));
            }
            foreach (var instance in configured)
            {
                CommonPlugins.Add(new(instance.PluginId, instance.InstanceId, package.Manifest.Name, instance.Enabled, true));
            }
        }
        foreach (var instance in _config.PluginInstances.Where(entry => !catalog.Packages.Any(package => package.Manifest.Id == entry.PluginId)))
        {
            CommonPlugins.Add(new(instance.PluginId, instance.InstanceId, instance.PluginId, instance.Enabled, false));
        }
        CommonPluginDiscoveryError = string.Join(Environment.NewLine, catalog.Errors);
        Raise(nameof(CommonPluginDiscoveryError));
    }

    /// <summary>Builds the view model over an ALREADY LOADED configuration instead of
    /// reading <c>%LOCALAPPDATA%\WSGM\config.json</c>. Tests must use this overload: the
    /// parameterless constructor's <see cref="ConfigStore.Load"/> reads the developer's
    /// real config, and its corrupt-file branch writes <c>config.bad.json</c> next to it,
    /// so merely constructing the view model touches the real per-user directory.</summary>
    /// <param name="config">The configuration this view model edits. It is taken over,
    /// not copied — the save path re-loads and merges before persisting anyway.</param>
    internal SettingsViewModel(AppConfig config)
        : this(config, installedPluginId: null, filterToInstalledPlugin: false) { }

    /// <summary>Builds a testable settings model while selecting the named installed plugin.</summary>
    /// <param name="config">The configuration this view model edits.</param>
    /// <param name="installedPluginId">Installed package ID, or null when the slot is empty or invalid.</param>
    internal SettingsViewModel(AppConfig config, string? installedPluginId)
        : this(config, installedPluginId, filterToInstalledPlugin: true) { }

    internal SettingsViewModel(
        AppConfig config,
        string? installedPluginId,
        bool filterToInstalledPlugin,
        SettingsServices? services = null)
    {
        _services = services ?? SettingsServices.Windows(this);
        SaveCommand = new AsyncRelayCommand(SaveWithStatusAsync);
        OpenLogLocationCommand = new RelayCommand(OpenLogLocation);
        TakeOverSteamAutostartCommand = new RelayCommand(TakeOverSteamAutostart);
        GameLayout = new DisplayLayoutEditor(RefreshLaunchSummary);
        DesktopLayout = new DisplayLayoutEditor(RefreshLaunchSummary);
        ActionLists =
        [
            new("Entering Game Mode", RefreshLaunchSummary),
            new("Leaving Game Mode", RefreshLaunchSummary),
            new("Desktop startup", RefreshLaunchSummary),
            new("Desktop wake", RefreshLaunchSummary),
        ];
        AddActionStepCommand = new RelayCommand<PluginActionListEditor>(list => list?.Add());
        RemoveActionStepCommand = new RelayCommand<PluginActionStepEditorRow>(RemoveActionStep);
        MoveActionStepUpCommand = new RelayCommand<PluginActionStepEditorRow>(row => MoveActionStep(row, -1));
        MoveActionStepDownCommand = new RelayCommand<PluginActionStepEditorRow>(row => MoveActionStep(row, +1));
        SnapshotGameLayoutCommand = new RelayCommand(() => SnapshotLayout(desktop: false));
        SnapshotDesktopLayoutCommand = new RelayCommand(() => SnapshotLayout(desktop: true));
        ForgetDisplayCommand = new RelayCommand<DisplayLayoutEditorRow>(ForgetDisplay);
        RebindDisplayCommand = new RelayCommand<DisplayLayoutEditorRow>(RebindDisplay);
        RemoveAppCommand = new RelayCommand<StartupAppRow>(row =>
        {
            if (row is not null)
            {
                StartupApps.Remove(row);
            }
        });
        MoveUpCommand = new RelayCommand<StartupAppRow>(row => MoveStartupApp(row, -1));
        MoveDownCommand = new RelayCommand<StartupAppRow>(row => MoveStartupApp(row, +1));

        // Normalize so an injected bare AppConfig gets the same non-null nested
        // sections (and clamped splash numbers) the load path guarantees.
        _config = ConfigStore.Normalize(config);
        LoadPluginSettings(_config, installedPluginId, filterToInstalledPlugin);

        SteamAutoRelaunch = _config.SteamAutoRelaunch;
        SteamLaunchUnelevated = _config.SteamLaunchUnelevated;
        SteamGridDbApiKey = _config.SteamGridDbApiKey;
        ScreenscraperEnabled = _config.ScreenscraperEnabled;
        ScreenscraperDevId = _config.ScreenscraperDevId;
        ScreenscraperDevPassword = _config.ScreenscraperDevPassword;
        ScreenscraperUser = _config.ScreenscraperUser;
        ScreenscraperUserPassword = _config.ScreenscraperUserPassword;
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
        DeviceIntegrationEnabled = _config.DeviceIntegration.Enabled;
        DeviceControllerManagementEnabled = _config.DeviceIntegration.ControllerManagementEnabled;
        DeviceControllerTargetIndex = (int)_config.DeviceIntegration.ControllerTarget;
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
        CefArtwork = _config.Cef.Artwork;
        CefWifiIndicator = _config.Cef.WifiIndicator;
        CefNativeQuickAccess = _config.Cef.NativeQuickAccess;
        CefDownloadKeepAwake = _config.Cef.DownloadKeepAwake;
        CefDownloadQueueSort = _config.Cef.DownloadQueueSort;
        CefConnectedLibraryCarousel = _config.Cef.ConnectedLibraryCarousel;
        CefCarouselShowUninstalled = _config.Cef.CarouselShowUninstalled;
        MuteWhileDisplayOff = _config.MuteWhileDisplayOff;
        ResuspendUnexplainedWakes = _config.ResuspendUnexplainedWakes;
        ModernStandbyReport standby = _services.ReadStandby();
        ModernStandbyStatusText = standby.ArmedWakeSources.Count == 0
            ? standby.Summary
            : $"{standby.Summary} Allowed to wake it: {string.Join(", ", standby.ArmedWakeSources)}.";
        VerboseLogging = _config.LogVerbosity == LogVerbosity.Verbose;
        _hotkey = _config.Hotkey;
        _chord = _config.GamepadChord;
        GestureBottom = _config.Gestures.BottomEdge;
        GestureTop = _config.Gestures.TopEdge;
        GestureLeftSteamMenu = _config.Gestures.LeftEdgeSteamMenu;
        GestureRightSteamQuickAccess = _config.Gestures.RightEdgeSteamQuickAccess;
        GlyphStyleIndex = (int)_config.GlyphStyle;
        AccentColorHex = _config.AccentColor;
        LoadSplash(_config.Splash);

        foreach (var app in _config.StartupApps)
        {
            StartupApps.Add(new StartupAppRow
            {
                Path = app.Path,
                Args = app.Args,
                Enabled = app.Enabled,
                Elevated = app.Elevated,
                AutoRelaunch = app.AutoRelaunch,
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

    // --- Commands (bound by the Settings pages; bodies stay on the named methods) ---
    /// <summary>Gets the command that merges and persists the edited settings,
    /// reporting the outcome (including the last-save time) via <see cref="StatusText"/>.</summary>
    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>Gets the command that reveals wsgm.log in Explorer.</summary>
    public RelayCommand OpenLogLocationCommand { get; }

    /// <summary>Gets the command that re-checks Windows' Steam startup entries and turns off any
    /// that came back. This configures how WSGM starts Steam, which is WSGM's own behavior; the
    /// exception for touching an external setting is recorded in <c>docs\decisions.md</c>.</summary>
    public RelayCommand TakeOverSteamAutostartCommand { get; }

    /// <summary>Gets the command that captures the current desktop as the Game Mode layout.</summary>
    public RelayCommand SnapshotGameLayoutCommand { get; }

    /// <summary>Gets the command that captures the current desktop as the Desktop layout.</summary>
    public RelayCommand SnapshotDesktopLayoutCommand { get; }

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

    /// <summary>The connected display a rebind will use, chosen from
    /// <see cref="RebindChoices"/>.</summary>
    public int RebindChoiceIndex
    {
        get => _rebindChoiceIndex;
        set { _rebindChoiceIndex = value; Raise(nameof(RebindChoiceIndex)); }
    }

    /// <summary>The displays a migrated row can be pointed at: the ones seen most recently.</summary>
    public ObservableCollection<string> RebindChoices { get; } = [];

    private int _rebindChoiceIndex;

    /// <summary>Gets the command that removes one startup-program row.</summary>
    public RelayCommand<StartupAppRow> RemoveAppCommand { get; }

    /// <summary>Gets the command that moves one startup-program row up.</summary>
    public RelayCommand<StartupAppRow> MoveUpCommand { get; }

    /// <summary>Gets the command that moves one startup-program row down.</summary>
    public RelayCommand<StartupAppRow> MoveDownCommand { get; }

    /// <summary>Edits the Game Mode layout.</summary>
    public DisplayLayoutEditor GameLayout { get; }

    /// <summary>Edits the Desktop layout leaving Game Mode restores.</summary>
    public DisplayLayoutEditor DesktopLayout { get; }

    /// <summary>The four action lists, in the order the page shows them.</summary>
    public IReadOnlyList<PluginActionListEditor> ActionLists { get; }

    /// <summary>Displays that can be chosen in the layout editor, present or remembered.</summary>
    public ObservableCollection<KnownDisplay> KnownDisplays { get; } = [];

    /// <summary>Sections the installed plugin declares, in render order.</summary>
    public ObservableCollection<PluginSettingSectionViewModel> PluginSettingSections { get; } = [];

    /// <summary>Whether the plugin settings page has anything to draw.</summary>
    public bool PluginSettingsAvailable => PluginSettingSections.Count > 0;

    /// <summary>Edits made on the plugin page, applied at save. Empty until the user changes one.</summary>
    private readonly Dictionary<string, CapabilityValue> _pluginSettingEdits =
        new(StringComparer.Ordinal);

    /// <summary>Authored fan and lighting profiles for the installed device.</summary>
    public ObservableCollection<DeviceProfileRowViewModel> DeviceProfiles { get; } = [];

    private DeviceProfileRowViewModel? _selectedDeviceProfile;

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

    private void OnSelectedDeviceProfileChanged(object? sender, PropertyChangedEventArgs e) =>
        _deviceProfilesEdited = true;

    /// <summary>Whether a profile is selected and the editor has something to draw.</summary>
    public bool HasSelectedDeviceProfile => _selectedDeviceProfile is not null;

    /// <summary>Whether the profile list was changed and should be written at save.</summary>
    /// <remarks>
    /// Tracked rather than always written, for the same reason the plugin settings are: a save
    /// triggered by an unrelated page must not overwrite what another process put there.
    /// </remarks>
    private bool _deviceProfilesEdited;

    /// <summary>Adds an empty fan curve the user can then shape.</summary>
    /// <param name="capabilityId">The capability the new profile authors.</param>
    /// <param name="color">Whether to author a colour rather than a curve.</param>
    /// <remarks>
    /// Seeded with two points at the ends rather than none. A curve needs at least two to be valid,
    /// and an editor opening on an empty plot gives the user nothing to grab.
    /// </remarks>
    internal void AddDeviceProfile(string capabilityId, bool color = false)
    {
        string id = $"profile-{Guid.NewGuid():N}"[..16];
        DeviceProfileRowViewModel row = new(new DeviceAuthoredProfile
        {
            ProfileId = id,
            Name = $"Profile {DeviceProfiles.Count + 1}",
            CapabilityId = capabilityId,
            // One or the other, never both: the capability being authored decides which, and a
            // profile carrying an unused half would let a capability change silently resurrect a
            // value the user set for something else.
            Curve = color
                ?
                []
                :
                [
                    new AuthoredCurvePoint { Input = 0, Output = 0 },
                    new AuthoredCurvePoint { Input = 100, Output = 100 },
                ],
            Color = color ? 0xFF9D3D : null,
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

        int index = DeviceProfiles.IndexOf(row);
        DeviceProfiles.Remove(row);
        _deviceProfilesEdited = true;
        SelectedDeviceProfile = DeviceProfiles.Count == 0
            ? null
            : DeviceProfiles[Math.Min(index, DeviceProfiles.Count - 1)];
    }

    /// <summary>Records that a profile changed.</summary>
    internal void NoteDeviceProfileEdited() => _deviceProfilesEdited = true;

    private string _pluginSettingsDevice = string.Empty;
    private string _pluginSettingsPlugin = string.Empty;

    private string _pluginSettingsEmptyReason =
        "No device plugin is installed, so there are no plugin settings to show.";

    /// <summary>
    /// Why the plugin settings page is empty.
    /// </summary>
    /// <remarks>
    /// Shown instead of a blank page. A plugin that declares no settings and a machine with no
    /// plugin at all look identical otherwise, and the user cannot tell whether something failed.
    /// </remarks>
    public string PluginSettingsEmptyReason
    {
        get => _pluginSettingsEmptyReason;
        set { _pluginSettingsEmptyReason = value; Raise(nameof(PluginSettingsEmptyReason)); }
    }

    /// <summary>Replaces the plugin settings page content.</summary>
    /// <param name="view">The projected sections and their settings, in draw order.</param>
    /// <param name="onEdited">Called with the setting id and new value after each edit.</param>
    /// <remarks>
    /// Rebuilt wholesale rather than reconciled in place: the manifest changes only when a plugin is
    /// installed or updated, so the simple path is also the correct one, and a partial reconcile
    /// would have to answer what happens to a row whose declared kind changed underneath it.
    /// <para>
    /// Section ids are kept on the section view models so the window's focus and scroll restoration
    /// still has a stable key after a rebuild.
    /// </para>
    /// </remarks>
    internal void SetPluginSettings(
        PluginSettingsView view,
        Action<string, CapabilityValue> onEdited)
    {
        ArgumentNullException.ThrowIfNull(onEdited);
        PluginSettingSections.Clear();
        foreach (PluginSettingSection section in view.Sections)
        {
            if (!view.Settings.TryGetValue(
                    section.SectionId,
                    out IReadOnlyList<PluginSettingView>? settings))
            {
                continue;
            }

            List<PluginSettingRowViewModel> rows = [];
            foreach (PluginSettingView setting in settings)
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
    /// Builds the plugin settings page from the most recently published declaration.
    /// </summary>
    /// <param name="config">The configuration to read the cache and the stored values from.</param>
    /// <param name="installedPluginId">Installed package ID, when discovery found one package.</param>
    /// <param name="filterToInstalledPlugin">Whether declarations from other package IDs are excluded.</param>
    /// <remarks>
    /// Settings does not activate device hardware, so the cached declaration is the only description
    /// of the plugin's settings available here. Stored values are still reconciled against it,
    /// because an older declaration can describe bounds the stored values no longer fit.
    /// <para>
    /// Exactly one scope is drawn — the one matching the installed plugin — and the reason is
    /// reported when none does, since a blank page cannot distinguish "no plugin" from "the page
    /// failed".
    /// </para>
    /// </remarks>
    private void LoadPluginSettings(
        AppConfig config,
        string? installedPluginId,
        bool filterToInstalledPlugin)
    {
        ArgumentNullException.ThrowIfNull(config);
        IEnumerable<PluginSettingsScope> candidates = config.DeviceIntegration.PluginSettings
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

        PluginSettingsScope? scope = candidates.LastOrDefault();
        if (scope?.Declaration is not { } declaration)
        {
            PluginSettingSections.Clear();
            PluginSettingsEmptyReason =
                "No device plugin has published settings yet. Start WSGM's shell once with the "
                + "plugin installed, then reopen Settings.";
            Raise(nameof(PluginSettingsAvailable));
            return;
        }

        PluginSettingsResolution resolution = PluginSettingsResolver.Resolve(
            declaration,
            scope.Values);
        foreach (EffectivePluginSetting rejected in resolution.Values
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
        try
        {
            InstalledDevicePackage? package = DevicePackagePolicy
                .Discover(DeviceInstallationPaths.InstalledPackageRoot)
                .InstalledPackage;
            return package is { Valid: true, Manifest: { } manifest }
                ? manifest.Id
                : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Plugin settings unavailable: installed package could not be inspected ({ex.Message}).");
            return null;
        }
    }

    private void LoadDeviceProfiles(PluginSettingsScope scope)
    {
        DeviceProfiles.Clear();
        foreach (DeviceAuthoredProfile profile in scope.Profiles)
        {
            DeviceProfiles.Add(new DeviceProfileRowViewModel(profile));
        }

        SelectedDeviceProfile = DeviceProfiles.FirstOrDefault();
        _deviceProfilesEdited = false;
    }

    /// <summary>Writes the authored profiles into the configuration being saved.</summary>
    /// <param name="config">The freshly loaded configuration the save is applied to.</param>
    /// <remarks>
    /// The whole list is replaced, not merged, because authoring is Settings-only (D22b) and this
    /// window holds the complete set — but only when the user actually changed something, so an
    /// unrelated save never overwrites profiles another process wrote.
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

    /// <summary>Finds this window's plugin-settings scope in the configuration being
    /// saved, adding it when a fresh load does not carry one yet.</summary>
    /// <param name="config">The freshly loaded configuration the save is applied to.</param>
    private PluginSettingsScope FindOrAddScope(AppConfig config)
    {
        List<PluginSettingsScope> scopes = config.DeviceIntegration.PluginSettings;
        PluginSettingsScope? scope = scopes.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceDefinitionId, _pluginSettingsDevice, StringComparison.Ordinal)
            && string.Equals(candidate.PluginId, _pluginSettingsPlugin, StringComparison.Ordinal));
        if (scope is null)
        {
            scope = new PluginSettingsScope
            {
                DeviceDefinitionId = _pluginSettingsDevice,
                PluginId = _pluginSettingsPlugin,
            };
            scopes.Add(scope);
        }

        return scope;
    }

    /// <summary>Writes the edited plugin settings into the configuration being saved.</summary>
    /// <param name="config">The freshly loaded configuration the save is applied to.</param>
    /// <remarks>
    /// Edits are recorded rather than written onto the configuration the page was built from,
    /// because the save re-reads configuration from disk and applies the view model onto THAT
    /// object — anything written to the loaded copy is discarded. It also means a setting the user
    /// never touched is left exactly as another process wrote it, instead of being rewritten with
    /// whatever this window happened to load.
    /// </remarks>
    internal void ApplyPluginSettingsTo(AppConfig config)
    {
        if (_pluginSettingEdits.Count == 0
            || _pluginSettingsDevice.Length == 0
            || _pluginSettingsPlugin.Length == 0)
        {
            return;
        }

        PluginSettingsScope scope = FindOrAddScope(config);
        foreach ((string settingId, CapabilityValue value) in _pluginSettingEdits)
        {
            PluginSettingValue? entry = scope.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.SettingId, settingId, StringComparison.Ordinal));
            if (entry is null)
            {
                entry = new PluginSettingValue { SettingId = settingId };
                scope.Values.Add(entry);
            }

            // Only the field matching the kind is written and the rest are cleared, so a setting
            // whose declared kind changed cannot leave a stale value of the old shape behind it.
            entry.Boolean = value.Kind is CapabilityValueKind.Boolean ? value.BooleanValue : null;
            entry.Integer = value.Kind is CapabilityValueKind.Integer ? value.IntegerValue : null;
            entry.Choice = value.Kind is CapabilityValueKind.Choice ? value.ChoiceValue : null;
            entry.Color = value.Kind is CapabilityValueKind.Color ? value.ColorValue : null;
            entry.Text = value.Kind is CapabilityValueKind.Text ? value.TextValue : null;
        }
    }

    /// <remarks>
    /// A custom title is plugin-supplied plain text, already bounded and validated by
    /// <see cref="PluginSettingSection"/>; it is rendered as text and never as markup. A keyed title
    /// is WSGM's, which is the entire reason the key exists.
    /// </remarks>
    private static string SectionTitle(PluginSettingSection section) =>
        section.Key is SettingSectionKey.Custom
            ? (section.CustomTitle ?? section.SectionId).ToUpperInvariant()
            : section.Key.ToString().ToUpperInvariant();

    private int _gameModeLaunchKindIndex;
    /// <summary>Selected <see cref="GameModeLaunchKind"/> index.</summary>
    public int GameModeLaunchKindIndex
    {
        get => _gameModeLaunchKindIndex;
        set
        {
            _gameModeLaunchKindIndex = value;
            Raise(nameof(GameModeLaunchKindIndex));
            Raise(nameof(ShowCustomLaunch));
        }
    }

    /// <summary>Whether the layout and wait fields apply to the selected launch kind.</summary>
    public bool ShowCustomLaunch => GameModeLaunchKindIndex == (int)GameModeLaunchKind.Custom;

    private int _gameModeReturnIndex;
    /// <summary>Selected <see cref="GameModeReturn"/> index.</summary>
    public int GameModeReturnIndex
    {
        get => _gameModeReturnIndex;
        set
        {
            _gameModeReturnIndex = value;
            Raise(nameof(GameModeReturnIndex));
            Raise(nameof(ShowDesktopLayout));
        }
    }

    /// <summary>Whether a Desktop layout is configured rather than captured at entry.</summary>
    public bool ShowDesktopLayout => GameModeReturnIndex == (int)GameModeReturn.DesktopLayout;

    private string _launchSummaryText = "";
    /// <summary>What the saved layouts describe, including anything needing confirmation.</summary>
    public string LaunchSummaryText
    {
        get => _launchSummaryText;
        private set { _launchSummaryText = value; Raise(nameof(LaunchSummaryText)); }
    }

    private int _waitForDisplayIndex;
    /// <summary>Index into <see cref="WaitForDisplayChoices"/>; zero means no wait.</summary>
    public int WaitForDisplayIndex
    {
        get => _waitForDisplayIndex;
        set { _waitForDisplayIndex = value; Raise(nameof(WaitForDisplayIndex)); }
    }

    /// <summary>"No display wait" followed by one entry per remembered display.</summary>
    public ObservableCollection<string> WaitForDisplayChoices { get; } = [];

    private string _statusText = "";

    /// <summary>Gets or sets the transient status line shown in the window's
    /// bottom strip: last-save time on success, otherwise the failure text.</summary>
    public string StatusText { get => _statusText; set { _statusText = value; Raise(nameof(StatusText)); } }

    /// <summary>Gets the compact logon-service state for the status strip,
    /// derived from the same flag the boot manifest is projected from.</summary>
    public string ServiceStateText => StartAtSignIn
        ? $"Sign-in start: {(StartModeIndex == (int)SessionStartMode.Desktop ? "Desktop" : "Game")}"
        : "Sign-in start: off";

    /// <summary>Gets the compact shell state for the status strip.</summary>
    public string ShellStateText => "Shell: Explorer";

    private string _steamAutostartStatusText = "";

    /// <summary>Gets what Windows would still start Steam from, refreshed on demand.</summary>
    public string SteamAutostartStatusText
    {
        get => _steamAutostartStatusText;
        private set { _steamAutostartStatusText = value; Raise(nameof(SteamAutostartStatusText)); }
    }

    /// <summary>Re-scans and, with the takeover accepted, disables what came back. Scanning alone
    /// changes nothing, so the button is safe to press before the choice has been made.</summary>
    private void TakeOverSteamAutostart()
    {
        try
        {
            var enabled = SteamAutostartService.Scan().Where(source => source.Enabled).ToArray();
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
            var result = SteamAutostartService.Apply(enabled, allowElevation: true);
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
            var log = System.IO.Path.Combine(Log.Directory, "wsgm.log");
            // Game mode has no Explorer in the session, and WSGM is normally elevated:
            // starting explorer.exe here would either break UWP for the session (an
            // elevated Explorer; see docs\elevation.md) or bring its taskbar up next to WSGM's
            // own tray host. Show the path instead; the user can open it in desktop mode.
            if (!ExplorerControl.IsRunningInSession())
            {
                Log.Info($"Open log location: no Explorer in this session — showing the path instead ({Log.Directory}).");
                StatusText = $"Log folder: {Log.Directory} (open it in desktop mode)";
                return;
            }
            // Absolute system path: a relative name would resolve via the process
            // working directory, which is the user-writable install dir.
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var explorer = System.IO.Path.Combine(windir, "explorer.exe");
            // Select the file when it exists so the user lands right on it;
            // otherwise just open the folder.
            var psi = System.IO.File.Exists(log)
                ? new System.Diagnostics.ProcessStartInfo(explorer, $"/select,\"{log}\"")
                : new System.Diagnostics.ProcessStartInfo(Log.Directory);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = windir;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the log location: {ex.Message}");
            StatusText = $"Could not open the log location: {ex.Message}";
        }
    }

    // --- Startup app suggestions ---
    /// <summary>Common handheld companions found on this PC, offered as one-click adds
    /// instead of making the user hunt for exe paths.</summary>
    public List<string> StartupSuggestions { get; private set; } = [];
    private List<(string Path, bool Elevated)> _startupSuggestionTargets = [];

    private int _selectedSuggestionIndex;
    /// <summary>Gets or sets the selected discovered startup-app suggestion.</summary>
    public int SelectedSuggestionIndex
    {
        get => _selectedSuggestionIndex;
        set { _selectedSuggestionIndex = value; Raise(nameof(SelectedSuggestionIndex)); }
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
    /// <returns><see langword="true"/> when a startup row was added; otherwise the caller should open a file picker.</returns>
    public bool AddSelectedStartupApp()
    {
        if (_selectedSuggestionIndex < 0 || _selectedSuggestionIndex >= _startupSuggestionTargets.Count)
        {
            return false;
        }
        var (path, elevated) = _startupSuggestionTargets[_selectedSuggestionIndex];
        if (string.IsNullOrEmpty(path))
        {
            return false;   // caller opens the file picker
        }
        StartupApps.Add(new StartupAppRow { Path = path, Elevated = elevated, Enabled = true });
        return true;
    }

    // --- Sign-in behavior ---
    private bool _startAtSignIn = true;

    /// <summary>Gets or sets whether the logon service starts WSGM at sign-in.
    /// Persisted via Save; the boot manifest is rewritten there.</summary>
    public bool StartAtSignIn { get => _startAtSignIn; set { _startAtSignIn = value; Raise(nameof(StartAtSignIn)); Raise(nameof(ServiceStateText)); Raise(nameof(ShellStatusText)); } }

    private bool _steamAutostartTakeoverAccepted;

    /// <summary>Gets or sets whether WSGM may own how Steam starts, turning Windows' own Steam
    /// startup entries off. Persisted via Save; the takeover itself runs after the save, outside
    /// the config lock, because it may need an elevation prompt.</summary>
    public bool SteamAutostartTakeoverAccepted
    {
        get => _steamAutostartTakeoverAccepted;
        set { _steamAutostartTakeoverAccepted = value; Raise(nameof(SteamAutostartTakeoverAccepted)); }
    }

    private int _startModeIndex = (int)SessionStartMode.Game;

    /// <summary>Gets or sets the session mode a start produces, as the selector's index:
    /// 0 = Desktop, 1 = Game. Independent of <see cref="StartAtSignIn"/>.</summary>
    public int StartModeIndex { get => _startModeIndex; set { _startModeIndex = value; Raise(nameof(StartModeIndex)); Raise(nameof(ServiceStateText)); Raise(nameof(ShellStatusText)); } }

    private bool _steamInputLeaseEnabled = true;

    /// <summary>Gets or sets whether WSGM leases the controller away from Steam
    /// Input while its focused surfaces are open. Off = Steam is never touched.</summary>
    public bool SteamInputLeaseEnabled { get => _steamInputLeaseEnabled; set { _steamInputLeaseEnabled = value; Raise(nameof(SteamInputLeaseEnabled)); } }

    /// <summary>Gets or sets whether WSGM deploys its Steam Input shim into Steam's
    /// own install directory, so Steam loads it and WSGM never injects.</summary>
    public bool SteamInputManagementEnabled
    {
        get => _steamInputManagementEnabled;
        set { _steamInputManagementEnabled = value; Raise(nameof(SteamInputManagementEnabled)); }
    }

    private bool _steamInputManagementEnabled = true;

    private bool _deviceIntegrationEnabled;
    private bool _deviceControllerManagementEnabled;
    private bool _deviceAutoTdpEnabled;
    private int _deviceControllerTargetIndex = (int)ManagedControllerTarget.SteamDeckComposite;
    private int _deviceGlyphSelectionIndex = (int)DeviceGlyphSelection.Automatic;
    private string _deviceOwnerStatusText = "No running device coordinator detected.";

    // Set by the property setters, cleared once after the constructor's own seeding, so they mean
    // "the user changed this here" rather than "this window has a value for it".
    private bool _deviceAutoTdpEdited;
    private bool _deviceControllerTargetEdited;
    private bool _deviceGlyphSelectionEdited;

    /// <summary>Gets or sets the optional production Device Integration master switch.</summary>
    public bool DeviceIntegrationEnabled
    {
        get => _deviceIntegrationEnabled;
        set
        {
            _deviceIntegrationEnabled = value;
            Raise(nameof(DeviceIntegrationEnabled));
        }
    }

    /// <summary>Gets or sets the remembered controller-management child preference.</summary>
    public bool DeviceControllerManagementEnabled
    {
        get => _deviceControllerManagementEnabled;
        set
        {
            _deviceControllerManagementEnabled = value;
            Raise(nameof(DeviceControllerManagementEnabled));
        }
    }

    /// <summary>Gets or sets whether AutoTDP controls the primary power limit.</summary>
    /// <remarks>
    /// One of the three device settings the running shell also owns: the overlay and the native
    /// quick-access menu persist all of them while this window is open. Each records whether it was
    /// edited here, because a save merges over a fresh load and an untouched snapshot would
    /// otherwise revert whatever the running session had changed. See <see cref="DeviceEditsMade"/>.
    /// </remarks>
    public bool DeviceAutoTdpEnabled
    {
        get => _deviceAutoTdpEnabled;
        set
        {
            _deviceAutoTdpEnabled = value;
            _deviceAutoTdpEdited = true;
            Raise(nameof(DeviceAutoTdpEnabled));
        }
    }

    /// <summary>Selected global managed-controller target index.</summary>
    public int DeviceControllerTargetIndex
    {
        get => _deviceControllerTargetIndex;
        set
        {
            _deviceControllerTargetIndex = value;
            _deviceControllerTargetEdited = true;
            Raise(nameof(DeviceControllerTargetIndex));
        }
    }

    /// <summary>Selected physical glyph-policy index.</summary>
    public int DeviceGlyphSelectionIndex
    {
        get => _deviceGlyphSelectionIndex;
        set
        {
            _deviceGlyphSelectionIndex = value;
            _deviceGlyphSelectionEdited = true;
            Raise(nameof(DeviceGlyphSelectionIndex));
        }
    }

    /// <summary>Which runtime-owned device settings this window actually edited.</summary>
    /// <remarks>
    /// Exposed for tests: the merge behaviour it drives is the whole point of the flags, and it
    /// cannot be observed from the saved configuration without a real config file.
    /// </remarks>
    internal (bool AutoTdp, bool ControllerTarget, bool GlyphSelection) DeviceEditsMade =>
        (_deviceAutoTdpEdited, _deviceControllerTargetEdited, _deviceGlyphSelectionEdited);

    /// <summary>Read-only status reported by the authoritative shell coordinator.</summary>
    public string DeviceOwnerStatusText
    {
        get => _deviceOwnerStatusText;
        private set { _deviceOwnerStatusText = value; Raise(nameof(DeviceOwnerStatusText)); }
    }

    /// <summary>Refreshes the read-only owner snapshot without creating a device cycle.</summary>
    public async Task RefreshDeviceOwnerStatusAsync()
    {
        try
        {
            DeviceCoordinatorDiagnosticsSnapshot? snapshot =
                await DeviceCoordinatorDiagnosticsClient.TryReadAsync(
                    (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId,
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

    /// <summary>Gets whether first-run Quick Setup still has to be answered.</summary>
    public bool QuickSetupPending => QuickSetup.ShouldShow(_config);

    /// <summary>Gets or sets whether the Quick Setup panel was answered in this
    /// session, so the next save stamps the revision it answered.</summary>
    public bool QuickSetupAnswered { get; set; }

    /// <summary>Gets a plain-language description of the shim deployment, naming the
    /// file so a pasted screenshot is diagnostic on its own.</summary>
    public string SteamInputShimStatusText
    {
        get
        {
            var status = SteamInputShim.LastStatus;
            var name = SteamInputShim.FileNameFor(status.Vector);
            return status.State switch
            {
                SteamInputShimState.SteamNotInstalled =>
                    "Steam was not found on this PC, so nothing was installed.",
                SteamInputShimState.Disabled =>
                    "Off. WSGM's file is parked next to Steam and does nothing; turning this back on restores it instantly.",
                SteamInputShimState.Deployed when SteamInputShim.LoadedVector is not null =>
                    $"Active - installed as {name} and loaded by the running Steam.",
                SteamInputShimState.Deployed =>
                    $"Installed as {name}. It takes effect the next time Steam starts.",
                SteamInputShimState.UpdatePending =>
                    "An update is waiting: Steam is using the old copy right now. WSGM replaces it the next time it starts Steam.",
                SteamInputShimState.Blocked =>
                    "Could not install: XInput1_4.dll and dinput8.dll in Steam's folder both belong to another program (ValvePlug or Special K, for example). WSGM will not overwrite them.",
                _ => "Could not write to Steam's folder. Run WSGM setup again, or start WSGM as administrator once.",
            };
        }
    }

    private bool _cefEnabled = true;
    private bool _cefLibraryTabs = true;
    private bool _cefCardManager = true;
    private bool _cefSdFormat = true;
    private bool _cefArtwork = true;
    private bool _cefWifiIndicator = true;
    private bool _cefNativeQuickAccess = true;
    private bool _cefDownloadKeepAwake = true;
    private bool _cefDownloadQueueSort = true;
    private bool _cefConnectedLibraryCarousel = true;
    private bool _cefCarouselShowUninstalled;
    private bool _performanceEnabled;
    private int _frameLimitStrategyIndex;
    private bool _muteWhileDisplayOff;
    private bool _resuspendUnexplainedWakes;
    private bool _verboseLogging;

    /// <summary>Gets or sets the shared RTSS performance integration master switch.</summary>
    public bool PerformanceEnabled
    {
        get => _performanceEnabled;
        set { _performanceEnabled = value; Raise(nameof(PerformanceEnabled)); }
    }

    private string _osdCustomOrder = "Time,GPU,CPU,VRAM,RAM,BATT,FPS";
    private int _osdCustomTimeIndex = 2;
    private int _osdCustomFpsIndex = 2;
    private int _osdCustomCpuIndex = 2;
    private int _osdCustomRamIndex = 2;
    private int _osdCustomGpuIndex = 2;
    private int _osdCustomVramIndex = 2;
    private int _osdCustomBatteryIndex = 2;

    /// <summary>The three detail options shared by every Custom-overlay widget selector.</summary>
    public List<string> OsdCustomLevels { get; } = ["Hidden", "Minimal", "Full"];

    /// <summary>Custom overlay (level 4) widget order, comma-separated widget names.</summary>
    public string OsdCustomOrder
    {
        get => _osdCustomOrder;
        set { _osdCustomOrder = value; Raise(nameof(OsdCustomOrder)); }
    }

    /// <summary>Clock detail for the Custom overlay.</summary>
    public int OsdCustomTimeIndex
    {
        get => _osdCustomTimeIndex;
        set { _osdCustomTimeIndex = value; Raise(nameof(OsdCustomTimeIndex)); }
    }

    /// <summary>Framerate detail for the Custom overlay.</summary>
    public int OsdCustomFpsIndex
    {
        get => _osdCustomFpsIndex;
        set { _osdCustomFpsIndex = value; Raise(nameof(OsdCustomFpsIndex)); }
    }

    /// <summary>CPU detail for the Custom overlay.</summary>
    public int OsdCustomCpuIndex
    {
        get => _osdCustomCpuIndex;
        set { _osdCustomCpuIndex = value; Raise(nameof(OsdCustomCpuIndex)); }
    }

    /// <summary>Memory detail for the Custom overlay.</summary>
    public int OsdCustomRamIndex
    {
        get => _osdCustomRamIndex;
        set { _osdCustomRamIndex = value; Raise(nameof(OsdCustomRamIndex)); }
    }

    /// <summary>GPU detail for the Custom overlay.</summary>
    public int OsdCustomGpuIndex
    {
        get => _osdCustomGpuIndex;
        set { _osdCustomGpuIndex = value; Raise(nameof(OsdCustomGpuIndex)); }
    }

    /// <summary>Video-memory detail for the Custom overlay.</summary>
    public int OsdCustomVramIndex
    {
        get => _osdCustomVramIndex;
        set { _osdCustomVramIndex = value; Raise(nameof(OsdCustomVramIndex)); }
    }

    /// <summary>Battery detail for the Custom overlay.</summary>
    public int OsdCustomBatteryIndex
    {
        get => _osdCustomBatteryIndex;
        set { _osdCustomBatteryIndex = value; Raise(nameof(OsdCustomBatteryIndex)); }
    }

    /// <summary>Gets or sets how a frame cap is paired with the panel's refresh rate.</summary>
    /// <remarks>
    /// Index into <see cref="FrameLimitStrategy"/>, in declaration order, so the combo box needs no
    /// converter. It decides both what the cap does to the display and which caps are offered at
    /// all: uncoupled offers a free range, and the two coupled strategies offer only caps that
    /// divide a real mode exactly.
    /// </remarks>
    public int FrameLimitStrategyIndex
    {
        get => _frameLimitStrategyIndex;
        set { _frameLimitStrategyIndex = value; Raise(nameof(FrameLimitStrategyIndex)); }
    }

    /// <summary>Gets or sets the master Steam CEF integration switch. Off closes the
    /// debug port, injects nothing, and hides the sub-toggles below and the overlay
    /// feature buttons.</summary>
    public bool CefEnabled { get => _cefEnabled; set { _cefEnabled = value; Raise(nameof(CefEnabled)); } }

    /// <summary>Gets or sets the injected library filter tabs, tab order, and native-tab hiding.</summary>
    public bool CefLibraryTabs { get => _cefLibraryTabs; set { _cefLibraryTabs = value; Raise(nameof(CefLibraryTabs)); } }

    /// <summary>Gets or sets the SD-card library manager (card tabs, badges, live labels).</summary>
    public bool CefCardManager { get => _cefCardManager; set { _cefCardManager = value; Raise(nameof(CefCardManager)); } }

    /// <summary>Gets or sets Format SD Card + live library registration.</summary>
    public bool CefSdFormat { get => _cefSdFormat; set { _cefSdFormat = value; Raise(nameof(CefSdFormat)); } }

    private bool _steamStorageFormat;

    /// <summary>Gets or sets whether Steam's own storage pages may erase a drive through WSGM.</summary>
    /// <remarks>
    /// An opt-out, separate from <see cref="CefSdFormat"/>: that one is WSGM's own guided flow,
    /// this one lets Steam's Format Drive modal start the same erase. The refusal Steam shows when
    /// this is off is a generic result code, so the switch has to be where the user can find it —
    /// which it was not, for a day.
    /// </remarks>
    public bool SteamStorageFormat
    {
        get => _steamStorageFormat;
        set { _steamStorageFormat = value; Raise(nameof(SteamStorageFormat)); }
    }

    /// <summary>Gets or sets the shortcut-artwork changer.</summary>
    public bool CefArtwork { get => _cefArtwork; set { _cefArtwork = value; Raise(nameof(CefArtwork)); } }

    /// <summary>Gets or sets the Big Picture Wi-Fi indicator.</summary>
    public bool CefWifiIndicator { get => _cefWifiIndicator; set { _cefWifiIndicator = value; Raise(nameof(CefWifiIndicator)); } }

    /// <summary>Gets or sets the fingerprint-gated native Steam Quick Access bootstrap.</summary>
    public bool CefNativeQuickAccess { get => _cefNativeQuickAccess; set { _cefNativeQuickAccess = value; Raise(nameof(CefNativeQuickAccess)); } }

    /// <summary>Gets or sets the automatic download wake lock (keep the device awake
    /// while Steam reports an active download).</summary>
    public bool CefDownloadKeepAwake { get => _cefDownloadKeepAwake; set { _cefDownloadKeepAwake = value; Raise(nameof(CefDownloadKeepAwake)); } }

    /// <summary>Gets or sets the Name/Size/Type sort buttons injected into Big
    /// Picture's download-queue header.</summary>
    public bool CefDownloadQueueSort { get => _cefDownloadQueueSort; set { _cefDownloadQueueSort = value; Raise(nameof(CefDownloadQueueSort)); } }

    /// <summary>Gets or sets whether Big Picture Home's carousel lists the games on the
    /// libraries attached right now.</summary>
    public bool CefConnectedLibraryCarousel { get => _cefConnectedLibraryCarousel; set { _cefConnectedLibraryCarousel = value; Raise(nameof(CefConnectedLibraryCarousel)); } }

    /// <summary>Gets or sets whether that carousel also lists owned games that are not
    /// installed, greyed.</summary>
    public bool CefCarouselShowUninstalled { get => _cefCarouselShowUninstalled; set { _cefCarouselShowUninstalled = value; Raise(nameof(CefCarouselShowUninstalled)); } }

    /// <summary>Gets or sets muting system audio while the screen is off.</summary>
    public bool MuteWhileDisplayOff { get => _muteWhileDisplayOff; set { _muteWhileDisplayOff = value; Raise(nameof(MuteWhileDisplayOff)); } }

    /// <summary>Gets or sets suspending again after a standby wake nothing accounts for.</summary>
    public bool ResuspendUnexplainedWakes { get => _resuspendUnexplainedWakes; set { _resuspendUnexplainedWakes = value; Raise(nameof(ResuspendUnexplainedWakes)); } }

    private string _modernStandbyStatusText = "";

    /// <summary>Gets Windows' own account of the last standby, for the settings surface.</summary>
    /// <remarks>
    /// Read once when the page loads rather than polled: it describes the last resume, and nothing
    /// about it changes while the settings window is open. Windows exposes no documented call for
    /// what woke the machine, so this never names a cause.
    /// </remarks>
    public string ModernStandbyStatusText
    {
        get => _modernStandbyStatusText;
        private set { _modernStandbyStatusText = value; Raise(nameof(ModernStandbyStatusText)); }
    }

    /// <summary>Gets or sets whether the log records debug detail.</summary>
    public bool VerboseLogging { get => _verboseLogging; set { _verboseLogging = value; Raise(nameof(VerboseLogging)); } }

    /// <summary>Gets a user-facing explanation of the current sign-in behavior.</summary>
    public string ShellStatusText => !StartAtSignIn
        ? "WSGM does not start at sign-in. Start it from the Start Menu; Explorer stays your Windows shell."
        : StartModeIndex == (int)SessionStartMode.Desktop
            ? "WSGM starts at sign-in and stays in desktop mode until you enter game mode. Explorer stays your Windows shell."
            : "Game mode starts at sign-in through the WSGM logon service. Explorer stays your Windows shell.";

    // --- UAC prompt level ---
    /// <summary>Gets whether UAC consent prompts are disabled for the machine.</summary>
    public bool UacPromptsDisabled => UacSettings.Read().PromptsDisabled;

    /// <summary>Gets a user-facing explanation of the current UAC prompt policy.</summary>
    public string UacStatusText => UacPromptsDisabled
        ? "UAC prompts are OFF — elevated apps start silently. Windows still runs with UAC enabled, but anything that asks for administrator rights gets them without asking you."
        : "UAC prompts are ON (Windows default). Each elevated launch shows a consent dialog, which interrupts boot-to-game on a handheld.";

    /// <summary>Toggles the machine UAC prompt level, off-thread: the elevated
    /// one-shot blocks for as long as the consent prompt is on screen — up to a
    /// minute if the user leaves it sitting — and in game mode the frozen window is
    /// the one holding the Steam Input lease, so the pad looks dead too and it reads
    /// as a hang.
    /// <para>Call from the UI thread: the continuation resumes there, so the property
    /// change notifications stay UI-thread owned.</para></summary>
    /// <param name="disable">Whether to suppress consent prompts.</param>
    /// <returns><see langword="true"/> when Windows accepted the policy change.</returns>
    public async Task<bool> SetUacPromptsAsync(bool disable)
    {
        var ok = await Task.Run(() => UacSettings.RequestChange(disable)).ConfigureAwait(true);
        Raise(nameof(UacPromptsDisabled));
        Raise(nameof(UacStatusText));
        return ok;
    }

    // --- Lock on wake ---
    /// <summary>Gets whether Windows will skip a sign-in prompt after display sleep.</summary>
    public bool LockOnWakeDisabled => LockScreenSettings.SignInOnWakeDisabled();

    /// <summary>Gets a user-facing explanation of the wake sign-in policy.</summary>
    public string LockOnWakeStatusText => LockOnWakeDisabled
        ? "Waking the device goes straight back to your game — no sign-in screen."
        : "Windows currently asks you to sign in again after the screen sleeps (Windows default).";

    /// <summary>Changes the Windows wake sign-in policy through the elevated helper,
    /// off-thread — see <see cref="SetUacPromptsAsync"/> for why a synchronous form
    /// would freeze the window. Call from the UI thread so the notifications resume
    /// there.</summary>
    /// <param name="disable">Whether to bypass the sign-in prompt after display sleep.</param>
    /// <returns><see langword="true"/> when Windows accepted the policy change.</returns>
    public async Task<bool> SetLockOnWakeAsync(bool disable)
    {
        var ok = await Task.Run(() => LockScreenSettings.RequestChange(disable)).ConfigureAwait(true);
        Raise(nameof(LockOnWakeDisabled));
        Raise(nameof(LockOnWakeStatusText));
        return ok;
    }

    // --- Steam (the only launcher; located via registry, nothing to configure) ---
    /// <summary>Gets Steam discovery status because game mode requires Steam.</summary>
    public string SteamStatusText => Steam.ExePath is { } exe
        ? $"Detected: {exe}"
        : "Steam was not found on this PC. Install Steam first — WSGM is Steam-exclusive.";

    private bool _steamAutoRelaunch;
    private bool _steamLaunchUnelevated;

    /// <summary>Gets or sets whether the Steam monitor restarts Steam after an unexpected exit.</summary>
    public bool SteamAutoRelaunch { get => _steamAutoRelaunch; set { _steamAutoRelaunch = value; Raise(nameof(SteamAutoRelaunch)); } }

    /// <summary>Whether the complete Steam client starts at medium integrity.</summary>
    public bool SteamLaunchUnelevated
    {
        get => _steamLaunchUnelevated;
        set { _steamLaunchUnelevated = value; Raise(nameof(SteamLaunchUnelevated)); }
    }

    private string _steamGridDbApiKey = "";

    /// <summary>Gets or sets the user's SteamGridDB API key (for the Change Artwork
    /// feature). Empty disables it; get a free key at <see cref="Core.SteamGridDb.KeyPageUrl"/>.</summary>
    public string SteamGridDbApiKey { get => _steamGridDbApiKey; set { _steamGridDbApiKey = value; Raise(nameof(SteamGridDbApiKey)); } }

    private bool _screenscraperEnabled;

    /// <summary>Gets or sets whether Screenscraper.fr is searched alongside SteamGridDB.</summary>
    /// <remarks>
    /// Off unless the user turns it on, because Screenscraper needs developer credentials WSGM
    /// cannot ship: it issues developer ids per application rather than offering a free personal
    /// key. See <see cref="Core.ScreenscraperProvider.AccountPageUrl"/>.
    /// </remarks>
    public bool ScreenscraperEnabled { get => _screenscraperEnabled; set { _screenscraperEnabled = value; Raise(nameof(ScreenscraperEnabled)); } }

    private string _screenscraperDevId = "";

    /// <summary>Gets or sets the Screenscraper developer id.</summary>
    public string ScreenscraperDevId { get => _screenscraperDevId; set { _screenscraperDevId = value; Raise(nameof(ScreenscraperDevId)); } }

    private string _screenscraperDevPassword = "";

    /// <summary>Gets or sets the Screenscraper developer password.</summary>
    public string ScreenscraperDevPassword { get => _screenscraperDevPassword; set { _screenscraperDevPassword = value; Raise(nameof(ScreenscraperDevPassword)); } }

    private string _screenscraperUser = "";

    /// <summary>Gets or sets the optional Screenscraper account name, which raises the quota.</summary>
    public string ScreenscraperUser { get => _screenscraperUser; set { _screenscraperUser = value; Raise(nameof(ScreenscraperUser)); } }

    private string _screenscraperUserPassword = "";

    /// <summary>Gets or sets the password for <see cref="ScreenscraperUser"/>.</summary>
    public string ScreenscraperUserPassword { get => _screenscraperUserPassword; set { _screenscraperUserPassword = value; Raise(nameof(ScreenscraperUserPassword)); } }

    // --- Startup apps ---
    /// <summary>Gets the ordered startup programs shown in the settings editor.</summary>
    public ObservableCollection<StartupAppRow> StartupApps { get; } = [];

    private int _startupDelayMs;

    /// <summary>Gets or sets the initial delay before launching configured startup programs.</summary>
    public int StartupDelayMs { get => _startupDelayMs; set { _startupDelayMs = value; Raise(nameof(StartupDelayMs)); } }

    private int _staggerDelayMs;

    /// <summary>Gets or sets the delay between successive configured startup programs.</summary>
    public int StaggerDelayMs { get => _staggerDelayMs; set { _staggerDelayMs = value; Raise(nameof(StaggerDelayMs)); } }

    private bool _bootSplashEnabled;

    /// <summary>Gets or sets whether a splash window is shown while game mode starts.</summary>
    public bool BootSplashEnabled { get => _bootSplashEnabled; set { _bootSplashEnabled = value; Raise(nameof(BootSplashEnabled)); } }

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

    // --- Overlay shortcuts (recorded, not picked from a list) ---
    private HotkeyConfig _hotkey = new();
    private GamepadChordConfig _chord = new();

    /// <summary>Gets the current keyboard shortcut or the key-recording prompt.</summary>
    public string HotkeyText => _hotkeyRecording ? "Press keys…" : KeyRecorder.Describe(_hotkey);

    /// <summary>Gets the current controller chord or the button-recording prompt.</summary>
    public string ChordText => _chordRecording
        ? "Press buttons…"
        : _chord.Enabled && _chord.Buttons != 0
            ? GamepadService.Describe((GamepadButtons)_chord.Buttons, _chord.Hold)
            : "None";

    private bool _hotkeyRecording;
    private bool _chordRecording;

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
    /// <param name="hotkey">The captured shortcut, or <see cref="KeyRecorder.Cleared"/>.</param>
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
            Hold = hold,
        };
        SetChordRecording(false);
    }

    /// <summary>Clears the keyboard shortcut.</summary>
    public void ClearHotkey() => ApplyRecordedHotkey(KeyRecorder.Cleared());

    /// <summary>Clears the controller chord.</summary>
    public void ClearChord() => ApplyRecordedChord(0, false);

    // --- Gestures / glyphs ---
    private bool _gestureBottom;
    private bool _gestureTop;
    private bool _gestureLeftSteamMenu;
    private bool _gestureRightSteamQuickAccess;
    private int _glyphStyleIndex;

    /// <summary>Gets or sets whether a bottom-edge swipe opens quick access on its Open apps strip (game mode).</summary>
    public bool GestureBottom { get => _gestureBottom; set { _gestureBottom = value; Raise(nameof(GestureBottom)); } }

    /// <summary>Gets or sets whether a top-edge swipe opens quick access.</summary>
    public bool GestureTop { get => _gestureTop; set { _gestureTop = value; Raise(nameof(GestureTop)); } }

    /// <summary>Gets or sets whether a left-edge swipe opens Steam's Big Picture menu.</summary>
    public bool GestureLeftSteamMenu { get => _gestureLeftSteamMenu; set { _gestureLeftSteamMenu = value; Raise(nameof(GestureLeftSteamMenu)); } }

    /// <summary>Gets or sets whether a right-edge swipe opens Steam's Big Picture quick-access menu.</summary>
    public bool GestureRightSteamQuickAccess { get => _gestureRightSteamQuickAccess; set { _gestureRightSteamQuickAccess = value; Raise(nameof(GestureRightSteamQuickAccess)); } }

    /// <summary>Gets or sets the selected controller-glyph family index.</summary>
    public int GlyphStyleIndex { get => _glyphStyleIndex; set { _glyphStyleIndex = value; Raise(nameof(GlyphStyleIndex)); Raise(nameof(GlyphStyle)); } }

    /// <summary>Gets the selected glyph family as its enum value — what the
    /// status strip's A/B glyph icons bind to.</summary>
    public GlyphStyle GlyphStyle => (GlyphStyle)Math.Clamp(_glyphStyleIndex, 0, 2);

    /// <summary>Gets the controller-glyph family names presented by the settings selector.</summary>
    public List<string> GlyphStyles { get; } = ["Xbox", "PlayStation", "Nintendo"];

    // --- Appearance: accent color ---
    private string _accentColorHex = AccentPalette.DefaultAccent;

    /// <summary>Gets or sets the UI accent color as a hex string (e.g. "#FF9D3D").
    /// An unparsable value falls back to the default accent when applied.</summary>
    public string AccentColorHex { get => _accentColorHex; set { _accentColorHex = value; Raise(nameof(AccentColorHex)); } }

    // --- Appearance: boot splash ---
    // The editor binds the SplashConfig instance directly ({Binding Splash.X}).
    // Only members with a dependent consumer keep an INPC wrapper here: the four
    // colors repaint their swatch previews on every keystroke, and the two image
    // paths drive the Appearance page's thumbnail refresh.
    private SplashConfig _splash = new();

    /// <summary>The splash section being edited. Replaced wholesale by
    /// <see cref="LoadSplash"/> (startup, preset apply, theme import), which raises
    /// this property so every nested binding re-evaluates.</summary>
    public SplashConfig Splash => _splash;

    /// <summary>Editable placement of the splash text stack.</summary>
    public SplashPlacementEditor TextPlacement { get; } = new();

    /// <summary>Editable placement of the splash spinner.</summary>
    public SplashPlacementEditor SpinnerPlacement { get; } = new();

    /// <summary>Editable placement of the splash logo.</summary>
    public SplashPlacementEditor LogoPlacement { get; } = new();

    /// <summary>Spinner styles offered by the settings selector.</summary>
    public static SplashSpinnerStyle[] SpinnerStyleValues { get; } = Enum.GetValues<SplashSpinnerStyle>();

    /// <summary>Sweep-line edges offered by the settings selector.</summary>
    public static SweepEdge[] SweepEdgeValues { get; } = Enum.GetValues<SweepEdge>();

    /// <summary>Placement modes offered for the spinner and logo.</summary>
    public static SplashPlacementMode[] PlacementModeValues { get; } = Enum.GetValues<SplashPlacementMode>();

    /// <summary>Placement modes offered for the text element itself, which cannot
    /// ride its own stack.</summary>
    public static SplashPlacementMode[] TextPlacementModeValues { get; } =
        [SplashPlacementMode.Anchor, SplashPlacementMode.Absolute];

    /// <summary>Nine-grid anchors offered by the settings selectors.</summary>
    public static SplashPlacementAnchor[] PlacementAnchorValues { get; } = Enum.GetValues<SplashPlacementAnchor>();

    /// <summary>Gets or sets the splash title color as a hex string.</summary>
    public string SplashTextColorHex { get => _splash.TextColor; set { _splash.TextColor = value; Raise(nameof(SplashTextColorHex)); } }

    /// <summary>Gets or sets the splash caption color as a hex string.</summary>
    public string SplashCaptionColorHex { get => _splash.CaptionColor; set { _splash.CaptionColor = value; Raise(nameof(SplashCaptionColorHex)); } }

    /// <summary>Gets or sets the spinner color as a hex string.</summary>
    public string SplashSpinnerColorHex { get => _splash.SpinnerColor; set { _splash.SpinnerColor = value; Raise(nameof(SplashSpinnerColorHex)); } }

    /// <summary>Gets or sets the splash background fill color as a hex string.</summary>
    public string SplashBackgroundColorHex { get => _splash.BackgroundColor; set { _splash.BackgroundColor = value; Raise(nameof(SplashBackgroundColorHex)); } }

    /// <summary>Gets or sets the splash logo image path; empty = no logo.</summary>
    public string SplashLogoPath { get => _splash.LogoImagePath; set { _splash.LogoImagePath = value; Raise(nameof(SplashLogoPath)); } }

    /// <summary>Gets or sets the splash background image path; empty = solid color.</summary>
    public string SplashBackgroundImagePath { get => _splash.BackgroundImagePath; set { _splash.BackgroundImagePath = value; Raise(nameof(SplashBackgroundImagePath)); } }

    /// <summary>Builds the splash section handed to Save, the preview window, and
    /// theme export: an isolated copy of the edited section, so the save path's
    /// asset staging can rewrite its image paths without touching the editor.</summary>
    internal SplashConfig BuildSplashConfig()
    {
        var splash = ConfigStore.CloneJson(_splash, ConfigJsonContext.Default.SplashConfig);
        // "With text" is a spinner/logo-only mode; the text element itself anchors.
        // Normalize accepts WithText on every placement, so an imported theme can
        // still carry it on the text placement — this is where it is coerced.
        if (splash.TextPlacement.Mode == SplashPlacementMode.WithText)
        {
            splash.TextPlacement.Mode = SplashPlacementMode.Anchor;
        }
        return splash;
    }

    /// <summary>Loads the splash editor from a splash section — used at startup, on
    /// preset apply, and after theme import. The section is copied and normalized,
    /// so later edits cannot mutate the caller's instance and an imported value can
    /// never carry an out-of-range enum into the editor.</summary>
    internal void LoadSplash(SplashConfig splash)
    {
        _splash = ConfigStore.NormalizeSplash(
            ConfigStore.CloneJson(splash, ConfigJsonContext.Default.SplashConfig));
        TextPlacement.Load(_splash.TextPlacement);
        SpinnerPlacement.Load(_splash.SpinnerPlacement);
        LogoPlacement.Load(_splash.LogoPlacement);
        Raise(nameof(Splash));
        Raise(nameof(SplashTextColorHex));
        Raise(nameof(SplashCaptionColorHex));
        Raise(nameof(SplashSpinnerColorHex));
        Raise(nameof(SplashBackgroundColorHex));
        Raise(nameof(SplashLogoPath));
        Raise(nameof(SplashBackgroundImagePath));
    }

    // --- Save ---
    private void ApplyTo(AppConfig config) => ApplyTo(config, BuildSplashConfig());

    /// <summary>Applies the UI-owned fields over <paramref name="config"/>, taking the
    /// splash section from <paramref name="splash"/> instead of rebuilding it — the save
    /// path prepares (and thereby path-rewrites) its splash section BEFORE it takes the
    /// config lock, and rebuilding here would throw that rewrite away.</summary>
    private void ApplyTo(AppConfig config, SplashConfig splash)
    {
        config.SteamAutoRelaunch = SteamAutoRelaunch;
        config.SteamLaunchUnelevated = SteamLaunchUnelevated;
        config.SteamGridDbApiKey = (SteamGridDbApiKey ?? "").Trim();
        config.ScreenscraperEnabled = ScreenscraperEnabled;
        config.ScreenscraperDevId = (ScreenscraperDevId ?? "").Trim();
        config.ScreenscraperDevPassword = (ScreenscraperDevPassword ?? "").Trim();
        config.ScreenscraperUser = (ScreenscraperUser ?? "").Trim();
        config.ScreenscraperUserPassword = (ScreenscraperUserPassword ?? "").Trim();
        config.StartupDelayMs = StartupDelayMs;
        config.StaggerDelayMs = StaggerDelayMs;
        config.BootSplashEnabled = BootSplashEnabled;
        config.StartAtSignIn = StartAtSignIn;
        config.StartMode = (SessionStartMode)Math.Clamp(StartModeIndex, 0, 1);
        config.SteamAutostartTakeoverAccepted = SteamAutostartTakeoverAccepted;
        ApplyLaunchTo(config.GameModeLaunch);
        config.SteamInputLeaseEnabled = SteamInputLeaseEnabled;
        config.SteamInputManagementEnabled = SteamInputManagementEnabled;
        config.DeviceIntegration.Enabled = DeviceIntegrationEnabled;
        config.DeviceIntegration.ControllerManagementEnabled = DeviceControllerManagementEnabled;
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
            config.DeviceIntegration.ControllerTarget = (ManagedControllerTarget)Math.Clamp(
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
        if (QuickSetupAnswered)
        {
            // Stamped only on a save that actually persists the answer, so a failed
            // save leaves the panel due to appear again rather than silently lost.
            QuickSetup.MarkCompleted(config);
        }
        config.Cef.Enabled = CefEnabled;
        config.Cef.LibraryTabs = CefLibraryTabs;
        config.Cef.CardManager = CefCardManager;
        config.Cef.SdFormat = CefSdFormat;
        config.SteamStorageFormatEnabled = SteamStorageFormat;
        config.Cef.Artwork = CefArtwork;
        config.Cef.WifiIndicator = CefWifiIndicator;
        config.Cef.NativeQuickAccess = CefNativeQuickAccess;
        config.Cef.DownloadKeepAwake = CefDownloadKeepAwake;
        config.Cef.DownloadQueueSort = CefDownloadQueueSort;
        config.Cef.ConnectedLibraryCarousel = CefConnectedLibraryCarousel;
        config.Cef.CarouselShowUninstalled = CefCarouselShowUninstalled;
        config.MuteWhileDisplayOff = MuteWhileDisplayOff;
        config.ResuspendUnexplainedWakes = ResuspendUnexplainedWakes;
        config.LogVerbosity = VerboseLogging ? LogVerbosity.Verbose : LogVerbosity.Normal;
        config.Hotkey = _hotkey;
        config.GamepadChord = _chord;
        config.Gestures.BottomEdge = GestureBottom;
        config.Gestures.TopEdge = GestureTop;
        config.Gestures.LeftEdgeSteamMenu = GestureLeftSteamMenu;
        config.Gestures.RightEdgeSteamQuickAccess = GestureRightSteamQuickAccess;
        config.GlyphStyle = GlyphStyle;
        config.AccentColor = AccentColorHex;
        config.Splash = splash;
        config.StartupApps = StartupApps
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new StartupAppConfig
            {
                Path = r.Path.Trim(),
                Args = r.Args.Trim(),
                Enabled = r.Enabled,
                Elevated = r.Elevated,
                AutoRelaunch = r.AutoRelaunch,
            })
            .ToList();
    }

    /// <summary>Seeds the launch fields from a stored configuration.</summary>
    /// <param name="launch">The stored configuration.</param>
    private void LoadLaunchConfiguration(GameModeLaunchConfiguration launch)
    {
        GameModeLaunchKindIndex = (int)launch.Kind;
        GameModeReturnIndex = (int)launch.Return;
        _gameLayout = launch.GameLayout;
        _desktopLayout = launch.DesktopLayout;
        _waitForDisplay = launch.WaitForDisplay;
        _enterActions = launch.EnterActions;
        _leaveActions = launch.LeaveActions;
        _desktopStartupActions = launch.DesktopStartupActions;
        _desktopWakeActions = launch.DesktopWakeActions;

        KnownDisplays.Clear();
        foreach (KnownDisplay display in launch.KnownDisplays) { KnownDisplays.Add(display); }
        // One observation on open, so a connected display can be configured without pressing
        // Snapshot first and an absent one is honestly badged as absent.
        try { MergeCatalog(_services.CaptureDisplays()); }
        catch (Exception ex) { _services.Report("Could not read the current displays for Settings", ex); }
        RefreshLaunchRows();
    }

    /// <summary>Rebuilds every rendered row from the fields the editor owns.</summary>
    private void RefreshLaunchRows()
    {
        GameLayout.Load(KnownDisplays, _present, _gameLayout);
        DesktopLayout.Load(KnownDisplays, _present, _desktopLayout);

        IReadOnlyList<PluginActionOption> options = ReadPluginActions();
        ActionLists[0].Load(_enterActions, options);
        ActionLists[1].Load(_leaveActions, options);
        ActionLists[2].Load(_desktopStartupActions, options);
        ActionLists[3].Load(_desktopWakeActions, options);

        WaitForDisplayChoices.Clear();
        WaitForDisplayChoices.Add("No display wait");
        foreach (KnownDisplay display in KnownDisplays)
        {
            WaitForDisplayChoices.Add(display.Target?.FriendlyName ?? "Unnamed display");
        }
        WaitForDisplayIndex = _waitForDisplay is null ? 0
            : Math.Max(0, KnownDisplays.ToList().FindIndex(
                display => display.Target?.Matches(_waitForDisplay) == true) + 1);

        RebindChoices.Clear();
        foreach (DisplayTargetIdentity target in _present)
        {
            RebindChoices.Add(target.FriendlyName.Length > 0 ? target.FriendlyName : "Unnamed display");
        }
        RebindChoiceIndex = RebindChoices.Count > 0 ? 0 : -1;
        RefreshLaunchSummary();
    }

    /// <summary>Restates what the two layouts describe. Called on every edit, so the page says
    /// whether the layout can be saved while it is being changed rather than only on Save.</summary>
    private void RefreshLaunchSummary()
    {
        int active = GameLayout.Rows.Count(row => row.Active);
        LaunchSummaryText = GameModeLaunchKindIndex == (int)GameModeLaunchKind.Default
            ? "Game Mode starts on the display Windows calls primary and adjusts scaling only."
            : active == 0
                ? "No display is switched on for Game Mode yet. Use Snapshot, or switch a remembered display on below."
                : GameLayout.ValidationText.Length > 0
                    ? "Game Mode layout: " + GameLayout.ValidationText
                    : DesktopLayout.ValidationText.Length > 0
                        ? "Desktop layout: " + DesktopLayout.ValidationText
                        : $"{active} display(s) in the Game Mode layout.";
        Raise(nameof(CanSaveLayouts));
    }

    /// <summary>Whether both layouts currently describe a desktop Windows would accept.</summary>
    public bool CanSaveLayouts =>
        (GameModeLaunchKindIndex != (int)GameModeLaunchKind.Custom
            || (!GameLayout.HasValidationError && !DesktopLayout.HasValidationError))
        && !ActionLists.Any(list => list.HasValidationError);

    private void ForgetDisplay(DisplayLayoutEditorRow? row)
    {
        if (row is null) { return; }
        GameLayout.Forget(row);
        // The same catalog entry backs a row in each editor; forgetting it must remove both.
        foreach (DisplayLayoutEditorRow other in DesktopLayout.Rows
            .Where(candidate => candidate.Display == row.Display).ToList())
        {
            DesktopLayout.Forget(other);
        }
        KnownDisplays.Remove(row.Display);
        if (_waitForDisplay is { } wait && row.Target?.Matches(wait) == true) { _waitForDisplay = null; }
        // Take the layouts from the editors before reloading them, or the reload adopts the saved
        // output for the display that was just forgotten and puts the row straight back.
        _gameLayout = GameLayout.Build();
        _desktopLayout = DesktopLayout.Build();
        RefreshLaunchRows();
    }

    private void RebindDisplay(DisplayLayoutEditorRow? row)
    {
        if (row is null || RebindChoiceIndex < 0 || RebindChoiceIndex >= _present.Count) { return; }
        DisplayTargetIdentity target = _present[RebindChoiceIndex];
        if (!row.NeedsRebind)
        {
            StatusText = "That row already names a display.";
            return;
        }

        // The chosen display usually already has its own catalog row, because it is connected and
        // Settings observed it on open. Merging is what the user means: the migrated row carries
        // the values, the real row carries the identity, and two rows for one monitor could never
        // both be applied.
        DisplayLayoutEditorRow? existing = GameLayout.Rows.FirstOrDefault(
            candidate => candidate != row && candidate.Target?.Matches(target) == true);
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
            if (!KnownDisplays.Contains(row.Display)) { KnownDisplays.Add(row.Display); }
        }
        StatusText = $"{target.FriendlyName} bound. Save to keep it.";
        RefreshLaunchSummary();
    }

    private IReadOnlyList<PluginActionOption> ReadPluginActions()
    {
        try { return _services.ReadPluginActions(); }
        catch (Exception ex)
        {
            _services.Report("Could not read the running plugin actions for Settings", ex);
            return [];
        }
    }

    private void RemoveActionStep(PluginActionStepEditorRow? row)
    {
        if (row is null) { return; }
        foreach (PluginActionListEditor list in ActionLists.Where(list => list.Rows.Contains(row)))
        {
            list.Remove(row);
        }
    }

    private void MoveActionStep(PluginActionStepEditorRow? row, int delta)
    {
        if (row is null) { return; }
        foreach (PluginActionListEditor list in ActionLists.Where(list => list.Rows.Contains(row)))
        {
            list.Move(row, delta);
        }
    }

    /// <summary>Captures the desktop as it is now into the layout the selected section owns.</summary>
    /// <param name="desktop">True for the Desktop layout, false for the Game Mode layout.</param>
    private void SnapshotLayout(bool desktop)
    {
        try
        {
            DisplayArrangement arrangement = _services.CaptureDisplays();
            DisplayLayout captured = new([.. arrangement.Targets
                .Where(target => target is { Active: true, Current: not null })
                .Select(target => target.Current!)]);
            if (DisplayLayouts.Describe(captured) is { } refused)
            {
                StatusText = "This desktop cannot be saved as a layout: " + refused;
                return;
            }
            // Both editors keep whatever the other one holds, so a Snapshot for one layout never
            // discards edits made to the other.
            _gameLayout = desktop ? GameLayout.Build() : captured;
            _desktopLayout = desktop ? captured : DesktopLayout.Build();
            MergeCatalog(arrangement);
            RefreshLaunchRows();
            StatusText = desktop
                ? "Desktop layout captured. Save to keep it."
                : "Game Mode layout captured. Save to keep it.";
        }
        catch (Exception ex)
        {
            _services.Report("Capturing the current display layout failed", ex);
            StatusText = "Could not read the current display layout.";
        }
    }

    /// <summary>Adds what this observation knows about each display to the remembered catalog, so a
    /// display stays configurable after it is unplugged.</summary>
    private void MergeCatalog(DisplayArrangement arrangement)
    {
        _present = [.. arrangement.Targets.Where(target => target.Available).Select(target => target.Target)];
        foreach (DisplayTargetObservation observed in arrangement.Targets)
        {
            if (!observed.Available) { continue; }
            KnownDisplay? existing = KnownDisplays.FirstOrDefault(
                display => display.Target?.Matches(observed.Target) == true);
            if (existing is null)
            {
                existing = new KnownDisplay { Target = observed.Target };
                KnownDisplays.Add(existing);
            }
            existing.Target = observed.Target;
            existing.LastSeen = arrangement.CapturedAt;
            if (!observed.Active) { continue; }
            // Only an active display can be asked what it supports, which is the whole reason the
            // answers are remembered: an unplugged television has none of this to give.
            if (_services.ReadDisplayFacts(observed.Target) is { } facts)
            {
                if (facts.Modes.Count > 0) { existing.Modes = [.. facts.Modes]; }
                existing.HdrSupported |= facts.HdrSupported;
                existing.MaximumDpiPercent = Math.Max(existing.MaximumDpiPercent, facts.MaximumDpiPercent);
            }
            if (observed.Current?.Hdr is not null) { existing.HdrSupported = true; }
            if (observed.Current is { } current)
            {
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
    }

    /// <summary>Writes the launch fields the page owns over a fresh configuration. The remembered
    /// display catalog is merged rather than replaced: the running shell adds to it whenever a
    /// display appears, and this window's copy may be older than that.</summary>
    /// <param name="launch">The section to write into.</param>
    private void ApplyLaunchTo(GameModeLaunchConfiguration launch)
    {
        launch.Kind = (GameModeLaunchKind)Math.Clamp(GameModeLaunchKindIndex, 0, 1);
        launch.Return = (GameModeReturn)Math.Clamp(GameModeReturnIndex, 0, 1);
        launch.GameLayout = GameLayout.Build();
        launch.DesktopLayout = DesktopLayout.Build();
        launch.WaitForDisplay = WaitForDisplayIndex > 0 && WaitForDisplayIndex <= KnownDisplays.Count
            ? KnownDisplays[WaitForDisplayIndex - 1].Target
            : null;
        launch.EnterActions = ActionLists[0].Build();
        launch.LeaveActions = ActionLists[1].Build();
        launch.DesktopStartupActions = ActionLists[2].Build();
        launch.DesktopWakeActions = ActionLists[3].Build();
        foreach (KnownDisplay display in KnownDisplays)
        {
            if (display.Target is not { } target) { continue; }
            KnownDisplay? stored = launch.KnownDisplays.FirstOrDefault(
                other => other.Target?.Matches(target) == true);
            if (stored is null) { launch.KnownDisplays.Add(display); continue; }
            stored.Target = target;
            stored.HdrSupported |= display.HdrSupported;
            stored.MaximumDpiPercent = Math.Max(stored.MaximumDpiPercent, display.MaximumDpiPercent);
            if (display.LastSeen > stored.LastSeen) { stored.LastSeen = display.LastSeen; }
        }
    }

    private DisplayLayout? _gameLayout;
    private DisplayLayout? _desktopLayout;
    private IReadOnlyList<DisplayTargetIdentity> _present = [];
    private DisplayTargetIdentity? _waitForDisplay;
    private List<PluginActionStep> _enterActions = [];
    private List<PluginActionStep> _leaveActions = [];
    private List<PluginActionStep> _desktopStartupActions = [];
    private List<PluginActionStep> _desktopWakeActions = [];

    internal sealed record SaveRequest(
        AppConfig Values,
        SplashConfig Splash,
        IReadOnlyDictionary<string, CapabilityValue> PluginEdits,
        IReadOnlyList<DeviceAuthoredProfile>? DeviceProfiles,
        string PluginDevice,
        string PluginId,
        bool AutoTdpEdited,
        bool ControllerTargetEdited,
        bool GlyphSelectionEdited,
        bool QuickSetupWasAnswered)
    {
        internal IReadOnlyList<CommonPluginInstanceConfig> CommonPluginEdits { get; init; } = [];
    }

    internal sealed record SaveResult(
        AppConfig Config,
        IReadOnlyList<string> FailedSlots,
        string? Failure);

    /// <summary>Captures every UI-owned value into an isolated graph on the UI thread.</summary>
    private SaveRequest CaptureSaveRequest()
    {
        SplashConfig splash = BuildSplashConfig();
        AppConfig values = ConfigStore.CloneJson(_config, ConfigJsonContext.Default.AppConfig);
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
            _deviceGlyphSelectionEdited,
            QuickSetupAnswered)
        {
            CommonPluginEdits = CommonPlugins.Where(row => row.Edited).Select(row => row.Capture()).ToArray(),
        };
    }

    private async Task SaveWithStatusAsync()
    {
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
            SaveRequest request = CaptureSaveRequest();
            SaveResult result = await _services.Persist(request);
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

    /// <summary>Applies the UI-owned fields over a FRESH load and saves that.
    /// While this window is open, the elevated one-shots (UAC, lock-on-wake) and
    /// the shell persist registry snapshots and display-scale state to the
    /// same file; serializing the startup-time _config would reset every one of
    /// those fields to defaults on disk, breaking exact restore on uninstall.
    /// The config mutex only serializes individual reads/writes — it cannot
    /// merge — so the merge has to happen here.</summary>
    public void Save()
    {
        _services.BeginImportSession();
        try
        {
            SaveResult result = _services.Persist(CaptureSaveRequest()).GetAwaiter().GetResult();
            CompletePersistedSave(result);
            _services.ApplySteamInput(result.Config).GetAwaiter().GetResult();
            Raise(nameof(SteamInputShimStatusText));
        }
        finally
        {
            _services.EndImportSession();
        }
    }

    /// <summary>Applies an immutable UI-thread snapshot onto a fresh on-disk load.</summary>
    internal static void ApplyCapturedValues(
        AppConfig config,
        SaveRequest request,
        SplashConfig preparedSplash)
    {
        AppConfig values = request.Values;
        foreach (var edit in request.CommonPluginEdits)
        {
            config.PluginInstances.RemoveAll(entry => entry.PluginId == edit.PluginId && entry.InstanceId == edit.InstanceId);
            config.PluginInstances.Add(new() { PluginId = edit.PluginId, InstanceId = edit.InstanceId, Enabled = edit.Enabled });
        }
        config.SteamAutoRelaunch = values.SteamAutoRelaunch;
        config.SteamLaunchUnelevated = values.SteamLaunchUnelevated;
        config.SteamGridDbApiKey = values.SteamGridDbApiKey;
        config.ScreenscraperEnabled = values.ScreenscraperEnabled;
        config.ScreenscraperDevId = values.ScreenscraperDevId;
        config.ScreenscraperDevPassword = values.ScreenscraperDevPassword;
        config.ScreenscraperUser = values.ScreenscraperUser;
        config.ScreenscraperUserPassword = values.ScreenscraperUserPassword;
        config.StartupDelayMs = values.StartupDelayMs;
        config.StaggerDelayMs = values.StaggerDelayMs;
        config.BootSplashEnabled = values.BootSplashEnabled;
        config.StartAtSignIn = values.StartAtSignIn;
        config.StartMode = values.StartMode;
        config.SteamAutostartTakeoverAccepted = values.SteamAutostartTakeoverAccepted;

        // Everything except the runtime's own recovery record, which Settings must never write:
        // a window left open across a crash would otherwise discard the layout a recovery start
        // has to put back.
        config.GameModeLaunch = values.GameModeLaunch;

        config.SteamInputLeaseEnabled = values.SteamInputLeaseEnabled;
        config.SteamInputManagementEnabled = values.SteamInputManagementEnabled;
        config.DeviceIntegration.Enabled = values.DeviceIntegration.Enabled;
        config.DeviceIntegration.ControllerManagementEnabled =
            values.DeviceIntegration.ControllerManagementEnabled;
        if (request.AutoTdpEdited)
        {
            config.DeviceIntegration.AutoTdpEnabled = values.DeviceIntegration.AutoTdpEnabled;
        }
        if (request.ControllerTargetEdited)
        {
            config.DeviceIntegration.ControllerTarget = values.DeviceIntegration.ControllerTarget;
        }
        if (request.GlyphSelectionEdited)
        {
            config.DeviceIntegration.GlyphSelection = values.DeviceIntegration.GlyphSelection;
        }

        if ((request.PluginEdits.Count > 0 || request.DeviceProfiles is not null)
            && request.PluginDevice.Length > 0
            && request.PluginId.Length > 0)
        {
            PluginSettingsScope scope = FindOrAddSaveScope(
                config,
                request.PluginDevice,
                request.PluginId);
            foreach ((string settingId, CapabilityValue value) in request.PluginEdits)
            {
                PluginSettingValue? entry = scope.Values.FirstOrDefault(candidate =>
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
                scope.Profiles = [.. request.DeviceProfiles];
            }
        }

        config.Performance.Enabled = values.Performance.Enabled;
        config.Performance.FrameLimitStrategy = values.Performance.FrameLimitStrategy;
        config.Performance.OsdCustomOrder = values.Performance.OsdCustomOrder;
        config.Performance.OsdCustomTime = values.Performance.OsdCustomTime;
        config.Performance.OsdCustomFps = values.Performance.OsdCustomFps;
        config.Performance.OsdCustomCpu = values.Performance.OsdCustomCpu;
        config.Performance.OsdCustomRam = values.Performance.OsdCustomRam;
        config.Performance.OsdCustomGpu = values.Performance.OsdCustomGpu;
        config.Performance.OsdCustomVram = values.Performance.OsdCustomVram;
        config.Performance.OsdCustomBattery = values.Performance.OsdCustomBattery;
        if (request.QuickSetupWasAnswered)
        {
            QuickSetup.MarkCompleted(config);
        }
        config.Cef.Enabled = values.Cef.Enabled;
        config.Cef.LibraryTabs = values.Cef.LibraryTabs;
        config.Cef.CardManager = values.Cef.CardManager;
        config.Cef.SdFormat = values.Cef.SdFormat;
        config.Cef.Artwork = values.Cef.Artwork;
        config.Cef.WifiIndicator = values.Cef.WifiIndicator;
        config.Cef.NativeQuickAccess = values.Cef.NativeQuickAccess;
        config.Cef.DownloadKeepAwake = values.Cef.DownloadKeepAwake;
        config.Cef.DownloadQueueSort = values.Cef.DownloadQueueSort;
        config.Cef.ConnectedLibraryCarousel = values.Cef.ConnectedLibraryCarousel;
        config.Cef.CarouselShowUninstalled = values.Cef.CarouselShowUninstalled;
        config.MuteWhileDisplayOff = values.MuteWhileDisplayOff;
        config.ResuspendUnexplainedWakes = values.ResuspendUnexplainedWakes;
        config.LogVerbosity = values.LogVerbosity;
        config.Hotkey = values.Hotkey;
        config.GamepadChord = values.GamepadChord;
        config.Gestures.BottomEdge = values.Gestures.BottomEdge;
        config.Gestures.TopEdge = values.Gestures.TopEdge;
        config.Gestures.LeftEdgeSteamMenu = values.Gestures.LeftEdgeSteamMenu;
        config.Gestures.RightEdgeSteamQuickAccess = values.Gestures.RightEdgeSteamQuickAccess;
        config.GlyphStyle = values.GlyphStyle;
        config.AccentColor = values.AccentColor;
        config.Splash = preparedSplash;
        config.StartupApps = [.. values.StartupApps];
    }

    private static PluginSettingsScope FindOrAddSaveScope(
        AppConfig config,
        string deviceDefinitionId,
        string pluginId)
    {
        PluginSettingsScope? scope = config.DeviceIntegration.PluginSettings.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceDefinitionId, deviceDefinitionId, StringComparison.Ordinal)
            && string.Equals(candidate.PluginId, pluginId, StringComparison.Ordinal));
        if (scope is null)
        {
            scope = new PluginSettingsScope
            {
                DeviceDefinitionId = deviceDefinitionId,
                PluginId = pluginId,
            };
            config.DeviceIntegration.PluginSettings.Add(scope);
        }
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
        // Mutate re-acquires the same named mutex inside this scope; a Win32 mutex
        // is owned per thread with a recursion count, so those nested acquisitions
        // balance their own releases and the outer hold survives (see AcquireLock).
        using (ConfigStore.AcquireLock())
        {
            // Captured BEFORE ApplyTo overwrites them: if a staged copy cannot be
            // promoted the persisted config has to go back to the path whose file is
            // actually there.
            var previousLogoPath = "";
            var previousBackgroundPath = "";
            // Any throw from here to Commit leaves the transaction uncommitted, and the
            // enclosing `using` rolls it back: the live splash assets stay untouched.
            config = ConfigStore.Mutate(fresh =>
            {
                previousLogoPath = fresh.Splash.LogoImagePath;
                previousBackgroundPath = fresh.Splash.BackgroundImagePath;
                ApplyCapturedValues(fresh, request, splash);
            });
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
        foreach (var row in CommonPlugins) { row.AcceptSaved(); }
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
            throw new System.IO.IOException(result.Failure);
        }
        _services.Report("Settings saved.", null);
    }

    /// <summary>Brings Steam's directory in line with the setting that was just
    /// persisted.</summary>
    /// <remarks>
    /// Deployment follows persisted intent and never precedes it: a save that failed
    /// must not leave Steam's directory describing a setting nobody wrote. It also
    /// runs outside <c>ConfigStore.AcquireLock</c> - that lock's timeout is sized for
    /// one small JSON write, not for file copies into Program Files.
    /// </remarks>
    private void ApplySteamInputManagementAfterSave(AppConfig config)
    {
        SteamInputShim.SetEnabled(config.SteamInputManagementEnabled);
        var status = SteamInputShim.Reconcile("settings-save");
        if (status.State == SteamInputShimState.Failed
            && status.Detail == "access denied"
            // Tri-state: only retry when we KNOW we are unelevated. Unknown stays
            // put rather than throwing a UAC prompt at a user who may not need one.
            && ElevationCheck.IsCurrentProcessElevated() == false)
        {
            // Steam normally lives under Program Files, which a desktop-mode Settings
            // process cannot write. Without this the toggle would appear to do nothing
            // at all on most machines.
            Log.Warn("Steam Input shim write refused - retrying elevated.");
            SelfElevation.RunElevatedAction(
                config.SteamInputManagementEnabled
                    ? "--apply-steam-input-shim"
                    : "--remove-steam-input-shim",
                "Steam Input shim");
            SteamInputShim.Probe();
        }
        if (!config.SteamInputManagementEnabled)
        {
            WarnAboutShimOnlyLaunchFixes(config);
        }
        ApplySteamAutostartAfterSave(config);
    }

    /// <summary>Turns Windows' own Steam startup entries off once the takeover has been persisted.
    /// Like the shim deployment, it follows persisted intent and runs outside the config lock: a
    /// machine-scope entry needs an elevation prompt, which has no business inside it.</summary>
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
            var result = SteamAutostartService.Apply(enabled, allowElevation: true);
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

    /// <summary>Names the games whose stored launch fix just stopped blocking.</summary>
    /// <remarks>
    /// Turning Steam Input Management off changes what an already-written
    /// <c>--input-lease</c> does: there is no resident shim left for it to use, so it
    /// fails open. One log line is what makes "why did my controller fix stop working"
    /// answerable from a pasted log instead of a bisect.
    /// </remarks>
    private static void WarnAboutShimOnlyLaunchFixes(AppConfig config)
    {
        var affected = config.LaunchWrappers
            .Where(wrapper => wrapper.Mode.HasFlag(LaunchWrapperMode.InputLease))
            .Select(wrapper => wrapper.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        if (affected.Count == 0)
        {
            return;
        }
        Log.Warn(
            $"Steam Input Management off - {affected.Count} game(s) still carry the shim-only " +
            $"launch fix (appids: {string.Join(", ", affected)}); re-apply the launch fix to " +
            "switch them to injection.");
    }

    private static bool Failed(IReadOnlyList<string> failedSlots, string slot) =>
        failedSlots.Contains(slot, StringComparer.OrdinalIgnoreCase);

    /// <summary>Syncs the editor back to the materialized copies — only once they ARE
    /// the live files: keeping the originally picked paths would re-copy on every save
    /// and, if the source vanished, clobber the stable copy's path with a dead one on
    /// the next save.
    /// <para>A FAILED slot is skipped on purpose. Whether the sidecar could not be
    /// staged (unreadable source, uncreatable target) or not promoted (locked live
    /// file), config.json keeps the conservative PREVIOUS path while the view model
    /// keeps the user's PICK, so pressing Save again after fixing the file actually
    /// retries that image instead of silently re-saving the old one.</para></summary>
    /// <param name="persisted">The splash section as it was just persisted (its paths
    /// are the materialized ones for every slot that went live).</param>
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

    /// <summary>Puts the previously persisted image path back for every slot that did
    /// not end up as a live copy — staging failed, or the staged copy could not be
    /// promoted — so the persisted state always names an image WSGM owns and that
    /// exists. Pure: it only mutates <paramref name="config"/> and builds the
    /// message — the caller performs the write (see
    /// <see cref="RestoreSlotsThatFailedToPromote"/>), so this step is testable without
    /// going anywhere near the real per-user config file.</summary>
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

    /// <summary>Repairs the config for every slot whose staged copy could not be
    /// promoted and re-persists it through <paramref name="save"/>.</summary>
    /// <param name="config">The just-saved configuration, repaired in place.</param>
    /// <param name="failedSlots">The slot names reported by the splash-asset commit.</param>
    /// <param name="previousLogoPath">The logo path persisted before this save.</param>
    /// <param name="previousBackgroundPath">The background path persisted before this save.</param>
    /// <param name="save">Writes the repaired configuration (ConfigStore.Save in production).</param>
    /// <returns>The message to fail the save with, or null when every slot committed.
    /// A failing repair write does NOT replace it: the promotion failure is the cause
    /// the user has to act on, and letting the secondary write's exception escape would
    /// mask it — so that one is logged instead.</returns>
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

    /// <summary>Builds an isolated configuration snapshot for the window's local
    /// overlay/taskbar preview surfaces, carrying every unsaved edit.</summary>
    /// <returns>A copy that will not change when this view model is later saved.</returns>
    public AppConfig SnapshotForPreview()
    {
        AppConfig snapshot = ConfigStore.CloneJson(_config, ConfigJsonContext.Default.AppConfig);
        ApplyTo(snapshot);
        // A real copy through the production JSON contract: the preview's
        // OverlayController must not see later Save() mutations of the live
        // _config outside its ApplyConfig wholesale-replace contract.
        return ConfigStore.CloneJson(snapshot, ConfigJsonContext.Default.AppConfig);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Editable placement of one boot-splash element. Writes through to the
/// placement object inside the view model's splash section, so the section and the
/// editor can never disagree; <see cref="Load"/> re-points it after a preset apply
/// or theme import and re-raises every property.</summary>
public sealed class SplashPlacementEditor : INotifyPropertyChanged
{
    /// <summary>Raised after a placement field changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private SplashElementPlacement _placement = new();

    /// <summary>Points the editor at a splash section's placement object.</summary>
    /// <param name="placement">The placement this editor writes through to.</param>
    internal void Load(SplashElementPlacement placement)
    {
        _placement = placement;
        Raise(nameof(Mode));
        Raise(nameof(Anchor));
        Raise(nameof(PaddingX));
        Raise(nameof(PaddingY));
        Raise(nameof(X));
        Raise(nameof(Y));
        Raise(nameof(IsAnchor));
        Raise(nameof(IsAbsolute));
    }

    /// <summary>Gets or sets how the element is positioned. Also switches which
    /// field group (<see cref="IsAnchor"/>/<see cref="IsAbsolute"/>) the editor shows.</summary>
    public SplashPlacementMode Mode
    {
        get => _placement.Mode;
        set
        {
            _placement.Mode = value;
            Raise(nameof(Mode));
            Raise(nameof(IsAnchor));
            Raise(nameof(IsAbsolute));
        }
    }

    /// <summary>Whether the editor shows the anchor + padding fields.</summary>
    public bool IsAnchor => _placement.Mode == SplashPlacementMode.Anchor;

    /// <summary>Whether the editor shows the absolute X/Y fields.</summary>
    public bool IsAbsolute => _placement.Mode == SplashPlacementMode.Absolute;

    /// <summary>Gets or sets the nine-grid anchor.</summary>
    public SplashPlacementAnchor Anchor
    {
        get => _placement.Anchor;
        set { _placement.Anchor = value; Raise(nameof(Anchor)); }
    }

    /// <summary>Gets or sets the horizontal padding from the anchored edge.</summary>
    public int PaddingX { get => _placement.PaddingX; set { _placement.PaddingX = value; Raise(nameof(PaddingX)); } }

    /// <summary>Gets or sets the vertical padding from the anchored edge.</summary>
    public int PaddingY { get => _placement.PaddingY; set { _placement.PaddingY = value; Raise(nameof(PaddingY)); } }

    /// <summary>Gets or sets the absolute X coordinate in logical pixels.</summary>
    public int X { get => _placement.X; set { _placement.X = value; Raise(nameof(X)); } }

    /// <summary>Gets or sets the absolute Y coordinate in logical pixels.</summary>
    public int Y { get => _placement.Y; set { _placement.Y = value; Raise(nameof(Y)); } }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

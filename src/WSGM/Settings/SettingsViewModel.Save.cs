using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Themes;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
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

    /// <summary>
    ///     The shared fields' values this window last loaded or saved: what "the user changed this
    ///     here" is measured against. Moved forward on each save, so a field saved once is not taken
    ///     as edited for the rest of the window's life.
    /// </summary>
    private readonly Dictionary<string, object> _sharedBaseline = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets whether an asynchronous save is currently persisting its captured
    ///     settings snapshot. Completion acknowledges captured values and keeps later edits pending.
    /// </summary>
    public bool IsSaving
    {
        get;
        private set => SetFieldIfChanged(ref field, value, nameof(IsSaving));
    }

    /// <summary>
    ///     Gets the command that merges and persists the edited settings,
    ///     reporting the outcome (including the last-save time) via <see cref="StatusText" />.
    /// </summary>
    public AsyncRelayCommand SaveCommand { get; }

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
        config.OtherManagersTakeoverAccepted = OtherManagersTakeoverAccepted;
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
        config.GameLibrary.ArtworkPreference = GameLibraryArtworkPreferenceIndex == 1
            ? ArtworkPreference.Providers
            : ArtworkPreference.Catalog;
        config.DeviceIntegration.Enabled = DeviceIntegrationEnabled;
        config.DeviceIntegration.ControllerManagementEnabled = DeviceControllerManagementEnabled;
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
            CommonPluginEdits = [.. CommonPlugins.Where(row => row.Edited).Select(row => row.Capture())],
            DeviceAutoTdp = DeviceAutoTdpEnabled,
            DeviceTargetIndex = DeviceControllerTargetIndex,
            DeviceGlyphIndex = DeviceGlyphSelectionIndex
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

        // Settings edits only the Game Library's defaults. Its sources and folders are ticked and
        // added on the library's own surfaces while this window may be open, so they come from the
        // fresh load: the window's older lists would undo them.
        var library = fresh.GameLibrary;
        library.DefaultMode = config.GameLibrary.DefaultMode;
        library.ImportUnroutable = config.GameLibrary.ImportUnroutable;
        library.ArtworkPreference = config.GameLibrary.ArtworkPreference;
        config.GameLibrary = library;
        config.LaunchWrappers = fresh.LaunchWrappers;
        config.SteamDelayMs = fresh.SteamDelayMs;
        config.SteamAutostartDisabled = fresh.SteamAutostartDisabled;
        config.OtherManagersDisabled = fresh.OtherManagersDisabled;
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
            if (!BootManifestWriter.WriteCurrent(config))
            {
                failure = string.Join(" ", new[] { failure, "The sign-in startup preference could not be applied." }
                    .Where(message => !string.IsNullOrEmpty(message)));
            }
        }

        return new SaveResult(config, failedSlots, failure);
    }

    private void CompletePersistedSave(SaveResult result)
    {
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
        ApplyOtherManagersAfterSave(config);
    }

    /// <summary>
    ///     Turns the other handheld managers off once the takeover has been persisted, for the same
    ///     reasons as the Steam autostart: persisted intent, outside the config lock, prompt allowed.
    /// </summary>
    /// <param name="config">The configuration that was just written.</param>
    private static void ApplyOtherManagersAfterSave(AppConfig config)
    {
        if (!config.OtherManagersTakeoverAccepted)
        {
            return;
        }

        try
        {
            var detected = OtherManagers.Detect();
            if (detected.Count == 0)
            {
                return;
            }

            var result = OtherManagers.Apply(detected, true);
            if (result.Failed.Count > 0)
            {
                Log.Warn("Other managers takeover incomplete: " + string.Join(", ", result.Failed));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Other managers takeover failed: {ex.Message}");
        }
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
        foreach (var row in CommonPlugins)
        {
            foreach (var saved in request.CommonPluginEdits)
            {
                row.AcceptSaved(saved);
            }
        }

        _savedAutoTdp = request.DeviceAutoTdp;
        _savedTargetIndex = request.DeviceTargetIndex;
        _savedGlyphIndex = request.DeviceGlyphIndex;
        _deviceAutoTdpEdited = DeviceAutoTdpEnabled != _savedAutoTdp;
        _deviceControllerTargetEdited = DeviceControllerTargetIndex != _savedTargetIndex;
        _deviceGlyphSelectionEdited = DeviceGlyphSelectionIndex != _savedGlyphIndex;
        var sameScope = _pluginSettingsDevice == request.PluginDevice
                        && _pluginSettingsPlugin == request.PluginId;
        foreach (var (id, value) in request.PluginEdits)
        {
            if (sameScope && _pluginSettingEdits.TryGetValue(id, out var current) && current == value)
            {
                _pluginSettingEdits.Remove(id);
            }
        }

        if (sameScope && request.DeviceProfiles is not null
                      && JsonSerializer.Serialize(DeviceProfiles.Select(profile => profile.ToStored()).ToArray())
                      == JsonSerializer.Serialize(request.DeviceProfiles))
        {
            _deviceProfilesEdited = false;
        }

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
        internal bool DeviceAutoTdp { get; init; }
        internal int DeviceTargetIndex { get; init; }
        internal int DeviceGlyphIndex { get; init; }
    }

    internal sealed record SaveResult(
        AppConfig Config,
        IReadOnlyList<string> FailedSlots,
        string? Failure);
}

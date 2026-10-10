using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Themes;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
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
    ///     Gets the accent config.json holds as far as this window knows: the loaded value, moved forward by
    ///     every committed save. Closing restores it after an abandoned preview without reading the store,
    ///     because Settings is the only writer of the accent.
    /// </summary>
    internal string SavedAccentColor { get; private set; }

    /// <summary>
    ///     Gets the command that merges and persists the edited settings,
    ///     reporting the outcome (including the last-save time) via <see cref="StatusText" />.
    /// </summary>
    public AsyncRelayCommand SaveCommand { get; }

    // --- Save ---
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
        config.DeviceIntegration.AutoTdpEnabled = DeviceAutoTdpEnabled;
        config.Profiles.Global.ControllerTarget = (ManagedControllerTarget)Math.Clamp(DeviceControllerTargetIndex,
            0, Enum.GetValues<ManagedControllerTarget>().Length - 1);
        config.DeviceIntegration.GlyphSelection = (DeviceGlyphSelection)Math.Clamp(DeviceGlyphSelectionIndex,
            0, Enum.GetValues<DeviceGlyphSelection>().Length - 1);

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
    /// <returns>
    ///     A detached request including edited-field baselines; safe for the persistence worker while the UI continues
    ///     editing.
    /// </returns>
    internal SaveRequest CaptureSaveRequest()
    {
        var splash = BuildSplashConfig();
        var values = new AppConfig();
        ApplyTo(values, splash);
        values.SteamCefPluginWarningAccepted = SteamCefPluginWarningAccepted;
        // ApplyTo intentionally reuses several bound objects. One final contract copy
        // makes the worker independent from edits made while the save is running.
        values = ConfigJson.Clone(values, ConfigJsonContext.Tolerant.AppConfig);
        splash = values.Splash;
        return new SaveRequest(
            values,
            splash,
            _deviceProfilesEdited ? [.. DeviceProfiles.Select(static profile => profile.ToStored())] : null,
            _deviceProfileDefinition,
            _deviceProfileFamily)
        {
            SharedEdits =
            [
                .. WsgmSharedSettings.All.Where(field => !Equals(field.Read(values), _sharedBaseline[field.Name]))
                    .Select(field => field.Name)
            ],
            SharedValues = WsgmSharedSettings.All.ToDictionary(field => field.Name, field => field.Read(values),
                StringComparer.Ordinal),
            ForgottenDisplays = [.. _forgottenDisplays],
            CommonPluginEdits =
                [.. CommonPlugins.Concat(GraphicsDrivers).Where(row => row.Edited).Select(row => row.Capture())]
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
            SaveResult result;
            try
            {
                result = await _services.Persist(request);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nothing was committed, so nothing outside the configuration follows it.
                _services.Report("Saving settings failed", ex);
                StatusText = $"Save failed: {ex.Message}";
                return;
            }

            // config.json is committed from here on, even when a splash image or the sign-in
            // preference then failed, so what follows it outside the configuration is applied too.
            AdvanceSharedBaseline(request);
            CompletePersistedSave(result, request);
            var applyFailure = await ApplyAfterSaveAsync(result.Config, result.Changes);
            Raise(nameof(SteamInputShimStatusText));
            StatusText = result.Failure is not null
                // The save did not do everything it said, so it must never read "Saved".
                ? $"Save failed: {result.Failure}"
                : applyFailure is not null
                    ? $"Saved; applying Steam Input failed: {applyFailure.Message}"
                    : $"Saved {DateTime.Now:HH:mm:ss}";
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

    private static SaveResult PersistSave(SaveRequest request, ConfigStore store)
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
        using var splashAssets = SplashAssets.Prepare(splash, store.Context);

        AppConfig config;
        SaveChanges changes;
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
        // The writer owns its read, publication, promotion repair and boot projection.
        using (var transaction = store.Transaction())
        {
            // Captured BEFORE ApplyTo overwrites them: if a staged copy cannot be
            // promoted the persisted config has to go back to the path whose file is
            // actually there.
            var previousLogoPath = "";
            var previousBackgroundPath = "";
            // Any throw from here to Commit leaves the transaction uncommitted, and the
            // enclosing `using` rolls it back: the live splash assets stay untouched.
            var fresh = transaction.Config;
            previousLogoPath = fresh.Splash.LogoImagePath;
            previousBackgroundPath = fresh.Splash.BackgroundImagePath;
            (config, changes) = SettingsSaveMerge.Apply(fresh, request, splash);
            transaction.Save(config);
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
                config, failedSlots, previousLogoPath, previousBackgroundPath, transaction.Save);
            // Keep the logon service's view in sync — every save may change the
            // enabled flag or the elevation inputs (elevated startup apps).
            if (!BootManifestWriter.WriteCurrent(transaction.Read, store.Context))
            {
                failure = string.Join(" ", new[] { failure, "The sign-in startup preference could not be applied." }
                    .Where(message => !string.IsNullOrEmpty(message)));
            }
        }

        return new SaveResult(config, failedSlots, failure, changes);
    }

    private void CompletePersistedSave(SaveResult result, SaveRequest request)
    {
        AdoptMaterializedPaths(result.Config.Splash, result.FailedSlots);
        // The accent this window restores when it closes: the commit happened, even when a splash
        // repair or the boot manifest then failed.
        SavedAccentColor = result.Config.AccentColor;
        // Re-color the running UI live; Application.Current is null in unit tests.
        if (Application.Current is { } app)
        {
            AccentPalette.Apply(app, AccentPalette.Parse(result.Config.AccentColor));
        }

        if (result.Failure is not null)
        {
            _services.Report("Saving settings failed", new IOException(result.Failure));
            return;
        }

        var graphicsEdits = request.CommonPluginEdits.Where(edit => BuiltinGpuDrivers.Contains(edit.PluginId))
            .Select(edit => $"{edit.PluginId}={edit.Enabled}");
        _services.Report($"Settings saved. Shared changes=[{string.Join(',', request.SharedEdits)}], "
                         + $"graphics changes=[{string.Join(',', graphicsEdits)}], "
                         + $"device integration={result.Config.DeviceIntegration.Enabled}, "
                         + $"controller management={result.Config.DeviceIntegration.ControllerManagementEnabled}.",
            null);
    }

    /// <summary>
    ///     Brings what lives outside the configuration in line with what this save changed: the Steam
    ///     Input shim in Steam's directory, Windows' own Steam startup entries and the other handheld
    ///     managers.
    /// </summary>
    /// <remarks>
    ///     Deployment follows persisted intent and never precedes it, so this runs only after the commit.
    ///     It runs on a worker outside the writer transaction, whose timeout is sized for one small JSON
    ///     write, not for file copies into Program Files or an elevation prompt. Only a step this save
    ///     changed runs: an unrelated save neither reconciles the shim, which can raise a UAC prompt, nor
    ///     rescans for a takeover accepted earlier; the two "Check and take over" buttons are the explicit
    ///     re-check. A failing step does not skip the later ones.
    /// </remarks>
    /// <param name="saved">The configuration that was just written.</param>
    /// <param name="changes">What this save changed in the persisted configuration.</param>
    /// <returns>The Steam Input reconcile failure, or null.</returns>
    private Task<Exception?> ApplyAfterSaveAsync(AppConfig saved, SaveChanges changes)
    {
        if (changes is { Shim: false, SteamAutostartAccepted: false, OtherManagersAccepted: false })
        {
            return Task.FromResult<Exception?>(null);
        }

        var services = _services;
        return Task.Run(() =>
        {
            Exception? shimFailure = null;
            if (changes.Shim)
            {
                try
                {
                    services.ReconcileSteamInputShim(saved);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    services.Report("Applying Steam Input management after a save failed", ex);
                    shimFailure = ex;
                }
            }

            if (changes.SteamAutostartAccepted)
            {
                try
                {
                    var enabled = services.ScanSteamAutostart().Where(source => source.Enabled).ToArray();
                    if (enabled.Length > 0 && !services.ApplySteamAutostart(enabled).Complete)
                    {
                        Log.Warn("Steam autostart takeover incomplete: Windows may still start Steam itself.");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Warn($"Steam autostart takeover failed: {ex.Message}");
                }
            }

            if (changes.OtherManagersAccepted)
            {
                try
                {
                    var detected = services.DetectOtherManagers();
                    if (detected.Count > 0 && services.ApplyOtherManagers(detected) is { Failed.Count: > 0 } result)
                    {
                        Log.Warn("Other managers takeover incomplete: " + string.Join(", ", result.Failed));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Warn($"Other managers takeover failed: {ex.Message}");
                }
            }

            return shimFailure;
        });
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
    /// <param name="save">Writes repaired configuration through the active transaction.</param>
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
        var request = CaptureSaveRequest();
        var (snapshot, _) = SettingsSaveMerge.Apply(ConfigJson.Clone(_config, ConfigJsonContext.Tolerant.AppConfig),
            request, request.Splash);
        return ConfigJson.Clone(snapshot, ConfigJsonContext.Tolerant.AppConfig);
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
        foreach (var row in CommonPlugins.Concat(GraphicsDrivers))
        {
            foreach (var saved in request.CommonPluginEdits)
            {
                row.AcceptSaved(saved);
            }
        }

        var sameScope = _deviceProfileDefinition == request.DeviceDefinitionId
                        && _deviceProfileFamily == request.FamilyId;
        if (sameScope && request.DeviceProfiles is not null
                      && JsonSerializer.Serialize(DeviceProfiles.Select(profile => profile.ToStored()).ToList(),
                          ConfigJsonContext.Default.ListDeviceAuthoredProfile)
                      == JsonSerializer.Serialize(request.DeviceProfiles.ToList(),
                          ConfigJsonContext.Default.ListDeviceAuthoredProfile))
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
        foreach (var field in WsgmSharedSettings.All)
        {
            _sharedBaseline[field.Name] = field.Read(config);
        }
    }

    /// <summary>Detached UI snapshot captured once for a worker-side merge onto fresh configuration.</summary>
    /// <param name="Values">Only Settings-owned values are merged; this is not a replacement for the fresh persisted graph.</param>
    /// <param name="Splash">Captured splash settings and staged asset references.</param>
    /// <param name="DeviceProfiles">Authored profile replacement when edited, otherwise null to preserve fresh profiles.</param>
    /// <param name="DeviceDefinitionId">Exact device scope for authored profiles.</param>
    /// <param name="FamilyId">Plain built-in family identity.</param>
    internal sealed record SaveRequest(
        AppConfig Values,
        SplashConfig Splash,
        IReadOnlyList<DeviceAuthoredProfile>? DeviceProfiles,
        string DeviceDefinitionId,
        string FamilyId)
    {
        /// <summary>Display identities explicitly removed by this captured edit, preserving unrelated fresh catalog entries.</summary>
        internal IReadOnlyList<DisplayTargetIdentity> ForgottenDisplays { get; init; } = [];

        /// <summary>The shared fields the user changed in this window; the rest keep the saved value.</summary>
        internal IReadOnlyList<string> SharedEdits { get; init; } = [];

        /// <summary>This window's value of every shared field, the baseline once the save succeeds.</summary>
        internal IReadOnlyDictionary<string, object> SharedValues { get; init; } =
            new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>Only common-plugin instances edited against the window's acknowledged baseline.</summary>
        internal IReadOnlyList<CommonPluginInstanceConfig> CommonPluginEdits { get; init; } = [];
    }

    /// <summary>Persistence result used to acknowledge captured edits and run only required post-save reconciliation.</summary>
    /// <param name="Config">Fresh merged configuration used by the save.</param>
    /// <param name="FailedSlots">Splash asset slots that could not be promoted.</param>
    /// <param name="Failure">Transaction or sidecar failure, or null on success.</param>
    /// <param name="Changes">Captured semantic changes that authorize post-save work.</param>
    internal sealed record SaveResult(
        AppConfig Config,
        IReadOnlyList<string> FailedSlots,
        string? Failure,
        SaveChanges Changes);
}

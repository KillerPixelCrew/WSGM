using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    private readonly Lock _configDebounceGate = new();

    private Timer? _configDebounce;
    private long _configReloadGeneration;

    // Field-rooted deliberately: an unreferenced enabled FileSystemWatcher is
    // GC-collectible (it holds only a WeakReference to itself in its pending
    // ReadDirectoryChangesW state) and silently stops raising events.
    private FileSystemWatcher? _configWatcher;

    /// <summary>Saves one change from WSGM's settings page in Steam.</summary>
    /// <param name="change">The field to write, applied to a fresh strict load.</param>
    /// <param name="boot">Whether boot.json follows the change, as it does for the start settings.</param>
    /// <returns>What was persisted.</returns>
    /// <remarks>
    ///     One transaction, as WSGM Settings saves: the store's lock is held across the write and the
    ///     boot manifest, so the service never starts WSGM from a manifest older than the config. The
    ///     writer supplies its read result to boot projection without nesting store operations.
    /// </remarks>
    private AppConfig CommitWsgmSetting(Action<AppConfig> change, bool boot)
    {
        using (var transaction = _store.Transaction())
        {
            var persisted = transaction.Config;
            var before = System.Text.Json.JsonSerializer.Serialize(persisted, ConfigJsonContext.Tolerant.AppConfig);
            change(persisted);
            var after = System.Text.Json.JsonSerializer.Serialize(persisted, ConfigJsonContext.Tolerant.AppConfig);
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                transaction.Save();
            }
            if (boot)
            {
                BootManifestWriter.WriteCurrent(transaction.Read, _store.Context);
            }

            return persisted;
        }
    }

    /// <summary>
    ///     Applies a Steam Input Management change that arrived through a
    ///     config reload.
    /// </summary>
    /// <remarks>
    ///     The park/restore rename touches Steam's directory, so it runs off the UI
    ///     thread. Reconciles are idempotent and serialized inside
    ///     <see cref="SteamInputShim" />, which is what lets the Settings save path and
    ///     this watcher both fire without coordinating.
    /// </remarks>
    private static void ApplySteamInputManagement(bool enabled)
    {
        if (SteamInputShim.Enabled == enabled)
        {
            return;
        }

        SteamInputShim.SetEnabled(enabled);
        _ = Task.Run(() => SteamInputShim.Reconcile("settings-change"));
    }

    /// <summary>
    ///     Whether the automatic download wake lock may poll Steam: its CEF
    ///     query is autonomous Steam traffic, so it stays off in overlay-test mode
    ///     alongside the other injections that mode excludes.
    /// </summary>
    /// <param name="config">The configuration to read the gates from.</param>
    private bool AutoKeepAwakeEnabled(AppConfig config)
    {
        return !_overlayTestOnly && config.Cef is { Enabled: true, DownloadKeepAwake: true };
    }

    /// <summary>
    ///     Whether the shared Steam download poll has at least one consumer.
    ///     The mute feature reuses the same answer even when its automatic wake lock is
    ///     disabled; overlay-test still excludes all autonomous Steam traffic.
    /// </summary>
    /// <param name="config">The configuration to read the gates from.</param>
    private bool DownloadMonitoringEnabled(AppConfig config)
    {
        return !_overlayTestOnly
               && config.Cef.Enabled
               && (config.Cef.DownloadKeepAwake || config.MuteWhileDisplayOff);
    }

    private void WatchConfig()
    {
        try
        {
            _configWatcher = new FileSystemWatcher(_store.Context.Root, "config.json")
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };

            // The LOAD stays off the UI thread: it takes the cross-process config
            // mutex (2 s timeout) that a settings save holds across the write, the
            // splash-asset promotion and the boot manifest — 500 ms of debounce does
            // not reliably outlast that. Only the cheap, UI-affine apply is posted.
            void Reload(object? state)
            {
                _ = Task.Run(() =>
                {
                    var generation = Interlocked.Read(ref _configReloadGeneration);
                    var read = _store.Read();
                    if (read.Config is not { } config)
                    {
                        Log.Warn($"Config reload skipped: {read.Outcome}; the running state is retained.");
                        return;
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || generation != Interlocked.Read(ref _configReloadGeneration))
                        {
                            return;
                        }

                        // One instance for every reader: the volume OSD's UI-scale
                        // callback and DisplayScale's saved-scale snapshot must not
                        // drift onto different AppConfig objects.
                        _config = config;
                        Log.SetVerbosity(_verboseLogging ? LogVerbosity.Verbose : config.LogVerbosity);
                        Log.Observe(_profiles.ReloadAsync(_shutdownCancellation.Token), "Profile config reload", true);
                        ApplyDeviceConfig(config);
                        ApplyPerformanceConfig(config);
                        ApplyCefMasterSwitch(config.Cef.Enabled);
                        _steamUi?.ApplyHostSteamUi(config.Cef.Enabled);
                        if (config.Cef.Enabled)
                        {
                            _steamUi?.Apply(config.Cef.NativeQuickAccess);
                            _steamUi?.ApplySurfaceObservation(true);
                            ApplyGlyphConfig(config);
                        }

                        ApplySteamInputManagement(config.SteamInputManagementEnabled);
                        ApplyNetworkIndicator(config.Cef is { Enabled: true, WifiIndicator: true });
                        ApplyDownloadSort(config.Cef is { Enabled: true, DownloadQueueSort: true });
                        ApplyLibraryBadge(config.Cef is { Enabled: true, CardManager: true });
                        ApplyHomeCarousel(
                            config.Cef is { Enabled: true, ConnectedLibraryCarousel: true },
                            config.Cef.CarouselShowUninstalled);
                        ApplyScreensaverTimeouts(config.Cef.Enabled);
                        // The artwork settings are in this file too. The browser reads them live,
                        // but a page already open still shows the old tabs, and a response the old
                        // key earned is still cached against the new one.
                        _artwork?.ConfigurationChanged();
                        _libraryImport?.ConfigurationChanged();
                        _themes?.ConfigurationChanged();
                        _animations?.ConfigurationChanged();
                        _sounds?.ConfigurationChanged();
                        _wsgmSettings?.ConfigurationChanged();
                        _displayMute?.ApplyConfig(config.MuteWhileDisplayOff);
                        _chordMirror?.Apply(config.DeviceIntegration.KeepGuideChordEdits, _steamDeckTargetActive);
                        _overlay?.ApplyConfig(config);
                        Log.Debug($"Config reloaded at {Log.MinimumLevel} minimum log level.");
                        _startupWatcher?.Apply(config.StartupApps);
                        _keepAwake?.ApplyConfig(
                            AutoKeepAwakeEnabled(config),
                            DownloadMonitoringEnabled(config));
                    });
                });
            }

            // Changed/Renamed fire on threadpool threads — the swap must be locked
            // so two near-simultaneous events can't both dispose the same timer and
            // orphan one that still fires.
            void Debounce()
            {
                lock (_configDebounceGate)
                {
                    Interlocked.Increment(ref _configReloadGeneration);
                    _configDebounce ??= new Timer(
                        Reload, null, Timeout.Infinite, Timeout.Infinite);
                    _configDebounce.Change(500, Timeout.Infinite);
                }
            }

            _configWatcher.Changed += (_, _) => Debounce();
            _configWatcher.Renamed += (_, _) => Debounce();
            // Internal-buffer overflow or a directory-level error kills the change
            // events silently — settings would stop applying for the rest of the
            // session with nothing in the log to diagnose it from. Log, reload once
            // (the missed write is already on disk), and re-arm by restarting the
            // watch. Deliberately NOT a recreate: this handler would resubscribe
            // itself and a persistently failing directory would spin.
            _configWatcher.Error += (sender, e) =>
            {
                Log.Warn($"Config watcher error: {e.GetException().Message} — re-arming.");
                Debounce();
                try
                {
                    if (sender is not FileSystemWatcher watcher)
                    {
                        return;
                    }

                    watcher.EnableRaisingEvents = false;
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Config watcher could not be re-armed: {ex.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Config watcher not available: {ex.Message}");
        }
    }

    private void ApplyDeviceConfig(AppConfig config)
    {
        _ = ApplyCommonPluginConfigAsync(config);
        var coordinator = _deviceCoordinator;
        if (coordinator is null)
        {
            return;
        }

        // AutoTDP is applied before the coordinator: turning Device Integration off must stop
        // AutoTDP and restore the previous power limit while the capability is still writable.
        _autoTdp?.SetTraceEnabled(config.AutoTdpTraceEnabled);
        _autoTdp?.Apply(ShouldRunAutoTdp(config.DeviceIntegration));
        Log.Observe(ApplyDeviceConfigAndTargetAsync(coordinator, config), "Device cycle config apply", true);
    }

    private async Task ApplyDeviceConfigAndTargetAsync(DeviceCoordinator coordinator, AppConfig config)
    {
        await coordinator.ApplyConfigAsync(config).ConfigureAwait(false);
        _runningApplicationTargets?.RefreshCurrent();
    }

    private async Task ApplyCommonPluginConfigAsync(AppConfig config)
    {
        try
        {
            if (_commonPlugins is { } manager)
            {
                // Exactly what the user enabled. Nothing is admitted implicitly: the auto-enable pass
                // existed for bundled packages, and WSGM bundles none.
                await manager.ReconcileAsync(config.PluginInstances, _shutdownCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Common plugin configuration failed", ex);
        }
    }

    private void ApplyPerformanceConfig(AppConfig config)
    {
        var performance = _performance;
        if (performance is null)
        {
            return;
        }

        if (_refreshPairing?.SetStrategy(config.Performance.FrameLimitStrategy) == true)
        {
            _pairedFrameLimit = -1;
            ApplyRefreshPairing(performance.Current.Desired.FrameLimit ?? 0, true);
        }

        performance.ApplyOsdCustomization(RtssOsdCustomSettings.FromConfig(config.Performance));
        Log.Observe(
            performance.ApplyProfilesAsync(_profiles.Current, PerformanceEnabled(config)),
            "RTSS performance config apply",
            true);
    }

    /// <summary>Saves a profile edit under the cross-process configuration lock, on a worker.</summary>
    private Task<ProfileConfig> MutateProfilesAsync(Func<ProfileConfig, bool> edit,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            ProfileConfig? stored = null;
            _store.Update(config =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var changed = edit(config.Profiles);
                stored = ConfigJson.Clone(config.Profiles, ConfigJsonContext.Tolerant.ProfileConfig);
                return changed;
            });
            return stored!;
        }, cancellationToken);
    }

    /// <summary>An in-memory profile store for overlay-test.</summary>
    private Func<Func<ProfileConfig, bool>, CancellationToken, Task<ProfileConfig>> MutateSimulatedProfilesAsync()
    {
        return new InMemoryProfileStore(_config.Profiles).MutateAsync;
    }
}

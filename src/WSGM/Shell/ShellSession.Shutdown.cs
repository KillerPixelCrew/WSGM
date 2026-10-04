using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Settings;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    private void OnSessionEnding()
    {
        var alreadyEnding = ApplicationShutdownRequest.SessionEnding;
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        if (!alreadyEnding)
        {
            Log.Info("Interactive session is ending; requesting bounded session cleanup.");
        }

        _ = ((App)Application.Current!).Runtime.RequestExit();
    }

    /// <summary>Runs session cleanup with the device protocol reason and one outer deadline.</summary>
    internal async ValueTask ShutdownAsync(
        ApplicationShutdownReason reason,
        DateTimeOffset deadline)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdownRequested = true;
        if (_controllerStatusSource is not null && _controllerStatusChanged is not null)
        {
            _controllerStatusSource.StatusChanged -= _controllerStatusChanged;
            _controllerStatusSource = null;
            _controllerStatusChanged = null;
        }
        _profiles.Close();
        _profileFanOut?.Close();
        // ReSharper disable once MethodHasAsyncOverload
        _shutdownCancellation.Cancel();
        _brightness?.Dispose();
        // Every cleanup step still runs after an earlier one fails; the collected
        // failures are reported once at the end so the outer coordinator records the
        // shutdown as unverified without any step having been skipped.
        List<Exception> failures = [];
        if (_startupTask is not null)
        {
            try
            {
                await _startupTask;
            }
            catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecordShutdownFailure(failures, "Shell startup failed before shutdown cleanup", ex);
            }
        }

        _bootTakeover?.RequestShutdown();
        _modes?.RequestShutdown();
        try
        {
            _splash?.Dismiss("application shutdown");
        }
        catch (Exception ex)
        {
            RecordShutdownFailure(failures, "Dismissing the boot splash during application shutdown failed", ex);
        }

        // Close input admission on the UI thread before any safety-critical asynchronous cleanup.
        try
        {
            _overlay?.Dispose();
        }
        catch (Exception ex)
        {
            RecordShutdownFailure(failures, "Closing overlay command admission during application shutdown failed", ex);
        }
        finally
        {
            _overlay = null;
        }

        // ReSharper disable once MethodHasAsyncOverload
        _tabBootSyncCancellation.Cancel();

        // Device cleanup is the safety-critical part of the outer application budget.
        // Run it before waiting on shell transitions or doing Explorer/CEF/RTSS teardown.
        // If the outer owner reaches its deadline, process exit still unloads the in-process
        // runtime while the shell anchor remains available for owner-loss desktop recovery.
        // Before the coordinator, deliberately. AutoTDP restores the limit it took over from
        // through that coordinator's capability path, so disposing it afterwards issued the restore
        // into an already-disconnected runtime and left the handheld on the last automatically
        // selected wattage on every exit, update, uninstall and session end.
        DetachOsdPowerStatus();
        if (_autoTdp is not null)
        {
            try
            {
                await _autoTdp.StopAsync(Deadline.At(deadline)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecordShutdownFailure(failures, "AutoTDP restoration was unverified during application shutdown", ex);
            }
            finally
            {
                _autoTdp = null;
                _deviceCoordinator?.AttachAutoTdpManualOverride(null);
                _deviceCoordinator?.AttachAutoTdpAvailability(null);
                if (_deviceCoordinator is { } coordinator)
                {
                    coordinator.PowerPresets.AutomaticPowerOwner = null;
                }
            }
        }

        if (_deviceCoordinator is not null)
        {
            var deviceReason = reason switch
            {
                ApplicationShutdownReason.Update =>
                    PluginStopReason.Updating,
                ApplicationShutdownReason.SessionEnd =>
                    PluginStopReason.SessionEnding,
                ApplicationShutdownReason.Uninstall =>
                    PluginStopReason.Uninstalling,
                _ => PluginStopReason.WsgmExiting
            };
            _deviceCoordinator.PhysicalGlyphCatalog.Changed -= OnPhysicalGlyphProfilesChanged;
            try
            {
                await _deviceCoordinator.ShutdownAsync(deviceReason, Deadline.At(deadline)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecordShutdownFailure(failures, "Device cleanup was unverified; remaining shell cleanup continues", ex);
            }
            finally
            {
                _deviceCoordinator = null;
            }
        }

        // Profile consumers cannot hold up controller safety. Join them only after device cleanup,
        // within the existing outer deadline; retain a hung pass rather than free its state.
        try
        {
            var completion = Task.WhenAll(_profiles.Completion,
                _profileFanOut?.Completion ?? Task.CompletedTask);
            if (completion.IsCompleted)
            {
                await completion.ConfigureAwait(false);
            }
            else
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("Profile work remains active at the shutdown deadline.");
                }

                await completion.WaitAsync(remaining).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Profile work did not finish during shutdown", ex);
        }

        try
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (_managerStartup.IsCompleted)
            {
                await _managerStartup.ConfigureAwait(false);
            }
            else if (remaining > TimeSpan.Zero)
            {
                await _managerStartup.WaitAsync(remaining).ConfigureAwait(false);
            }
            else
            {
                Log.Warn("Other-manager startup work remains active at the shutdown deadline.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Other-manager startup work did not finish during shutdown", ex);
        }

        if (_commonPlugins is { } commonPlugins)
        {
            try
            {
                await commonPlugins.StopAsync(Deadline.At(deadline)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecordShutdownFailure(failures,
                    "Common plugin cleanup was unconfirmed; remaining shell cleanup continues", ex);
            }
        }

        // After the graphics plugins stopped, so no channel is still open when its router goes.
        if (_gpu is { } gpu)
        {
            try
            {
                gpu.AttachManualVariableRefreshOverride(null);
                await gpu.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecordShutdownFailure(failures, "Graphics capability cleanup failed", ex);
            }
        }

        // From here on every step is independently guarded: a throw from any one of them,
        // Steam UI, AutoTDP-adjacent handoff, performance, display restore, or a manager
        // disposal, must not skip the ones after it. A single shared try around this whole
        // stretch previously let one failure stop everything below it, contradicting the
        // invariant above and, on an Update reason, leaving the audio, radio and drive
        // managers holding endpoints and device notifications while the installer replaces
        // files underneath them.
        try
        {
            // Shutdown rejects every new transition before reaching this point. Let the one
            // existing transition and the separately-rooted boot worker cross their Explorer/UI
            // boundaries before disposing anything they can still access. The application
            // coordinator owns the only deadline; a nested timeout here could retire the recovery
            // anchor underneath them.
            if (_modes is not null)
            {
                await _modes.WaitForTransitionAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures,
                "Waiting for the in-flight mode transition during application shutdown failed", ex);
        }

        try
        {
            if (_bootWork is not null)
            {
                await _bootWork.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Waiting for the boot worker during application shutdown failed", ex);
        }
        finally
        {
            _bootWork = null;
        }

        try
        {
            // Ends on the cancelled session token; awaited so it can never re-decide the
            // transport after the disposal below has begun.
            if (_transportGateWork is not null)
            {
                await _transportGateWork.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Waiting for the transport gate during application shutdown failed", ex);
        }
        finally
        {
            _transportGateWork = null;
        }

        var trayRetired = false;
        try
        {
            trayRetired = await Dispatcher.UIThread.InvokeAsync(() => RetireTrayHostForShutdown(failures));
        }
        catch (Exception ex)
        {
            RecordShutdownFailure(failures, "Retiring the tray host during application shutdown failed", ex);
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => DisposeUiOwnedSessionResources(failures));
        }
        catch (Exception ex)
        {
            RecordShutdownFailure(failures, "UI-owned shell cleanup failed during application shutdown", ex);
        }

        var desktopVerified = false;
        try
        {
            desktopVerified = trayRetired
                              && await RestoreDesktopBeforeShutdownAsync(reason, deadline, failures)
                                  .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Restoring the desktop during application shutdown failed", ex);
        }

        try
        {
            if (desktopVerified && !ApplicationShutdownRequest.SessionEnding && _desktopHost is not null)
            {
                await _desktopHost.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the desktop host during application shutdown failed", ex);
        }
        finally
        {
            _desktopHost = null;
        }

        // AutoTDP is already gone: it is disposed before the device coordinator, above,
        // because its restoration needs that coordinator's write path.
        try
        {
            if (_runningApplicationTargets is not null)
            {
                await _runningApplicationTargets.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing running-application targets during application shutdown failed",
                ex);
        }
        finally
        {
            _runningApplicationTargets = null;
        }

        try
        {
            if (_foregroundWindows is not null)
            {
                _foregroundWindows.ApplicationChanged -= OnForegroundApplicationChanged;
                _foregroundWindows.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures,
                "Disposing the foreground window watcher during application shutdown failed", ex);
        }
        finally
        {
            _foregroundWindows = null;
        }

        try
        {
            if (_runningApplications is not null)
            {
                await _runningApplications.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing running applications during application shutdown failed", ex);
        }
        finally
        {
            _runningApplications = null;
        }

        // After the monitor, which is the only thing that reads it.
        _pairingFrametimes?.Dispose();
        _pairingFrametimes = null;
        await _cefMasterGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_steamUi is not null)
            {
                await _steamUi.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the Steam UI session during application shutdown failed", ex);
        }
        finally
        {
            _steamUi = null;
            _cefMasterGate.Release();
        }

        // After the host that read it; it only unsubscribes from the graphics coordinator.
        _steamGraphics?.Dispose();
        _steamGraphics = null;

        try
        {
            if (_steamUiTransport is not null)
            {
                SteamUiTransportSession.Detach(_steamUiTransport);
                await _steamUiTransport.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the Steam UI transport during application shutdown failed", ex);
        }
        finally
        {
            _steamUiTransport = null;
        }

        try
        {
            if (_performance is not null)
            {
                _performance.StateChanged -= OnPerformanceStateForPairing;
                await _performance.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing performance monitoring during application shutdown failed", ex);
        }
        finally
        {
            _performance = null;
        }

        // Before the session ends, not after: the applied rate is transient and would
        // heal on its own eventually, but leaving the desktop at 48 Hz until something
        // else resets it is a change the user never made and would have to hunt for.
        try
        {
            if (_refreshPairing is not null && !_refreshPairing.Restore())
            {
                failures.Add(new InvalidOperationException(
                    "The pre-game display refresh rate could not be restored."));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures,
                "Restoring the pre-game display refresh rate during application shutdown failed", ex);
        }
        finally
        {
            _refreshPairing = null;
        }

        // Same reasoning, and separately owned: a resolution the user picked from the menu
        // is transient too, and leaving the desktop at a game's resolution is the more
        // visible of the two changes to be left with.
        try
        {
            if (_resolutions is not null && !_resolutions.Restore())
            {
                failures.Add(new InvalidOperationException(
                    "The pre-game display resolution could not be restored."));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures,
                "Restoring the pre-game display resolution during application shutdown failed", ex);
        }
        finally
        {
            _resolutions = null;
        }

        // After the Steam host and the overlay, both of which hold them.
        try
        {
            // Valve's chord template goes back when WSGM leaves; the next start mirrors again.
            _chordMirror?.Dispose();
            if (_audioProfiles is not null)
            {
                await _audioProfiles.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the audio profile service during application shutdown failed",
                ex);
        }
        finally
        {
            _audioProfiles = null;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => _audio?.Dispose());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the audio manager during application shutdown failed", ex);
        }
        finally
        {
            _audio = null;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => _radios?.Dispose());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the radio manager during application shutdown failed", ex);
        }
        finally
        {
            _radios = null;
        }

        // Before the drive manager, whose collection the bridge is subscribed to. The format
        // manager holds no timer or handle to release; its work is a task already cancelled
        // with the session, so only the drive manager is disposed after it.
        try
        {
            _libraryImport?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the library importer during application shutdown failed", ex);
        }
        finally
        {
            _libraryImport = null;
        }

        try
        {
            _libraryArtwork?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the library artwork stage during application shutdown failed",
                ex);
        }
        finally
        {
            _libraryArtwork = null;
        }

        try
        {
            _themes?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the themes during application shutdown failed", ex);
        }
        finally
        {
            _themes = null;
        }

        try
        {
            if (_sounds is { } sounds)
            {
                await sounds.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing sound packs during application shutdown failed", ex);
        }
        finally
        {
            _sounds = null;
        }

        try
        {
            _animations?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the animations during application shutdown failed", ex);
        }
        finally
        {
            _animations = null;
        }

        try
        {
            _artwork?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the artwork browser during application shutdown failed", ex);
        }
        finally
        {
            _artwork = null;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => _steamStorage?.Dispose());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the Steam storage bridge during application shutdown failed",
                ex);
        }
        finally
        {
            _steamStorage = null;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => _drives?.Dispose());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the drive manager during application shutdown failed", ex);
        }
        finally
        {
            _drives = null;
        }

        _formats = null;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => CleanupUiResource(failures, "message window", () =>
            {
                _messageWindow?.Dispose();
                _messageWindow = null;
            }));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the message window during shutdown failed", ex);
        }

        _tabBootSyncCancellation.Dispose();
        _shutdownCancellation.Dispose();

        if (!desktopVerified)
        {
            failures.Add(new InvalidOperationException(
                "Application shutdown could not verify a usable Explorer desktop; "
                + "the retained shell anchor will recover after process exit."));
        }

        if (ShutdownFailure(failures) is { } unverified)
        {
            throw unverified;
        }
    }

    /// <summary>Keeps a failed shutdown step for the final report and logs it now.</summary>
    private static void RecordShutdownFailure(List<Exception> failures, string message, Exception ex)
    {
        failures.Add(ex);
        Log.Error(message, ex);
    }

    /// <summary>Reports collected cleanup failures once, or null when every step was verified.</summary>
    /// <remarks>
    ///     The single-failure case keeps that exception as the inner one rather than burying it in a
    ///     one-element aggregate, because the log line a maintainer reads is the inner message. That
    ///     every step still ran is guaranteed by the straight-line shutdown above, which has no early
    ///     return — this only decides how what failed is reported.
    /// </remarks>
    internal static Exception? ShutdownFailure(IReadOnlyList<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Count == 0
            ? null
            : new InvalidOperationException(
                "Application shutdown completed its remaining cleanup, but one or more steps were unverified.",
                failures.Combine("Multiple application shutdown steps were unverified."));
    }

    private void DisposeUiOwnedSessionResources(List<Exception> failures)
    {
        SettingsPluginActions.Withdraw();
        CleanupUiResource(failures, "config watcher", () =>
        {
            lock (_configDebounceGate)
            {
                _configDebounce?.Dispose();
                _configDebounce = null;
            }

            _configWatcher?.Dispose();
            _configWatcher = null;
        });
        _splash = null;
        var messageWindow = _messageWindow;
        if (messageWindow is not null)
        {
            messageWindow.SessionEnding -= OnSessionEnding;
            messageWindow.SessionLocked -= OnSessionLocked;
            messageWindow.SessionUnlocked -= OnSessionUnlocked;
            messageWindow.SystemSuspending -= OnSystemSuspending;
            messageWindow.SystemResumed -= OnSystemResumed;
            messageWindow.PowerSourceChanged -= OnPowerSourceChanged;
        }

        CleanupUiResource(failures, "_overlay", () =>
        {
            _overlay?.Dispose();
            _overlay = null;
        });
        CleanupUiResource(failures, "_performanceOverlay", () =>
        {
            _performanceOverlay?.Dispose();
            _performanceOverlay = null;
        });
        CleanupUiResource(failures, "_deviceOverlay", () =>
        {
            _deviceOverlay?.Dispose();
            _deviceOverlay = null;
        });
        CleanupUiResource(failures, "_graphicsOverlay", () =>
        {
            _graphicsOverlay?.Dispose();
            _graphicsOverlay = null;
        });
        CleanupUiResource(failures, "_standbyGuard", () =>
        {
            _standbyGuard?.Dispose();
            _standbyGuard = null;
        });
        CleanupUiResource(failures, "_displayMute", () =>
        {
            _displayMute?.Dispose();
            _displayMute = null;
        });
        CleanupUiResource(failures, "_updates", () =>
        {
            _updates?.Dispose();
            _updates = null;
        });
        CleanupUiResource(failures, "_volumeButtons", () =>
        {
            _volumeButtons?.Dispose();
            _volumeButtons = null;
        });
        CleanupUiResource(failures, "_cardVolumes", () =>
        {
            _cardVolumes?.Dispose();
            _cardVolumes = null;
        });
        CleanupUiResource(failures, "_cardAcfWatcher", () =>
        {
            _cardAcfWatcher?.Dispose();
            _cardAcfWatcher = null;
        });
        CleanupUiResource(failures, "_startupWatcher", () =>
        {
            _startupWatcher?.Dispose();
            _startupWatcher = null;
        });
        CleanupUiResource(failures, "_displayChangeWindow", () =>
        {
            _displayChangeWindow?.Dispose();
            _displayChangeWindow = null;
        });
        CleanupUiResource(failures, "keep awake", () =>
        {
            if (_keepAwake is not null)
            {
                _keepAwake.DownloadActivityChanged -= OnDownloadActivityChanged;
                _keepAwake.Dispose();
                _keepAwake = null;
            }
        });
        CleanupUiResource(failures, "Steam monitor", () =>
        {
            _monitor?.Dispose();
            _monitor = null;
        });
    }

    private bool RetireTrayHostForShutdown(List<Exception> failures)
    {
        CleanupUiResource(failures, "Settings activation", () =>
        {
            _settingsActivation?.Dispose();
            _settingsActivation = null;
        });
        CleanupUiResource(failures, "desktop tray", () =>
        {
            _desktopTray?.Dispose();
            _desktopTray = null;
        });
        CleanupUiResource(failures, "activation", () =>
        {
            _activation?.Dispose();
            _activation = null;
        });
        var retired = false;
        CleanupUiResource(failures, "tray host", () =>
        {
            _trayHost?.Dispose();
            _trayHost = null;
            retired = true;
        });
        return retired;
    }

    private static void CleanupUiResource(List<Exception> failures, string name, Action dispose)
    {
        try
        {
            dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, $"Disposing {name} failed", ex);
        }
    }

    private async Task<bool> RestoreDesktopBeforeShutdownAsync(
        ApplicationShutdownReason reason,
        DateTimeOffset deadline,
        List<Exception> failures)
    {
        var desktopHost = _desktopHost;
        if (ApplicationShutdownRequest.SessionEnding || reason is ApplicationShutdownReason.SessionEnd)
        {
            return false;
        }

        if (desktopHost is null)
        {
            return true;
        }

        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Log.Warn("Application shutdown reached its deadline before Explorer desktop recovery.");
            return false;
        }

        try
        {
            // Reproduce the non-Explorer half of the ordinary desktop transition before the shell
            // appears. Update already asked Steam to exit so its mapped payload can be replaced;
            // never race that exit with a protocol URL that could start the client again.
            if (!ApplicationShutdownRequest.SessionEnding
                && ApplicationShutdownRequest.Current is not ApplicationShutdownReason.Update
                && _modes is not null)
            {
                SessionModes.ExitBigPicture();
            }

            DisplayScale.ApplyDesktopMode(_store, _config);
        }
        catch (Exception ex)
        {
            // Explorer recovery is the higher-priority safety boundary. Program's final posture
            // cleanup gets another chance after Avalonia exits.
            Log.Error("Preparing desktop posture during application shutdown failed", ex);
        }

        remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Log.Warn("Application shutdown reached its deadline before Explorer desktop recovery.");
            return false;
        }

        var stateRestored = false;
        string? pending = null;
        try
        {
            pending = GameModeReturnRecovery.PendingFingerprint(_store);
            using var stateBudget =
                new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(10, remaining.TotalSeconds)));
            stateRestored = await GameModeReturnRecovery.RestorePendingAsync(_store, stateBudget.Token, _audioProfiles)
                .ConfigureAwait(false);
            if (!stateRestored)
            {
                failures.Add(
                    new InvalidOperationException("The recorded desktop display or audio state remains pending."));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Restoring recorded desktop display and audio state failed", ex);
        }

        try
        {
            if (ApplicationShutdownRequest.SessionEnding)
            {
                return false;
            }

            remaining = deadline - DateTimeOffset.UtcNow;
            var result = await desktopHost
                .RestoreDesktopAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
            if (stateRestored && pending is not null
                              && result.Outcome is ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded)
            {
                GameModeReturnRecovery.ClearRestored(_store, pending);
            }

            return result.Outcome is ExplorerDesktopOutcome.Normal
                or ExplorerDesktopOutcome.Degraded;
        }
        catch (Exception ex)
        {
            Log.Error("Application shutdown Explorer desktop recovery failed", ex);
            return false;
        }
    }
}

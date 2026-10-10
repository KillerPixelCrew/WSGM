using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    private readonly CancellationTokenSource _shutdownDeadlineCancellation = new();
    private long _shutdownDeadlineTicks;

    private Task? _shutdownTask;
    private DateTimeOffset ShutdownDeadline => new(Interlocked.Read(ref _shutdownDeadlineTicks), TimeSpan.Zero);

    private void OnSessionEnding()
    {
        var alreadyEnding = ApplicationShutdownRequest.SessionEnding;
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        if (!alreadyEnding)
        {
            Log.Info("Interactive session is ending; requesting bounded session cleanup.");
        }

        var runtime = ((App)Application.Current!).Runtime;
        _ = runtime.RequestExit();
        if (_shutdownTask is not null)
        {
            TightenShutdownDeadline(runtime.Deadline);
        }
    }

    /// <summary>Returns the session's single cleanup task; called by the runtime on the UI thread.</summary>
    /// <param name="reason">Shutdown reason; the first call starts cleanup with this reason.</param>
    /// <param name="deadline">Outer deadline; later calls can tighten it but cannot extend it.</param>
    /// <returns>The shared cleanup task, which may fault with accumulated failures or unverified teardown.</returns>
    internal ValueTask ShutdownAsync(ApplicationShutdownReason reason, DateTimeOffset deadline)
    {
        TightenShutdownDeadline(deadline);
        if (_shutdownTask is null)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = completion.Task;
            _ = CompleteShutdownAsync(completion, reason);
        }

        return new ValueTask(_shutdownTask);
    }

    private void TightenShutdownDeadline(DateTimeOffset deadline)
    {
        if (Interlocked.Read(ref _shutdownDeadlineTicks) != 0 && deadline >= ShutdownDeadline)
        {
            return;
        }

        Interlocked.Exchange(ref _shutdownDeadlineTicks, deadline.UtcTicks);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _shutdownDeadlineCancellation.Cancel();
        }
        else
        {
            _shutdownDeadlineCancellation.CancelAfter(remaining);
        }
    }

    private async Task CompleteShutdownAsync(TaskCompletionSource completion,
        ApplicationShutdownReason reason)
    {
        try
        {
            await RunShutdownAsync(reason).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private static HandheldStopReason DeviceShutdownReason(ApplicationShutdownReason reason)
    {
        return ApplicationShutdownRequest.SessionEnding
            ? HandheldStopReason.SessionEnding
            : reason switch
            {
                ApplicationShutdownReason.Update => HandheldStopReason.Updating,
                ApplicationShutdownReason.SessionEnd => HandheldStopReason.SessionEnding,
                ApplicationShutdownReason.Uninstall => HandheldStopReason.Uninstalling,
                _ => HandheldStopReason.WsgmExiting
            };
    }

    /// <summary>Runs session cleanup with the device protocol reason and one outer deadline.</summary>
    /// <remarks>
    ///     One ordered routine: safety first, every step attempted whatever failed before it, and every
    ///     wait bounded by the same deadline. A SessionEnd that arrives meanwhile only tightens the
    ///     runtime's deadline and sets the sticky flag that keeps Explorer from being started.
    /// </remarks>
    private async Task RunShutdownAsync(ApplicationShutdownReason reason)
    {
        // Failures are collected and reported once at the end, so the outer coordinator records the
        // shutdown as unverified without any step having been skipped.
        var failures = new ConcurrentQueue<Exception>();

        // 0. Stop new work. This runs on the UI thread, where startup builds the owners, so a startup
        //    that has not reached the UI thread yet sees _disposed and builds nothing.
        _disposed = true;
        _shutdownRequested = true;
        Step(failures, "Closing library import admission failed", () => _libraryImport?.CloseAdmission());
        // Steam's Quick Access stops reaching the device, AutoTDP and GPU owners before they stop
        // below. No CEF round trip: the patches stay until the host is disposed after device cleanup.
        Step(failures, "Closing Steam UI admission failed", () => _steamUi?.CloseAdmission());
        Step(failures, "Closing device admission failed", () => _deviceCoordinator?.CloseAdmission());
        Step(failures, "Closing common plugin admission failed", () => _commonPlugins?.CloseAdmission());
        Step(failures, "Closing GPU driver admission failed", () => _builtinGpu?.CloseAdmission());
        Step(failures, "Closing graphics admission failed", () => _gpu?.CloseAdmission());
        Step(failures, "Cancelling library tab boot sync failed", CancelTabBootSync);
        var libraryTabWork = Task.CompletedTask;
        Step(failures, "Closing library tab admission failed", () => libraryTabWork = LibraryTabManager.CloseAsync());
        Step(failures, "Detaching controller status failed", () =>
        {
            if (_controllerStatusSource is not null)
            {
                _controllerStatusSource.StatusChanged -= OnControllerStatusChanged;
                _controllerStatusSource = null;
            }
        });
        Step(failures, "Closing profile admission failed", _profiles.Close);
        Step(failures, "Closing profile fan-out admission failed", () => _profileFanOut?.Close());
        Step(failures, "Cancelling boot takeover failed", () => _bootTakeover?.RequestShutdown());
        Step(failures, "Closing mode transition admission failed", () => _modes?.RequestShutdown());
        // ReSharper disable once MethodHasAsyncOverload
        Step(failures, "Cancelling session work failed", _shutdownCancellation.Cancel);
        Step(failures, "Closing config watcher failed", CloseConfigWatcher);
        Step(failures, "Closing startup app watcher failed", () =>
        {
            _startupWatcher?.Dispose();
            _startupWatcher = null;
        });
        Step(failures, "Dismissing the boot splash during application shutdown failed",
            () => _splash?.Dismiss("application shutdown"));
        Step(failures, "Closing overlay command admission during application shutdown failed", () =>
        {
            _overlay?.Dispose();
            _overlay = null;
        });
        Step(failures, "Disposing the brightness service during application shutdown failed",
            () => _brightness?.Dispose());

        // 1. Join the startup task and the device power queue, so neither still commands the device when
        //    it stops. An unadopted coordinator is disposed by startup's own finally, and a power
        //    transition that has not started yet never runs: the device stops below either way.
        Task powerWork;
        lock (_devicePowerGate)
        {
            if (_latestPowerTransition is { Started: false } queued)
            {
                queued.Cancelled = true;
            }

            powerWork = _devicePowerWork;
        }

        await JoinAsync(failures, "Shell startup and device power transition",
            Task.WhenAll(_startupTask ?? Task.CompletedTask, powerWork)).ConfigureAwait(false);

        // 2 and 3. The coordinator's shutdown restores AutoTDP's original limit first, through its still
        //    open capability path, then releases the controller, shows the pad again and stops the device.
        //    If the deadline passes, process exit unloads the in-process runtime while the shell anchor
        //    stays available for owner-loss desktop recovery.
        Step(failures, "Detaching AutoTDP status failed", DetachOsdPowerStatus);
        _autoTdp = null;
        if (_deviceCoordinator is { } coordinator)
        {
            var deviceReason = DeviceShutdownReason(reason);
            Step(failures, "Detaching physical glyph profiles failed",
                () => coordinator.PhysicalGlyphCatalog.Changed -= OnPhysicalGlyphProfilesChanged);
            _deviceCoordinator = null;
            await StepAsync(failures, "Device cleanup was unverified; remaining shell cleanup continues",
                    () => coordinator.ShutdownAsync(deviceReason, Deadline.At(ShutdownDeadline)).AsTask())
                .ConfigureAwait(false);
        }

        // Work that cannot hold up controller safety, joined after it: profile passes (a hung pass is
        // retained rather than freed), the other-manager reconciliation, and the Steam Input shim and
        // management applies, which rename files in Steam's folder.
        await JoinAsync(failures, "Profile work",
            Task.WhenAll(_profiles.Completion, _profileFanOut?.Completion ?? Task.CompletedTask)).ConfigureAwait(false);
        await JoinAsync(failures, "Other-manager startup work", _managerStartup).ConfigureAwait(false);
        await JoinAsync(failures, "Steam Input shim reconcile", _steamInputReconcile).ConfigureAwait(false);
        await JoinAsync(failures, "Steam Input management apply",
            _wsgmSettings?.SteamInputApplyCompletion ?? Task.CompletedTask).ConfigureAwait(false);

        // 4. Common plugins, then the graphics router, so no graphics channel is open when it goes.
        if (_commonPlugins is { } commonPlugins)
        {
            await StepAsync(failures, "Common plugin cleanup was unconfirmed; remaining shell cleanup continues",
                () => commonPlugins.StopAsync(Deadline.At(ShutdownDeadline))).ConfigureAwait(false);
        }

        if (_gpu is { } gpu)
        {
            if (_builtinGpu is { } drivers)
            {
                await StepAsync(failures, "GPU driver cleanup was unconfirmed",
                    () => drivers.StopAsync(Deadline.At(ShutdownDeadline))).ConfigureAwait(false);
            }

            await StepAsync(failures, "Graphics capability cleanup failed",
                () => gpu.DisposeAsync().AsTask()).ConfigureAwait(false);
        }

        // 5. Shutdown already refuses new transitions. Let the running one, the boot worker and the
        //    transport gate loop cross their Explorer and UI boundaries before anything they use goes.
        //    One still running at the deadline is left behind; Explorer is then not restored below, and
        //    the retained anchor recovers the desktop after process exit.
        await JoinAsync(failures, "The in-flight mode transition",
            _modes?.WaitForTransitionAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await JoinAsync(failures, "The boot worker", _bootWork ?? Task.CompletedTask).ConfigureAwait(false);
        await JoinAsync(failures, "The transport gate", _transportGateWork ?? Task.CompletedTask)
            .ConfigureAwait(false);

        // 6. Retire the tray, then the session's event sources. Explorer may only come back once WSGM's
        //    taskbar is verifiably gone.
        var trayRetired = false;
        await StepAsync(failures, "Retiring the tray host during application shutdown failed", async () =>
                trayRetired = await Dispatcher.UIThread.InvokeAsync(() => RetireTrayHostForShutdown(failures)))
            .ConfigureAwait(false);

        // 7. Explorer, only when the reason allows it and the tray is gone. Otherwise the retained shell
        //    anchor recovers the desktop after process exit.
        var desktopVerified = false;
        await StepAsync(failures, "Restoring the desktop during application shutdown failed", async () =>
            desktopVerified = trayRetired
                              && await RestoreDesktopBeforeShutdownAsync(reason, failures)
                                  .ConfigureAwait(false)).ConfigureAwait(false);
        await StepAsync(failures, "Disposing the desktop host during application shutdown failed", async () =>
        {
            var desktopHost = _desktopHost;
            _desktopHost = null;
            if (desktopVerified && !ApplicationShutdownRequest.SessionEnding && desktopHost is not null)
            {
                await desktopHost.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        // Owners the Steam host no longer reaches; AutoTDP already stopped with the device.
        await StepAsync(failures, "Disposing running-application targets during application shutdown failed",
            async () =>
            {
                var targets = _runningApplicationTargets;
                _runningApplicationTargets = null;
                if (targets is not null)
                {
                    await targets.DisposeAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        await UiStepAsync(failures, "Disposing the foreground window watcher during application shutdown failed",
            () =>
            {
                if (_foregroundWindows is { } foreground)
                {
                    _foregroundWindows = null;
                    foreground.ApplicationChanged -= OnForegroundApplicationChanged;
                    foreground.Dispose();
                }
            }).ConfigureAwait(false);
        await StepAsync(failures, "Disposing running applications during application shutdown failed", async () =>
        {
            var running = _runningApplications;
            _runningApplications = null;
            if (running is not null)
            {
                await running.DisposeAsync().ConfigureAwait(false);
            }

            // After the monitor, which is the only thing that reads it.
            _pairingFrametimes?.Dispose();
            _pairingFrametimes = null;
        }).ConfigureAwait(false);

        // 8. Steam. The master-switch applies, the Big Picture restore, the tab boot sync and the
        //    library-tab work started before shutdown and none starts again; join what is left.
        Task steamUiWork;
        lock (_steamUiWorkGate)
        {
            steamUiWork = _steamUiWork;
        }

        Task tabBootWorker;
        lock (_tabBootGate)
        {
            tabBootWorker = _tabBootWorker ?? Task.CompletedTask;
        }

        await JoinAsync(failures, "Steam UI and library tab work",
            Task.WhenAll(steamUiWork, tabBootWorker, libraryTabWork)).ConfigureAwait(false);

        // Steam's own Startup Movie choice goes back while the transport is still open: at every exit
        // when no WSGM movie is chosen, and at uninstall whatever is chosen.
        if (_animations is { } bootMovies)
        {
            await StepAsync(failures, "Handing back Steam's startup movie choice failed",
                    () => bootMovies.HandBackSteamChoiceAsync(
                        reason is ApplicationShutdownReason.Uninstall, Deadline.At(ShutdownDeadline)))
                .ConfigureAwait(false);
        }

        await StepAsync(failures, "Disposing the Steam UI session during application shutdown failed",
            () => RetractSteamUiAsync(failures)).ConfigureAwait(false);
        await UiStepAsync(failures, "Disposing the Steam graphics and plugin projections during shutdown failed",
            () =>
            {
                // After the host that read them; they only unsubscribe from their sources.
                _steamGraphics?.Dispose();
                _steamGraphics = null;
                if (_pluginSteamUi is { } pluginSteamUi)
                {
                    _pluginSteamUi = null;
                    if (_wsgmSettings is { } settingsService)
                    {
                        pluginSteamUi.Changed -= settingsService.Refresh;
                    }

                    pluginSteamUi.Dispose();
                }
            }).ConfigureAwait(false);
        await StepAsync(failures, "Disposing the Steam UI transport during application shutdown failed", async () =>
        {
            var transport = _steamUiTransport;
            _steamUiTransport = null;
            var consoleSubscriptions = _steamUiConsoleSubscriptions;
            _steamUiConsoleSubscriptions = [];
            try
            {
                foreach (var subscription in consoleSubscriptions)
                {
                    var lease = await subscription.ConfigureAwait(false);
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (transport is not null)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);

        // 9. Feature owners. Synchronous disposals run on the UI thread that built them; stops and joins
        //    are awaited within the deadline.
        await UiStepAsync(failures, "UI-owned shell cleanup failed during application shutdown",
            () => DisposeUiOwnedSessionResources(failures)).ConfigureAwait(false);
        await StepAsync(failures, "Disposing performance monitoring during application shutdown failed", async () =>
        {
            var performance = _performance;
            _performance = null;
            if (performance is not null)
            {
                performance.StateChanged -= OnPerformanceStateForPairing;
                await performance.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        // The applied refresh rate and a resolution picked from the menu are transient, but leaving the
        // desktop at a game's 48 Hz or resolution is a change the user never made and would have to hunt for.
        Step(failures, "Restoring the pre-game display refresh rate during application shutdown failed", () =>
        {
            var pairing = _refreshPairing;
            _refreshPairing = null;
            if (pairing is not null && !pairing.Restore())
            {
                failures.Enqueue(
                    new InvalidOperationException("The pre-game display refresh rate could not be restored."));
            }
        });
        Step(failures, "Restoring the pre-game display resolution during application shutdown failed", () =>
        {
            var resolutions = _resolutions;
            _resolutions = null;
            if (resolutions is not null && !resolutions.Restore())
            {
                failures.Enqueue(
                    new InvalidOperationException("The pre-game display resolution could not be restored."));
            }
        });

        // After the Steam host and the overlay, both of which hold them. Valve's chord template goes back
        // when WSGM leaves; the next start mirrors again.
        await UiStepAsync(failures, "Disposing the chord mirror during application shutdown failed", () =>
        {
            _chordMirror?.Dispose();
            _chordMirror = null;
        }).ConfigureAwait(false);
        await StepAsync(failures, "Disposing the audio profile service during application shutdown failed",
            async () =>
            {
                var audioProfiles = _audioProfiles;
                _audioProfiles = null;
                if (audioProfiles is not null)
                {
                    await audioProfiles.DisposeAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        await UiStepAsync(failures, "Disposing the audio manager during application shutdown failed", () =>
        {
            _audio?.Dispose();
            _audio = null;
        }).ConfigureAwait(false);
        await StepAsync(failures, "Disposing the volume feedback during application shutdown failed",
            () => Task.Run(VolumeFeedback.Dispose)).ConfigureAwait(false);
        await UiStepAsync(failures, "Disposing the radio manager during application shutdown failed", () =>
        {
            _radios?.Dispose();
            _radios = null;
        }).ConfigureAwait(false);

        // The format manager holds no timer or handle; its work is a task cancelled with the session.
        await JoinAsync(failures, "Format work", _formats?.Completion ?? Task.CompletedTask)
            .ConfigureAwait(false);
        _formats = null;
        // Before the drive manager, whose collection the importer's bridge is subscribed to.
        await StepAsync(failures, "Stopping content availability during application shutdown failed", async () =>
        {
            var availability = _contentAvailability;
            _contentAvailability = null;
            if (availability is not null)
            {
                await availability.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await StepAsync(failures, "Disposing the library importer during application shutdown failed", async () =>
        {
            var libraryImport = _libraryImport;
            _libraryImport = null;
            if (libraryImport is not null)
            {
                await libraryImport.StopAsync(Deadline.At(ShutdownDeadline)).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await StepAsync(failures, "Disposing the emulator manager during application shutdown failed", () =>
        {
            _emulatorTool?.Dispose();
            _emulatorTool = null;
            _emulators?.Dispose();
            _emulators = null;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await UiStepAsync(failures, "Disposing the library artwork stage during application shutdown failed", () =>
        {
            _libraryArtwork?.Dispose();
            _libraryArtwork = null;
        }).ConfigureAwait(false);

        // Disposing cancels the store and repository reads, installs, downloads, update checks and Steam
        // choice work; the joins wait for them.
        var themes = _themes;
        _themes = null;
        await UiStepAsync(failures, "Disposing the themes during application shutdown failed",
            () => themes?.Dispose()).ConfigureAwait(false);
        await JoinAsync(failures, "Theme work", themes?.Completion ?? Task.CompletedTask)
            .ConfigureAwait(false);
        await StepAsync(failures, "Disposing sound packs during application shutdown failed", async () =>
        {
            var sounds = _sounds;
            _sounds = null;
            if (sounds is not null)
            {
                await sounds.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        var animations = _animations;
        _animations = null;
        await UiStepAsync(failures, "Disposing the animations during application shutdown failed",
            () => animations?.Dispose()).ConfigureAwait(false);
        await JoinAsync(failures, "Animation work", animations?.Completion ?? Task.CompletedTask)
            .ConfigureAwait(false);
        await UiStepAsync(failures, "Disposing the artwork browser during application shutdown failed", () =>
        {
            _artwork?.Dispose();
            _artwork = null;
        }).ConfigureAwait(false);

        // 10. Native providers: the Steam storage bridge, then the drive manager with its message-window
        //     registration.
        await UiStepAsync(failures, "Disposing the Steam storage bridge during application shutdown failed", () =>
        {
            _steamStorage?.Dispose();
            _steamStorage = null;
        }).ConfigureAwait(false);
        await UiStepAsync(failures, "Disposing the drive manager during application shutdown failed", () =>
        {
            _drives?.Dispose();
            _drives = null;
        }).ConfigureAwait(false);

        // 11. The message window last: until here a session end still sets the sticky SessionEnd flag.
        await UiStepAsync(failures, "Disposing the message window during shutdown failed", DisposeMessageWindow)
            .ConfigureAwait(false);

        // The cancellation source stays undisposed: work left behind at the deadline may still read its
        // token, and process exit follows.
        if (!desktopVerified && reason is not ApplicationShutdownReason.SessionEnd
                             && !ApplicationShutdownRequest.SessionEnding)
        {
            failures.Enqueue(new InvalidOperationException(
                "Application shutdown could not verify a usable Explorer desktop; "
                + "the retained shell anchor will recover after process exit."));
        }

        if (ShutdownFailure(failures.ToArray()) is { } unverified)
        {
            throw unverified;
        }
    }

    /// <summary>
    ///     Retracts Steam's patches and stops the host. Bounded: a retraction wedged in Steam must not hold
    ///     shutdown past its deadline. Without the gate the host is left undisposed and the transport
    ///     disposal ends its traffic.
    /// </summary>
    /// <param name="failures">The shutdown's collected failures.</param>
    private async Task RetractSteamUiAsync(ConcurrentQueue<Exception> failures)
    {
        if (_shutdownDeadlineCancellation.IsCancellationRequested)
        {
            failures.Enqueue(new TimeoutException(
                "A Steam UI retraction was still running at the shutdown deadline; its patches were not retracted."));
            Log.Warn("Steam UI: a retraction held the gate at the shutdown deadline; skipping the host's retraction.");
            return;
        }

        await _cefMasterGate.WaitAsync(_shutdownDeadlineCancellation.Token).ConfigureAwait(false);

        try
        {
            if (_steamUi is { } steamUi)
            {
                // The toolkit's runtime and patch manager stop waiting at the deadline and name what they
                // left, rather than holding shutdown behind a hung renderer.
                await steamUi.StopAsync(_shutdownDeadlineCancellation.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _steamUi = null;
            _cefMasterGate.Release();
        }
    }

    /// <summary>Awaits work started before shutdown, giving up at the deadline rather than waiting on.</summary>
    /// <param name="work">The work to join.</param>
    /// <param name="deadlineCancellation">Cancelled when the shutdown's deadline is reached or tightened.</param>
    /// <param name="name">What the work is, for the timeout message.</param>
    /// <returns>Completion of the underlying work, propagating its failure or cancellation.</returns>
    /// <exception cref="TimeoutException">The deadline expired before the work finished; the work itself is not stopped.</exception>
    internal static async Task JoinWithinDeadlineAsync(Task work, CancellationToken deadlineCancellation, string name)
    {
        if (work.IsCompleted)
        {
            await work.ConfigureAwait(false);
            return;
        }

        try
        {
            await work.WaitAsync(deadlineCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
        {
            work.ObserveFaults();
            throw new TimeoutException($"{name} remains active at the shutdown deadline.");
        }
    }

    /// <summary>Joins work within the deadline as one step; work that ended by cancellation is finished.</summary>
    /// <param name="failures">The shutdown's collected failures.</param>
    /// <param name="name">What the work is, for the log.</param>
    /// <param name="work">The work to join.</param>
    private Task JoinAsync(ConcurrentQueue<Exception> failures, string name, Task work)
    {
        return StepAsync(failures, $"{name} failed or did not finish during application shutdown", async () =>
        {
            try
            {
                await JoinWithinDeadlineAsync(work, _shutdownDeadlineCancellation.Token, name).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown cancelled it, and cancelled work is finished work.
            }
        });
    }

    /// <summary>Runs one shutdown step, keeping its failure so every later step still runs.</summary>
    /// <param name="failures">The shutdown's collected failures.</param>
    /// <param name="message">What to log when the step fails.</param>
    /// <param name="step">The step.</param>
    internal static void Step(ConcurrentQueue<Exception> failures, string message, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, message, ex);
        }
    }

    /// <summary>Runs one asynchronous shutdown step, keeping its failure so every later step still runs.</summary>
    /// <param name="failures">The shutdown's collected failures.</param>
    /// <param name="message">What to log when the step fails.</param>
    /// <param name="step">The step.</param>
    private async Task StepAsync(ConcurrentQueue<Exception> failures, string message, Func<Task> step)
    {
        try
        {
            await JoinWithinDeadlineAsync(step(), _shutdownDeadlineCancellation.Token, message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, message, ex);
        }
    }

    /// <summary>Runs one synchronous shutdown step on the UI thread, where the owner was built.</summary>
    /// <param name="failures">The shutdown's collected failures.</param>
    /// <param name="message">What to log when the step fails.</param>
    /// <param name="step">The step.</param>
    private Task UiStepAsync(ConcurrentQueue<Exception> failures, string message, Action step)
    {
        return StepAsync(failures, message, async () => await Dispatcher.UIThread.InvokeAsync(step));
    }

    /// <summary>Keeps a failed shutdown step for the final report and logs it now.</summary>
    private static void RecordShutdownFailure(ConcurrentQueue<Exception> failures, string message, Exception ex)
    {
        failures.Enqueue(ex);
        Log.Error(message, ex);
    }

    /// <summary>Reports collected cleanup failures once, or null when every step was verified.</summary>
    /// <remarks>
    ///     The single-failure case keeps that exception as the inner one rather than burying it in a
    ///     one-element aggregate, because the log line a maintainer reads is the inner message. That
    ///     every step still ran is guaranteed by the step helpers above; this only decides how what
    ///     failed is reported.
    /// </remarks>
    /// <param name="failures">Collected cleanup failures; an empty collection represents no recorded failure.</param>
    /// <returns>A wrapper retaining one or all failures, or null for an empty collection.</returns>
    internal static Exception? ShutdownFailure(IReadOnlyList<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Count == 0
            ? null
            : new InvalidOperationException(
                "Application shutdown completed its remaining cleanup, but one or more steps were unverified.",
                failures.Combine("Multiple application shutdown steps were unverified."));
    }

    private void DisposeUiOwnedSessionResources(ConcurrentQueue<Exception> failures)
    {
        _splash = null;
        Step(failures, "Disposing _performanceOverlay failed", () =>
        {
            _performanceOverlay?.Dispose();
            _performanceOverlay = null;
        });
        Step(failures, "Disposing _deviceOverlay failed", () =>
        {
            _deviceOverlay?.Dispose();
            _deviceOverlay = null;
        });
        Step(failures, "Disposing _graphicsOverlay failed", () =>
        {
            _graphicsOverlay?.Dispose();
            _graphicsOverlay = null;
        });
        Step(failures, "Disposing _standbyGuard failed", () =>
        {
            _standbyGuard?.Dispose();
            _standbyGuard = null;
        });
        Step(failures, "Disposing _displayMute failed", () =>
        {
            _displayMute?.Dispose();
            _displayMute = null;
        });
        Step(failures, "Disposing _updates failed", () =>
        {
            _updates?.Dispose();
            _updates = null;
        });
        Step(failures, "Disposing _volumeButtons failed", () =>
        {
            _volumeButtons?.Dispose();
            _volumeButtons = null;
        });
        Step(failures, "Disposing _cardVolumes failed", () =>
        {
            _cardVolumes?.Dispose();
            _cardVolumes = null;
        });
        Step(failures, "Disposing _cardAcfWatcher failed", () =>
        {
            _cardAcfWatcher?.Dispose();
            _cardAcfWatcher = null;
        });
        Step(failures, "Disposing _libraryBadgeWatcher failed", () =>
        {
            _libraryBadgeWatcher?.Dispose();
            _libraryBadgeWatcher = null;
        });
        Step(failures, "Disposing _startupWatcher failed", () =>
        {
            _startupWatcher?.Dispose();
            _startupWatcher = null;
        });
        Step(failures, "Disposing _displayChangeWindow failed", () =>
        {
            if (_displayChangeWindow is { } displayWindow)
            {
                displayWindow.DisplaysChanged -= OnDisplayTopologyChanged;
            }

            _displayChangeWindow?.Dispose();
            _displayChangeWindow = null;
        });
        Step(failures, "Disposing keep awake failed", () =>
        {
            if (_keepAwake is not null)
            {
                _keepAwake.DownloadActivityChanged -= OnDownloadActivityChanged;
                _keepAwake.Dispose();
                _keepAwake = null;
            }
        });
        Step(failures, "Disposing Steam monitor failed", () =>
        {
            _monitor?.Dispose();
            _monitor = null;
        });
    }

    /// <summary>
    ///     Disposes the process's one message window, last. Its power notifications are refused since
    ///     shutdown began, and its session-end notification keeps setting the sticky SessionEnd flag until
    ///     here.
    /// </summary>
    private void DisposeMessageWindow()
    {
        if (_messageWindow is not { } messageWindow)
        {
            return;
        }

        _messageWindow = null;
        messageWindow.SessionEnding -= OnSessionEnding;
        messageWindow.SessionLocked -= OnSessionLocked;
        messageWindow.SessionUnlocked -= OnSessionUnlocked;
        messageWindow.SystemSuspending -= OnSystemSuspending;
        messageWindow.SystemResumed -= OnSystemResumed;
        messageWindow.PowerSourceChanged -= OnPowerSourceChanged;
        messageWindow.Dispose();
    }

    private bool RetireTrayHostForShutdown(ConcurrentQueue<Exception> failures)
    {
        Step(failures, "Disposing Settings activation failed", () =>
        {
            _settingsActivation?.Dispose();
            _settingsActivation = null;
        });
        Step(failures, "Disposing desktop tray failed", () =>
        {
            _desktopTray?.Dispose();
            _desktopTray = null;
        });
        // A save still running finishes before the window closes itself; the session's own exit
        // (application shutdown) is never held up by it.
        Step(failures, "Disposing Settings window failed", () =>
        {
            _settingsSurface?.Close();
            _settingsSurface = null;
        });
        Step(failures, "Disposing activation failed", () =>
        {
            _activation?.Dispose();
            _activation = null;
        });
        var retired = false;
        Step(failures, "Disposing tray host failed", () =>
        {
            if (_trayHost is not null && !_trayHost.Retire())
            {
                throw new InvalidOperationException("The tray host's native window is still active.");
            }

            _trayHost = null;
            retired = true;
        });
        return retired;
    }

    private async Task<bool> RestoreDesktopBeforeShutdownAsync(
        ApplicationShutdownReason reason,
        ConcurrentQueue<Exception> failures)
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

        var remaining = ShutdownDeadline - DateTimeOffset.UtcNow;
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

            DisplayScale.ApplyDesktopMode(_store, _store.Read().RequireConfig());
        }
        catch (Exception ex)
        {
            // Explorer recovery is the higher-priority safety boundary. Program's final posture
            // cleanup gets another chance after Avalonia exits.
            Log.Error("Preparing desktop posture during application shutdown failed", ex);
        }

        remaining = ShutdownDeadline - DateTimeOffset.UtcNow;
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
            stateRestored = await RestorePendingDesktopAsync(_shutdownDeadlineCancellation.Token).ConfigureAwait(false);
            if (!stateRestored)
            {
                failures.Enqueue(
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

            remaining = ShutdownDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var result = await desktopHost
                .RestoreDesktopAsync(remaining, _shutdownDeadlineCancellation.Token)
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

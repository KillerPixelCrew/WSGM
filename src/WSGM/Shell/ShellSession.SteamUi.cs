using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    /// <summary>What Steam UI calls report while the transport waits for Big Picture.</summary>
    private const string SteamUiHeldReason = "Steam UI transport held until Big Picture exists.";

    // One gate for the whole master-switch workflow: a retraction is three CEF
    // round-trips long, and overlapping applies must not interleave their
    // retract-then-close ordering.
    private readonly SemaphoreSlim _cefMasterGate = new(1, 1);

    // The session's one readiness: the transport gate below writes it, and the boot sync, the card
    // watchers, the keep-awake poll and Steam's startup-movie choice wait on or read it.
    private readonly SteamUiReadiness _steamUiReadiness = new();

    // The master-switch applies and the Big Picture restore still running, joined at shutdown.
    private readonly Lock _steamUiWorkGate = new();

    // The tab boot sync is one worker started with the session. A request supersedes the pass in
    // flight; a cancel drops it and any request not yet started. Only the worker creates and disposes
    // a pass's source, always under _tabBootGate, so a disposed source is never cancelled.
    private readonly Lock _tabBootGate = new();
    private readonly SemaphoreSlim _tabBootSignal = new(0, 1);

    // The transport's enabled flag is the one choke point every automatic CEF touch
    // passes: the patch host, the running-application probe and the static
    // evaluators. Its open/closed state is decided only by the readiness loop
    // (see SteamUiReadiness.TransportShouldBeOpen) and always under _cefMasterGate,
    // so a mode change or Steam lifecycle edge merely signals a re-check instead of
    // flipping the transport underneath a retract-then-close in flight.
    private readonly SemaphoreSlim _transportGateSignal = new(0);

    // The same hold for the opposite direction: true from just before the desktop return asks Steam
    // to close Big Picture until that return settles (PrepareSteamUiForDesktopAsync /
    // ReleaseSteamUiBigPictureHold). Leaving rebuilds the front-end exactly as entering does.
    private volatile bool _bigPictureExitPending;

    // Last applied master CEF state, so a reload can tell an on->off transition
    // (which must retract first) from a repeat of the same value. Volatile: the
    // retraction task reads it to decide whether closing the choke point is still
    // wanted, while the UI thread writes it.
    private volatile bool _cefMasterEnabled;

    // True from just before a transition asks Steam for Big Picture until that transition
    // settles (PrepareSteamUiForBigPictureAsync / ReleaseSteamUiBigPictureHold). The request
    // rebuilds Steam's front-end, so the transport hold must begin before it fires.
    private volatile bool _gameModeCefTransitionPending;
    private Task _steamUiWork = Task.CompletedTask;
    private CancellationTokenSource? _tabBootPass;
    private bool _tabBootRequested;
    private Task? _tabBootWorker;

    private Task? _transportGateWork;

    private void OnDisplayTopologyChanged()
    {
        if (_shutdownRequested)
        {
            return;
        }

        _resolutions?.InvalidateOptions();
        _steamUi?.RefreshDisplayState();
        if (_builtinGpu is { } drivers)
        {
            Log.Observe(drivers.ReconcileAsync(_config, _shutdownCancellation.Token),
                "Graphics adapter topology refresh", true);
        }
    }

    /// <summary>
    ///     Opens or closes the Steam UI transport from the master switch, the shell mode and
    ///     the Big Picture window. Callers that can race the master switch hold <c>_cefMasterGate</c>.
    /// </summary>
    /// <remarks>
    ///     Only game mode asks Windows anything: a desktop session opens on the master switch
    ///     alone, so the poll costs nothing there.
    /// </remarks>
    private void ApplySteamUiTransportGate()
    {
        if (_shutdownRequested)
        {
            return;
        }

        var master = _cefMasterEnabled;
        var inGameMode = _inGameMode;
        var transitionPending = _gameModeCefTransitionPending;
        var exitPending = _bigPictureExitPending;
        var bigPictureReady = master && (inGameMode || transitionPending) && SteamUiReadiness.BigPictureUp;
        var open = SteamUiReadiness.TransportShouldBeOpen(
            master, inGameMode, transitionPending, bigPictureReady, exitPending);
        // A held transport is not a disabled one: patches that meet it must say they are waiting.
        // Overlay-test has no transport and nothing to open.
        try
        {
            _steamUiTransport?.SetEnabled(open, master ? SteamUiHeldReason : null);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already disposed it; there is nothing left to open or close.
        }

        // The one-shot jobs wait on this decision, so their ready edge is the transport's own.
        _steamUiReadiness.Observe(open);
        string state;
        if (open)
        {
            state = inGameMode || transitionPending
                ? "Steam UI transport open: Big Picture window is up."
                : "Steam UI transport open: desktop mode.";
        }
        else if (master && exitPending)
        {
            state = "Steam UI transport closed: Big Picture was asked to close — "
                    + "holding every automatic CEF touch until the desktop return settles.";
        }
        else if (master)
        {
            state = inGameMode
                ? "Steam UI transport closed: game mode without a Big Picture window — "
                  + "holding every automatic CEF touch until Steam's UI exists."
                : "Steam UI transport closed: Big Picture was requested — "
                  + "holding every automatic CEF touch until Steam's UI exists.";
        }
        else
        {
            state = "Steam UI transport closed: Steam CEF integration is off.";
        }

        Log.Change("steam-ui-transport-gate", state);
    }

    /// <summary>Asks the gate loop to re-read the shell state now rather than at its next tick.</summary>
    private void RequestSteamUiTransportGateCheck()
    {
        if (_transportGateWork is null || _shutdownRequested)
        {
            return;
        }

        _transportGateSignal.Release();
    }

    /// <summary>
    ///     Retracts every injected Steam UI surface and closes the transport before a
    ///     transition asks Steam for Big Picture.
    /// </summary>
    /// <remarks>
    ///     Steam rebuilds its whole front-end for that request, and the gamepad UI bootstraps against
    ///     whatever <c>SteamClient.System.*</c> then says exists. Namespaces WSGM supplied from
    ///     desktop mode were found there and went unanswered the moment the game-mode gate closed the
    ///     transport two seconds later: the desired Big Picture window stayed recorded native-side
    ///     while no window was ever created (device-diagnosed over CDP, 2026-09-01). Stock Windows
    ///     client state is the one bootstrap Valve ships on this platform, so that is what the rebuild
    ///     must see; everything re-applies through the normal gate once the window exists.
    /// </remarks>
    private async Task PrepareSteamUiForBigPictureAsync()
    {
        if (_steamUiTransport is null || _shutdownRequested)
        {
            return;
        }

        _gameModeCefTransitionPending = true;
        await _cefMasterGate.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(false);
        try
        {
            if (_shutdownRequested)
            {
                return;
            }

            if (_cefMasterEnabled)
            {
                await RetractSteamUiAsync("the Big Picture request").ConfigureAwait(false);
            }

            ApplySteamUiTransportGate();
        }
        finally
        {
            _cefMasterGate.Release();
        }
    }

    /// <summary>
    ///     Retracts every injected Steam UI surface and closes the transport before a
    ///     transition asks Steam to leave Big Picture.
    /// </summary>
    /// <remarks>
    ///     The mirror of <see cref="PrepareSteamUiForBigPictureAsync" />, and for the same reason:
    ///     <c>steam://close/bigpicture</c> makes Steam rebuild its whole front-end back to the desktop
    ///     client, and WSGM used to keep the transport open and every patch applied straight through
    ///     that rebuild. On 2026-09-26 that wedged steamwebhelper — every evaluation timed out for
    ///     three minutes until the websocket closed and Steam restarted its helper — and the fresh
    ///     Explorer started underneath it never answered a liveness probe, so the desktop return
    ///     failed outright. Nothing may touch Steam's UI between the close request and the settled
    ///     desktop.
    /// </remarks>
    private async Task PrepareSteamUiForDesktopAsync()
    {
        if (_steamUiTransport is null || _shutdownRequested)
        {
            return;
        }

        _bigPictureExitPending = true;
        await _cefMasterGate.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(false);
        try
        {
            if (_shutdownRequested)
            {
                return;
            }

            if (_cefMasterEnabled)
            {
                await RetractSteamUiAsync("the Big Picture close").ConfigureAwait(false);
            }

            ApplySteamUiTransportGate();
        }
        finally
        {
            _cefMasterGate.Release();
        }
    }

    /// <summary>Retracts the native Steam UI patch and the library tabs. The caller holds <c>_cefMasterGate</c>.</summary>
    /// <param name="reason">What the retraction is for, for the log.</param>
    private async Task RetractSteamUiAsync(string reason)
    {
        if (_steamUi is not null)
        {
            try
            {
                await _steamUi.DisableAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Retracting the native Steam UI patch for {reason} failed: {ex.Message}");
            }
        }

        try
        {
            if (_steamClient is { } steam)
            {
                await SteamLibraryTabs.DisableAsync(steam).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Retracting the library tabs for {reason} failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Ends the Big Picture request or close hold and re-applies the configured Steam UI
    ///     state for whichever mode the transition settled in. UI thread; safe when no hold is pending.
    /// </summary>
    private void ReleaseSteamUiBigPictureHold()
    {
        if (!_gameModeCefTransitionPending && !_bigPictureExitPending)
        {
            return;
        }

        _gameModeCefTransitionPending = false;
        _bigPictureExitPending = false;
        RequestSteamUiTransportGateCheck();
        if (!_shutdownRequested)
        {
            TrackSteamUiWork(RestoreSteamUiAfterBigPictureAsync(), "Steam UI transition restore");
        }
    }

    /// <summary>Keeps a master-switch or Big Picture task for the shutdown join and logs its failure.</summary>
    private void TrackSteamUiWork(Task work, string operation)
    {
        Log.Observe(work, operation);
        lock (_steamUiWorkGate)
        {
            _steamUiWork = _steamUiWork.IsCompleted ? work : Task.WhenAll(_steamUiWork, work);
        }
    }

    /// <summary>Restores the configured surfaces after even a timed-out retraction has finished.</summary>
    private async Task RestoreSteamUiAfterBigPictureAsync()
    {
        await _cefMasterGate.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || !_cefMasterEnabled
                              || _gameModeCefTransitionPending || _bigPictureExitPending)
                {
                    return;
                }

                // One apply of every surface, the glyph profile included: DisableAsync cleared them
                // all, and a device publication may already have arrived during the retraction.
                ApplySteamUiSurfaces();
                KickTabBootSync();
            }, DispatcherPriority.Normal, _shutdownCancellation.Token);
        }
        finally
        {
            _cefMasterGate.Release();
        }
    }

    /// <summary>
    ///     Owns the transport gate for the session: re-decides it on every signal and at
    ///     <see cref="SteamUiReadiness.TransportGatePollInterval" />, always under the master-switch
    ///     gate so it can never interleave with a retraction.
    /// </summary>
    private async Task RunSteamUiTransportGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _cefMasterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ApplySteamUiTransportGate();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Steam UI transport gate check failed: {ex.Message}");
                }
                finally
                {
                    _cefMasterGate.Release();
                }

                await _transportGateSignal
                    .WaitAsync(SteamUiReadiness.TransportGatePollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session shutdown; the owner disposes the transport itself.
        }
    }

    /// <summary>
    ///     Asks the boot-sync worker for a fresh pass (waits for Steam's UI, then injects tabs and
    ///     pushes the badge map), superseding the one in flight. Safe to call on every trigger: rapid
    ///     requests collapse into one pass after the last of them.
    /// </summary>
    private void KickTabBootSync()
    {
        lock (_tabBootGate)
        {
            if (_shutdownRequested)
            {
                return;
            }

            _tabBootRequested = true;
            _tabBootPass?.Cancel();
            _tabBootWorker ??= Task.Run(() => RunTabBootSyncWorkerAsync(_shutdownCancellation.Token));
            if (_tabBootSignal.CurrentCount == 0)
            {
                _tabBootSignal.Release();
            }
        }
    }

    /// <summary>
    ///     Stops the boot-sync pass in flight and drops a request not yet started, so nothing re-injects
    ///     the tabs during a retraction. The next <see cref="KickTabBootSync" /> runs normally.
    /// </summary>
    private void CancelTabBootSync()
    {
        lock (_tabBootGate)
        {
            _tabBootRequested = false;
            _tabBootPass?.Cancel();
        }
    }

    private async Task RunTabBootSyncWorkerAsync(CancellationToken lifetime)
    {
        try
        {
            while (true)
            {
                await _tabBootSignal.WaitAsync(lifetime).ConfigureAwait(false);
                CancellationTokenSource pass;
                lock (_tabBootGate)
                {
                    if (!_tabBootRequested)
                    {
                        continue;
                    }

                    _tabBootRequested = false;
                    pass = _tabBootPass = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                }

                try
                {
                    if (_steamClient is { } steam)
                    {
                        await LibraryTabManager.SyncOnBootAsync(_store, steam, _steamUiReadiness, pass.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (pass.IsCancellationRequested)
                {
                    Log.Info("Library tab boot sync superseded or cancelled.");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Without this a failed boot sync left the tabs missing with nothing in the log.
                    Log.Warn($"Library tab boot sync failed: {ex.Message}");
                }
                finally
                {
                    lock (_tabBootGate)
                    {
                        _tabBootPass = null;
                        pass.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Session shutdown.
        }
    }

    /// <summary>Applies every Steam surface switch from the configuration in force, in one call.</summary>
    /// <remarks>
    ///     The only way the session moves the host's switches: host creation, a reload, the Big Picture
    ///     restore, the master switch and a glyph profile change all come here, so each one leaves the
    ///     switches of the configuration in force rather than its own share of them. While a Big Picture
    ///     request or close is pending, the surfaces are retracted and the restore applies them once the
    ///     transition settles, so nothing is applied in between. Either mode: Big Picture on the desktop
    ///     draws the same header and download queue.
    /// </remarks>
    private void ApplySteamUiSurfaces()
    {
        if (_steamUi is not { } steamUi || _gameModeCefTransitionPending || _bigPictureExitPending)
        {
            return;
        }

        var config = _config;
        var nativeArtwork = config.DeviceIntegration.GlyphSelection is DeviceGlyphSelection.NativeSteam;
        // The selector alone changes nothing a user can see: without the resolved profile the
        // stylesheet has no rules, and the patch refuses to install an empty one.
        var profile = nativeArtwork
            ? _deviceCoordinator?.PhysicalControlSelectionSnapshot().Profile
            : _deviceCoordinator?.PhysicalGlyphSelectionSnapshot().Profile;
        var switches = SteamUiSurfaceSwitches.From(config, _cefMasterEnabled, profile, nativeArtwork);
        Log.Change(
            "steam.ui.switches",
            $"Steam UI surfaces: quickAccess={switches.NativeQuickAccess}, host={switches.HostSurfaces}, "
            + $"wifiIndicator={switches.NetworkIndicator}, downloadSort={switches.DownloadSort}, "
            + $"libraryBadge={switches.LibraryBadge}, homeCarousel={switches.HomeCarousel}, "
            + $"carouselUninstalled={switches.CarouselShowUninstalled}, "
            + $"screensaverRows={switches.ScreensaverRows}, glyphs={switches.Glyphs}.");
        steamUi.Apply(switches);
    }

    /// <summary>
    ///     Mirrors the master CEF switch, retracting anything WSGM already
    ///     injected on the way down. Ordering is load-bearing: the switch fails every
    ///     evaluation closed, including WSGM's own retractions, so flipping it first
    ///     would strand the registered patches, tabs and badge in Steam until the client
    ///     restarted — with the desktop-trip cleanup dead for the same reason. Both
    ///     directions run through <c>_cefMasterGate</c> and re-read the field (the
    ///     wanted state) once they own it, so a flip landing inside a retraction's
    ///     removal sequence cannot leave the choke point closed while the field —
    ///     and the equality guard that would have repaired it — say enabled.
    /// </summary>
    /// <param name="enabled">The reloaded <c>Cef.Enabled</c> value.</param>
    private void ApplyCefMasterSwitch(bool enabled)
    {
        // Admission closed at shutdown start: a late reload must not reopen what it closed.
        if (_cefMasterEnabled == enabled || _shutdownRequested)
        {
            return;
        }

        _cefMasterEnabled = enabled;
        _runningApplications?.SetSteamEnabled(enabled);
        if (enabled)
        {
            TrackSteamUiWork(Task.Run(async () =>
            {
                await _cefMasterGate.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(false);
                try
                {
                    if (_shutdownRequested || !_cefMasterEnabled)
                    {
                        // Turned off again before this apply owned the gate — that
                        // apply's retraction owns the choke point now.
                        return;
                    }

                    // Through the readiness gate, not straight to open: a master switch
                    // turned on while Steam is cold-starting in game mode still waits
                    // for its window.
                    ApplySteamUiTransportGate();
                }
                finally
                {
                    _cefMasterGate.Release();
                }

                // Field-mutating and fire-and-forget from the UI thread, like every
                // other caller.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || !_cefMasterEnabled
                                  || _gameModeCefTransitionPending || _bigPictureExitPending)
                    {
                        return;
                    }

                    ApplyCardServices(_inGameMode);
                    KickTabBootSync();
                    ApplySteamUiSurfaces();
                });
            }), "Steam CEF integration enable");
            return;
        }

        // The volume monitor owns autonomous CEF traffic. Stop it as soon as the
        // master gate closes; the ACF watcher remains because it is Steam-file only.
        ApplyCardServices(_inGameMode);
        // A boot sync still in its retry loop would otherwise re-inject the tabs
        // between the awaited DisableAsync and the choke point closing behind it,
        // stranding them until Steam restarts (the desktop trip cancels for the
        // same reason).
        CancelTabBootSync();
        TrackSteamUiWork(Task.Run(async () =>
        {
            await _cefMasterGate.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(false);
            try
            {
                if (_shutdownRequested)
                {
                    return;
                }

                await RetractSteamUiAsync("the master switch").ConfigureAwait(false);
            }
            finally
            {
                // Only close the choke point while OFF is still the wanted state:
                // a re-enable that landed during these three round-trips already
                // reopened it, and the equality guard above means no later reload
                // would ever repair an overwrite here.
                if (!_shutdownRequested && !_cefMasterEnabled)
                {
                    ApplySteamUiTransportGate();
                    Log.Info("Steam CEF integration disabled — injected UI retracted.");
                }
                else if (!_shutdownRequested)
                {
                    Log.Info("Steam CEF integration was re-enabled during the retraction — " +
                             "leaving the choke point to the enable apply.");
                }

                _cefMasterGate.Release();
            }
        }), "Steam CEF integration disable");
    }

    private void OnPhysicalGlyphProfilesChanged()
    {
        ApplySteamUiSurfaces();
    }
}

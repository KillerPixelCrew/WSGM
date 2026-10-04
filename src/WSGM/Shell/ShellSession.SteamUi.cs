using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    /// <summary>What Steam UI calls report while the transport waits for Big Picture.</summary>
    private const string SteamUiHeldReason = "Steam UI transport held until Big Picture exists.";

    // One gate for the whole master-switch workflow: a retraction is three CEF
    // round-trips long, and overlapping applies must not interleave their
    // retract-then-close ordering.
    private readonly SemaphoreSlim _cefMasterGate = new(1, 1);

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

    private bool _carouselShowUninstalled;

    // Last applied master CEF state, so a reload can tell an on->off transition
    // (which must retract first) from a repeat of the same value. Volatile: the
    // retraction task reads it to decide whether closing the choke point is still
    // wanted, while the UI thread writes it.
    private volatile bool _cefMasterEnabled;

    // Same for the injected download-queue sort buttons. The session host owns
    // their target generation and retries through the common patch registry.
    private bool _downloadSortEnabled;

    // True from just before a transition asks Steam for Big Picture until that transition
    // settles (PrepareSteamUiForBigPictureAsync / ReleaseSteamUiBigPictureHold). The request
    // rebuilds Steam's front-end, so the transport hold must begin before it fires.
    private volatile bool _gameModeCefTransitionPending;

    private bool _homeCarouselEnabled;
    private bool _libraryBadgeEnabled;
    private bool _screensaverTimeoutsEnabled;

    // Replaced (not just cancelled) on every game-mode entry: a single cancelled
    // source would permanently kill boot syncing after the first desktop trip.
    private CancellationTokenSource _tabBootSyncCancellation = new();

    private Task? _transportGateWork;

    // Live Wi-Fi-indicator gate: the applied state, so a reload can tell an
    // on->off transition from a repeat of the same value.
    private bool _wifiIndicatorEnabled;

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
        var master = _cefMasterEnabled;
        var inGameMode = _inGameMode;
        var transitionPending = _gameModeCefTransitionPending;
        var exitPending = _bigPictureExitPending;
        var bigPictureReady = master && (inGameMode || transitionPending) && SteamUiReadiness.IsReady;
        var open = SteamUiReadiness.TransportShouldBeOpen(
            master, inGameMode, transitionPending, bigPictureReady, exitPending);
        // A held transport is not a disabled one: patches that meet it must say they are waiting.
        SteamUiTransportSession.SetEnabled(open, master ? SteamUiHeldReason : null);
        // The one-shot jobs wait on this decision, so their ready edge is the transport's own.
        SteamUiReadiness.Observe(open);
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
        if (_steamUiTransport is null)
        {
            return;
        }

        _gameModeCefTransitionPending = true;
        await _cefMasterGate.WaitAsync().ConfigureAwait(false);
        try
        {
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
        if (_steamUiTransport is null)
        {
            return;
        }

        _bigPictureExitPending = true;
        await _cefMasterGate.WaitAsync().ConfigureAwait(false);
        try
        {
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
            await SteamLibraryTabs.DisableAsync().ConfigureAwait(false);
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
        Log.Observe(RestoreSteamUiAfterBigPictureAsync(), "Steam UI transition restore");
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

                _steamUi?.Apply(_config.Cef is { Enabled: true, NativeQuickAccess: true });
                _steamUi?.ApplyHostSteamUi(_config.Cef.Enabled);
                _steamUi?.ApplySurfaceObservation(_config.Cef.Enabled);
                _steamUi?.ApplyNetworkIndicator(_wifiIndicatorEnabled);
                ApplySteamUiSurfacePreferences();
                // DisableAsync clears the profile. Restore it explicitly instead of depending on
                // a device publication that may have already arrived during the retraction.
                ApplyGlyphConfig(_config);
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
    ///     Cancels any in-flight boot sync and starts a fresh one (waits for
    ///     Steam's UI, then injects tabs and pushes the badge map). Safe to call on
    ///     every trigger — SyncAllAsync's gate serializes overlapping runs (each queued
    ///     caller still runs a full sync; they are not collapsed into one).
    /// </summary>
    private void KickTabBootSync()
    {
        if (_shutdownRequested)
        {
            return;
        }

        var previous = _tabBootSyncCancellation;
        var current = new CancellationTokenSource();
        _tabBootSyncCancellation = current;
        previous.Cancel();
        _ = RunTabBootSyncAsync(current);
    }

    private async Task RunTabBootSyncAsync(CancellationTokenSource owner)
    {
        try
        {
            await LibraryTabManager.SyncOnBootAsync(_store, owner.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            Log.Info("Library tab boot sync superseded or cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fire-and-forget: without this a failed boot sync left the tabs missing with
            // nothing in the log.
            Log.Warn($"Library tab boot sync failed: {ex.Message}");
        }
        finally
        {
            if (!ReferenceEquals(_tabBootSyncCancellation, owner))
            {
                owner.Dispose();
            }
        }
    }

    /// <summary>
    ///     Re-applies the Steam UI surfaces that work without native Quick Access from the
    ///     session's saved preferences, after the host was created or retracted them.
    /// </summary>
    private void ApplySteamUiSurfacePreferences()
    {
        _steamUi?.ApplyHostSteamUi(_config.Cef.Enabled);
        _steamUi?.ApplyDownloadSort(_downloadSortEnabled);
        _steamUi?.ApplyLibraryBadge(_libraryBadgeEnabled);
        _steamUi?.ApplyHomeCarousel(_homeCarouselEnabled, _carouselShowUninstalled);
        _steamUi?.ApplyScreensaverTimeouts(_screensaverTimeoutsEnabled);
    }

    /// <summary>
    ///     Starts or retracts the injected download-queue sort buttons to match a
    ///     reloaded configuration, so the toggle applies without a re-logon.
    /// </summary>
    /// <param name="enabled">Whether the sort buttons should be injected.</param>
    private void ApplyDownloadSort(bool enabled)
    {
        if (_overlayTestOnly || enabled == _downloadSortEnabled)
        {
            _downloadSortEnabled = enabled;
            return;
        }

        _downloadSortEnabled = enabled;
        // Either mode: Big Picture on the desktop draws the same download queue.
        _steamUi?.ApplyDownloadSort(enabled);
        Log.Info($"Download queue sorting {(enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>
    ///     Shows or retracts the library badge on Steam's tiles to match a reloaded
    ///     configuration, so the card manager toggle applies without a re-logon.
    /// </summary>
    /// <param name="enabled">Whether the badge should be drawn.</param>
    private void ApplyLibraryBadge(bool enabled)
    {
        if (_overlayTestOnly || enabled == _libraryBadgeEnabled)
        {
            _libraryBadgeEnabled = enabled;
            return;
        }

        _libraryBadgeEnabled = enabled;
        _steamUi?.ApplyLibraryBadge(enabled);
        Log.Info($"Library badge {(enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>
    ///     Applies the connected-library Home carousel and its uninstalled-games preference
    ///     from a reloaded configuration, so both switches apply without a re-logon.
    /// </summary>
    /// <param name="enabled">Whether Home's carousel lists the attached libraries.</param>
    /// <param name="showUninstalled">Whether it also lists owned games that are not installed.</param>
    private void ApplyHomeCarousel(bool enabled, bool showUninstalled)
    {
        var changed = enabled != _homeCarouselEnabled || showUninstalled != _carouselShowUninstalled;
        _homeCarouselEnabled = enabled;
        _carouselShowUninstalled = showUninstalled;
        if (_overlayTestOnly || !changed)
        {
            return;
        }

        _steamUi?.ApplyHomeCarousel(enabled, showUninstalled);
        Log.Info($"Home carousel {(enabled ? "enabled" : "disabled")}"
                 + $"{(enabled ? $", uninstalled games {(showUninstalled ? "shown" : "hidden")}" : "")}.");
    }

    /// <summary>
    ///     Adds or retracts the display-off rows in Steam's Screensaver settings to match a
    ///     reloaded configuration. They follow the CEF master switch alone.
    /// </summary>
    /// <param name="enabled">Whether the rows should be drawn.</param>
    private void ApplyScreensaverTimeouts(bool enabled)
    {
        if (_overlayTestOnly || enabled == _screensaverTimeoutsEnabled)
        {
            _screensaverTimeoutsEnabled = enabled;
            return;
        }

        _screensaverTimeoutsEnabled = enabled;
        _steamUi?.ApplyScreensaverTimeouts(enabled);
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
        if (_cefMasterEnabled == enabled)
        {
            return;
        }

        _cefMasterEnabled = enabled;
        _runningApplications?.SetSteamEnabled(enabled);
        if (enabled)
        {
            _ = Task.Run(async () =>
            {
                await _cefMasterGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_cefMasterEnabled)
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
                    ApplySteamUiSurfacePreferences();
                    ApplyGlyphConfig(_config);
                });
            });
            return;
        }

        // The volume monitor owns autonomous CEF traffic. Stop it as soon as the
        // master gate closes; the ACF watcher remains because it is Steam-file only.
        ApplyCardServices(_inGameMode);
        // A boot sync still in its retry loop would otherwise re-inject the tabs
        // between the awaited DisableAsync and the choke point closing behind it,
        // stranding them until Steam restarts (the desktop trip cancels for the
        // same reason).
        _tabBootSyncCancellation.Cancel();
        _ = Task.Run(async () =>
        {
            await _cefMasterGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await RetractSteamUiAsync("the master switch").ConfigureAwait(false);
            }
            finally
            {
                // Only close the choke point while OFF is still the wanted state:
                // a re-enable that landed during these three round-trips already
                // reopened it, and the equality guard above means no later reload
                // would ever repair an overwrite here.
                if (!_cefMasterEnabled)
                {
                    ApplySteamUiTransportGate();
                    Log.Info("Steam CEF integration disabled — injected UI retracted.");
                }
                else
                {
                    Log.Info("Steam CEF integration was re-enabled during the retraction — " +
                             "leaving the choke point to the enable apply.");
                }

                _cefMasterGate.Release();
            }
        });
    }

    /// <summary>Starts or stops the game-mode card services from one shared policy.</summary>
    /// <remarks>
    ///     Initial direct boot and a later desktop-to-game transition are separate entry
    ///     paths: only the latter raises <c>GameModeEntered</c>. Keeping their activation
    ///     here prevents one path from silently losing volume notifications again.
    /// </remarks>
    /// <param name="gameModeActive">Whether the destination/current mode is game mode.</param>
    private void ApplyCardServices(bool gameModeActive)
    {
        var state = GameModeCardServicePolicy.Decide(
            gameModeActive, _overlayTestOnly, _cefMasterEnabled);

        if (state.WatchAppManifests)
        {
            _cardAcfWatcher ??= CardAcfWatcher.StartNew(_store);
        }
        else
        {
            _cardAcfWatcher?.Dispose();
            _cardAcfWatcher = null;
        }

        // Eject and format stand the watcher down for their whole run; its directory handles
        // would otherwise veto their own volume lock.
        if (_drives is not null)
        {
            _drives.CardWatcher = _cardAcfWatcher;
        }

        if (_formats is not null)
        {
            _formats.CardWatcher = _cardAcfWatcher;
        }

        if (state.ReconcileSteamLibraries)
        {
            // Card swaps are reconciled against Steam's install-folder list on the
            // volume notification itself. The callback refreshes both consumers of
            // the changed library membership after Steam accepts the reconcile.
            _cardVolumes ??= CardVolumeMonitor.StartNew(
                MessageWindow.Create(),
                () => _cefMasterEnabled,
                () =>
                {
                    Dispatcher.UIThread.Post(KickTabBootSync);
                    return Task.CompletedTask;
                },
                _libraryPolicy);
        }
        else
        {
            _cardVolumes?.Dispose();
            _cardVolumes = null;
        }
    }

    /// <summary>
    ///     Starts or stops the Big Picture Wi-Fi indicator to match a reloaded
    ///     configuration. Without this the feed keeps running (and keeps being recreated
    ///     on every game-mode entry) after the user turns the toggle off, because the
    ///     start gates read the boot-time configuration.
    /// </summary>
    /// <param name="enabled">Whether the indicator should be feeding Steam.</param>
    private void ApplyNetworkIndicator(bool enabled)
    {
        if (_overlayTestOnly)
        {
            _wifiIndicatorEnabled = enabled;
            Log.Change(
                "steam.network-indicator",
                "Big Picture Wi-Fi indicator not applied: mode=overlay-test.");
            return;
        }

        if (enabled == _wifiIndicatorEnabled)
        {
            _wifiIndicatorEnabled = enabled;
            return;
        }

        _wifiIndicatorEnabled = enabled;
        if (!enabled)
        {
            _steamUi?.ApplyNetworkIndicator(false);
            Log.Info("Big Picture Wi-Fi indicator turned off.");
            return;
        }

        // Either mode: Big Picture on the desktop draws the same header indicator.
        _steamUi?.ApplyNetworkIndicator(true);
        Log.Info("Big Picture Wi-Fi indicator turned on.");
    }

    private static bool GlyphsEnabled(AppConfig config)
    {
        return config.Cef.Enabled
               && config.DeviceIntegration.Enabled;
    }

    private void OnPhysicalGlyphProfilesChanged()
    {
        ApplyGlyphConfig(_config);
    }

    /// <summary>
    ///     Applies both halves of physical glyph presentation: whether it is on, and what to draw.
    /// </summary>
    /// <remarks>
    ///     The selector alone changes nothing a user can see. Without the resolved profile the
    ///     stylesheet has no rules, and the patch refuses to install an empty one — which is how
    ///     physical glyphs were inert.
    /// </remarks>
    private void ApplyGlyphConfig(AppConfig config)
    {
        var steamUi = _steamUi;
        if (steamUi is null)
        {
            return;
        }

        var enabled = GlyphsEnabled(config);
        var nativeArtwork = config.DeviceIntegration.GlyphSelection is DeviceGlyphSelection.NativeSteam;
        steamUi.ApplyGlyphs(
            enabled,
            enabled
                ? nativeArtwork
                    ? _deviceCoordinator?.PhysicalControlSelectionSnapshot().Profile
                    : _deviceCoordinator?.PhysicalGlyphSelectionSnapshot().Profile
                : null,
            nativeArtwork);
    }
}

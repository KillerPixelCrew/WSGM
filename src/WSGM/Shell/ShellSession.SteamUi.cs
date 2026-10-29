using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    /// <summary>What Steam UI calls report while the transport waits for Big Picture.</summary>
    private const string SteamUiHeldReason = "Steam UI transport held until Big Picture exists.";

    /// <summary>How long the master switch's retraction may run before the transport closes on what is left.</summary>
    private static readonly TimeSpan MasterSwitchRetractionBudget = TimeSpan.FromSeconds(15);

    // One gate for the whole master-switch workflow: a retraction is three CEF
    // round-trips long, and overlapping applies must not interleave their
    // retract-then-close ordering. Every retraction under it has a deadline, so the
    // gate loop never waits behind a hung renderer for longer than that.
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
    // flipping the transport underneath a retract-then-close in flight. The one exception
    // only ever closes it: a hold whose retraction overran (CloseSteamUiTransportForHold).
    private readonly SemaphoreSlim _transportGateSignal = new(0, 1);

    // Whether the gate worker's last observation admitted Steam's Big Picture window
    // (BigPictureAdmission). Written by the worker only; read by every gate decision.
    private volatile bool _bigPictureReady;

    // Last applied master CEF state, so a reload can tell an on->off transition
    // (which must retract first) from a repeat of the same value. Volatile: the
    // retraction task reads it to decide whether closing the choke point is still
    // wanted, while the UI thread writes it.
    private volatile bool _cefMasterEnabled;

    // The holds in force, as SteamUiHold bits; Resuming is derived from _steamUiResumeSettledAt.
    private int _steamUiHolds;

    // Until when, in Stopwatch ticks, the Resuming hold keeps the transport closed after a wake.
    private long _steamUiResumeSettledAt;

    private Task _steamUiWork = Task.CompletedTask;
    private CancellationTokenSource? _tabBootPass;
    private bool _tabBootRequested;
    private Task? _tabBootWorker;

    private Task? _transportGateWork;

    /// <summary>Gets the holds in force, including a resume that has not settled yet.</summary>
    private SteamUiHold SteamUiHolds
    {
        get
        {
            var holds = (SteamUiHold)Volatile.Read(ref _steamUiHolds);
            return Stopwatch.GetTimestamp() < Interlocked.Read(ref _steamUiResumeSettledAt)
                ? holds | SteamUiHold.Resuming
                : holds;
        }
    }

    private bool HasSteamUiHold(SteamUiHold hold)
    {
        return (SteamUiHolds & hold) != SteamUiHold.None;
    }

    private void AddSteamUiHold(SteamUiHold hold)
    {
        Interlocked.Or(ref _steamUiHolds, (int)hold);
    }

    private void RemoveSteamUiHold(SteamUiHold hold)
    {
        Interlocked.And(ref _steamUiHolds, ~(int)hold);
    }

    private void OnDisplayTopologyChanged()
    {
        if (_shutdownRequested)
        {
            return;
        }

        _resolutions?.InvalidateOptions();
        _scaledResolutions?.Invalidate();
        _refreshPairing?.Invalidate();
        _steamUi?.RefreshDisplayState();
        // Steam can rebuild its window for a new display; ask the page whether WSGM is still in it.
        RequestSteamUiHealthCheck();
        if (_builtinGpu is { } drivers)
        {
            Log.Observe(drivers.RefreshTopologyAsync(_config, _shutdownCancellation.Token),
                "Graphics adapter topology refresh", true);
        }
    }

    /// <summary>
    ///     Opens or closes the Steam UI transport from the master switch, the shell mode, the holds and
    ///     the admitted Big Picture window. Callers that can race the master switch hold <c>_cefMasterGate</c>.
    /// </summary>
    /// <returns>Whether the transport is open.</returns>
    /// <remarks>
    ///     Nothing here asks Windows anything: the gate worker observes Steam outside the gate and
    ///     leaves its verdict in <c>_bigPictureReady</c>, so the gate is held for microseconds.
    /// </remarks>
    private bool ApplySteamUiTransportGate()
    {
        if (_shutdownRequested)
        {
            return false;
        }

        var master = _cefMasterEnabled;
        var inGameMode = _inGameMode;
        var holds = SteamUiHolds;
        var open = SteamUiReadiness.TransportShouldBeOpen(master, inGameMode, holds, _bigPictureReady);
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
        var wasReady = _steamUiReadiness.IsReady;
        _steamUiReadiness.Observe(open);
        if (!wasReady && _steamUiReadiness.IsReady)
        {
            _emulatorTool?.RequestShortcutSync();
            _contentAvailability?.Request();
        }
        Log.Change("steam-ui-transport-gate", DescribeSteamUiTransportGate(open, master, inGameMode, holds));
        return open;
    }

    private static string DescribeSteamUiTransportGate(bool open, bool master, bool inGameMode, SteamUiHold holds)
    {
        if (open)
        {
            return inGameMode || (holds & SteamUiHold.BigPictureRequest) != SteamUiHold.None
                ? "Steam UI transport open: Big Picture window is up."
                : "Steam UI transport open: desktop mode.";
        }

        if (!master)
        {
            return "Steam UI transport closed: Steam CEF integration is off.";
        }

        if ((holds & SteamUiHold.Activation) != SteamUiHold.None)
        {
            return "Steam UI transport closed: restarting Steam to activate CEF debugging.";
        }

        if ((holds & SteamUiHold.Suspended) != SteamUiHold.None)
        {
            return "Steam UI transport closed: the system is suspending.";
        }

        if ((holds & SteamUiHold.Resuming) != SteamUiHold.None)
        {
            return "Steam UI transport closed: the system resumed; reconnecting once Steam has settled.";
        }

        if ((holds & SteamUiHold.BigPictureClose) != SteamUiHold.None)
        {
            return "Steam UI transport closed: Big Picture was asked to close — "
                   + "holding every automatic CEF touch until the desktop return settles.";
        }

        return inGameMode
            ? "Steam UI transport closed: game mode without a Big Picture window — "
              + "holding every automatic CEF touch until Steam's UI exists."
            : "Steam UI transport closed: Big Picture was requested — "
              + "holding every automatic CEF touch until Steam's UI exists.";
    }

    /// <summary>
    ///     Closes the transport without the master gate, for a hold that cannot wait for it: a
    ///     retraction that overran its budget, or a suspend. Safe because the hold is recorded first,
    ///     so every gate decision made under the lock from then on says closed as well.
    /// </summary>
    private void CloseSteamUiTransportForHold()
    {
        try
        {
            _steamUiTransport?.SetEnabled(false, SteamUiHeldReason);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already disposed it.
        }

        _steamUiReadiness.Observe(false);
    }

    /// <summary>Asks the gate loop to re-read the shell state now rather than at its next tick.</summary>
    private void RequestSteamUiTransportGateCheck()
    {
        if (_transportGateWork is null || _shutdownRequested)
        {
            return;
        }

        try
        {
            _transportGateSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A check is already pending; the loop reads the state once for all of them.
        }
    }

    /// <summary>Asks the page whether WSGM's bridge still holds, when the transport is open.</summary>
    private void RequestSteamUiHealthCheck()
    {
        if (!_shutdownRequested && _steamUiReadiness.IsReady)
        {
            _steamUi?.CheckHealth();
        }
    }

    /// <summary>Closes the transport before the machine sleeps, so nothing is in flight across it.</summary>
    /// <remarks>
    ///     A request in flight at the freeze timed out the moment the machine woke and was reported as
    ///     unanswered. The transport reconnects after the resume settles (<see cref="ReleaseSteamUiAfterResume" />).
    /// </remarks>
    private void HoldSteamUiForSuspend()
    {
        if (_steamUiTransport is null || _shutdownRequested)
        {
            return;
        }

        AddSteamUiHold(SteamUiHold.Suspended);
        CloseSteamUiTransportForHold();
        Log.Change("steam-ui-transport-gate", DescribeSteamUiTransportGate(false, _cefMasterEnabled, _inGameMode,
            SteamUiHolds));
    }

    /// <summary>Reconnects the transport to a settled Steam after every resume, suspend seen or not.</summary>
    /// <remarks>
    ///     The transport used to be trusted to notice a sleep on its own, which it does only when a socket
    ///     closes or two questions go unanswered; nothing asked the page anything after a wake. Now every
    ///     resume closes the transport for <see cref="SteamUiReadiness.ResumeSettle" /> and reopens it,
    ///     which is a new connection and a new generation: every patch is verified or applied again.
    /// </remarks>
    private void ReleaseSteamUiAfterResume()
    {
        if (_steamUiTransport is null || _shutdownRequested)
        {
            return;
        }

        Interlocked.Exchange(ref _steamUiResumeSettledAt,
            Stopwatch.GetTimestamp() + (long)(SteamUiReadiness.ResumeSettle.TotalSeconds * Stopwatch.Frequency));
        RemoveSteamUiHold(SteamUiHold.Suspended);
        RequestSteamUiTransportGateCheck();
    }

    /// <summary>
    ///     Retracts every injected Steam UI surface and closes the transport before a
    ///     transition asks Steam for Big Picture.
    /// </summary>
    /// <param name="budget">Fires when the transition will not wait any longer.</param>
    /// <remarks>
    ///     Steam rebuilds its whole front-end for that request, and the gamepad UI bootstraps against
    ///     whatever <c>SteamClient.System.*</c> then says exists. Namespaces WSGM supplied from
    ///     desktop mode were found there and went unanswered the moment the game-mode gate closed the
    ///     transport two seconds later: the desired Big Picture window stayed recorded native-side
    ///     while no window was ever created (device-diagnosed over CDP, 2026-09-01). Stock Windows
    ///     client state is the one bootstrap Valve ships on this platform, so that is what the rebuild
    ///     must see; everything re-applies through the normal gate once the window exists.
    /// </remarks>
    private Task PrepareSteamUiForBigPictureAsync(CancellationToken budget)
    {
        return PrepareSteamUiForRebuildAsync(SteamUiHold.BigPictureRequest, "the Big Picture request", budget);
    }

    /// <summary>
    ///     Retracts every injected Steam UI surface and closes the transport before a
    ///     transition asks Steam to leave Big Picture.
    /// </summary>
    /// <param name="budget">Fires when the transition will not wait any longer.</param>
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
    private Task PrepareSteamUiForDesktopAsync(CancellationToken budget)
    {
        return PrepareSteamUiForRebuildAsync(SteamUiHold.BigPictureClose, "the Big Picture close", budget);
    }

    /// <summary>Records the hold, retracts within the budget and closes the transport, whatever happens.</summary>
    /// <param name="hold">The hold the transition keeps until it settles.</param>
    /// <param name="reason">What the retraction is for, for the log.</param>
    /// <param name="budget">Fires when the transition will not wait any longer.</param>
    /// <remarks>
    ///     When the budget fires the transport closes at once, before the retraction has returned. The
    ///     transition only used to stop waiting: a retraction that overran kept sending patch traffic
    ///     into the front-end Steam was rebuilding for the request fired meanwhile, which is exactly
    ///     what the hold exists to prevent.
    /// </remarks>
    private async Task PrepareSteamUiForRebuildAsync(SteamUiHold hold, string reason, CancellationToken budget)
    {
        if (_steamUiTransport is null || _shutdownRequested)
        {
            return;
        }

        AddSteamUiHold(hold);
        await using var closeAtBudget = budget.Register(CloseSteamUiTransportForHold);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token, budget);
        try
        {
            await _cefMasterGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_shutdownCancellation.IsCancellationRequested)
        {
            Log.Warn($"Steam UI retraction for {reason} could not start in time; "
                     + "the transport is closed with WSGM's surfaces still in Steam.");
            return;
        }

        try
        {
            if (!_shutdownRequested && _cefMasterEnabled)
            {
                await RetractSteamUiAsync(reason, deadline.Token).ConfigureAwait(false);
            }

            if (budget.IsCancellationRequested)
            {
                Log.Warn($"Steam UI retraction for {reason} ran out of time; "
                         + "the transport closed with the rest still in Steam.");
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
    /// <param name="deadline">Ends the retraction; what was not removed by then stays in Steam.</param>
    private async Task RetractSteamUiAsync(string reason, CancellationToken deadline)
    {
        if (_steamUi is not null)
        {
            try
            {
                await _steamUi.DisableAsync(deadline).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Retracting the native Steam UI patch for {reason} failed: {ex.Message}");
            }
        }

        try
        {
            if (_steamClient is { } steam && !deadline.IsCancellationRequested)
            {
                await SteamLibraryTabs.DisableAsync(steam, deadline).ConfigureAwait(false);
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
        if (!HasSteamUiHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose))
        {
            return;
        }

        RemoveSteamUiHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose);
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
                              || HasSteamUiHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose))
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
    ///     Owns the transport gate and Steam's debugging activation for the session: observes Steam
    ///     once per <see cref="SteamUiReadiness.TransportGatePollInterval" /> or signal, decides the
    ///     gate under the master-switch gate so it can never interleave with a retraction, and asks
    ///     the page for its health while the transport is open.
    /// </summary>
    /// <remarks>
    ///     Nothing but shutdown ends this loop. In 2.3.1 one exception escaped it during a cold start,
    ///     the unobserved task ended, and WSGM never attached to Steam again for the rest of that
    ///     session: every later wake showed an empty Quick Access (issue #226).
    /// </remarks>
    private async Task RunSteamUiTransportGateAsync(CancellationToken cancellationToken)
    {
        var activation = new SteamCefActivationPolicy();
        var steamProcesses = new SteamProcessObserver();
        var admission = new BigPictureAdmission();
        var lastHealthCheck = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Observed outside the gate: a process snapshot and a window search are the slow part.
                var steam = _cefMasterEnabled ? steamProcesses.Observe() : default;
                var now = Stopwatch.GetTimestamp();
                _bigPictureReady = admission.Observe(steam, SteamUiHolds != SteamUiHold.None, now);
                bool open;
                await _cefMasterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    open = ApplySteamUiTransportGate();
                }
                finally
                {
                    _cefMasterGate.Release();
                }

                if (!open || Stopwatch.GetElapsedTime(lastHealthCheck, now) >= SteamUiReadiness.HealthCheckInterval)
                {
                    // The first check comes one interval after the transport opens: the open itself
                    // is a new generation, and the full pass it brings verifies everything anyway.
                    if (open)
                    {
                        _steamUi?.CheckHealth();
                    }

                    lastHealthCheck = now;
                }

                activation = await EnsureSteamCefActivatedAsync(activation, steam, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Steam UI transport gate pass failed; trying again at the next tick", ex);
            }

            try
            {
                await _transportGateSignal
                    .WaitAsync(SteamUiReadiness.TransportGatePollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Restarts Steam once when its browser process runs without remote debugging.</summary>
    /// <param name="activation">The worker's policy.</param>
    /// <param name="steam">This tick's observation.</param>
    /// <param name="cancellationToken">Session shutdown.</param>
    /// <returns>The policy to keep; a fresh one when session policy deferred the restart.</returns>
    private async Task<SteamCefActivationPolicy> EnsureSteamCefActivatedAsync(
        SteamCefActivationPolicy activation, SteamProcessSnapshot steam, CancellationToken cancellationToken)
    {
        var modes = _modes;
        if (modes is null || _shutdownRequested)
        {
            return activation;
        }

        var enabled = _cefMasterEnabled && !modes.SteamClosedByUser;
        if (enabled && steam.Running)
        {
            LogSteamCefActivation(steam);
        }

        var transition = modes.TransitionInProgress
                         || HasSteamUiHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose);
        var decision = activation.Decide(enabled, transition, steam);
        if (decision == SteamCefActivation.GaveUp)
        {
            Log.Warn($"Steam's replacement browser process (pid {steam.BrowserProcessId}) also started without "
                     + "remote debugging; WSGM will not restart Steam again for it.");
            modes.ReportWarning(
                "Steam restarted, but its debugging port is still unavailable. Check wsgm.log for details.");
            return activation;
        }

        if (decision != SteamCefActivation.Restart)
        {
            return activation;
        }

        AddSteamUiHold(SteamUiHold.Activation);
        try
        {
            await _cefMasterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ApplySteamUiTransportGate();
            }
            finally
            {
                _cefMasterGate.Release();
            }

            if (!await modes.RestartSteamForCefAsync(_inGameMode, cancellationToken).ConfigureAwait(false))
            {
                // Deferred, not attempted: the next observation may claim it again.
                return new SteamCefActivationPolicy();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Steam CEF activation failed", ex);
            modes.ReportWarning("Couldn't activate WSGM's Steam integration. Check wsgm.log for details.");
        }
        finally
        {
            RemoveSteamUiHold(SteamUiHold.Activation);
            RequestSteamUiTransportGateCheck();
        }

        return activation;
    }

    private static void LogSteamCefActivation(SteamProcessSnapshot steam)
    {
        if (steam.BrowserProcessId == 0)
        {
            Log.Change("steam-cef-activation", "Steam CEF activation: waiting for Steam's browser process.");
        }
        else if (steam.BrowserDebugging is not { } debugging)
        {
            Log.Change("steam-cef-activation",
                $"Steam CEF activation: the command line of Steam's browser process (pid {steam.BrowserProcessId}) "
                + "is unreadable; WSGM will not restart Steam for it.", LogLevel.Warn);
        }
        else
        {
            Log.Change("steam-cef-activation",
                $"Steam CEF activation: Steam's browser process (pid {steam.BrowserProcessId}) runs "
                + (debugging ? "with" : "without") + " remote debugging.");
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
        if (_steamUi is not { } steamUi
            || HasSteamUiHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose))
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
            // The flag only takes effect at Steam's next start, which may be one WSGM does not make.
            SteamCef.EnsureRemoteDebuggingEnabled(Steam.InstallDirectory, true);
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
                                  || HasSteamUiHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose))
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

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
                deadline.CancelAfter(MasterSwitchRetractionBudget);
                await RetractSteamUiAsync("the master switch", deadline.Token).ConfigureAwait(false);
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

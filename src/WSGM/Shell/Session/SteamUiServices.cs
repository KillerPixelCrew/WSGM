using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The session's Steam UI set: the one transport and client, the readiness gate that opens the
///     transport, Steam's debugging activation, the master switch and the Big Picture holds, the
///     library-tab boot sync, and the host that patches WSGM's surfaces into Steam with the
///     projections only it reads.
/// </summary>
/// <remarks>
///     Overlay-test has no transport and no client: its Steam rows report Steam as unreachable, and
///     nothing here opens anything.
/// </remarks>
internal sealed class SteamUiServices
{
    /// <summary>What Steam UI calls report while the transport waits for Big Picture.</summary>
    private const string SteamUiHeldReason = "Steam UI transport held until Big Picture exists.";

    /// <summary>How long the master switch's retraction may run before the transport closes on what is left.</summary>
    private static readonly TimeSpan MasterSwitchRetractionBudget = TimeSpan.FromSeconds(15);

    private readonly Action _applyCardServices;
    private readonly Action _becameReady;

    // One gate for the whole master-switch workflow: a retraction is three CEF
    // round-trips long, and overlapping applies must not interleave their
    // retract-then-close ordering. Every retraction under it has a deadline, so the
    // gate loop never waits behind a hung renderer for longer than that.
    private readonly SemaphoreSlim _cefMasterGate = new(1, 1);

    private Task<IAsyncDisposable>[] _consoleSubscriptions = [];
    private readonly IntegrationServices _integrations;
    private readonly Func<SessionModes?> _modes;
    private readonly PerformanceServices _performance;
    private readonly SessionState _state;

    // The transport's enabled flag is the one choke point every automatic CEF touch
    // passes: the patch host, the running-application probe and the static
    // evaluators. Its open/closed state is decided only by the readiness loop
    // (see SteamUiReadiness.TransportShouldBeOpen) and always under _cefMasterGate,
    // so a mode change or Steam lifecycle edge merely signals a re-check instead of
    // flipping the transport underneath a retract-then-close in flight. The one exception
    // only ever closes it: a hold whose retraction overran (CloseTransportForHold).
    private readonly SemaphoreSlim _transportGateSignal = new(0, 1);

    // The master-switch applies and the Big Picture restore still running, joined at shutdown.
    private readonly Lock _workGate = new();

    // Whether the gate worker's last observation admitted Steam's Big Picture window
    // (BigPictureAdmission). Written by the worker only; read by every gate decision.
    private volatile bool _bigPictureReady;

    // Last applied master CEF state, so a reload can tell an on->off transition
    // (which must retract first) from a repeat of the same value. Volatile: the
    // retraction task reads it to decide whether closing the choke point is still
    // wanted, while the UI thread writes it.
    private volatile bool _cefMasterEnabled;

    // The holds in force, as SteamUiHold bits; Resuming is derived from _resumeSettledAt.
    private int _holds;

    // Until when, in Stopwatch ticks, the Resuming hold keeps the transport closed after a wake.
    private long _resumeSettledAt;

    private Task? _transportGateWork;

    private Task _work = Task.CompletedTask;

    /// <summary>
    ///     Creates the transport closed, with its diagnostics subscriptions, and the one Steam client over
    ///     it. The real shell opens the transport only through the readiness gate, once it is running and
    ///     knows whether Steam is cold-starting under it.
    /// </summary>
    /// <param name="state">The session's live state.</param>
    /// <param name="integrations">The device whose glyph profile the surfaces show.</param>
    /// <param name="performance">The running-application monitor the master switch reaches.</param>
    /// <param name="modes">Reads the session's mode coordinator, which exists once the UI-thread start built it.</param>
    /// <param name="becameReady">Runs when the transport opens, for the one-shot jobs that wait for Steam.</param>
    /// <param name="applyCardServices">Re-decides the card services after the master switch moved.</param>
    internal SteamUiServices(
        SessionState state,
        IntegrationServices integrations,
        PerformanceServices performance,
        Func<SessionModes?> modes,
        Action becameReady,
        Action applyCardServices)
    {
        _state = state;
        _integrations = integrations;
        _performance = performance;
        _modes = modes;
        _becameReady = becameReady;
        _applyCardServices = applyCardServices;
        _cefMasterEnabled = state.Config.Cef.Enabled;
        // One transport and one client for the session. The real shell opens the transport only
        // through the readiness gate, once it is running and knows whether Steam is cold-starting
        // under it, so it starts closed. Overlay-test has neither, and its Steam rows report Steam
        // as unreachable.
        if (!state.OverlayTestOnly)
        {
            Transport = new PersistentSteamUiTransport(true);
            Transport.SetEnabled(false);
            // Diagnostics use the existing target connections and obey the same readiness gate.
            _consoleSubscriptions =
            [
                Transport.SubscribeAsync(SteamUiTargetRole.SharedJsContext).AsTask(),
                Transport.SubscribeAsync(SteamUiTargetRole.MainWindow).AsTask()
            ];
            Client = new SteamClient(Transport);
        }

        TabBootSync = new LibraryTabBootSync(state.Store, Client, Readiness, () => state.ShutdownRequested,
            state.ShutdownToken);
    }

    /// <summary>The session's one Steam UI transport, or null in overlay-test.</summary>
    internal PersistentSteamUiTransport? Transport { get; private set; }

    /// <summary>
    ///     The one Steam client every one-shot read and write goes through, over <see cref="Transport" />.
    ///     Null in overlay-test, which never talks to Steam.
    /// </summary>
    internal SteamClient? Client { get; }

    /// <summary>
    ///     The session's one readiness: the transport gate writes it, and the boot sync, the card
    ///     watchers, the keep-awake poll and Steam's startup-movie choice wait on or read it.
    /// </summary>
    internal SteamUiReadiness Readiness { get; } = new();

    /// <summary>The library-tab boot sync, kicked on every Steam UI trigger and cancelled during a retraction.</summary>
    internal LibraryTabBootSync TabBootSync { get; }

    /// <summary>The host that patches WSGM's surfaces into Steam, or null until it was created.</summary>
    internal SteamUiSessionHost? Host { get; private set; }

    /// <summary>Steam's Switch to Desktop, which follows the mode. Null in overlay-test and before the host exists.</summary>
    internal SteamPowerMenuBackend? PowerMenu { get; private set; }

    /// <summary>The GPU projection shared by Steam Quick Access and Display settings.</summary>
    internal SteamGraphicsService? Graphics { get; private set; }

    /// <summary>The plugins' Steam surfaces, read by the host and the Steam settings page.</summary>
    internal CommonPluginSteamUiSource? PluginSource { get; private set; }

    /// <summary>WSGM's own settings, from its row in Steam's main menu.</summary>
    internal WsgmSteamSettingsService? SettingsPage { get; private set; }

    /// <summary>Whether the master Steam integration switch is on as last applied.</summary>
    internal bool MasterEnabled => _cefMasterEnabled;

    /// <summary>The master-switch applies and the Big Picture restore still running.</summary>
    internal Task Work
    {
        get
        {
            lock (_workGate)
            {
                return _work;
            }
        }
    }

    /// <summary>Gets the holds in force, including a resume that has not settled yet.</summary>
    private SteamUiHold Holds
    {
        get
        {
            var holds = (SteamUiHold)Volatile.Read(ref _holds);
            return Stopwatch.GetTimestamp() < Interlocked.Read(ref _resumeSettledAt)
                ? holds | SteamUiHold.Resuming
                : holds;
        }
    }

    private bool HasHold(SteamUiHold hold)
    {
        return (Holds & hold) != SteamUiHold.None;
    }

    private void AddHold(SteamUiHold hold)
    {
        Interlocked.Or(ref _holds, (int)hold);
    }

    private void RemoveHold(SteamUiHold hold)
    {
        Interlocked.And(ref _holds, ~(int)hold);
    }

    /// <summary>Whether Steam's shared JavaScript context is connected and ready now.</summary>
    /// <returns>True only while the transport reports that target ready.</returns>
    internal bool SharedContextReady()
    {
        return Transport?.GetSnapshots().Any(snapshot =>
            snapshot.Role == SteamUiTargetRole.SharedJsContext && snapshot.Health == SteamUiTransportHealth.Ready) == true;
    }

    /// <summary>
    ///     Applies the gate once, then starts the loop that owns it for the session. An open transport
    ///     with a subscriber starts discovering Steam's port at once.
    /// </summary>
    internal void StartTransportGate()
    {
        // The transport was created closed with the session; the gate decides when it opens.
        ApplyTransportGate();
        _transportGateWork = Task.Run(() =>
            RunTransportGateAsync(_state.ShutdownToken));
        Log.Observe(_transportGateWork, "Steam UI transport gate");
    }

    /// <summary>The transport gate loop, or a completed task when it never started.</summary>
    internal Task TransportGateWork => _transportGateWork ?? Task.CompletedTask;

    /// <summary>
    ///     Opens or closes the Steam UI transport from the master switch, the shell mode, the holds and
    ///     the admitted Big Picture window. Callers that can race the master switch hold <c>_cefMasterGate</c>.
    /// </summary>
    /// <returns>Whether the transport is open.</returns>
    /// <remarks>
    ///     Nothing here asks Windows anything: the gate worker observes Steam outside the gate and
    ///     leaves its verdict in <c>_bigPictureReady</c>, so the gate is held for microseconds.
    /// </remarks>
    private bool ApplyTransportGate()
    {
        if (_state.ShutdownRequested)
        {
            return false;
        }

        var master = _cefMasterEnabled;
        var inGameMode = _state.InGameMode;
        var holds = Holds;
        var open = SteamUiReadiness.TransportShouldBeOpen(master, inGameMode, holds, _bigPictureReady);
        // A held transport is not a disabled one: patches that meet it must say they are waiting.
        // Overlay-test has no transport and nothing to open.
        try
        {
            Transport?.SetEnabled(open, master ? SteamUiHeldReason : null);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already disposed it; there is nothing left to open or close.
        }

        // The one-shot jobs wait on this decision, so their ready edge is the transport's own.
        var wasReady = Readiness.IsReady;
        Readiness.Observe(open);
        if (!wasReady && Readiness.IsReady)
        {
            _becameReady();
        }

        Log.Change("steam-ui-transport-gate", DescribeTransportGate(open, master, inGameMode, holds));
        return open;
    }

    private static string DescribeTransportGate(bool open, bool master, bool inGameMode, SteamUiHold holds)
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
    private void CloseTransportForHold()
    {
        try
        {
            Transport?.SetEnabled(false, SteamUiHeldReason);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already disposed it.
        }

        Readiness.Observe(false);
    }

    /// <summary>Asks the gate loop to re-read the shell state now rather than at its next tick.</summary>
    internal void RequestGateCheck()
    {
        if (_transportGateWork is null || _state.ShutdownRequested)
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
    internal void RequestHealthCheck()
    {
        if (!_state.ShutdownRequested && Readiness.IsReady)
        {
            Host?.CheckHealth();
        }
    }

    /// <summary>Closes the transport before the machine sleeps, so nothing is in flight across it.</summary>
    /// <remarks>
    ///     A request in flight at the freeze timed out the moment the machine woke and was reported as
    ///     unanswered. The transport reconnects after the resume settles (<see cref="ReleaseAfterResume" />).
    /// </remarks>
    internal void HoldForSuspend()
    {
        if (Transport is null || _state.ShutdownRequested)
        {
            return;
        }

        AddHold(SteamUiHold.Suspended);
        CloseTransportForHold();
        Log.Change("steam-ui-transport-gate", DescribeTransportGate(false, _cefMasterEnabled, _state.InGameMode,
            Holds));
    }

    /// <summary>Reconnects the transport to a settled Steam after every resume, suspend seen or not.</summary>
    /// <remarks>
    ///     The transport used to be trusted to notice a sleep on its own, which it does only when a socket
    ///     closes or two questions go unanswered; nothing asked the page anything after a wake. Now every
    ///     resume closes the transport for <see cref="SteamUiReadiness.ResumeSettle" /> and reopens it,
    ///     which is a new connection and a new generation: every patch is verified or applied again.
    /// </remarks>
    internal void ReleaseAfterResume()
    {
        if (Transport is null || _state.ShutdownRequested)
        {
            return;
        }

        Interlocked.Exchange(ref _resumeSettledAt,
            Stopwatch.GetTimestamp() + (long)(SteamUiReadiness.ResumeSettle.TotalSeconds * Stopwatch.Frequency));
        RemoveHold(SteamUiHold.Suspended);
        RequestGateCheck();
    }

    /// <summary>
    ///     Retracts every injected Steam UI surface and closes the transport before a
    ///     transition asks Steam for Big Picture.
    /// </summary>
    /// <param name="budget">Fires when the transition will not wait any longer.</param>
    /// <returns>Completion of the retraction, or of the wait the budget ended.</returns>
    /// <remarks>
    ///     Steam rebuilds its whole front-end for that request, and the gamepad UI bootstraps against
    ///     whatever <c>SteamClient.System.*</c> then says exists. Namespaces WSGM supplied from
    ///     desktop mode were found there and went unanswered the moment the game-mode gate closed the
    ///     transport two seconds later: the desired Big Picture window stayed recorded native-side
    ///     while no window was ever created (device-diagnosed over CDP, 2026-09-01). Stock Windows
    ///     client state is the one bootstrap Valve ships on this platform, so that is what the rebuild
    ///     must see; everything re-applies through the normal gate once the window exists.
    /// </remarks>
    internal Task PrepareForBigPictureAsync(CancellationToken budget)
    {
        return PrepareForRebuildAsync(SteamUiHold.BigPictureRequest, "the Big Picture request", budget);
    }

    /// <summary>
    ///     Retracts every injected Steam UI surface and closes the transport before a
    ///     transition asks Steam to leave Big Picture.
    /// </summary>
    /// <param name="budget">Fires when the transition will not wait any longer.</param>
    /// <returns>Completion of the retraction, or of the wait the budget ended.</returns>
    /// <remarks>
    ///     The mirror of <see cref="PrepareForBigPictureAsync" />, and for the same reason:
    ///     <c>steam://close/bigpicture</c> makes Steam rebuild its whole front-end back to the desktop
    ///     client, and WSGM used to keep the transport open and every patch applied straight through
    ///     that rebuild. On 2026-09-26 that wedged steamwebhelper — every evaluation timed out for
    ///     three minutes until the websocket closed and Steam restarted its helper — and the fresh
    ///     Explorer started underneath it never answered a liveness probe, so the desktop return
    ///     failed outright. Nothing may touch Steam's UI between the close request and the settled
    ///     desktop.
    /// </remarks>
    internal Task PrepareForDesktopAsync(CancellationToken budget)
    {
        return PrepareForRebuildAsync(SteamUiHold.BigPictureClose, "the Big Picture close", budget);
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
    private async Task PrepareForRebuildAsync(SteamUiHold hold, string reason, CancellationToken budget)
    {
        if (Transport is null || _state.ShutdownRequested)
        {
            return;
        }

        AddHold(hold);
        await using var closeAtBudget = budget.Register(CloseTransportForHold);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_state.ShutdownToken, budget);
        try
        {
            await _cefMasterGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_state.ShutdownToken.IsCancellationRequested)
        {
            Log.Warn($"Steam UI retraction for {reason} could not start in time; "
                     + "the transport is closed with WSGM's surfaces still in Steam.");
            return;
        }

        try
        {
            if (!_state.ShutdownRequested && _cefMasterEnabled)
            {
                await RetractAsync(reason, deadline.Token).ConfigureAwait(false);
            }

            if (budget.IsCancellationRequested)
            {
                Log.Warn($"Steam UI retraction for {reason} ran out of time; "
                         + "the transport closed with the rest still in Steam.");
            }

            ApplyTransportGate();
        }
        finally
        {
            _cefMasterGate.Release();
        }
    }

    /// <summary>Retracts the native Steam UI patch and the library tabs. The caller holds <c>_cefMasterGate</c>.</summary>
    /// <param name="reason">What the retraction is for, for the log.</param>
    /// <param name="deadline">Ends the retraction; what was not removed by then stays in Steam.</param>
    private async Task RetractAsync(string reason, CancellationToken deadline)
    {
        if (Host is not null)
        {
            try
            {
                await Host.DisableAsync(deadline).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Retracting the native Steam UI patch for {reason} failed: {ex.Message}");
            }
        }

        try
        {
            if (Client is { } steam && !deadline.IsCancellationRequested)
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
    internal void ReleaseBigPictureHold()
    {
        if (!HasHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose))
        {
            return;
        }

        RemoveHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose);
        RequestGateCheck();
        if (!_state.ShutdownRequested)
        {
            TrackWork(RestoreAfterBigPictureAsync(), "Steam UI transition restore");
        }
    }

    /// <summary>Keeps a master-switch or Big Picture task for the shutdown join and logs its failure.</summary>
    private void TrackWork(Task work, string operation)
    {
        Log.Observe(work, operation);
        lock (_workGate)
        {
            _work = _work.IsCompleted ? work : Task.WhenAll(_work, work);
        }
    }

    /// <summary>Restores the configured surfaces after even a timed-out retraction has finished.</summary>
    private async Task RestoreAfterBigPictureAsync()
    {
        await _cefMasterGate.WaitAsync(_state.ShutdownToken).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_state.ShutdownRequested || !_cefMasterEnabled
                                             || HasHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose))
                {
                    return;
                }

                // One apply of every surface, the glyph profile included: DisableAsync cleared them
                // all, and a device publication may already have arrived during the retraction.
                ApplySurfaces();
                TabBootSync.Kick();
            }, DispatcherPriority.Normal, _state.ShutdownToken);
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
    private async Task RunTransportGateAsync(CancellationToken cancellationToken)
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
                _bigPictureReady = admission.Observe(steam, Holds != SteamUiHold.None, now);
                bool open;
                await _cefMasterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    open = ApplyTransportGate();
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
                        Host?.CheckHealth();
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
        var modes = _modes();
        if (modes is null || _state.ShutdownRequested)
        {
            return activation;
        }

        var enabled = _cefMasterEnabled && !modes.SteamLifecycle.ClosedByUser;
        if (enabled && steam.Running)
        {
            LogSteamCefActivation(steam);
        }

        var transition = modes.TransitionInProgress
                         || HasHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose);
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

        AddHold(SteamUiHold.Activation);
        try
        {
            await _cefMasterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ApplyTransportGate();
            }
            finally
            {
                _cefMasterGate.Release();
            }

            if (!await modes.SteamLifecycle.RestartForCefAsync(_state.InGameMode, cancellationToken)
                    .ConfigureAwait(false))
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
            RemoveHold(SteamUiHold.Activation);
            RequestGateCheck();
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

    /// <summary>Applies every Steam surface switch from the configuration in force, in one call.</summary>
    /// <remarks>
    ///     The only way the session moves the host's switches: host creation, a reload, the Big Picture
    ///     restore, the master switch and a glyph profile change all come here, so each one leaves the
    ///     switches of the configuration in force rather than its own share of them. While a Big Picture
    ///     request or close is pending, the surfaces are retracted and the restore applies them once the
    ///     transition settles, so nothing is applied in between. Either mode: Big Picture on the desktop
    ///     draws the same header and download queue.
    /// </remarks>
    internal void ApplySurfaces()
    {
        if (Host is not { } steamUi
            || HasHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose))
        {
            return;
        }

        var config = _state.Config;
        var nativeArtwork = config.DeviceIntegration.GlyphSelection is DeviceGlyphSelection.NativeSteam;
        // The selector alone changes nothing a user can see: without the resolved profile the
        // stylesheet has no rules, and the patch refuses to install an empty one.
        var profile = nativeArtwork
            ? _integrations.Device?.PhysicalControlSelectionSnapshot().Profile
            : _integrations.Device?.PhysicalGlyphSelectionSnapshot().Profile;
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
    internal void ApplyMasterSwitch(bool enabled)
    {
        // Admission closed at shutdown start: a late reload must not reopen what it closed.
        if (_cefMasterEnabled == enabled || _state.ShutdownRequested)
        {
            return;
        }

        _cefMasterEnabled = enabled;
        _performance.SetSteamEnabled(enabled);
        if (enabled)
        {
            // The flag only takes effect at Steam's next start, which may be one WSGM does not make.
            SteamCef.EnsureRemoteDebuggingEnabled(Steam.InstallDirectory, true);
            TrackWork(Task.Run(async () =>
            {
                await _cefMasterGate.WaitAsync(_state.ShutdownToken).ConfigureAwait(false);
                try
                {
                    if (_state.ShutdownRequested || !_cefMasterEnabled)
                    {
                        // Turned off again before this apply owned the gate — that
                        // apply's retraction owns the choke point now.
                        return;
                    }

                    // Through the readiness gate, not straight to open: a master switch
                    // turned on while Steam is cold-starting in game mode still waits
                    // for its window.
                    ApplyTransportGate();
                }
                finally
                {
                    _cefMasterGate.Release();
                }

                // Field-mutating and fire-and-forget from the UI thread, like every
                // other caller.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_state.ShutdownRequested || !_cefMasterEnabled
                                                 || HasHold(SteamUiHold.BigPictureRequest | SteamUiHold.BigPictureClose))
                    {
                        return;
                    }

                    _applyCardServices();
                    TabBootSync.Kick();
                    ApplySurfaces();
                });
            }), "Steam CEF integration enable");
            return;
        }

        // The volume monitor owns autonomous CEF traffic. Stop it as soon as the
        // master gate closes; the ACF watcher remains because it is Steam-file only.
        _applyCardServices();
        // A boot sync still in its retry loop would otherwise re-inject the tabs
        // between the awaited DisableAsync and the choke point closing behind it,
        // stranding them until Steam restarts (the desktop trip cancels for the
        // same reason).
        TabBootSync.Cancel();
        TrackWork(Task.Run(async () =>
        {
            await _cefMasterGate.WaitAsync(_state.ShutdownToken).ConfigureAwait(false);
            try
            {
                if (_state.ShutdownRequested)
                {
                    return;
                }

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_state.ShutdownToken);
                deadline.CancelAfter(MasterSwitchRetractionBudget);
                await RetractAsync("the master switch", deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                // Only close the choke point while OFF is still the wanted state:
                // a re-enable that landed during these three round-trips already
                // reopened it, and the equality guard above means no later reload
                // would ever repair an overwrite here.
                if (!_state.ShutdownRequested && !_cefMasterEnabled)
                {
                    ApplyTransportGate();
                    Log.Info("Steam CEF integration disabled — injected UI retracted.");
                }
                else if (!_state.ShutdownRequested)
                {
                    Log.Info("Steam CEF integration was re-enabled during the retraction — " +
                             "leaving the choke point to the enable apply.");
                }

                _cefMasterGate.Release();
            }
        }), "Steam CEF integration disable");
    }

    /// <summary>Creates WSGM's settings page in Steam's main menu.</summary>
    /// <param name="steamInput">The process's Steam Input lease owner and, through it, its shim.</param>
    /// <remarks>
    ///     Reads the session's live config and writes one field at a time through the store; the config
    ///     reload then applies it. Plugins are read through the same source the Quick Access tab uses,
    ///     created with the host.
    /// </remarks>
    internal void StartSettingsPage(SteamInputBlocker steamInput)
    {
        SettingsPage = new WsgmSteamSettingsService(
            () => _state.Config,
            _state.CommitSetting,
            config => SteamInputManagement.Apply(steamInput.Shim, config, "steam-settings"),
            () => SteamInputManagement.Describe(steamInput.Shim),
            () =>
            [
                .. (_integrations.CommonPlugins?.Catalog.Common ?? []).Select(package =>
                    new InstalledCommonPlugin(package.Manifest.Id, package.Manifest.Name,
                        _integrations.CommonPlugins?.EnabledByDefault(package.Manifest) == true))
            ],
            () => PluginSource?.ReadSettings() ?? [],
            (id, key, value, revision, token) => PluginSource is { } source
                ? source.ConfigureAsync(id, key, value, revision, token)
                : Task.FromResult(new SteamUiCommandResult(false, "Plugins are not available.")));
    }

    /// <summary>
    ///     Creates the projections only the host reads: the plugins' Steam surfaces, Steam's Switch to
    ///     Desktop and the GPU projection. The session builds every other backend and disposes these after
    ///     the host.
    /// </summary>
    /// <param name="switchToDesktop">Answers Switch to Desktop in Steam's power menu.</param>
    internal void StartProjections(Func<CancellationToken, Task<SteamUiCommandResult>> switchToDesktop)
    {
        PluginSource = _integrations.CommonPlugins is null
            ? null
            : new CommonPluginSteamUiSource(_integrations.CommonPlugins, _integrations.PluginHost,
                Program.CefPluginsOff);
        if (PluginSource is not null && SettingsPage is { } wsgmSettings)
        {
            // A plugin starting, stopping or taking a setting changes the Plugins page.
            PluginSource.Changed += wsgmSettings.Refresh;
        }

        PowerMenu = new SteamPowerMenuBackend(_state.InGameMode, switchToDesktop);
        // Steam reads the overlay's graphics projection; the session disposes it after the host.
        Graphics = _integrations.GraphicsOverlay is GraphicsOverlayBridge graphics
            ? new SteamGraphicsService(graphics)
            : null;
    }

    /// <summary>
    ///     Creates the host over the backends, applies the surfaces once and follows the device's glyph
    ///     profiles. A host that cannot be created leaves WSGM's Steam surfaces off and the session running.
    /// </summary>
    /// <param name="toggleOverlay">Toggles WSGM's overlay from Steam.</param>
    /// <param name="backends">Every backend behind Steam's surfaces.</param>
    internal void StartHost(Func<CancellationToken, Task<bool>> toggleOverlay, SteamUiBackends backends)
    {
        var steamUiTransport = Transport
                               ?? throw new InvalidOperationException("Steam UI transport was not created.");
        try
        {
            Host = new SteamUiSessionHost(steamUiTransport, toggleOverlay, backends);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Steam's surfaces degrade alone: the rest of the session keeps working without them.
            Log.Error($"Steam UI host could not be created; WSGM's Steam surfaces stay off: {ex.Message}");
        }

        ApplySurfaces();
        if (_integrations.Device is { } device)
        {
            // Two sources change the active profile: the package publishing its profiles, and
            // the user changing the selection mode. Both land on the same apply.
            device.PhysicalGlyphCatalog.Changed += OnPhysicalGlyphProfilesChanged;
        }
    }

    private void OnPhysicalGlyphProfilesChanged()
    {
        ApplySurfaces();
    }

    /// <summary>Stops following the device's glyph profiles, before the device stops.</summary>
    internal void DetachDevice()
    {
        if (_integrations.Device is { } device)
        {
            device.PhysicalGlyphCatalog.Changed -= OnPhysicalGlyphProfilesChanged;
        }
    }

    /// <summary>
    ///     Stops Steam's Quick Access reaching the device, AutoTDP and GPU owners before they stop. No CEF
    ///     round trip: the patches stay until the host is stopped after device cleanup.
    /// </summary>
    internal void CloseHostAdmission()
    {
        Host?.CloseAdmission();
    }

    /// <summary>
    ///     Retracts Steam's patches and stops the host. Bounded: a retraction wedged in Steam must not hold
    ///     shutdown past its deadline. Without the gate the host is left undisposed and the transport
    ///     disposal ends its traffic.
    /// </summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Completion of the retraction.</returns>
    internal async Task RetractForShutdownAsync(ShutdownSteps steps)
    {
        if (steps.Token.IsCancellationRequested)
        {
            steps.Record(new TimeoutException(
                "A Steam UI retraction was still running at the shutdown deadline; its patches were not retracted."));
            Log.Warn("Steam UI: a retraction held the gate at the shutdown deadline; skipping the host's retraction.");
            return;
        }

        await _cefMasterGate.WaitAsync(steps.Token).ConfigureAwait(false);

        try
        {
            if (Host is { } steamUi)
            {
                // The toolkit's runtime and patch manager stop waiting at the deadline and name what they
                // left, rather than holding shutdown behind a hung renderer.
                await steamUi.StopAsync(steps.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            Host = null;
            _cefMasterGate.Release();
        }
    }

    /// <summary>Disposes the graphics and plugin projections after the host that read them; they only unsubscribe.</summary>
    internal void DisposeProjections()
    {
        Graphics?.Dispose();
        Graphics = null;
        if (PluginSource is { } pluginSteamUi)
        {
            PluginSource = null;
            if (SettingsPage is { } settingsService)
            {
                pluginSteamUi.Changed -= settingsService.Refresh;
            }

            pluginSteamUi.Dispose();
        }
    }

    /// <summary>Ends the diagnostics subscriptions, then disposes the transport.</summary>
    /// <returns>Completion of both.</returns>
    internal async Task DisposeTransportAsync()
    {
        var transport = Transport;
        Transport = null;
        var consoleSubscriptions = _consoleSubscriptions;
        _consoleSubscriptions = [];
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
    }
}

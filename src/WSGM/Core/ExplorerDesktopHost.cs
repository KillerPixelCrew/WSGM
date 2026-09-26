using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>
///     Session-owned normal Explorer launch path. It captures the canonical taskbar owner
///     before each orderly exit and retains a medium, jobless fixed-purpose anchor across the exit.
/// </summary>
internal sealed class ExplorerDesktopHost : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReadinessStability = TimeSpan.FromMilliseconds(500);

    /// <summary>Deadline share kept for starting Explorer after a retired shell was waited for.</summary>
    private static readonly TimeSpan LaunchReserve = TimeSpan.FromSeconds(8);

    private readonly DesktopAppLifecycle _desktopApps = new(new DesktopAppProcessBackend(), Log.Warn);

    // Anchor replacement, Explorer dispatch, and disposal share one owner. Disposal closes
    // admission before waiting so no caller can pass a stale disposed check and publish an anchor
    // after teardown has already detached the previous one.
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly int _sessionId;
    private ExplorerShellAnchor? _anchor;
    private int _desktopAppsGeneration;
    private int _desktopAppsSuspended;
    private int _disposeState;

    /// <summary>Creates a desktop-host owner for the current interactive session.</summary>
    internal ExplorerDesktopHost()
    {
        _sessionId = WindowFinder.CurrentSessionId;
    }

    private static string ExplorerPath => ExplorerControl.ExplorerPath;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return;
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var anchor = _anchor;
            _anchor = null;
            if (anchor is not null)
            {
                await anchor.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _disposeState, 2);
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Captures the current canonical taskbar owner and creates the replacement launch
    ///     anchor before the orderly Explorer exit becomes irreversible.
    /// </summary>
    internal async Task<ExplorerPreparationResult> PrepareForExplorerExitAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposalRequested();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposalRequested();
            return await PrepareForExplorerExitUnderGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<ExplorerPreparationResult> PrepareForExplorerExitUnderGateAsync(
        CancellationToken cancellationToken)
    {
        LogObservation(
            "WSGM",
            new ExplorerDesktopObservation(
                NativeShellProcess.Inspect(checked((uint)Environment.ProcessId)),
                0,
                0,
                false,
                false,
                new ExplorerShellAcceptance(false, ExplorerShellRejection.NotReady),
                ExplorerDesktopOutcome.Failed));

        // Capturing a launch parent needs the real shell owner and its token, not a fast UI
        // response. Display changes and desktop hooks can briefly occupy Explorer's UI thread.
        var shell = ObserveCurrentDesktop(_sessionId, false);
        LogObservation("Explorer capture", shell);
        if (!CanCaptureShell(shell))
        {
            var detail = $"current-shell-{shell.Acceptance.Rejection}";
            Log.Warn($"Explorer takeover refused before orderly exit: {shell.Acceptance.Rejection}. "
                     + "The current desktop was preserved.");
            return new ExplorerPreparationResult(false, detail);
        }

        if (!NativeShellProcess.TryOpenLaunchParent(
                shell.Process.ProcessId,
                out var parent,
                out var openError))
        {
            var detail = $"parent-open-error-{openError}";
            Log.Warn($"Explorer takeover refused: taskbar owner pid {shell.Process.ProcessId} could not be retained "
                     + $"as a launch parent (error {openError}). Sign out or reboot once before retrying.");
            return new ExplorerPreparationResult(false, detail);
        }

        ExplorerShellAnchorStartResult started;
        using (parent)
        {
            started = await ExplorerShellAnchor.StartAsync(
                parent!,
                Environment.ProcessId,
                _sessionId,
                cancellationToken).ConfigureAwait(false);
        }

        if (started.Anchor is null)
        {
            var stale = _anchor;
            _anchor = null;
            if (stale is not null)
            {
                await stale.DisposeAsync().ConfigureAwait(false);
            }

            Log.Warn($"Explorer takeover refused: normal shell anchor creation failed: {started.Error}");
            return new ExplorerPreparationResult(false, started.Error);
        }

        var replacement = started.Anchor;
        var anchorInfo = NativeShellProcess.Inspect(replacement.ProcessId);
        var anchorExecutable = ExplorerShellAnchor.ExecutablePath
                               ?? throw new InvalidOperationException(
                                   "The shell-anchor executable path disappeared after launch.");
        var anchorAcceptance = ExplorerShellPolicy.EvaluateLaunchAnchor(
            anchorInfo,
            anchorExecutable,
            _sessionId,
            shell.Process.JobMembership is NativeJobMembership.InJob);
        LogObservation(
            "Explorer launch anchor",
            new ExplorerDesktopObservation(
                anchorInfo,
                0,
                0,
                false,
                false,
                anchorAcceptance,
                !anchorAcceptance.Accepted
                    ? ExplorerDesktopOutcome.Failed
                    : anchorAcceptance.JobBoundLikeSource
                        ? ExplorerDesktopOutcome.Degraded
                        : ExplorerDesktopOutcome.Normal));
        if (!anchorAcceptance.Accepted)
        {
            Log.Warn("Explorer takeover refused: launch anchor did not inherit normal process semantics "
                     + $"({anchorAcceptance.Rejection}).");
            await replacement.DisposeAsync().ConfigureAwait(false);
            return new ExplorerPreparationResult(false, $"anchor-{anchorAcceptance.Rejection}");
        }

        var previous = _anchor;
        _anchor = replacement;
        if (previous is not null)
        {
            // The replacement is already installed, so retiring the old anchor cannot change the
            // outcome of this takeover and must never be able to fail it.
            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Retiring the previous shell anchor failed, continuing with the new one "
                         + $"(pid {replacement.ProcessId}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (anchorAcceptance.JobBoundLikeSource)
        {
            Log.Warn("Explorer launch anchor is job-bound like the Explorer it replaces "
                     + $"(pid {shell.Process.ProcessId}); continuing with a degraded desktop.");
        }

        Log.Info($"Explorer launch anchor ready (pid {_anchor.ProcessId}, "
                 + $"parent pid {shell.Process.ProcessId}).");
        return new ExplorerPreparationResult(true, "ready");
    }

    // A job-bound shell may supply its verified medium token. The anchor it yields may be job-bound
    // too (EvaluateLaunchAnchor), but still has to pass every other acceptance check before exit.
    private static bool CanCaptureShell(ExplorerDesktopObservation shell)
    {
        return shell.Acceptance.Accepted || shell.Acceptance.Rejection is ExplorerShellRejection.JobBound;
    }

    internal bool IsApplicationLaunchSuppressed(string path)
    {
        return Volatile.Read(ref _desktopAppsSuspended) != 0 && DesktopAppLifecycle.MatchesPath(path);
    }

    internal int ApplicationLaunchGeneration(string path)
    {
        return DesktopAppLifecycle.MatchesPath(path) ? Volatile.Read(ref _desktopAppsGeneration) : 0;
    }

    /// <summary>
    ///     Stops captured desktop integrations before the irreversible Explorer exit.
    ///     A refused or partial app exit keeps Explorer and restores the affected applications.
    /// </summary>
    internal async Task<bool> ExitExplorerAndWaitAsync(TimeSpan timeout)
    {
        ThrowIfDisposalRequested();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposalRequested();
            Volatile.Write(ref _desktopAppsSuspended, 1);
            Interlocked.Increment(ref _desktopAppsGeneration);
            var stopped = await _desktopApps.StopAsync(CancellationToken.None).ConfigureAwait(false);
            // The transition's shared desktop-return sequence owns every failed exit, including
            // partial shutdown. Never infer a preserved desktop from a surviving Explorer PID.
            if (!stopped)
            {
                return false;
            }

            var exited = false;
            try
            {
                exited = await Task.Run(() => ExplorerControl.ExitExplorerAndWait(timeout)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Explorer exit failed", ex);
            }

            return exited;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    ///     Adopts an already-normal taskbar owner or restores Explorer through the captured
    ///     jobless anchor, waiting for the resulting taskbar owner rather than trusting the created PID.
    /// </summary>
    internal async Task<ExplorerDesktopResult> RestoreDesktopAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposalRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var deadline = DateTimeOffset.UtcNow + timeout;
        var elapsed = Stopwatch.StartNew();
        var gateRemaining = Remaining(deadline);
        if (gateRemaining <= TimeSpan.Zero)
        {
            return CreateOperationGateTimeout(elapsed.Elapsed);
        }

        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gateCancellation.CancelAfter(gateRemaining);
        try
        {
            await _operationGate.WaitAsync(gateCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfDisposalRequested();
            // A preceding serialized operation may already have dispatched Explorer. Timeout at
            // this boundary is therefore uncertain and must suppress any competing TrayHost.
            return CreateOperationGateTimeout(elapsed.Elapsed);
        }

        try
        {
            ThrowIfDisposalRequested();
            var remaining = Remaining(deadline);
            if (remaining <= TimeSpan.Zero)
            {
                return CreateOperationGateTimeout(elapsed.Elapsed);
            }

            var result = await RestoreDesktopUnderGateAsync(deadline, elapsed, cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome is not (ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded))
            {
                return result;
            }

            await _desktopApps.RestoreAsync(deadline).ConfigureAwait(false);
            Volatile.Write(ref _desktopAppsSuspended, 0);
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<ExplorerDesktopResult> RestoreDesktopUnderGateAsync(
        DateTimeOffset deadline,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        var existing = ObserveCurrentDesktop(_sessionId);
        if (existing.HasShellSurface)
        {
            var adopted = await WaitForDesktopAsync(
                deadline,
                ExplorerDesktopRoute.ExistingShell,
                0,
                false,
                elapsed,
                cancellationToken).ConfigureAwait(false);
            if (adopted.Outcome is ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded
                || adopted.ShellSurfacePresent)
            {
                LogResult("adopt", adopted);
                return adopted;
            }
        }

        var anchorError = "No anchor was captured.";
        // A retired shell still finishing its exit is waited for, never killed, before a new one starts:
        // a fresh Explorer takes the shell mutex and polls GetShellWindow for 3 s to decide what it is,
        // and one started beside a lingering shell came up unresponsive (2026-09-13). The wait keeps
        // enough of the deadline for that decision and the readiness window.
        var retiredWait = Remaining(deadline) - LaunchReserve;
        if (retiredWait > TimeSpan.Zero)
        {
            await Task.Run(() => ExplorerControl.WaitForRetiredShell(retiredWait), cancellationToken)
                .ConfigureAwait(false);
        }

        if (_anchor is not null)
        {
            var launch = await _anchor.StartExplorerAsync(
                Remaining(deadline),
                cancellationToken).ConfigureAwait(false);
            anchorError = launch.Detail;
            Log.Info($"Explorer anchor request: anchor pid {_anchor.ProcessId}, "
                     + $"disposition={launch.Disposition}, created pid={launch.ProcessId}, detail={launch.Detail}.");
            if (!ExplorerShellPolicy.CanDispatchScheduler(
                    launch.Disposition,
                    false))
            {
                var result = await WaitForDesktopAsync(
                    deadline,
                    ExplorerDesktopRoute.ShellAnchor,
                    launch.ProcessId,
                    true,
                    elapsed,
                    cancellationToken).ConfigureAwait(false);
                LogResult("anchor", result);
                return result;
            }
        }

        // A taskbar or shell surface can appear between an explicit anchor failure and fallback.
        // Once one exists, never dispatch a second shell; let that owner settle or fail explicitly.
        var beforeFallback = ObserveCurrentDesktop(_sessionId);
        if (!ExplorerShellPolicy.CanDispatchScheduler(
                ExplorerAnchorLaunchDisposition.NotDispatched,
                beforeFallback.HasShellSurface))
        {
            var settling = await WaitForDesktopAsync(
                deadline,
                ExplorerDesktopRoute.ShellAnchor,
                0,
                true,
                elapsed,
                cancellationToken).ConfigureAwait(false);
            LogResult("late-anchor", settling);
            return settling;
        }

        // Scheduler registration, dispatch, deletion, and the readiness observation all consume
        // this restoration's one absolute deadline. Cleanup is best effort once that budget closes.
        Log.Warn("Explorer shell anchor unavailable; using degraded scheduler recovery. " + anchorError);
        var schedulerDisposition =
            await UnelevatedLauncher.TryStartViaScheduledTaskAsync(
                ExplorerPath,
                "",
                deadline,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        var schedulerMayHaveDispatched =
            ExplorerShellPolicy.SchedulerMayHaveDispatched(schedulerDisposition);
        if (!schedulerMayHaveDispatched)
        {
            var failed = CreateFailure(
                ExplorerDesktopRoute.ScheduledTaskRecovery,
                0,
                false,
                elapsed.Elapsed,
                "scheduler-launch-failed");
            LogResult("scheduler", failed);
            return failed;
        }

        if (schedulerDisposition is ScheduledTaskLaunchDisposition.Unknown)
        {
            Log.Warn("Explorer scheduler request crossed an uncertain dispatch boundary; "
                     + "waiting for the desktop without recreating game-mode shell surfaces.");
        }

        var scheduler = await WaitForDesktopAsync(
            deadline,
            ExplorerDesktopRoute.ScheduledTaskRecovery,
            0,
            schedulerMayHaveDispatched,
            elapsed,
            cancellationToken).ConfigureAwait(false);
        LogResult("scheduler", scheduler);
        return scheduler;
    }

    /// <summary>
    ///     Observes both shell surfaces and requires GetShellWindow and Shell_TrayWnd to have
    ///     the same owner. Restored desktops also require responsive windows; launch-parent capture
    ///     only needs the process identity and token.
    /// </summary>
    internal static ExplorerDesktopObservation ObserveCurrentDesktop(
        int expectedSessionId, bool requireResponsive = true)
    {
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        var taskbarPresent = taskbar != 0 && NativeMethods.IsWindow(taskbar);
        uint taskbarOwner = 0;
        if (taskbarPresent)
        {
            NativeMethods.GetWindowThreadProcessId(taskbar, out taskbarOwner);
        }

        var shellWindow = NativeMethods.GetShellWindow();
        var shellPresent = shellWindow != 0 && NativeMethods.IsWindow(shellWindow);
        uint shellOwner = 0;
        if (shellPresent)
        {
            NativeMethods.GetWindowThreadProcessId(shellWindow, out shellOwner);
        }

        var processId = taskbarOwner != 0 ? taskbarOwner : shellOwner;
        var process = processId == 0
            ? NativeShellProcessInfo.Unavailable(0, 0)
            : NativeShellProcess.Inspect(processId);
        var ownsSurfaces = ExplorerShellPolicy.OwnsShellSurfaces(
            taskbarPresent,
            shellPresent,
            taskbarOwner,
            shellOwner);
        var responsive = !requireResponsive
                         || (ownsSurfaces && IsResponsive(taskbar) && IsResponsive(shellWindow));
        var ready = ownsSurfaces && responsive;
        var acceptance = ExplorerShellPolicy.Evaluate(
            process,
            ExplorerPath,
            expectedSessionId,
            ready,
            true);
        // Only the liveness probe failed: the canonical Explorer owns both surfaces and passed every
        // identity check. Name that separately so the desktop is reported as degraded rather than
        // absent when a third-party window blocks Explorer's UI thread (Steam Big Picture, 2026-09-26).
        if (!acceptance.Accepted
            && acceptance.Rejection is ExplorerShellRejection.NotReady
            && ownsSurfaces)
        {
            acceptance = new ExplorerShellAcceptance(false, ExplorerShellRejection.ShellUnresponsive);
        }

        var outcome = ExplorerShellPolicy.ClassifyDesktop(
            acceptance,
            ExplorerDesktopRoute.ExistingShell);
        return new ExplorerDesktopObservation(
            process,
            taskbarOwner,
            shellOwner,
            taskbarPresent || shellPresent,
            ready,
            acceptance,
            outcome);
    }

    private static bool IsResponsive(nint window)
    {
        return NativeMethods.SendMessageTimeoutW(window, 0, 0, 0, NativeMethods.SmtoAbortIfHung,
            500, out _) != 0;
    }

    private async Task<ExplorerDesktopResult> WaitForDesktopAsync(
        DateTimeOffset deadline,
        ExplorerDesktopRoute route,
        uint createdProcessId,
        bool launchDispatched,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        uint stableProcessId = 0;
        var stableOutcome = ExplorerDesktopOutcome.Failed;
        Stopwatch? stable = null;
        ExplorerDesktopObservation last;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = ObserveCurrentDesktop(_sessionId);
            var outcome = ExplorerShellPolicy.ClassifyDesktop(last.Acceptance, route);
            // An unresponsive owner is a usable desktop, but only as the deadline fallback below.
            // A starting Explorer is briefly unresponsive on every return, so accepting it here
            // would stop the wait early and report every ordinary return as degraded.
            if (last.Acceptance.Rejection is ExplorerShellRejection.ShellUnresponsive)
            {
                outcome = ExplorerDesktopOutcome.Failed;
            }

            if (outcome is ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded)
            {
                if (stable is null
                    || stableProcessId != last.Process.ProcessId
                    || stableOutcome != outcome)
                {
                    stableProcessId = last.Process.ProcessId;
                    stableOutcome = outcome;
                    stable = Stopwatch.StartNew();
                }
                else if (stable.Elapsed >= ReadinessStability)
                {
                    return new ExplorerDesktopResult(
                        outcome,
                        route,
                        last.Process.ProcessId,
                        createdProcessId,
                        outcome is ExplorerDesktopOutcome.Normal ? "normal-stable" : "degraded-stable",
                        launchDispatched,
                        last.HasShellSurface,
                        elapsed.Elapsed);
                }
            }
            else
            {
                stable = null;
                stableProcessId = 0;
                stableOutcome = ExplorerDesktopOutcome.Failed;
            }

            var delay = Remaining(deadline);
            if (delay <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(delay < PollInterval ? delay : PollInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        // One final observation closes the race where the taskbar appeared on the deadline, and it
        // decides what the caller is handed. A desktop the canonical Explorer already owns is
        // reported as degraded rather than as a total failure: the whole desktop return is abandoned
        // on Failed, and a shell that is merely late or blocked behind another application's hung
        // window left the session with no desktop, no Steam and no retry (Claw, 2026-09-26).
        last = ObserveCurrentDesktop(_sessionId);
        var finalOutcome = ExplorerShellPolicy.ClassifyDesktop(last.Acceptance, route);
        var unresponsive = last.Acceptance.Rejection is ExplorerShellRejection.ShellUnresponsive;
        var usable = finalOutcome is ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded;
        var detail = unresponsive
            ? "timeout-unresponsive-shell"
            : usable
                ? "timeout-not-stable"
                : $"timeout-{last.Acceptance.Rejection}";
        return new ExplorerDesktopResult(
            usable ? ExplorerDesktopOutcome.Degraded : ExplorerDesktopOutcome.Failed,
            route,
            last.Process.ProcessId,
            createdProcessId,
            detail,
            launchDispatched,
            last.HasShellSurface,
            elapsed.Elapsed);
    }

    private ExplorerDesktopResult CreateFailure(
        ExplorerDesktopRoute route,
        uint createdProcessId,
        bool launchDispatched,
        TimeSpan elapsed,
        string detail)
    {
        var observation = ObserveCurrentDesktop(_sessionId);
        return new ExplorerDesktopResult(
            ExplorerDesktopOutcome.Failed,
            route,
            observation.Process.ProcessId,
            createdProcessId,
            detail,
            launchDispatched,
            observation.HasShellSurface,
            elapsed);
    }

    private static ExplorerDesktopResult CreateOperationGateTimeout(TimeSpan elapsed)
    {
        return new ExplorerDesktopResult(
            ExplorerDesktopOutcome.Failed,
            ExplorerDesktopRoute.ShellAnchor,
            0,
            0,
            "operation-gate-timeout",
            // The preceding serialized operation may already have crossed a launch boundary.
            true,
            false,
            elapsed);
    }

    private static void LogObservation(string label, ExplorerDesktopObservation observation)
    {
        var process = observation.Process;
        Log.Info($"{label}: pid={process.ProcessId}, session={process.SessionId?.ToString() ?? "unknown"}, "
                 + $"integrity={process.Integrity}, job={process.JobMembership}, "
                 + $"taskbarOwner={observation.TaskbarOwnerProcessId}, "
                 + $"shellOwner={observation.ShellOwnerProcessId}, ready={observation.Initialized}, "
                 + $"image={process.ImagePath ?? "unknown"}, errors={process.Errors}.");
    }

    private void LogResult(string source, ExplorerDesktopResult result)
    {
        LogObservation($"Explorer desktop {source} observation", ObserveCurrentDesktop(_sessionId));
        var message = $"Explorer desktop {source}: route={result.Route}, outcome={result.Outcome}, "
                      + $"result pid={result.ProcessId}, created pid={result.CreatedProcessId}, "
                      + $"launchDispatched={result.LaunchDispatched}, shellSurface={result.ShellSurfacePresent}, "
                      + $"elapsed={result.Elapsed.TotalMilliseconds:0} ms, detail={result.Detail}.";
        if (result.Outcome is ExplorerDesktopOutcome.Normal)
        {
            Log.Info(message);
        }
        else
        {
            Log.Warn(message);
        }
    }

    private static TimeSpan Remaining(DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void ThrowIfDisposalRequested()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }
}

/// <summary>Result of capturing a canonical Explorer and creating its replacement anchor.</summary>
internal readonly record struct ExplorerPreparationResult(
    bool Prepared,
    string Detail);

/// <summary>One atomic observation of Explorer's shell and taskbar surfaces.</summary>
internal readonly record struct ExplorerDesktopObservation(
    NativeShellProcessInfo Process,
    uint TaskbarOwnerProcessId,
    uint ShellOwnerProcessId,
    bool HasShellSurface,
    bool Initialized,
    ExplorerShellAcceptance Acceptance,
    ExplorerDesktopOutcome Outcome);

/// <summary>Quality of the restored desktop shell.</summary>
internal enum ExplorerDesktopOutcome
{
    /// <summary>The taskbar owner is canonical, medium-integrity, and jobless.</summary>
    Normal,

    /// <summary>A canonical current-session medium Explorer is usable through recovery only.</summary>
    Degraded,

    /// <summary>No verified usable taskbar was produced.</summary>
    Failed
}

/// <summary>Route used to obtain the observed desktop.</summary>
internal enum ExplorerDesktopRoute
{
    /// <summary>An already-running valid shell was adopted.</summary>
    ExistingShell,

    /// <summary>The captured fixed-purpose jobless anchor started Explorer.</summary>
    ShellAnchor,

    /// <summary>The scheduled-task path restored a usable but recovery-only shell.</summary>
    ScheduledTaskRecovery
}

/// <summary>Verified result of a desktop restoration attempt.</summary>
internal readonly record struct ExplorerDesktopResult
{
    /// <summary>
    ///     Creates a restoration result while enforcing that the scheduled-task route is
    ///     recovery-only even when its observed process happens to pass the normal shell checks.
    /// </summary>
    internal ExplorerDesktopResult(
        ExplorerDesktopOutcome outcome,
        ExplorerDesktopRoute route,
        uint processId,
        uint createdProcessId,
        string detail,
        bool launchDispatched,
        bool shellSurfacePresent,
        TimeSpan elapsed)
    {
        Outcome = route is ExplorerDesktopRoute.ScheduledTaskRecovery
                  && outcome is ExplorerDesktopOutcome.Normal
            ? ExplorerDesktopOutcome.Degraded
            : outcome;
        Route = route;
        ProcessId = processId;
        CreatedProcessId = createdProcessId;
        Detail = detail;
        LaunchDispatched = launchDispatched;
        ShellSurfacePresent = shellSurfacePresent;
        Elapsed = elapsed;
    }

    /// <summary>Gets the verified quality of the restored desktop.</summary>
    internal ExplorerDesktopOutcome Outcome { get; }

    /// <summary>Gets the route that produced the observed desktop.</summary>
    internal ExplorerDesktopRoute Route { get; }

    /// <summary>Gets the process that owns the verified shell surfaces.</summary>
    internal uint ProcessId { get; }

    /// <summary>Gets the process identifier returned by the launch operation, if any.</summary>
    internal uint CreatedProcessId { get; }

    /// <summary>Gets the diagnostic result detail.</summary>
    internal string Detail { get; }

    /// <summary>Gets whether an Explorer launch crossed its dispatch boundary.</summary>
    internal bool LaunchDispatched { get; }

    /// <summary>Gets whether any shell surface was observed.</summary>
    internal bool ShellSurfacePresent { get; }

    /// <summary>Gets the elapsed restoration time.</summary>
    internal TimeSpan Elapsed { get; }
}

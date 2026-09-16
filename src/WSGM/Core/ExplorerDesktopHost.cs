using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Session-owned normal Explorer launch path. It captures the canonical taskbar owner
/// before each orderly exit and retains a medium, jobless fixed-purpose anchor across the exit.</summary>
internal sealed class ExplorerDesktopHost : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReadinessStability = TimeSpan.FromMilliseconds(500);
    private readonly int _sessionId;
    // Anchor replacement, Explorer dispatch, and disposal share one owner. Disposal closes
    // admission before waiting so no caller can pass a stale disposed check and publish an anchor
    // after teardown has already detached the previous one.
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly DesktopAppLifecycle _desktopApps = new(new DesktopAppProcessBackend(), Log.Warn);
    private int _desktopAppsSuspended;
    private int _desktopAppsGeneration;
    private ExplorerShellAnchor? _anchor;
    private int _disposeState;

    /// <summary>Creates a desktop-host owner for the current interactive session.</summary>
    internal ExplorerDesktopHost()
    {
        _sessionId = WindowFinder.CurrentSessionId;
    }

    /// <summary>Captures the current canonical taskbar owner and creates the replacement launch
    /// anchor before the orderly Explorer exit becomes irreversible.</summary>
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
        var shell = ObserveCurrentDesktop(_sessionId, requireResponsive: false);
        LogObservation("Explorer capture", shell);
        if (!CanCaptureShell(shell))
        {
            var detail = $"current-shell-{shell.Acceptance.Rejection}";
            Log.Warn($"Explorer takeover refused before orderly exit: {shell.Acceptance.Rejection}. "
                + "The current desktop was preserved.");
            return new ExplorerPreparationResult(false, shell.Acceptance.Rejection, detail);
        }

        if (!NativeShellProcess.TryOpenLaunchParent(
                shell.Process.ProcessId,
                out var parent,
                out var openError))
        {
            var detail = $"parent-open-error-{openError}";
            Log.Warn($"Explorer takeover refused: taskbar owner pid {shell.Process.ProcessId} could not be retained "
                + $"as a launch parent (error {openError}). Sign out or reboot once before retrying.");
            return new ExplorerPreparationResult(false, ExplorerShellRejection.ProcessUnavailable, detail);
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
            return new ExplorerPreparationResult(false, ExplorerShellRejection.ProcessUnavailable, started.Error);
        }

        var replacement = started.Anchor;
        var anchorInfo = NativeShellProcess.Inspect(replacement.ProcessId);
        var anchorExecutable = ExplorerShellAnchor.ExecutablePath
                               ?? throw new InvalidOperationException("The shell-anchor executable path disappeared after launch.");
        var anchorAcceptance = ExplorerShellPolicy.Evaluate(
            anchorInfo,
            anchorExecutable,
            _sessionId,
            ownsReadyTaskbar: false,
            requireReadyTaskbar: false);
        LogObservation(
            "Explorer launch anchor",
            new ExplorerDesktopObservation(
                anchorInfo,
                0,
                0,
                false,
                false,
                anchorAcceptance,
                anchorAcceptance.Accepted ? ExplorerDesktopOutcome.Normal : ExplorerDesktopOutcome.Failed));
        if (!anchorAcceptance.Accepted)
        {
            Log.Warn("Explorer takeover refused: launch anchor did not inherit normal process semantics "
                + $"({anchorAcceptance.Rejection}).");
            await replacement.DisposeAsync().ConfigureAwait(false);
            return new ExplorerPreparationResult(false, anchorAcceptance.Rejection, $"anchor-{anchorAcceptance.Rejection}");
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
        Log.Info($"Explorer launch anchor ready (pid {_anchor.ProcessId}, "
            + $"parent pid {shell.Process.ProcessId}).");
        return new ExplorerPreparationResult(true, ExplorerShellRejection.None, "ready");
    }

    // A job-bound shell may supply its verified medium token, but may not supply the new
    // anchor's job. The anchor still has to pass every normal acceptance check before exit.
    private static bool CanCaptureShell(ExplorerDesktopObservation shell) =>
        shell.Acceptance.Accepted || shell.Acceptance.Rejection is ExplorerShellRejection.JobBound;

    internal bool IsApplicationLaunchSuppressed(string path) =>
        Volatile.Read(ref _desktopAppsSuspended) != 0 && DesktopAppLifecycle.MatchesPath(path);

    internal int ApplicationLaunchGeneration(string path) =>
        DesktopAppLifecycle.MatchesPath(path) ? Volatile.Read(ref _desktopAppsGeneration) : 0;

    /// <summary>Stops captured desktop integrations before the irreversible Explorer exit.
    /// A refused or partial app exit keeps Explorer and restores the affected applications.</summary>
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
            var exited = false;
            if (stopped)
            {
                try
                {
                    exited = await Task.Run(() => ExplorerControl.ExitExplorerAndWait(timeout)).ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Error("Explorer exit failed", ex); }
            }
            // The transition's shared desktop-return sequence owns every failed exit, including
            // partial shutdown. Never infer a preserved desktop from a surviving Explorer PID.
            return exited;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Adopts an already-normal taskbar owner or restores Explorer through the captured
    /// jobless anchor, waiting for the resulting taskbar owner rather than trusting the created PID.</summary>
    internal async Task<ExplorerDesktopResult> RestoreDesktopAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposalRequested();
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

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
            if (result.Outcome is ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded)
            {
                await _desktopApps.RestoreAsync(deadline).ConfigureAwait(false);
                Volatile.Write(ref _desktopAppsSuspended, 0);
            }
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
                createdProcessId: 0,
                launchDispatched: false,
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
                    shellSurfacePresent: false))
            {
                var result = await WaitForDesktopAsync(
                    deadline,
                    ExplorerDesktopRoute.ShellAnchor,
                    launch.ProcessId,
                    launchDispatched: true,
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
                createdProcessId: 0,
                launchDispatched: true,
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
                cancellationToken).ConfigureAwait(false);
        var schedulerMayHaveDispatched =
            ExplorerShellPolicy.SchedulerMayHaveDispatched(schedulerDisposition);
        if (!schedulerMayHaveDispatched)
        {
            var failed = CreateFailure(
                ExplorerDesktopRoute.ScheduledTaskRecovery,
                0,
                launchDispatched: false,
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
            createdProcessId: 0,
            launchDispatched: schedulerMayHaveDispatched,
            elapsed,
            cancellationToken).ConfigureAwait(false);
        LogResult("scheduler", scheduler);
        return scheduler;
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

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

    /// <summary>Observes both shell surfaces and requires GetShellWindow and Shell_TrayWnd to have
    /// the same owner. Restored desktops also require responsive windows; launch-parent capture
    /// only needs the process identity and token.</summary>
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
        var ready = ExplorerShellPolicy.IsInitializedShellOwner(
            taskbarPresent,
            shellPresent,
            taskbarOwner,
            shellOwner,
            responsive: !requireResponsive || (taskbarPresent && shellPresent
                && IsResponsive(taskbar) && IsResponsive(shellWindow)));
        var acceptance = ExplorerShellPolicy.Evaluate(
            process,
            ExplorerPath,
            expectedSessionId,
            ready,
            requireReadyTaskbar: true);
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

    private static bool IsResponsive(nint window) =>
        NativeMethods.SendMessageTimeoutW(window, 0, 0, 0, NativeMethods.SmtoAbortIfHung,
            500, out _) != 0;

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
        var last = ObserveCurrentDesktop(_sessionId);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = ObserveCurrentDesktop(_sessionId);
            var outcome = ExplorerShellPolicy.ClassifyDesktop(last.Acceptance, route);
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

        // One final observation closes the race where the taskbar appeared on the deadline. It is
        // deliberately not accepted without the stability window, but it prevents TrayHost from
        // being recreated next to a late Explorer.
        last = ObserveCurrentDesktop(_sessionId);
        var finalOutcome = ExplorerShellPolicy.ClassifyDesktop(last.Acceptance, route);
        var detail = finalOutcome is ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded
            ? "timeout-not-stable"
            : $"timeout-{last.Acceptance.Rejection}";
        return new ExplorerDesktopResult(
            ExplorerDesktopOutcome.Failed,
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

    private static ExplorerDesktopResult CreateOperationGateTimeout(TimeSpan elapsed) =>
        new(
            ExplorerDesktopOutcome.Failed,
            ExplorerDesktopRoute.ShellAnchor,
            0,
            0,
            "operation-gate-timeout",
            // The preceding serialized operation may already have crossed a launch boundary.
            launchDispatched: true,
            shellSurfacePresent: false,
            elapsed: elapsed);

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

    private void ThrowIfDisposalRequested() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private static string ExplorerPath => ExplorerControl.ExplorerPath;
}

/// <summary>Result of capturing a canonical Explorer and creating its replacement anchor.</summary>
internal readonly record struct ExplorerPreparationResult(
    bool Prepared,
    ExplorerShellRejection Rejection,
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
    /// <summary>Creates a restoration result while enforcing that the scheduled-task route is
    /// recovery-only even when its observed process happens to pass the normal shell checks.</summary>
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

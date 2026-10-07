using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>
///     Session-owned normal Explorer launch path. It captures the canonical taskbar owner
///     before each orderly exit and retains a medium fixed-purpose anchor across the exit.
///     A job-bound source may supply a job-bound anchor as explicitly degraded recovery.
/// </summary>
internal sealed class ExplorerDesktopHost : IAsyncDisposable
{
    private const uint ExitExplorerMessage = 0x05B4;
    private const uint WmClose = 0x0010;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReadinessStability = TimeSpan.FromMilliseconds(500);

    /// <summary>Deadline share kept for starting Explorer after a retired shell was waited for.</summary>
    private static readonly TimeSpan LaunchReserve = TimeSpan.FromSeconds(8);

    private readonly UserDataContext _context;

    private readonly DesktopAppLifecycle _desktopApps;

    // Anchor replacement, Explorer dispatch, and disposal share one owner. Disposal closes
    // admission before waiting so no caller can pass a stale disposed check and publish an anchor
    // after teardown has already detached the previous one.
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly int _sessionId;
    private ExplorerShellAnchor? _anchor;
    private int _desktopAppsGeneration;

    // Held from the integration stop until a verified Normal or Degraded restore restarts them; a
    // failed restore keeps it, so the launch sequence never starts a listed integration meanwhile.
    private int _desktopAppsSuspended;
    private int _disposeState;
    private Process? _retired;

    /// <summary>Creates a desktop-host owner for the current interactive session.</summary>
    /// <param name="context">Interactive user-data context used for desktop integration and scheduler recovery.</param>
    internal ExplorerDesktopHost(UserDataContext context)
    {
        _context = context;
        _desktopApps = new DesktopAppLifecycle(new DesktopAppProcessBackend(context), Log.Warn);
        _sessionId = WindowFinder.CurrentSessionId;
    }

    private static string ExplorerPath => ExplorerControl.ExplorerPath;

    /// <summary>Closes operation admission, retires the owned anchor, and releases captured process handles.</summary>
    /// <returns>A task completing after the serialized teardown; repeated calls return without awaiting the first.</returns>
    /// <remarks>This does not restore Explorer. The session must complete desktop recovery before disposing this owner.</remarks>
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
            _retired?.Dispose();
            _retired = null;
            Volatile.Write(ref _disposeState, 2);
            _operationGate.Release();
        }
    }

    /// <summary>
    ///     Captures the current canonical taskbar owner and creates the replacement launch
    ///     anchor before the orderly Explorer exit becomes irreversible.
    /// </summary>
    /// <param name="cancellationToken">Cancels lock admission and bounded anchor preparation.</param>
    /// <returns>Whether a verified replacement anchor is retained; false leaves the current desktop intact.</returns>
    /// <exception cref="ObjectDisposedException">Teardown has begun.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled preparation.</exception>
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
                try
                {
                    await stale.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Warn($"Retiring the previous shell anchor failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Log.Warn($"Explorer takeover refused: normal shell anchor creation failed: {started.Error}");
            return new ExplorerPreparationResult(false, started.Error);
        }

        var replacement = started.Anchor;
        NativeShellProcessInfo anchorInfo;
        ExplorerShellAcceptance anchorAcceptance;
        try
        {
            anchorInfo = NativeShellProcess.Inspect(replacement.ProcessId);
            var anchorExecutable = ExplorerShellAnchor.ExecutablePath
                                   ?? throw new InvalidOperationException(
                                       "The shell-anchor executable path disappeared after launch.");
            anchorAcceptance = ExplorerShellPolicy.EvaluateLaunchAnchor(
                anchorInfo,
                anchorExecutable,
                _sessionId,
                shell.Process.JobMembership is NativeJobMembership.InJob);
        }
        catch
        {
            // The started anchor is not installed yet, so this is its only owner.
            await replacement.DisposeAsync().ConfigureAwait(false);
            throw;
        }

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

    /// <summary>Checks whether takeover currently owns restoration of a listed desktop integration.</summary>
    /// <param name="path">Configured executable or protocol target.</param>
    /// <returns>True only for a cataloged integration while the suspension latch is held.</returns>
    internal bool IsApplicationLaunchSuppressed(string path)
    {
        return Volatile.Read(ref _desktopAppsSuspended) != 0 && DesktopAppLifecycle.MatchesPath(path);
    }

    /// <summary>Reads the takeover generation used to reject stale integration launch decisions.</summary>
    /// <param name="path">Configured executable or protocol target.</param>
    /// <returns>The current generation for cataloged integrations, or zero for unrelated targets.</returns>
    internal int ApplicationLaunchGeneration(string path)
    {
        return DesktopAppLifecycle.MatchesPath(path) ? Volatile.Read(ref _desktopAppsGeneration) : 0;
    }

    /// <summary>
    ///     Stops captured desktop integrations before the irreversible Explorer exit.
    ///     A refused or partial app exit preserves Explorer; the caller must run the shared desktop
    ///     return sequence to restore affected applications and clear launch suppression.
    /// </summary>
    /// <param name="timeout">Budget for observing orderly shell exit after integration shutdown.</param>
    /// <param name="cancellationToken">Cancels admission and waits; an already-dispatched exit is not undone.</param>
    /// <returns>True only after the exit policy accepts sustained absence; false requires desktop-return recovery.</returns>
    /// <exception cref="ObjectDisposedException">Teardown has begun.</exception>
    internal async Task<bool> ExitExplorerAndWaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposalRequested();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposalRequested();
            Volatile.Write(ref _desktopAppsSuspended, 1);
            Interlocked.Increment(ref _desktopAppsGeneration);
            var stopped = await _desktopApps.StopAsync(cancellationToken).ConfigureAwait(false);
            // The transition's shared desktop-return sequence owns every failed exit, including
            // partial shutdown. Never infer a preserved desktop from a surviving Explorer PID.
            if (!stopped)
            {
                return false;
            }

            var exited = false;
            try
            {
                exited = await ExitShellUnderGateAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
    ///     anchor, waiting for the resulting taskbar owner rather than trusting the created PID.
    /// </summary>
    /// <param name="timeout">Positive total restoration budget, including operation-gate admission.</param>
    /// <param name="cancellationToken">Cancels admission and recovery waits, without undoing an accepted launch.</param>
    /// <returns>Observed desktop quality, route, and dispatch certainty; uncertain dispatch forbids competing shell surfaces.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The budget is not positive.</exception>
    /// <exception cref="ObjectDisposedException">Teardown has begun.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled restoration.</exception>
    internal async Task<ExplorerDesktopResult> RestoreDesktopAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDesktopRestoreCancelled(cancellationToken);
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

            ThrowIfDesktopRestoreCancelled(cancellationToken);
            var result = await RestoreDesktopUnderGateAsync(deadline, elapsed, cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome is not (ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded))
            {
                return result;
            }

            ThrowIfDesktopRestoreCancelled(cancellationToken);
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
            await WaitForRetiredShellUnderGateAsync(retiredWait, cancellationToken).ConfigureAwait(false);
        }

        if (_anchor is not null)
        {
            ThrowIfDesktopRestoreCancelled(cancellationToken);
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
        ThrowIfDesktopRestoreCancelled(cancellationToken);
        var schedulerDisposition =
            await ExplorerLauncher.StartAsync(_context, deadline, cancellationToken).ConfigureAwait(false);
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

    private static void ThrowIfDesktopRestoreCancelled(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ApplicationShutdownRequest.SessionEnding)
        {
            throw new OperationCanceledException("The interactive session is ending; Explorer restoration is refused.");
        }
    }

    /// <summary>
    ///     Observes both shell surfaces and requires GetShellWindow and Shell_TrayWnd to have
    ///     the same owner. Restored desktops also require responsive windows; launch-parent capture
    ///     only needs the process identity and token.
    /// </summary>
    /// <param name="expectedSessionId">Session whose canonical Explorer must own the shell surfaces.</param>
    /// <param name="requireResponsive">Whether to probe shell responsiveness in addition to identity/ownership.</param>
    /// <returns>One sampled observation; windows and processes may change immediately afterward.</returns>
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

    private async Task<bool> ExitShellUnderGateAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = DateTime.UtcNow + timeout;
        for (var attempt = 0; attempt < 2 && DateTime.UtcNow < deadline; attempt++)
        {
            var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == 0)
            {
                return await WaitForShellAbsenceAsync(deadline, cancellationToken).ConfigureAwait(false);
            }

            if (!ExplorerControl.IsCurrentSessionWindow(taskbar))
            {
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(taskbar, out var owner);
            using var original = TryGetExitOwner(owner);
            if (original is null)
            {
                return await WaitForShellAbsenceAsync(deadline, cancellationToken).ConfigureAwait(false);
            }

            // Keep the handle, not merely the PID, so its exit is observed on the right process.
            _ = original.Handle;
            if (!string.Equals(NativeShellProcess.TryGetImagePath(checked((uint)original.Id)), ExplorerPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Log.Info($"Requesting orderly Explorer exit (pid {owner}).");
            if (!NativeMethods.PostMessageW(taskbar, ExitExplorerMessage, 0, 0))
            {
                return false;
            }

            RememberRetired(original);
            DateTime? absentSince = null;
            var replacement = false;
            var closeRequested = false;
            var uncleanExit = false;
            var exitSeen = false;
            while (DateTime.UtcNow < deadline)
            {
                var currentTaskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
                var shell = NativeMethods.GetShellWindow();
                var surfaces = currentTaskbar != 0 || shell != 0;
                if (currentTaskbar != 0 && !IsWindowOwnedByProcess(currentTaskbar, owner))
                {
                    replacement = true;
                    Log.Info("A replacement desktop appeared; requesting its orderly exit once.");
                    break;
                }

                absentSince = surfaces ? null : absentSince ?? DateTime.UtcNow;
                var absent = absentSince is { } since ? DateTime.UtcNow - since : TimeSpan.Zero;
                if (original.HasExited && !exitSeen)
                {
                    exitSeen = true;
                    uncleanExit = ExitedUncleanly(original, owner);
                }

                var action = ExplorerExitPolicy.Decide(surfaces, original.HasExited, absent, closeRequested,
                    uncleanExit);
                // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                switch (action)
                {
                    case ExplorerExitAction.Complete:
                        if (original.HasExited)
                        {
                            Log.Info("Explorer desktop exited and remained absent.");
                        }
                        else
                        {
                            Log.Warn($"Explorer desktop exited; retired pid {owner} still owns no shell and is "
                                     + "left to finish on its own.");
                            RememberRetired(original);
                        }

                        return true;
                    case ExplorerExitAction.RequestClose:
                        // Never terminated: Winlogon respawns a killed shell. Ask its remaining
                        // windows to close, as Task Manager's End task asks first.
                        closeRequested = true;
                        var asked = CloseWindowsOf(owner);
                        Log.Info(
                            $"Retired Explorer pid {owner} is still running; asked {asked} window(s) to close. "
                            + $"Third-party modules: {ThirdPartyModules(original)}.");
                        break;
                    case ExplorerExitAction.Wait:
                        break;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (!replacement)
            {
                break;
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        Log.Warn("Explorer desktop exit was not confirmed; desktop recovery is required.");
        return false;
    }

    private async Task WaitForRetiredShellUnderGateAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retired = _retired;
        if (retired is null)
        {
            return;
        }

        if (!retired.HasExited)
        {
            var asked = CloseWindowsOf(checked((uint)retired.Id));
            Log.Info($"Waiting for retired Explorer pid {retired.Id} before restoring the desktop; "
                     + $"asked {asked} window(s) to close.");
            if (!await NativeShellProcess.WaitForExitAsync(retired.Handle, timeout, cancellationToken)
                    .ConfigureAwait(false))
            {
                Log.Warn($"Retired Explorer pid {retired.Id} is still running; restoring the desktop beside it.");
            }
        }

        // Cancellation retains the handle for the next explicit return attempt.
        _retired = null;
        retired.Dispose();
    }

    private void RememberRetired(Process original)
    {
        try
        {
            // A second handle to the same process, checked by start time so a reused PID never counts.
            var copy = Process.GetProcessById(original.Id);
            if (copy.StartTime == original.StartTime)
            {
                _retired?.Dispose();
                _retired = copy;
                return;
            }

            copy.Dispose();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // It exited meanwhile, which is the outcome being waited for.
        }
    }

    private static Process? TryGetExitOwner(uint processId)
    {
        try
        {
            return Process.GetProcessById(checked((int)processId));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool ExitedUncleanly(Process original, uint owner)
    {
        try
        {
            var code = original.ExitCode;
            if (code == 0)
            {
                Log.Info($"Retired Explorer pid {owner} exited cleanly.");
                return false;
            }

            Log.Warn($"Retired Explorer pid {owner} exited with code 0x{code:X8}; Winlogon may respawn the shell, "
                     + "so the replacement is awaited before Game Mode continues.");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Log.Warn($"Retired Explorer pid {owner} exited; its exit code could not be read ({ex.Message}).");
            return true;
        }
    }

    private static string ThirdPartyModules(Process process)
    {
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var names = new List<string>();
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    var path = module.FileName;
                    if (!string.IsNullOrEmpty(path)
                        && !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(module.ModuleName);
                    }
                }
            }

            return names.Count == 0 ? "none" : string.Join(", ", names);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return $"unreadable ({ex.GetType().Name})";
        }
    }

    private static int CloseWindowsOf(uint processId)
    {
        var asked = 0;
        nint window = 0;
        while ((window = NativeMethods.FindWindowExW(0, window, null, null)) != 0)
        {
            NativeMethods.GetWindowThreadProcessId(window, out var windowOwner);
            if (windowOwner == processId && NativeMethods.PostMessageW(window, WmClose, 0, 0))
            {
                asked++;
            }
        }

        return asked;
    }

    private static async Task<bool> WaitForShellAbsenceAsync(DateTime deadline, CancellationToken cancellationToken)
    {
        DateTime? absentSince = null;
        while (DateTime.UtcNow < deadline)
        {
            var present = NativeMethods.FindWindowW("Shell_TrayWnd", null) != 0
                          || NativeMethods.GetShellWindow() != 0;
            absentSince = present ? null : absentSince ?? DateTime.UtcNow;
            if (absentSince is { } since && DateTime.UtcNow - since >= ExplorerExitPolicy.StableAbsence)
            {
                return true;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static bool IsWindowOwnedByProcess(nint window, uint processId)
    {
        if (window == 0 || !NativeMethods.IsWindow(window))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var currentOwner);
        return currentOwner == processId;
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
/// <param name="Prepared">Whether a verified anchor is retained before shell exit.</param>
/// <param name="Detail">Diagnostic readiness or refusal reason.</param>
internal readonly record struct ExplorerPreparationResult(
    bool Prepared,
    string Detail);

/// <summary>One sampled observation of Explorer's shell and taskbar surfaces.</summary>
/// <param name="Process">Inspected candidate shell owner, including native query failures.</param>
/// <param name="TaskbarOwnerProcessId">Observed taskbar owner, or zero when absent.</param>
/// <param name="ShellOwnerProcessId">Observed desktop window owner, or zero when absent.</param>
/// <param name="HasShellSurface">Whether any shell surface was observed, even if it was not usable.</param>
/// <param name="Initialized">Whether one owner has both shell surfaces and passed any requested responsiveness probe.</param>
/// <param name="Acceptance">Identity/readiness acceptance result.</param>
/// <param name="Outcome">Quality classification derived from the observations.</param>
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

    /// <summary>The captured fixed-purpose anchor started Explorer.</summary>
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
    /// <param name="outcome">Observed desktop quality; scheduler Normal is downgraded to Degraded.</param>
    /// <param name="route">Launch or adoption mechanism used.</param>
    /// <param name="processId">Observed shell owner PID, or zero when unavailable.</param>
    /// <param name="createdProcessId">PID returned by creation, or zero; it may differ from the shell owner.</param>
    /// <param name="detail">Diagnostic reason for the result.</param>
    /// <param name="launchDispatched">Whether launch occurred or may still complete after an uncertain result.</param>
    /// <param name="shellSurfacePresent">Whether any shell surface remains to be preserved.</param>
    /// <param name="elapsed">Elapsed restoration duration, including serialized admission.</param>
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

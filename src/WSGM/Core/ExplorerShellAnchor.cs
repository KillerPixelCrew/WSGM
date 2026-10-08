using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;
using WSGM.Shared;

namespace WSGM.Core;

/// <summary>
///     A fixed-purpose, medium-integrity launch owner created through the canonical Explorer before
///     orderly shell exit. The desktop host verifies joblessness or an explicitly degraded source-matched job.
/// </summary>
internal sealed class ExplorerShellAnchor : IAsyncDisposable
{
    private const string AnchorArgument = "--shell-anchor";

    /// <summary>Fixed recovery image name kept distinct from the installer's primary WSGM process target.</summary>
    internal const string ExecutableFileName = "WSGM.ShellAnchor.exe";

    /// <summary>Cross-version session event signaled after anchor recovery has settled.</summary>
    internal const string RecoverySettledEventName = SessionProtocolNames.AnchorRecoverySettled;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OwnerExitSettle = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly NamedPipeClientStream _pipe;
    private readonly NativeShellChildProcess _process;
    private readonly StreamReader _reader;
    private readonly string _secret;
    private readonly StreamWriter _writer;
    private int _disposeState;
    private int _ipcFaulted;

    private ExplorerShellAnchor(
        string secret,
        NativeShellChildProcess process,
        NamedPipeClientStream pipe,
        StreamReader reader,
        StreamWriter writer)
    {
        _secret = secret;
        _process = process;
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
    }

    /// <summary>Gets the anchor process identifier.</summary>
    internal uint ProcessId => _process.ProcessId;

    /// <summary>
    ///     Gets the separately named copy of WSGM used as the recovery process. Keeping the
    ///     image distinct lets installer force-stop the primary without killing its recovery owner.
    /// </summary>
    internal static string? ExecutablePath
    {
        get
        {
            var ownerExecutable = Environment.ProcessPath;
            var directory = ownerExecutable is null
                ? null
                : Path.GetDirectoryName(ownerExecutable);
            return string.IsNullOrWhiteSpace(directory)
                ? null
                : Path.Combine(directory, ExecutableFileName);
        }
    }

    /// <summary>Retires the owned anchor through authenticated stop, then bounded process cleanup.</summary>
    /// <returns>A task completing after handle/IPC cleanup; repeated calls do not join an earlier disposal.</returns>
    /// <remarks>The termination fallback targets only this WSGM-owned anchor, never Explorer.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        var graceful = false;
        var gateHeld = false;
        try
        {
            using CancellationTokenSource timeout = new(StopTimeout);
            if (Volatile.Read(ref _ipcFaulted) == 0)
            {
                await _commandGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                gateHeld = true;
                await _writer.WriteLineAsync($"stop {_secret}".AsMemory(), timeout.Token)
                    .ConfigureAwait(false);
                var response = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                graceful = string.Equals(response, "stopped", StringComparison.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            graceful = false;
        }
        finally
        {
            if (gateHeld)
            {
                _commandGate.Release();
            }
        }

        if (graceful)
        {
            graceful = await _process.WaitForExitAsync(StopTimeout).ConfigureAwait(false);
        }

        if (!graceful && !_process.HasExited)
        {
            _ = _process.TryTerminate();
            _ = await _process.WaitForExitAsync(StopTimeout).ConfigureAwait(false);
        }

        // Disposing a StreamWriter flushes it, and flushing to a pipe whose peer has exited throws
        // IOException — a dead peer is the ordinary state here, so these disposals must tolerate it
        // (an unguarded flush once failed every later game-mode transition in the session).
        DisposeQuietly(_writer, nameof(_writer));
        DisposeQuietly(_reader, nameof(_reader));
        DisposeQuietly(_pipe, nameof(_pipe));
        _process.Dispose();
        _commandGate.Dispose();
    }


    /// <summary>Gets whether this session currently has a WSGM-owned anchor recovery process.</summary>
    /// <param name="sessionId">Interactive session whose stop event is probed.</param>
    /// <returns>Whether its anchor event can be opened; false also covers denied or failed access.</returns>
    internal static bool HasRecoveryOwner(int sessionId)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(StopEventName(sessionId), out var stop))
            {
                return false;
            }

            stop.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Creates and authenticates an anchor under the retained shell parent.</summary>
    /// <param name="parent">Borrowed retained Explorer token/parent handles, required until startup completes.</param>
    /// <param name="ownerProcessId">WSGM process whose authenticated lifetime the anchor will watch.</param>
    /// <param name="sessionId">Interactive session for the anchor and recovery events.</param>
    /// <param name="cancellationToken">Cancels stale-owner retirement, connection, and authentication waits.</param>
    /// <returns>A caller-owned anchor to dispose, or a diagnostic result after bounded launch/connection failure.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled startup.</exception>
    internal static async Task<ExplorerShellAnchorStartResult> StartAsync(
        NativeShellLaunchParent parent,
        int ownerProcessId,
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        var executable = ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable)
            || !Path.IsPathFullyQualified(executable)
            || !File.Exists(executable))
        {
            return new ExplorerShellAnchorStartResult(null,
                $"The fixed shell-anchor executable is unavailable ({executable ?? "unknown"}).");
        }

        var staleError = await StopStaleAnchorAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (staleError is not null)
        {
            return new ExplorerShellAnchorStartResult(null, staleError);
        }

        var pipeName = $"wsgm-shell-anchor-{sessionId}-{Guid.NewGuid():N}";
        var secret = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var commandLine = string.Join(' ',
            SelfElevation.Quote(executable),
            AnchorArgument,
            SelfElevation.Quote(pipeName),
            SelfElevation.Quote(secret),
            ownerProcessId.ToString(CultureInfo.InvariantCulture),
            sessionId.ToString(CultureInfo.InvariantCulture));

        var repairJob = NativeShellProcess.Inspect(parent.ProcessId).JobMembership
            is NativeJobMembership.InJob;
        var created = repairJob
            ? NativeShellProcess.TryStartWithShellToken(parent, executable, commandLine,
                Path.GetDirectoryName(executable)!, out var process, out var launchError)
            : NativeShellProcess.TryStartWithParent(parent, executable, commandLine,
                Path.GetDirectoryName(executable)!, out process, out launchError);
        if (!created)
        {
            return new ExplorerShellAnchorStartResult(null,
                $"{(repairJob ? "CreateProcessWithTokenW" : "CreateProcessW")} failed with error {launchError}.");
        }

        Log.Info($"Shell anchor creation: route={(repairJob ? "verified-shell-token" : "shell-parent")}, "
                 + $"source={parent.ProcessId}, created={process!.ProcessId}.");

        NamedPipeClientStream pipe = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        StreamReader? reader = null;
        StreamWriter? writer = null;
        try
        {
            using var timeout = CreateTimeout(ConnectTimeout, cancellationToken);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true)
            {
                AutoFlush = true
            };
            var ready = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.Equals(ready, $"ready {secret}", StringComparison.Ordinal))
            {
                return new ExplorerShellAnchorStartResult(
                    new ExplorerShellAnchor(secret, process!, pipe, reader, writer),
                    string.Empty);
            }

            await StopFailedChildAsync(process!, pipe, reader, writer).ConfigureAwait(false);
            return new ExplorerShellAnchorStartResult(null,
                $"Anchor pid {process!.ProcessId} returned an invalid readiness handshake.");
        }
        catch (Exception ex)
        {
            await StopFailedChildAsync(process!, pipe, reader, writer).ConfigureAwait(false);
            switch (ex)
            {
                case OperationCanceledException when !cancellationToken.IsCancellationRequested:
                    return new ExplorerShellAnchorStartResult(null,
                        $"Anchor pid {process!.ProcessId} did not become ready before the timeout.");
                case IOException or TimeoutException:
                    return new ExplorerShellAnchorStartResult(null,
                        $"Anchor pid {process!.ProcessId} did not become ready: {ex.Message}");
            }

            throw;
        }
    }

    /// <summary>Asks the anchor to launch the fixed Windows Explorer path.</summary>
    /// <param name="timeout">Caller's remaining budget; capped by the three-second anchor command budget.</param>
    /// <param name="cancellationToken">Cancels waiting; a sent request can still launch Explorer.</param>
    /// <returns>Dispatch certainty and optional created PID. Unknown forbids a competing launch attempt.</returns>
    internal async Task<ExplorerAnchorLaunchResult> StartExplorerAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return new ExplorerAnchorLaunchResult(ExplorerAnchorLaunchDisposition.NotDispatched, 0, "anchor-disposed");
        }

        if (Volatile.Read(ref _ipcFaulted) != 0)
        {
            return new ExplorerAnchorLaunchResult(ExplorerAnchorLaunchDisposition.Unknown, 0, "anchor-ipc-faulted");
        }

        var sent = false;
        using var bounded = CreateTimeout(
            timeout < CommandTimeout ? timeout : CommandTimeout,
            cancellationToken);
        try
        {
            await _commandGate.WaitAsync(bounded.Token).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync($"start {_secret}".AsMemory(), bounded.Token)
                    .ConfigureAwait(false);
                sent = true;
                var response = await _reader.ReadLineAsync(bounded.Token).ConfigureAwait(false);
                if (response is not null
                    && response.StartsWith("started ", StringComparison.Ordinal)
                    && uint.TryParse(response.AsSpan("started ".Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var processId))
                {
                    return new ExplorerAnchorLaunchResult(ExplorerAnchorLaunchDisposition.Dispatched, processId,
                        "started");
                }

                if (response?.StartsWith("failed ", StringComparison.Ordinal) == true)
                {
                    return new ExplorerAnchorLaunchResult(ExplorerAnchorLaunchDisposition.NotDispatched, 0, response);
                }

                Interlocked.Exchange(ref _ipcFaulted, 1);
                // The command was written, so the dispatch state is genuinely unknown.
                return new ExplorerAnchorLaunchResult(
                    ExplorerAnchorLaunchDisposition.Unknown,
                    0,
                    response ?? "anchor-disconnected");
            }
            finally
            {
                _commandGate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _ipcFaulted, 1);
            return new ExplorerAnchorLaunchResult(
                sent ? ExplorerAnchorLaunchDisposition.Unknown : ExplorerAnchorLaunchDisposition.NotDispatched,
                0,
                "anchor-command-timeout");
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _ipcFaulted, 1);
            throw;
        }
        catch (ObjectDisposedException)
        {
            return new ExplorerAnchorLaunchResult(
                sent ? ExplorerAnchorLaunchDisposition.Unknown : ExplorerAnchorLaunchDisposition.NotDispatched,
                0,
                "anchor-disposed");
        }
        catch (IOException ex)
        {
            Interlocked.Exchange(ref _ipcFaulted, 1);
            return new ExplorerAnchorLaunchResult(
                sent ? ExplorerAnchorLaunchDisposition.Unknown : ExplorerAnchorLaunchDisposition.NotDispatched,
                0,
                ex.Message);
        }
    }

    /// <summary>
    ///     Parses and runs the hidden fixed-purpose anchor mode before WSGM initializes any
    ///     normal application service.
    /// </summary>
    /// <param name="args">Process arguments, with --shell-anchor first for this hidden mode.</param>
    /// <param name="exitCode">Receives the anchor process status, 64 for invalid anchor arguments, or zero when not selected.</param>
    /// <returns>True when anchor mode was selected and ran or rejected its arguments; false continues normal startup.</returns>
    /// <remarks>Blocks for the anchor lifetime and must run before ordinary application initialization.</remarks>
    internal static bool TryRunProcessMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], AnchorArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length != 5
            || string.IsNullOrWhiteSpace(args[1])
            || string.IsNullOrWhiteSpace(args[2])
            || !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerProcessId)
            || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId))
        {
            exitCode = 64;
            return true;
        }

        exitCode = RunProcessModeAsync(args[1], args[2], ownerProcessId, sessionId)
            .GetAwaiter().GetResult();
        return true;
    }

    /// <summary>Releases one pipe-backed resource, tolerating a peer that has already gone.</summary>
    /// <param name="resource">The resource to release.</param>
    /// <param name="name">Its field name, for the log when release was not clean.</param>
    /// <remarks>
    ///     Only the broken-pipe family is tolerated. Anything else still surfaces, because a disposal
    ///     failing for an unexpected reason is a real defect and swallowing it here would hide it in
    ///     the one place nobody looks.
    /// </remarks>
    private static void DisposeQuietly(IDisposable resource, string name)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Log.Change(
                "anchor.dispose." + name,
                $"Shell anchor {name} released after its peer had gone: {ex.Message}");
        }
    }

    private static async Task<int> RunProcessModeAsync(
        string pipeName,
        string secret,
        int ownerProcessId,
        int expectedSessionId)
    {
        if (WindowFinder.CurrentSessionId != expectedSessionId)
        {
            return 2;
        }

        using EventWaitHandle recoverySettled = new(
            false,
            EventResetMode.ManualReset,
            RecoverySettledEventName,
            out var createdRecoveryEvent);
        if (!createdRecoveryEvent)
        {
            return 3;
        }

        try
        {
            Process owner;
            try
            {
                owner = Process.GetProcessById(ownerProcessId);
                if (owner.SessionId != expectedSessionId)
                {
                    owner.Dispose();
                    return 2;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return 2;
            }

            using EventWaitHandle stop = new(
                false,
                EventResetMode.ManualReset,
                StopEventName(expectedSessionId),
                out var createdStopEvent);
            if (!createdStopEvent)
            {
                owner.Dispose();
                return 3;
            }

            using (owner)
            await using (NamedPipeServerStream pipe = new(
                             pipeName,
                             PipeDirection.InOut,
                             1,
                             PipeTransmissionMode.Byte,
                             PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                var ownerExit = owner.WaitForExitAsync();

                // One dispatch for every owner-wait observation point: Recover runs abnormal
                // owner-loss recovery, Exit and Recover both end the anchor, Wait continues.
                async Task<bool> ShouldExitForOwnerActionAsync(ExplorerAnchorDisconnectAction action)
                {
                    if (action is not ExplorerAnchorDisconnectAction.Recover)
                    {
                        return action is ExplorerAnchorDisconnectAction.Exit;
                    }

                    // ReSharper disable once AccessToDisposedClosure
                    await RecoverAfterOwnerLossAsync(expectedSessionId, stop).ConfigureAwait(false);
                    return true;
                }

                var connection = pipe.WaitForConnectionAsync();
                var ownerWaitFaulted = false;
                while (!connection.IsCompleted)
                {
                    _ = ownerWaitFaulted
                        ? await Task.WhenAny(connection, Task.Delay(200)).ConfigureAwait(false)
                        : await Task.WhenAny(connection, ownerExit, Task.Delay(200)).ConfigureAwait(false);
                    if (await ShouldExitForOwnerActionAsync(ObserveOwnerWait(owner, ownerExit, stop))
                            .ConfigureAwait(false))
                    {
                        return 0;
                    }

                    ownerWaitFaulted = OwnerWaitFaulted(ownerExit);
                }

                await connection.ConfigureAwait(false);

                if (await ShouldExitForOwnerActionAsync(ObserveOwnerWait(owner, ownerExit, stop))
                        .ConfigureAwait(false))
                {
                    return 0;
                }

                ownerWaitFaulted = OwnerWaitFaulted(ownerExit);

                using StreamReader reader = new(pipe, Encoding.UTF8, false, 1024, true);
                await using StreamWriter writer = new(pipe, new UTF8Encoding(false), 1024, true);
                writer.AutoFlush = true;
                await writer.WriteLineAsync($"ready {secret}").ConfigureAwait(false);

                while (true)
                {
                    var read = reader.ReadLineAsync();
                    while (!read.IsCompleted)
                    {
                        _ = ownerWaitFaulted
                            ? await Task.WhenAny(read, Task.Delay(200)).ConfigureAwait(false)
                            : await Task.WhenAny(read, ownerExit, Task.Delay(200)).ConfigureAwait(false);
                        if (await ShouldExitForOwnerActionAsync(ObserveOwnerWait(owner, ownerExit, stop))
                                .ConfigureAwait(false))
                        {
                            return 0;
                        }

                        ownerWaitFaulted = OwnerWaitFaulted(ownerExit);
                    }

                    var commandRead =
                        await CompleteCommandReadAsync(
                                read,
                                // ReSharper disable once AccessToDisposedClosure
                                () => WaitAfterPipeDisconnectAsync(owner, ownerExit, stop))
                            .ConfigureAwait(false);
                    if (commandRead.DisconnectAction is { } action)
                    {
                        _ = await ShouldExitForOwnerActionAsync(action).ConfigureAwait(false);
                        return 0;
                    }

                    var command = commandRead.Command!;
                    if (string.Equals(command, $"start {secret}", StringComparison.Ordinal))
                    {
                        var (processId, failure) = StartFixedExplorer(expectedSessionId);
                        await writer.WriteLineAsync(failure is null
                            ? $"started {processId.ToString(CultureInfo.InvariantCulture)}"
                            : $"failed {failure}").ConfigureAwait(false);
                        continue;
                    }

                    if (string.Equals(command, $"stop {secret}", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync("stopped").ConfigureAwait(false);
                        return 0;
                    }

                    await writer.WriteLineAsync("failed invalid-command").ConfigureAwait(false);
                }
            }
        }
        finally
        {
            recoverySettled.Set();
        }
    }

    /// <summary>
    ///     Completes one pipe read without treating an I/O fault as recovery settlement. EOF
    ///     and read faults both enter the same owner-loss/explicit-stop observation path.
    /// </summary>
    /// <param name="read">Already-started command read; borrowed task whose I/O failure means disconnection.</param>
    /// <param name="waitAfterDisconnect">Waits for verified owner loss or explicit stop after EOF/I/O failure.</param>
    /// <returns>A command, or the settled disconnect action; non-I/O read failures propagate.</returns>
    /// <exception cref="ArgumentNullException">A required task or callback is null.</exception>
    internal static async Task<ExplorerAnchorCommandReadResult> CompleteCommandReadAsync(
        Task<string?> read,
        Func<Task<ExplorerAnchorDisconnectAction>> waitAfterDisconnect)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(waitAfterDisconnect);
        try
        {
            var command = await read.ConfigureAwait(false);
            if (command is not null)
            {
                return new ExplorerAnchorCommandReadResult(command, null);
            }
        }
        catch (IOException)
        {
            // A broken command pipe says nothing about whether the authenticated owner still
            // exists. Retain the recovery role until owner loss or explicit stop is verified.
        }

        var action =
            await waitAfterDisconnect().ConfigureAwait(false);
        return new ExplorerAnchorCommandReadResult(null, action);
    }

    private static async Task<ExplorerAnchorDisconnectAction> WaitAfterPipeDisconnectAsync(
        Process owner,
        Task ownerExit,
        EventWaitHandle stop)
    {
        while (true)
        {
            var action = ObserveOwnerWait(owner, ownerExit, stop);
            if (action is not ExplorerAnchorDisconnectAction.Wait)
            {
                return action;
            }

            // A failed process wait is not proof that the authenticated owner exited. Keep the
            // recovery owner alive for an explicit stop instead of treating an observation failure
            // as a crash and potentially starting Explorer beside a still-running WSGM process.
            if (ownerExit.IsCompleted)
            {
                _ = ownerExit.Exception;
                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }

            _ = await Task.WhenAny(ownerExit, Task.Delay(200)).ConfigureAwait(false);
        }
    }

    private static ExplorerAnchorDisconnectAction ObserveOwnerWait(
        Process owner,
        Task ownerExit,
        EventWaitHandle stop)
    {
        var ownerExitVerifiedSeparately = OwnerWaitFaulted(ownerExit)
                                          && TryVerifyOwnerExited(owner);
        if (OwnerWaitFaulted(ownerExit))
        {
            // Observe the task's exception. Its failure is retained as an unobservable owner, not
            // converted into a recovery-settled process exit.
            _ = ownerExit.Exception;
        }

        return ExplorerShellPolicy.DecideAnchorOwnerWait(
            ownerExit.IsCompletedSuccessfully,
            ownerExitVerifiedSeparately,
            stop.WaitOne(0));
    }

    private static bool OwnerWaitFaulted(Task ownerExit)
    {
        return ownerExit is { IsCompleted: true, IsCompletedSuccessfully: false };
    }

    private static bool TryVerifyOwnerExited(Process owner)
    {
        try
        {
            return owner.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or Win32Exception
                                       or NotSupportedException)
        {
            // The exact owner still cannot be observed. Retaining the anchor for an explicit stop
            // is safer than publishing recovery completion or starting Explorer beside a live WSGM.
            return false;
        }
    }

    private static async Task RecoverAfterOwnerLossAsync(
        int expectedSessionId,
        EventWaitHandle stop)
    {
        if (stop.WaitOne(0))
        {
            return;
        }

        var settle = Stopwatch.StartNew();
        ExplorerDesktopObservation observation;
        do
        {
            observation = ExplorerDesktopHost.ObserveCurrentDesktop(expectedSessionId);
            if (observation.HasShellSurface)
            {
                break;
            }

            await Task.Delay(200).ConfigureAwait(false);
        } while (settle.Elapsed < OwnerExitSettle);

        var sessionActive = NativeShellProcess.IsSessionActive(expectedSessionId, out _);
        var action = ExplorerShellPolicy.DecideOwnerLoss(
            stop.WaitOne(0),
            sessionActive,
            observation.HasShellSurface);
        if (action is ExplorerAnchorOwnerLossAction.RestoreExplorer)
        {
            _ = StartFixedExplorer(expectedSessionId);
        }
    }

    private static (uint ProcessId, string? Failure) StartFixedExplorer(int expectedSessionId)
    {
        if (!NativeShellProcess.IsSessionActive(expectedSessionId, out var sessionError))
        {
            return (0, $"session-not-active-{sessionError}");
        }

        var existing = ExplorerDesktopHost.ObserveCurrentDesktop(expectedSessionId);
        if (existing.HasShellSurface)
        {
            return existing.Outcome is ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded
                ? (existing.Process.ProcessId, null)
                : (0, $"existing-shell-{existing.Acceptance.Rejection}");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(ExplorerControl.ExplorerPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            });
            return process is null
                ? (0, "process-not-created")
                : (checked((uint)process.Id), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return (0, ex.Message);
        }
    }

    private static async Task<string?> StopStaleAnchorAsync(
        int sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(StopEventName(sessionId), out var stale))
            {
                return null;
            }

            using (stale)
            {
                stale.Set();
            }

            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < StopTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!HasRecoveryOwner(sessionId))
                {
                    return null;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return "A stale WSGM shell anchor did not stop within the bounded cleanup window.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Stale WSGM shell anchor cleanup failed: {ex.Message}";
        }
    }

    private static async Task StopFailedChildAsync(
        NativeShellChildProcess process,
        NamedPipeClientStream pipe,
        StreamReader? reader,
        StreamWriter? writer)
    {
        try
        {
            if (!process.HasExited)
            {
                _ = process.TryTerminate();
                _ = await process.WaitForExitAsync(StopTimeout).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Failed Explorer anchor child could not be stopped: {ex.Message}");
        }

        try
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Failed Explorer anchor writer could not be disposed: {ex.Message}");
        }

        try
        {
            reader?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Failed Explorer anchor reader could not be disposed: {ex.Message}");
        }

        try
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Failed Explorer anchor pipe could not be disposed: {ex.Message}");
        }

        try
        {
            process.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Failed Explorer anchor process handle could not be disposed: {ex.Message}");
        }
    }

    private static CancellationTokenSource CreateTimeout(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout);
        return source;
    }

    private static string StopEventName(int sessionId)
    {
        return $@"Local\WSGM.ShellAnchor.Stop.{sessionId}";
    }
}

/// <summary>Result of starting and authenticating a shell anchor.</summary>
/// <param name="Anchor">New caller-owned authenticated anchor, or null when startup failed.</param>
/// <param name="Error">Failure detail; empty on successful startup.</param>
internal readonly record struct ExplorerShellAnchorStartResult(
    ExplorerShellAnchor? Anchor,
    string Error);

/// <summary>
///     Whether an Explorer launch request definitely did, definitely did not, or may have
///     crossed the anchor IPC boundary.
/// </summary>
internal enum ExplorerAnchorLaunchDisposition
{
    /// <summary>No launch was dispatched, so a different recovery route is safe.</summary>
    NotDispatched,

    /// <summary>The anchor confirmed that it dispatched the fixed Explorer launch.</summary>
    Dispatched,

    /// <summary>The request may have been received, so launching another shell would race it.</summary>
    Unknown
}

/// <summary>Bounded result of an authenticated anchor launch request.</summary>
/// <param name="Disposition">Whether the request crossed, may have crossed, or did not cross dispatch.</param>
/// <param name="ProcessId">Created process PID when returned, or zero; this is not proof of shell ownership.</param>
/// <param name="Detail">Protocol or failure diagnostic.</param>
internal readonly record struct ExplorerAnchorLaunchResult(
    ExplorerAnchorLaunchDisposition Disposition,
    uint ProcessId,
    string Detail);

/// <summary>One completed anchor command read or the verified action reached after disconnect.</summary>
/// <param name="Command">Received line, or null after disconnection.</param>
/// <param name="DisconnectAction">Verified post-disconnect action, or null when a command was received.</param>
internal readonly record struct ExplorerAnchorCommandReadResult(
    string? Command,
    ExplorerAnchorDisconnectAction? DisconnectAction);

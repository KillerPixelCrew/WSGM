using System;
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
    /// <summary>Hidden first argument that selects the shell-anchor process mode.</summary>
    internal const string AnchorArgument = "--shell-anchor";

    /// <summary>Fixed recovery image name kept distinct from the installer's primary WSGM process target.</summary>
    internal const string ExecutableFileName = "WSGM.ShellAnchor.exe";

    /// <summary>Cross-version session event signaled after anchor recovery has settled.</summary>
    internal const string RecoverySettledEventName = SessionProtocolNames.AnchorRecoverySettled;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
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
            WindowsCommandLine.Quote(executable),
            AnchorArgument,
            WindowsCommandLine.Quote(pipeName),
            WindowsCommandLine.Quote(secret),
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

    /// <summary>Session-local event that asks a running anchor to stop without recovery.</summary>
    /// <param name="sessionId">Interactive session that owns the anchor.</param>
    /// <returns>The local kernel event name.</returns>
    internal static string StopEventName(int sessionId)
    {
        return $@"Local\WSGM.ShellAnchor.Stop.{sessionId}";
    }
}

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

namespace WSGM.Core;
/// <summary>
///     The separate --shell-anchor process: it serves the authenticated launch pipe for its WSGM owner and restores
///     Explorer when that owner is lost. It runs before logging, configuration and Avalonia initialization.
/// </summary>
internal static class ExplorerShellAnchorProcess
{
    private static readonly TimeSpan OwnerExitSettle = TimeSpan.FromSeconds(2);

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
        if (args.Length == 0 || !string.Equals(args[0], ExplorerShellAnchor.AnchorArgument, StringComparison.OrdinalIgnoreCase))
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

    private static async Task<int> RunProcessModeAsync(
        string pipeName,
        string secret,
        int ownerProcessId,
        int expectedSessionId)
    {
        if (ProcessLookup.CurrentSessionId != expectedSessionId)
        {
            return 2;
        }

        using EventWaitHandle recoverySettled = new(
            false,
            EventResetMode.ManualReset,
            ExplorerShellAnchor.RecoverySettledEventName,
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
                ExplorerShellAnchor.StopEventName(expectedSessionId),
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
            observation = ExplorerDesktop.Observe(expectedSessionId);
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

        var existing = ExplorerDesktop.Observe(expectedSessionId);
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
}

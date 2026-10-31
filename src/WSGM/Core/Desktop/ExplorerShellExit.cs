using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>
///     Ends the session's Explorer shell for a takeover: the orderly <c>0x5B4</c> exit request to the verified
///     taskbar owner, termination of that exact held process when it ignores the request, and the retired owner
///     kept until the desktop returns.
/// </summary>
/// <remarks>
///     Only the held shell owner is ever terminated, with <see cref="ExplorerExitPolicy.ForcedExitCode" />; File
///     Explorer folder processes are never swept. <see cref="ExplorerDesktopHost" /> serializes every call under
///     its transition gate.
/// </remarks>
internal sealed class ExplorerShellExit : IDisposable
{
    private const uint ExitExplorerMessage = 0x05B4;

    private readonly int _sessionId;
    private Process? _retired;

    /// <summary>Creates the exit owner for one interactive session.</summary>
    /// <param name="sessionId">The session whose shell may be ended.</param>
    internal ExplorerShellExit(int sessionId)
    {
        _sessionId = sessionId;
    }

    /// <summary>Releases the retired owner's handle without ending it.</summary>
    public void Dispose()
    {
        _retired?.Dispose();
        _retired = null;
    }

    /// <summary>
    ///     Requests the orderly exit, enforces it on an owner that ignores it, and waits for sustained shell absence,
    ///     following a replacement shell Winlogon starts within the budget.
    /// </summary>
    /// <param name="timeout">The budget for the whole exit.</param>
    /// <param name="cancellationToken">Cancels waiting; a dispatched request is not undone.</param>
    /// <returns>True only after the exit policy accepted sustained absence.</returns>
    internal async Task<bool> ExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
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
            if (!string.Equals(NativeShellProcess.TryGetImagePath(checked((uint)original.Id)),
                    ExplorerControl.ExplorerPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Log.Info($"Requesting orderly Explorer exit (pid {owner}).");
            var requested = Stopwatch.StartNew();
            var forced = false;
            if (!NativeMethods.PostMessageW(taskbar, ExitExplorerMessage, 0, 0))
            {
                ForceStop(original);
                forced = true;
            }

            RememberRetired(original);
            DateTime? absentSince = null;
            var replacement = false;
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
                    Log.Info("A replacement desktop appeared; enforcing the requested exit on its new owner.");
                    break;
                }

                absentSince = surfaces ? null : absentSince ?? DateTime.UtcNow;
                var absent = absentSince is { } since ? DateTime.UtcNow - since : TimeSpan.Zero;
                var originalExited = original.HasExited;
                if (originalExited && !exitSeen)
                {
                    exitSeen = true;
                    uncleanExit = ExitedUncleanly(original, owner);
                    if (uncleanExit || forced)
                    {
                        // Winlogon's replacement follows process exit, which may be later than
                        // the old shell windows disappearing. Observe the full grace from here.
                        absentSince = surfaces ? null : DateTime.UtcNow;
                        absent = TimeSpan.Zero;
                    }
                }

                var action = ExplorerExitPolicy.Decide(surfaces, originalExited, absent,
                    uncleanExit, requested.Elapsed, forcedExit: forced);
                // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                switch (action)
                {
                    case ExplorerExitAction.Complete:
                        Log.Info("Explorer desktop exited and remained absent.");
                        return true;
                    case ExplorerExitAction.Wait:
                        break;
                    case ExplorerExitAction.ForceStop:
                        if (!forced)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            ForceStop(original);
                            forced = true;
                        }

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

    /// <summary>Terminates one held Explorer owner after checking it is this session's canonical explorer.exe.</summary>
    /// <param name="process">The held shell owner.</param>
    /// <exception cref="InvalidOperationException">The process is not this session's Explorer.</exception>
    internal void ForceStop(Process process)
    {
        // Retain the exact handle before identity inspection; never sweep Explorer folder processes.
        _ = process.Handle;
        if (process.HasExited)
        {
            return;
        }

        if (process.SessionId != _sessionId
            || !string.Equals(NativeShellProcess.TryGetImagePath(checked((uint)process.Id)),
                ExplorerControl.ExplorerPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The blocking Explorer owner could not be identified.");
        }

        Log.Warn($"Enforcing the requested shell transition: terminating Explorer pid {process.Id}.");
        NativeShellProcess.Terminate(process, ExplorerExitPolicy.ForcedExitCode);
    }

    /// <summary>
    ///     Finishes the shell owner retired by the last exit before a desktop restart, since it can retain Explorer's
    ///     singleton after its windows disappeared.
    /// </summary>
    /// <param name="deadline">The restoration's deadline.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>A task that completes once no retired owner remains.</returns>
    /// <exception cref="TimeoutException">The deadline passed before the retired owner exited.</exception>
    internal async Task ReleaseRetiredOwnerAsync(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var retired = _retired;
        if (retired is null)
        {
            return;
        }

        if (!retired.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log.Info($"Finishing retired Explorer pid {retired.Id} before the requested desktop restart.");
            ForceStop(retired);
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("The retired Explorer did not release the desktop before its deadline.");
            }

            await retired.WaitForExitAsync(cancellationToken).WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
        }

        _retired = null;
        retired.Dispose();
    }

    /// <summary>Opens a process by id with its handle held, so a later exit is observed on that exact process.</summary>
    /// <param name="processId">The process id.</param>
    /// <returns>The process, or null when it is gone or cannot be opened.</returns>
    internal static Process? TryGetExitOwner(uint processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(checked((int)processId));
            _ = process.Handle;
            return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            process?.Dispose();
            return null;
        }
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
}

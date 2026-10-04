using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Interop;

/// <summary>
///     Owns the Win32 process inspection and parent-process launch primitives used to
///     preserve normal Explorer shell process semantics across game-mode transitions.
/// </summary>
internal static partial class NativeShellProcess
{
    private const uint ProcessCreateProcess = 0x0080;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenDuplicate = 0x0002;
    private const int TokenIntegrityLevel = 25;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint WaitObject0 = 0;
    private const int ErrorInvalidParameter = 87;

    /// <summary>Inspects a process without retaining a handle.</summary>
    /// <param name="processId">Process identifier to inspect.</param>
    /// <returns>The values Windows exposed, including explicit unknown states.</returns>
    internal static NativeShellProcessInfo Inspect(uint processId)
    {
        var process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return NativeShellProcessInfo.Unavailable(processId, Marshal.GetLastPInvokeError());
        }

        try
        {
            var imagePath = QueryImagePath(process, out var imageError);
            var sessionKnown = ProcessIdToSessionId(processId, out var session);
            var sessionError = sessionKnown ? 0 : Marshal.GetLastPInvokeError();
            int? sessionId = sessionKnown ? checked((int)session) : null;
            var jobKnown = IsProcessInJob(process, 0, out var inJob);
            var jobError = jobKnown ? 0 : Marshal.GetLastPInvokeError();
            var jobMembership = jobKnown
                ? inJob ? NativeJobMembership.InJob : NativeJobMembership.NotInJob
                : NativeJobMembership.Unknown;
            var integrity = QueryIntegrity(process, out var integrityError);
            return new NativeShellProcessInfo(
                processId,
                imagePath,
                sessionId,
                integrity,
                jobMembership,
                new NativeShellProcessErrors(0, imageError, sessionError, integrityError, jobError));
        }
        finally
        {
            Win32Common.CloseHandle(process);
        }
    }

    /// <summary>
    ///     Reads a process's full image path, opening it with the limited query right.
    ///     Null when the process cannot be opened or queried — ordinary for an elevated or
    ///     protected process. The one shared image-path primitive for every caller that only
    ///     needs the path, not the full inspection.
    /// </summary>
    internal static string? TryGetImagePath(uint processId)
    {
        var process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            return QueryImagePath(process, out _);
        }
        finally
        {
            Win32Common.CloseHandle(process);
        }
    }

    /// <summary>
    ///     Reads a process's terminal-services session without opening a .NET Process, which would
    ///     snapshot every process on the machine. Null when Windows does not answer.
    /// </summary>
    internal static int? TryGetSessionId(uint processId)
    {
        return ProcessIdToSessionId(processId, out var session) ? checked((int)session) : null;
    }

    /// <summary>
    ///     Opens the process and token rights required to use a verified shell as a
    ///     designated process-creation parent.
    /// </summary>
    /// <param name="processId">Verified taskbar-owner process identifier.</param>
    /// <param name="parent">Owned launch-parent handle on success.</param>
    /// <param name="error">Win32 error on failure.</param>
    /// <returns>Whether both process and token handles were opened.</returns>
    internal static bool TryOpenLaunchParent(
        uint processId,
        out NativeShellLaunchParent? parent,
        out int error)
    {
        parent = null;
        var process = NativeMethods.OpenProcess(
            ProcessCreateProcess | ProcessQueryLimitedInformation,
            false,
            processId);
        if (process == 0)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery | TokenDuplicate, out var token))
        {
            error = Marshal.GetLastPInvokeError();
            Win32Common.CloseHandle(process);
            return false;
        }

        parent = new NativeShellLaunchParent(processId, process, token);
        error = 0;
        return true;
    }

    /// <summary>
    ///     Starts a fixed executable with the designated process as its creation parent and
    ///     with the designated parent's user environment.
    /// </summary>
    /// <param name="parent">The retained canonical shell parent.</param>
    /// <param name="applicationPath">Absolute executable path.</param>
    /// <param name="commandLine">Mutable Windows command line including argv[0].</param>
    /// <param name="workingDirectory">Absolute working directory.</param>
    /// <param name="process">Owned handle for the exact created process on success.</param>
    /// <param name="error">Win32 error on failure.</param>
    /// <returns>Whether process creation succeeded.</returns>
    internal static bool TryStartWithParent(
        NativeShellLaunchParent parent,
        string applicationPath,
        string commandLine,
        string workingDirectory,
        out NativeShellChildProcess? process,
        out int error)
    {
        ArgumentNullException.ThrowIfNull(parent);
        process = null;
        if (!ParentProcessStart.TryCreate(parent.ProcessHandle, parent.TokenHandle, applicationPath, commandLine,
                workingDirectory, out var processId, out var processHandle, out error))
        {
            return false;
        }

        process = new NativeShellChildProcess(processId, processHandle);
        return true;
    }

    private static string? QueryImagePath(nint process, out int error)
    {
        var buffer = ArrayPool<char>.Shared.Rent(32768);
        try
        {
            var length = checked((uint)buffer.Length);
            if (NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref length))
            {
                error = 0;
                return new string(buffer, 0, checked((int)length));
            }

            error = Marshal.GetLastPInvokeError();
            return null;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static unsafe NativeIntegrityLevel QueryIntegrity(nint process, out int error)
    {
        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token))
        {
            error = Marshal.GetLastPInvokeError();
            return NativeIntegrityLevel.Unknown;
        }

        try
        {
            _ = NativeMethods.GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var required);
            if (required < (uint)sizeof(nint))
            {
                error = Marshal.GetLastPInvokeError();
                return NativeIntegrityLevel.Unknown;
            }

            var buffer = NativeMemory.Alloc(required);
            if (buffer == null)
            {
                error = 8; // ERROR_NOT_ENOUGH_MEMORY
                return NativeIntegrityLevel.Unknown;
            }

            try
            {
                if (!NativeMethods.GetTokenInformation(token, TokenIntegrityLevel, (nint)buffer, required, out _))
                {
                    error = Marshal.GetLastPInvokeError();
                    return NativeIntegrityLevel.Unknown;
                }

                var sid = *(nint*)buffer;
                if (sid == 0)
                {
                    error = 13; // ERROR_INVALID_DATA
                    return NativeIntegrityLevel.Unknown;
                }

                var subAuthorityCount = *((byte*)sid + 1);
                if (subAuthorityCount == 0)
                {
                    error = 13; // ERROR_INVALID_DATA
                    return NativeIntegrityLevel.Unknown;
                }

                var rid = *(uint*)((byte*)sid + 8 + (subAuthorityCount - 1) * sizeof(uint));
                error = 0;
                return rid switch
                {
                    < 0x1000 => NativeIntegrityLevel.Untrusted,
                    < 0x2000 => NativeIntegrityLevel.Low,
                    < 0x3000 => NativeIntegrityLevel.Medium,
                    < 0x4000 => NativeIntegrityLevel.High,
                    _ => NativeIntegrityLevel.System
                };
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        finally
        {
            Win32Common.CloseHandle(token);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        nint process,
        nint job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    /// <summary>Waits for one owned process handle without blocking the caller.</summary>
    internal static async Task<bool> WaitForExitAsync(
        nint processHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        return await Task.Run(() =>
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = timeout - elapsed.Elapsed;
                var milliseconds = remaining <= TimeSpan.Zero ? 0 : (uint)Math.Min(remaining.TotalMilliseconds, 100);
                var result = Win32Common.WaitForSingleObject(processHandle, milliseconds);
                if (result == WaitObject0)
                {
                    return true;
                }

                if (result != 0x102 || elapsed.Elapsed >= timeout)
                {
                    return false;
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a process is known to have exited.</summary>
    /// <param name="processId">The process to check.</param>
    /// <param name="imagePath">Its image path when it was seen, which tells a reused identifier apart.</param>
    /// <returns>
    ///     True only when Windows says so: the identifier is gone, the process signaled, or the identifier
    ///     now belongs to another image. A process that cannot be opened for another reason counts as
    ///     running.
    /// </returns>
    internal static bool HasExited(uint processId, string? imagePath)
    {
        if (processId == 0)
        {
            return false;
        }

        var process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation | NativeMethods.Synchronize, false,
            processId);
        if (process == 0)
        {
            return Marshal.GetLastPInvokeError() == ErrorInvalidParameter;
        }

        try
        {
            if (HasExited(process))
            {
                return true;
            }

            return imagePath is { Length: > 0 }
                   && QueryImagePath(process, out _) is { Length: > 0 } current
                   && !string.Equals(current, imagePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Win32Common.CloseHandle(process);
        }
    }

    /// <summary>Gets whether an owned process handle has signaled.</summary>
    internal static bool HasExited(nint processHandle)
    {
        return Win32Common.WaitForSingleObject(processHandle, 0) == WaitObject0;
    }

    /// <summary>
    ///     Queries whether a terminal-services session is currently active. Recovery callers
    ///     use this after owner loss so logoff never causes a replacement desktop to be launched.
    /// </summary>
    internal static bool IsSessionActive(int sessionId, out int error)
    {
        if (!Win32Common.WTSQuerySessionInformationW(
                0,
                checked((uint)sessionId),
                8, // WTSConnectState
                out var buffer,
                out var bytes))
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        try
        {
            if (bytes < sizeof(int))
            {
                error = 13; // ERROR_INVALID_DATA
                return false;
            }

            error = 0;
            return Marshal.ReadInt32(buffer) == 0; // WTSActive
        }
        finally
        {
            Win32Common.WTSFreeMemory(buffer);
        }
    }
}

/// <summary>Process attributes relevant to accepting a normal desktop shell or launch owner.</summary>
internal readonly record struct NativeShellProcessInfo(
    uint ProcessId,
    string? ImagePath,
    int? SessionId,
    NativeIntegrityLevel Integrity,
    NativeJobMembership JobMembership,
    NativeShellProcessErrors Errors)
{
    /// <summary>Creates an unavailable inspection result.</summary>
    internal static NativeShellProcessInfo Unavailable(uint processId, int error)
    {
        return new NativeShellProcessInfo(
            processId,
            null,
            null,
            NativeIntegrityLevel.Unknown,
            NativeJobMembership.Unknown,
            new NativeShellProcessErrors(error, 0, 0, 0, 0));
    }
}

/// <summary>Exact Win32 failures produced by each independent process-inspection query.</summary>
internal readonly record struct NativeShellProcessErrors(
    int Open,
    int Image,
    int Session,
    int Integrity,
    int Job);

/// <summary>Windows mandatory integrity classification.</summary>
internal enum NativeIntegrityLevel
{
    /// <summary>The token could not be inspected.</summary>
    Unknown,

    /// <summary>Untrusted integrity.</summary>
    Untrusted,

    /// <summary>Low integrity.</summary>
    Low,

    /// <summary>Medium or medium-plus integrity.</summary>
    Medium,

    /// <summary>High integrity.</summary>
    High,

    /// <summary>System or protected integrity.</summary>
    System
}

/// <summary>Tri-state process job membership; a failed query never becomes jobless.</summary>
internal enum NativeJobMembership
{
    /// <summary>Windows did not answer the query.</summary>
    Unknown,

    /// <summary>The process is not associated with a job.</summary>
    NotInJob,

    /// <summary>The process is associated with a job.</summary>
    InJob
}

/// <summary>Retained native handles for a verified process-creation parent.</summary>
internal sealed class NativeShellLaunchParent : IDisposable
{
    private nint _processHandle;
    private nint _tokenHandle;

    /// <summary>Creates the owned handle pair.</summary>
    internal NativeShellLaunchParent(uint processId, nint processHandle, nint tokenHandle)
    {
        ProcessId = processId;
        _processHandle = processHandle;
        _tokenHandle = tokenHandle;
    }

    /// <summary>Gets the designated parent's process identifier.</summary>
    internal uint ProcessId { get; }

    /// <summary>Gets the retained process handle.</summary>
    internal nint ProcessHandle => _processHandle;

    /// <summary>Gets the retained token handle.</summary>
    internal nint TokenHandle => _tokenHandle;

    /// <inheritdoc />
    public void Dispose()
    {
        var token = Interlocked.Exchange(ref _tokenHandle, 0);
        if (token != 0)
        {
            Win32Common.CloseHandle(token);
        }

        var process = Interlocked.Exchange(ref _processHandle, 0);
        if (process != 0)
        {
            Win32Common.CloseHandle(process);
        }
    }
}

/// <summary>
///     Owns the exact process handle returned by CreateProcessW so a failed anchor startup
///     can stop only the child it created and cannot act on a recycled process identifier.
/// </summary>
internal sealed partial class NativeShellChildProcess : IDisposable
{
    private nint _processHandle;

    /// <summary>Creates an owned child-process handle.</summary>
    internal NativeShellChildProcess(uint processId, nint processHandle)
    {
        ProcessId = processId;
        _processHandle = processHandle;
    }

    /// <summary>Gets the created process identifier for diagnostics only.</summary>
    internal uint ProcessId { get; }

    /// <summary>Gets whether the exact created process has exited.</summary>
    internal bool HasExited => _processHandle == 0 || NativeShellProcess.HasExited(_processHandle);

    /// <inheritdoc />
    public void Dispose()
    {
        var process = Interlocked.Exchange(ref _processHandle, 0);
        if (process != 0)
        {
            Win32Common.CloseHandle(process);
        }
    }

    /// <summary>Waits boundedly for the exact created process to exit.</summary>
    internal Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var handle = _processHandle;
        return handle == 0
            ? Task.FromResult(true)
            : NativeShellProcess.WaitForExitAsync(handle, timeout, cancellationToken);
    }

    /// <summary>
    ///     Terminates only the exact owned child. Used solely when anchor setup or its
    ///     authenticated stop handshake failed before the child could be released normally.
    /// </summary>
    internal bool TryTerminate()
    {
        var handle = _processHandle;
        return handle == 0 || HasExited || TerminateProcess(handle, 1);
    }

    // Private to the owned anchor child: Explorer is never terminated.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);
}

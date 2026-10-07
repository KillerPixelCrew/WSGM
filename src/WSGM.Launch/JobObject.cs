using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Launch;

/// <summary>
///     A Windows job object holding the launched target and everything it spawns, so
///     the wrapper's lifetime tracks the whole process tree instead of the process it
///     started directly.
/// </summary>
/// <remarks>
///     Games fronted by a launcher (emulators, store front-ends, some anti-cheat
///     bootstrappers) exit their root process seconds in and leave the real game
///     running. Waiting on that root alone ends the wrapper early: Steam marks the game
///     as stopped and, with <c>--input-lease</c>, the Steam Input block is released
///     mid-session. Both the native lease wrapper and the medium child now create the target
///     suspended, assign its containment job, and resume only after assignment succeeds. The medium
///     child carries the original argument vector, Steam environment and RunAsInvoker layer.
/// </remarks>
internal sealed partial class JobObject : IDisposable
{
    private const uint JobObjectBasicAccountingInformation = 1;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private nint _handle;

    private JobObject(nint handle)
    {
        _handle = handle;
    }

    /// <summary>
    ///     Closes the job handle. The job is deliberately created without
    ///     kill-on-close, so anything still running outlives the wrapper instead of
    ///     dying with it.
    /// </summary>
    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        Win32Common.CloseHandle(_handle);
        _handle = 0;
    }

    internal static JobObject Create()
    {
        var handle = CreateJobObjectW(0, null);
        return handle != 0
            ? new JobObject(handle)
            : throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the target containment job.");
    }

    internal void Assign(nint process)
    {
        if (!AssignProcessToJobObject(_handle, process))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not contain the suspended target.");
        }
    }

    /// <summary>Completes once no process in the job is left running.</summary>
    /// <param name="cancellationToken">Stops waiting (the caller is shutting down).</param>
    internal async Task WaitUntilEmptyAsync(CancellationToken cancellationToken)
    {
        // Polled, like the native wrapper: a job object has no "became empty"
        // waitable state without an IO completion port, and the resolution here
        // only decides how quickly the wrapper notices a finished game.
        while (!cancellationToken.IsCancellationRequested && ActiveProcesses() > 0)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ends every process still in the job.</summary>
    internal bool TerminateTree()
    {
        if (_handle == 0)
        {
            return false;
        }

        if (TerminateJobObject(_handle, 1))
        {
            return true;
        }

        LaunchLog.Error($"Could not terminate the job object (error {Marshal.GetLastPInvokeError()}).");
        return false;
    }

    private uint ActiveProcesses()
    {
        if (_handle == 0)
        {
            return 0;
        }

        var info = default(JobObjectBasicAccountingInfo);
        if (!QueryInformationJobObject(
                _handle,
                JobObjectBasicAccountingInformation,
                ref info,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInfo>(),
                0))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not query the wrapper's target job object.");
        }

        return info.ActiveProcesses;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        nint job, uint infoClass, ref JobObjectBasicAccountingInfo info, uint length, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);


    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInfo
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }
}

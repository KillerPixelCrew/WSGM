using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WSGM.DeviceLab.Application;

/// <summary>Windows job which kills every disposable worker descendant when supervision ends.</summary>
internal sealed partial class WorkerJobObject : IDisposable
{
    private const uint BasicAccountingInformation = 1;
    private const uint ExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x00002000;
    private SafeFileHandle? _handle;

    private WorkerJobObject(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        handle?.Dispose();
    }

    internal static unsafe WorkerJobObject Create()
    {
        var handle = CreateJobObjectW(0, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Could not create the disposable worker job.");
        }

        JobObjectExtendedLimitInformation information = new();
        information.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;
        if (SetInformationJobObject(
                handle,
                ExtendedLimitInformation,
                &information,
                (uint)sizeof(JobObjectExtendedLimitInformation)))
        {
            return new WorkerJobObject(handle);
        }

        var configurationError = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(configurationError, "Could not configure disposable worker containment.");
    }

    internal void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var handle = _handle
                     ?? throw new ObjectDisposedException(nameof(WorkerJobObject));
        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not assign the disposable worker to its containment job.");
        }
    }

    /// <summary>Terminates every process in the job and waits for Windows to confirm it is empty.</summary>
    /// <param name="timeout">Bounded teardown interval.</param>
    /// <returns>True only when no process remains in the job.</returns>
    internal async Task<bool> TerminateAndWaitAsync(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var handle = _handle
                     ?? throw new ObjectDisposedException(nameof(WorkerJobObject));
        if (!TryGetActiveProcessCount(handle, out var activeProcesses))
        {
            return false;
        }

        if (activeProcesses == 0)
        {
            return true;
        }

        if (!TerminateJobObject(handle, 1))
        {
            return false;
        }

        var elapsed = Stopwatch.StartNew();
        do
        {
            if (!TryGetActiveProcessCount(handle, out activeProcesses))
            {
                return false;
            }

            if (activeProcesses == 0)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
        } while (elapsed.Elapsed < timeout);

        return TryGetActiveProcessCount(handle, out activeProcesses) && activeProcesses == 0;
    }

    private static unsafe bool TryGetActiveProcessCount(
        SafeFileHandle handle,
        out uint activeProcesses)
    {
        JobObjectBasicAccountingInformation information = new();
        if (!QueryInformationJobObject(
                handle,
                BasicAccountingInformation,
                &information,
                (uint)sizeof(JobObjectBasicAccountingInformation),
                null))
        {
            activeProcesses = 0;
            return false;
        }

        activeProcesses = information.ActiveProcesses;
        return true;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(
        SafeFileHandle job,
        uint informationClass,
        void* information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryInformationJobObject(
        SafeFileHandle job,
        uint informationClass,
        void* information,
        uint informationLength,
        uint* returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        internal long TotalUserTime;
        internal long TotalKernelTime;
        internal long ThisPeriodTotalUserTime;
        internal long ThisPeriodTotalKernelTime;
        internal uint TotalPageFaultCount;
        internal uint TotalProcesses;
        internal uint ActiveProcesses;
        internal uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }
}

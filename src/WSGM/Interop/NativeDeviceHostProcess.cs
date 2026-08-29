using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Native process launch and job containment for DeviceHost.</summary>
internal static partial class NativeDeviceHostProcess
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    /// <summary>Creates and configures a single-process, kill-on-close DeviceHost job.</summary>
    /// <param name="processHandle">Fresh DeviceHost process handle.</param>
    /// <param name="jobHandle">Owned job handle on success.</param>
    /// <returns>Zero on success, otherwise the Win32 error code.</returns>
    internal static unsafe int CreateKillOnCloseJob(
        nint processHandle,
        out nint jobHandle)
    {
        jobHandle = CreateJobObjectW(0, null);
        if (jobHandle == 0)
        {
            return Marshal.GetLastPInvokeError();
        }

        JobObjectExtendedLimitInformation limits = new();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitActiveProcess
            | JobObjectLimitDieOnUnhandledException
            | JobObjectLimitKillOnJobClose;
        limits.BasicLimitInformation.ActiveProcessLimit = 1;
        if (!SetInformationJobObject(
            jobHandle,
            JobObjectExtendedLimitInformationClass,
            &limits,
            (uint)sizeof(JobObjectExtendedLimitInformation)))
        {
            int error = Marshal.GetLastPInvokeError();
            CloseHandle(jobHandle);
            jobHandle = 0;
            return error;
        }

        if (!AssignProcessToJobObject(jobHandle, processHandle))
        {
            int error = Marshal.GetLastPInvokeError();
            CloseHandle(jobHandle);
            jobHandle = 0;
            return error;
        }

        return 0;
    }

    /// <summary>Terminates every process in a DeviceHost job.</summary>
    internal static bool TerminateJob(nint jobHandle, uint exitCode) =>
        TerminateJobObject(jobHandle, exitCode);

    /// <summary>Closes an owned native job handle.</summary>
    internal static void CloseJob(nint jobHandle)
    {
        if (jobHandle != 0)
        {
            CloseHandle(jobHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(
        nint job,
        uint informationClass,
        void* information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

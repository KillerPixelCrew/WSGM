using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Native process launch and job containment for DeviceHost.</summary>
internal static partial class NativeDeviceHostProcess
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint MaximumAllowed = 0x02000000;
    private const uint SecurityImpersonation = 2;
    private const uint TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectCpuRateControlInformationClass = 15;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectCpuRateControlEnable = 0x1;
    private const uint JobObjectCpuRateControlHardCap = 0x4;

    /// <summary>Starts DeviceHost with the interactive shell's medium-integrity token.</summary>
    /// <param name="shellProcessHandle">Handle of Explorer in the current interactive session.</param>
    /// <param name="applicationPath">Canonical DeviceHost executable path.</param>
    /// <param name="commandLine">Quoted command line beginning with the executable.</param>
    /// <param name="workingDirectory">Immutable executable directory.</param>
    /// <param name="environmentBlock">Sorted double-null-terminated Unicode environment.</param>
    /// <param name="processId">Created process identifier.</param>
    /// <returns>Zero on success, otherwise the Win32 error code.</returns>
    internal static unsafe int StartAsShellUser(
        nint shellProcessHandle,
        string applicationPath,
        string commandLine,
        string workingDirectory,
        string environmentBlock,
        out uint processId)
    {
        processId = 0;
        if (!OpenProcessToken(
            shellProcessHandle,
            TokenAssignPrimary | TokenDuplicate | TokenQuery,
            out nint shellToken))
        {
            return Marshal.GetLastPInvokeError();
        }

        try
        {
            if (!DuplicateTokenEx(
                shellToken,
                MaximumAllowed,
                0,
                SecurityImpersonation,
                TokenPrimary,
                out nint primaryToken))
            {
                return Marshal.GetLastPInvokeError();
            }

            try
            {
                StartupInfo startup = new() { StructSize = (uint)sizeof(StartupInfo) };
                ProcessInformation process = default;
                fixed (char* application = applicationPath)
                fixed (char* command = commandLine)
                fixed (char* directory = workingDirectory)
                fixed (char* environment = environmentBlock)
                {
                    if (!CreateProcessWithTokenW(
                        primaryToken,
                        0,
                        application,
                        command,
                        CreateUnicodeEnvironment | CreateNoWindow,
                        environment,
                        directory,
                        ref startup,
                        out process))
                    {
                        return Marshal.GetLastPInvokeError();
                    }
                }

                processId = process.ProcessId;
                CloseHandle(process.Thread);
                CloseHandle(process.Process);
                return 0;
            }
            finally
            {
                CloseHandle(primaryToken);
            }
        }
        finally
        {
            CloseHandle(shellToken);
        }
    }

    /// <summary>Creates and configures a single-process, kill-on-close DeviceHost job.</summary>
    /// <param name="processHandle">Fresh DeviceHost process handle.</param>
    /// <param name="memoryLimitBytes">Per-process committed-memory ceiling.</param>
    /// <param name="cpuRateHundredths">Hard CPU cap in hundredths of one CPU.</param>
    /// <param name="jobHandle">Owned job handle on success.</param>
    /// <returns>Zero on success, otherwise the Win32 error code.</returns>
    internal static unsafe int CreateContainedJob(
        nint processHandle,
        nuint memoryLimitBytes,
        uint cpuRateHundredths,
        out nint jobHandle)
    {
        jobHandle = CreateJobObjectW(0, null);
        if (jobHandle == 0)
        {
            return Marshal.GetLastPInvokeError();
        }

        JobObjectExtendedLimitInformation limits = new();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitActiveProcess
            | JobObjectLimitProcessMemory
            | JobObjectLimitDieOnUnhandledException
            | JobObjectLimitKillOnJobClose;
        limits.BasicLimitInformation.ActiveProcessLimit = 1;
        limits.ProcessMemoryLimit = memoryLimitBytes;
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

        JobObjectCpuRateControlInformation cpu = new()
        {
            ControlFlags = JobObjectCpuRateControlEnable | JobObjectCpuRateControlHardCap,
            CpuRate = cpuRateHundredths,
        };
        if (!SetInformationJobObject(
            jobHandle,
            JobObjectCpuRateControlInformationClass,
            &cpu,
            (uint)sizeof(JobObjectCpuRateControlInformation))
            || !AssignProcessToJobObject(jobHandle, processHandle))
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
    private struct StartupInfo
    {
        public uint StructSize;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Length;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectCpuRateControlInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(
        nint existingToken,
        uint desiredAccess,
        nint tokenAttributes,
        uint impersonationLevel,
        uint tokenType,
        out nint newToken);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessWithTokenW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessWithTokenW(
        nint token,
        uint logonFlags,
        char* applicationName,
        char* commandLine,
        uint creationFlags,
        char* environment,
        char* currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

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

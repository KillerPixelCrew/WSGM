// Shared between WSGM and WSGM.PackagedLaunch (linked as a source file): starting a program with
// another process as its parent and with that parent's own user environment, declared once.

using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Starts a program as the child of another process, in that process's user environment.</summary>
/// <remarks>
///     <para>
///         WSGM starts Explorer's shell work under the retained shell, and the packaged launcher starts
///         another launcher under Explorer or WSGM so it sits outside Steam's process tree. Both need
///         the same thing: <c>PROC_THREAD_ATTRIBUTE_PARENT_PROCESS</c>, and an environment built from
///         the parent's token rather than inherited from the caller. The second half matters as much
///         as the first: a program started outside Steam's tree must not carry Steam's launch
///         variables either, such as its controller exclusion or the calling game's app id.
///     </para>
///     <para>
///         The caller owns both handles it passes and the process handle it gets back.
///     </para>
/// </remarks>
internal static partial class ParentProcessStart
{
    private const uint CreateUnicodeEnvironment = 0x0000_0400;
    private const uint ExtendedStartupInfoPresent = 0x0008_0000;
    private const nuint ProcThreadAttributeParentProcess = 0x0002_0000;
    private const int ErrorNotEnoughMemory = 8;

    /// <summary>Creates the process.</summary>
    /// <param name="parentProcess">
    ///     The designated parent, opened with <c>PROCESS_CREATE_PROCESS</c>.
    /// </param>
    /// <param name="parentToken">
    ///     The parent's token, opened with <c>TOKEN_QUERY</c> and <c>TOKEN_DUPLICATE</c>; the new
    ///     process's environment is built from it.
    /// </param>
    /// <param name="applicationPath">The absolute executable path.</param>
    /// <param name="commandLine">The Windows command line, including argv[0].</param>
    /// <param name="workingDirectory">The absolute working directory.</param>
    /// <param name="processId">The created process's id, on success.</param>
    /// <param name="processHandle">An owned handle to the created process, on success.</param>
    /// <param name="error">The Win32 error, on failure.</param>
    /// <returns>Whether the process was created.</returns>
    internal static unsafe bool TryCreate(
        nint parentProcess,
        nint parentToken,
        string applicationPath,
        string commandLine,
        string workingDirectory,
        out uint processId,
        out nint processHandle,
        out int error)
    {
        ArgumentNullException.ThrowIfNull(applicationPath);
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        processId = 0;
        processHandle = 0;
        error = 0;
        nint environment = 0;
        nint attributeList = 0;
        var attributeListInitialized = false;

        try
        {
            if (!Win32Common.CreateEnvironmentBlock(out environment, parentToken, false))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            if (attributeListSize == 0)
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            attributeList = (nint)NativeMemory.Alloc(attributeListSize);
            if (attributeList == 0)
            {
                error = ErrorNotEnoughMemory;
                return false;
            }

            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            attributeListInitialized = true;
            var parent = parentProcess;
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeParentProcess,
                    (nint)(&parent),
                    (nuint)sizeof(nint),
                    0,
                    0))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            StartupInfoEx startup = new()
            {
                StartupInfo = new StartupInfo { Size = checked((uint)sizeof(StartupInfoEx)) },
                AttributeList = attributeList
            };

            char[] mutableCommandLine = [.. commandLine, '\0'];
            fixed (char* application = applicationPath)
            fixed (char* command = mutableCommandLine)
            fixed (char* directory = workingDirectory)
            {
                if (!CreateProcessW(
                        application,
                        command,
                        0,
                        0,
                        false,
                        CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                        environment,
                        directory,
                        in startup,
                        out var created))
                {
                    error = Marshal.GetLastPInvokeError();
                    return false;
                }

                Win32Common.CloseHandle(created.Thread);
                processId = created.ProcessId;
                processHandle = created.Process;
                return true;
            }
        }
        finally
        {
            if (attributeListInitialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }

            if (attributeList != 0)
            {
                NativeMemory.Free((void*)attributeList);
            }

            if (environment != 0)
            {
                Win32Common.DestroyEnvironmentBlock(environment);
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        nint attributeList,
        int attributeCount,
        uint flags,
        ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(
        char* applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        char* currentDirectory,
        in StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Size;
        internal nint Reserved;
        internal nint Desktop;
        internal nint Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2;
        internal nint Reserved2Pointer;
        internal nint StandardInput;
        internal nint StandardOutput;
        internal nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process;
        internal nint Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }
}

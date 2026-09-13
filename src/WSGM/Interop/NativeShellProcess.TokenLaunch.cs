using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

internal static partial class NativeShellProcess
{
    private const uint MaximumAllowedTokenAccess = 0x02000000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateBreakawayFromJob = 0x01000000;

    /// <summary>Starts a fixed recovery owner with the retained shell's primary token, without
    /// selecting that shell as its process parent. The caller must verify the resulting process
    /// before using it as a recovery owner.</summary>
    internal static unsafe bool TryStartWithShellToken(
        NativeShellLaunchParent parent,
        string applicationPath,
        string commandLine,
        string workingDirectory,
        out NativeShellChildProcess? process,
        out int error)
    {
        ArgumentNullException.ThrowIfNull(parent);
        process = null;
        nint token = 0;
        nint environment = 0;
        try
        {
            // Preserve the available rights on a primary token derived from the actual medium
            // shell. This is not the elevated caller's linked token.
            if (!DuplicateTokenEx(parent.TokenHandle, MaximumAllowedTokenAccess, 0, 2, 1, out token)
                || !CreateEnvironmentBlock(out environment, token, false))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }
            StartupInfo startup = new() { Size = checked((uint)sizeof(StartupInfo)) };
            char[] mutableCommandLine = [.. commandLine, '\0'];
            fixed (char* application = applicationPath)
            fixed (char* command = mutableCommandLine)
            fixed (char* directory = workingDirectory)
            {
                if (!CreateProcessWithTokenW(token, 0, application, command,
                        CreateUnicodeEnvironment | CreateNoWindow | CreateBreakawayFromJob, environment, directory,
                        in startup, out ProcessInformation created))
                {
                    error = Marshal.GetLastPInvokeError();
                    return false;
                }
                NativeMethods.CloseHandle(created.Thread);
                process = new NativeShellChildProcess(created.ProcessId, created.Process);
                error = 0;
                return true;
            }
        }
        finally
        {
            if (environment != 0) { DestroyEnvironmentBlock(environment); }
            if (token != 0) { NativeMethods.CloseHandle(token); }
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(nint existingToken, uint access, nint attributes,
        int impersonationLevel, int tokenType, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessWithTokenW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessWithTokenW(nint token, uint logonFlags,
        char* application, char* commandLine, uint creationFlags, nint environment,
        char* directory, in StartupInfo startup, out ProcessInformation process);
}

// Shared between WSGM.Launch and WSGM.PackagedLaunch (linked as a source file).

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Starts a child suspended until its owner has contained the complete process tree.</summary>
internal static unsafe partial class ContainedProcessStart
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    /// <summary>Preserves the established launcher contract even for targets requesting administrator integrity.</summary>
    internal static void UseCallerIntegrity(ProcessStartInfo start)
    {
        start.Environment.TryGetValue("__COMPAT_LAYER", out var existing);
        var layers = existing?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("RunAsInvoker", StringComparer.OrdinalIgnoreCase) == true
            ? existing
            : string.IsNullOrEmpty(existing)
                ? "RunAsInvoker"
                : "RunAsInvoker " + existing;
        start.Environment["__COMPAT_LAYER"] = layers;
        Environment.SetEnvironmentVariable("__COMPAT_LAYER", layers);
    }

    internal static Process Start(ProcessStartInfo start, Func<nint, bool> contain)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(contain);
        if (start.ArgumentList.Count > 0 && start.Arguments.Length > 0)
        {
            throw new ArgumentException("Use either typed arguments or a raw argument string for this launch.",
                nameof(start));
        }

        var arguments = start.ArgumentList.Count > 0
            ? LaunchArguments.Join(start.ArgumentList)
            : start.Arguments;
        var commandLine = WindowsCommandLine.Quote(start.FileName) + (arguments.Length > 0 ? " " + arguments : "");
        if (commandLine.Length >= WindowsCommandLine.MaximumLength)
        {
            throw new ArgumentException("The game's launch command is too long for Windows.", nameof(start));
        }

        var command = (commandLine + '\0').ToCharArray();
        var environment = BuildEnvironment(start);
        ParentProcessStart.StartupInfo startup = new() { Size = (uint)sizeof(ParentProcessStart.StartupInfo) };
        ParentProcessStart.ProcessInformation information = default;
        Process? process = null;
        try
        {
            fixed (char* commandPointer = command)
            fixed (char* environmentPointer = environment)
            {
                if (!CreateProcessW(null, commandPointer, 0, 0, false,
                        CreateSuspended | CreateUnicodeEnvironment, environmentPointer,
                        start.WorkingDirectory, ref startup, out information))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the suspended target.");
                }
            }

            if (!contain(information.Process))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "The game's process tree could not be tracked.");
            }

            process = Process.GetProcessById((int)information.ProcessId);
            _ = process.Handle;
            if (ResumeThread(information.Thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not resume the contained target.");
            }

            return process;
        }
        catch
        {
            if (information.Process != 0)
            {
                _ = TerminateProcess(information.Process, 1);
            }

            process?.Dispose();
            throw;
        }
        finally
        {
            if (information.Thread != 0)
            {
                Win32Common.CloseHandle(information.Thread);
            }

            if (information.Process != 0)
            {
                Win32Common.CloseHandle(information.Process);
            }
        }
    }

    internal static string BuildEnvironment(ProcessStartInfo start)
    {
        StringBuilder environment = new();
        foreach (var pair in start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Key.Contains('\0') || pair.Value?.Contains('\0') == true)
            {
                throw new ArgumentException("A process environment value contains a NUL character.");
            }

            environment.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        environment.Append('\0');
        if (environment.Length == 1)
        {
            environment.Append('\0');
        }

        return environment.ToString();
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string? application, char* command, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, char* environment,
        string directory, ref ParentProcessStart.StartupInfo startup,
        out ParentProcessStart.ProcessInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint code);
}

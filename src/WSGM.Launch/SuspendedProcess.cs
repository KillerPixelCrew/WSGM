using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using WSGM.Core;

namespace WSGM.Launch;

/// <summary>Starts the medium target only after its whole process tree is contained.</summary>
internal static unsafe partial class SuspendedProcess
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    internal static (Process Process, JobObject Job) Start(ProcessStartInfo start)
    {
        var job = JobObject.Create();
        Process? process = null;
        ProcessInformation information = default;
        try
        {
            var arguments = string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList)
                .Select(WindowsCommandLine.Quote));
            var command = (arguments + '\0').ToCharArray();
            var environment = BuildEnvironment(start);
            StartupInformation startup = new() { Size = (uint)sizeof(StartupInformation) };
            fixed (char* commandPointer = command)
            fixed (char* environmentPointer = environment)
            {
                if (!CreateProcessW(null, commandPointer, 0, 0, false, CreateSuspended | CreateUnicodeEnvironment,
                        environmentPointer, start.WorkingDirectory, ref startup, out information))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the suspended target.");
                }
            }

            job.Assign(information.Process);
            process = Process.GetProcessById((int)information.ProcessId);
            _ = process.Handle; // Hold this exact process before it can exit and its PID be reused.
            if (ResumeThread(information.Thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not resume the contained target.");
            }

            return (process, job);
        }
        catch
        {
            if (information.Process != 0)
            {
                _ = TerminateProcess(information.Process, 1);
            }

            process?.Dispose();
            job.Dispose();
            throw;
        }
        finally
        {
            if (information.Thread != 0)
            {
                CloseHandle(information.Thread);
            }

            if (information.Process != 0)
            {
                CloseHandle(information.Process);
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
        string directory, ref StartupInformation startup, out ProcessInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint code);

    [LibraryImport("kernel32.dll")]
    private static partial void CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformation
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint Width;
        public uint Height;
        public uint Columns;
        public uint Rows;
        public uint Fill;
        public uint Flags;
        public ushort Show;
        public ushort ReservedSize;
        public nint ReservedBytes;
        public nint Input;
        public nint Output;
        public nint Error;
    }
}

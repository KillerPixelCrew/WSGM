using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.PackagedLaunch;

/// <summary>Starts a launcher program outside Steam's process tree.</summary>
/// <remarks>
///     <para>
///         Steam follows the tree of what a shortcut starts. A launcher such as Epic's or Ubisoft's
///         keeps running in the tray after its game exits; started as this process's child it would
///         sit in that tree, and Steam would show the game as running until the launcher closed. So
///         it is started with another process as its parent: Explorer where it runs, otherwise WSGM,
///         which is the shell in Game Mode. Both run as the same user in the same session.
///     </para>
///     <para>
///         When neither can be used, the program is started as an ordinary child. That still launches
///         the game; it only costs the accurate stopped state, and the log says so.
///     </para>
///     <para>
///         <c>SDL_GAMECONTROLLER_IGNORE_DEVICES</c> is removed from the environment, as it is for
///         every child WSGM's launchers start: Steam sets it for its own game and it would hide the
///         controller from this one.
///     </para>
/// </remarks>
internal static class DetachedStart
{
    private static readonly string[] Parents = ["explorer.exe", "WSGM.exe"];

    /// <summary>Starts the program and returns its process id, or -1 when it could not be started.</summary>
    /// <param name="program">The program, absolute.</param>
    /// <param name="arguments">Its arguments, verbatim.</param>
    /// <param name="detail">What happened, for the log.</param>
    internal static int Start(string program, string arguments, out string detail)
    {
        var directory = Path.GetDirectoryName(program) ?? Environment.CurrentDirectory;
        var commandLine = $"\"{program}\"{(arguments.Length > 0 ? " " + arguments : string.Empty)}";
        foreach (var name in Parents)
        {
            var parent = FindParent(name);
            if (parent == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var id = Create(commandLine, directory, parent, out var error);
                if (id > 0)
                {
                    detail = $"Started {Path.GetFileName(program)} ({id}) under {name}, outside Steam's tree.";
                    return id;
                }

                PackagedLaunchLog.Warn($"Starting {Path.GetFileName(program)} under {name} failed: error {error}.");
            }
            finally
            {
                NativeMethods.CloseHandle(parent);
            }
        }

        try
        {
            ProcessStartInfo start = new(program, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = directory
            };
            start.Environment.Remove("SDL_GAMECONTROLLER_IGNORE_DEVICES");
            using var process = Process.Start(start);
            if (process is null)
            {
                detail = $"{Path.GetFileName(program)} did not start.";
                return -1;
            }

            detail = $"Started {Path.GetFileName(program)} ({process.Id}) as a child: no parent outside "
                     + "Steam's tree could be used, so Steam may keep the game running while the launcher does.";
            return process.Id;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                       or IOException)
        {
            detail = $"{Path.GetFileName(program)} could not be started: {ex.Message}";
            return -1;
        }
    }

    /// <summary>Opens a same-session, same-user process by name that may parent a new one.</summary>
    private static IntPtr FindParent(string name)
    {
        if (!NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var session))
        {
            return IntPtr.Zero;
        }

        foreach (var entry in ProcessInspector.Snapshot())
        {
            if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)
                || !NativeMethods.ProcessIdToSessionId((uint)entry.Id, out var theirs)
                || theirs != session)
            {
                continue;
            }

            // Opening it for process creation fails for an elevated process from a medium one, which
            // is the right answer: a launcher must not inherit elevation it was never given.
            var handle = NativeMethods.OpenProcess(NativeMethods.ProcessCreateProcess, false, (uint)entry.Id);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static int Create(string commandLine, string directory, IntPtr parent, out int error)
    {
        error = 0;
        var size = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        var parentSlot = Marshal.AllocHGlobal(IntPtr.Size);
        var environment = IntPtr.Zero;
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                error = Marshal.GetLastWin32Error();
                return -1;
            }

            try
            {
                Marshal.WriteIntPtr(parentSlot, parent);
                if (!NativeMethods.UpdateProcThreadAttribute(list, 0, NativeMethods.ProcThreadAttributeParentProcess,
                        parentSlot, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    error = Marshal.GetLastWin32Error();
                    return -1;
                }

                environment = Marshal.StringToHGlobalUni(EnvironmentBlock());
                NativeMethods.StartupInfoEx startup = new()
                {
                    StartupInfo = new NativeMethods.StartupInfo { cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>() },
                    lpAttributeList = list
                };
                StringBuilder command = new(commandLine, commandLine.Length + 1);
                if (!NativeMethods.CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false,
                        NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                        environment, directory, ref startup, out var created))
                {
                    error = Marshal.GetLastWin32Error();
                    return -1;
                }

                NativeMethods.CloseHandle(created.hThread);
                NativeMethods.CloseHandle(created.hProcess);
                return created.dwProcessId;
            }
            finally
            {
                NativeMethods.DeleteProcThreadAttributeList(list);
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }

            Marshal.FreeHGlobal(parentSlot);
            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary>This process's environment without Steam's controller exclusion, as a Unicode block.</summary>
    private static string EnvironmentBlock()
    {
        StringBuilder block = new();
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is not string key
                || string.Equals(key, "SDL_GAMECONTROLLER_IGNORE_DEVICES", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            block.Append(key).Append('=').Append(variable.Value as string ?? string.Empty).Append('\0');
        }

        return block.Append('\0').ToString();
    }
}

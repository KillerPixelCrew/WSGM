using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WSGM.Interop;
using WSGM.Launch;

namespace WSGM.PackagedLaunch;

/// <summary>A launcher program this session started, held by its handle so its exit can be read.</summary>
/// <remarks>
///     Held rather than looked up by id: the id alone can be reused as soon as the program exits,
///     and a launcher that hands its request to an already running copy of itself exits at once.
/// </remarks>
internal sealed class StartedLauncher : IDisposable
{
    private const uint StillActive = 259;

    private readonly SafeProcessHandle _handle;

    /// <summary>Takes ownership of a process handle.</summary>
    /// <param name="id">The process id, for the log.</param>
    /// <param name="handle">An owned handle with at least limited query access.</param>
    /// <param name="name">The program's file name.</param>
    internal StartedLauncher(int id, SafeProcessHandle handle, string name)
    {
        Id = id;
        _handle = handle;
        Name = name;
    }

    /// <summary>The process id, for the log only.</summary>
    internal int Id { get; }

    /// <summary>The program's file name, which is also the name a resident copy of it runs under.</summary>
    internal string Name { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _handle.Dispose();
    }

    /// <summary>The program's exit code once it has exited, or null while it runs or when unreadable.</summary>
    internal int? ExitCode()
    {
        return NativeMethods.GetExitCodeProcess(_handle, out var code) && code != StillActive
            ? unchecked((int)code)
            : null;
    }
}

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
///         It also gets that parent's own user environment rather than this process's, which Steam
///         set up for the game it thinks it launched. A launcher that stays resident would otherwise
///         hand Steam's app id and controller exclusion to every game it starts later, outside Steam.
///         The start itself is WSGM's shared <see cref="ParentProcessStart" />.
///     </para>
///     <para>
///         When neither parent can be used, the program is started as an ordinary child with this
///         process's environment less <see cref="SteamControllerExclusion.Variable" />. That still
///         launches the game; it only costs the accurate stopped state, and the log says so.
///     </para>
/// </remarks>
internal static class DetachedStart
{
    private const uint TokenDuplicate = 0x0002;
    private static readonly string[] Parents = ["explorer.exe", "WSGM.exe"];

    /// <summary>Starts the program.</summary>
    /// <param name="program">The program, absolute.</param>
    /// <param name="arguments">Its arguments, verbatim.</param>
    /// <param name="detail">What happened, for the log.</param>
    /// <returns>The started program, or null when it could not be started.</returns>
    internal static StartedLauncher? Start(string program, string arguments, out string detail)
    {
        var name = Path.GetFileName(program);
        var directory = Path.GetDirectoryName(program) ?? Environment.CurrentDirectory;
        var commandLine = $"\"{program}\"{(arguments.Length > 0 ? " " + arguments : string.Empty)}";
        foreach (var parentName in Parents)
        {
            if (!TryOpenParent(parentName, out var parent, out var token))
            {
                continue;
            }

            try
            {
                if (ParentProcessStart.TryCreate(parent, token, program, commandLine, directory, out var id,
                        out var handle, out var error))
                {
                    detail = $"Started {name} ({id}) under {parentName}, outside Steam's tree and its environment.";
                    return new StartedLauncher((int)id, new SafeProcessHandle(handle, true), name);
                }

                PackagedLaunchLog.Warn($"Starting {name} under {parentName} failed: error {error}.");
            }
            finally
            {
                WSGM.Interop.Win32Common.CloseHandle(token);
                WSGM.Interop.Win32Common.CloseHandle(parent);
            }
        }

        return StartAsChild(program, arguments, directory, name, out detail);
    }

    private static StartedLauncher? StartAsChild(
        string program, string arguments, string directory, string name, out string detail)
    {
        try
        {
            ProcessStartInfo start = new(program, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = directory
            };
            start.Environment.Remove(SteamControllerExclusion.Variable);
            using var process = Process.Start(start);
            if (process is null)
            {
                detail = $"{name} did not start.";
                return null;
            }

            // A handle of its own, taken while the Process object still holds the id: the object is
            // disposed here and its handle with it.
            var handle = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation | NativeMethods.Synchronize, false, (uint)process.Id);
            detail = $"Started {name} ({process.Id}) as a child: no parent outside Steam's tree could be used, "
                     + "so Steam may keep the game running while the launcher does.";
            return handle == IntPtr.Zero
                ? new StartedLauncher(process.Id, new SafeProcessHandle(), name)
                : new StartedLauncher(process.Id, new SafeProcessHandle(handle, true), name);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            detail = $"{name} could not be started: {ex.Message}";
            return null;
        }
    }

    /// <summary>Opens a same-session, same-user process by name that may parent a new one, and its token.</summary>
    private static bool TryOpenParent(string name, out IntPtr parent, out IntPtr token)
    {
        parent = IntPtr.Zero;
        token = IntPtr.Zero;
        if (!NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var session))
        {
            return false;
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
            var process = NativeMethods.OpenProcess(
                NativeMethods.ProcessCreateProcess | NativeMethods.ProcessQueryLimitedInformation, false,
                (uint)entry.Id);
            if (process == IntPtr.Zero)
            {
                continue;
            }

            // Its token builds the launcher's environment: the user's own, as that parent has it.
            if (NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery | TokenDuplicate, out var opened))
            {
                parent = process;
                token = opened;
                return true;
            }

            PackagedLaunchLog.Warn(
                $"Could not read {name}'s environment (error {Marshal.GetLastWin32Error()}); trying another parent.");
            WSGM.Interop.Win32Common.CloseHandle(process);
        }

        return false;
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WSGM.PackagedLaunch;

/// <summary>Holds the game's processes so they die with this wrapper.</summary>
/// <remarks>
///     <para>
///         The opposite choice from <c>WSGM.Launch</c>, whose job deliberately lets the tree outlive
///         the wrapper. Here the wrapper is Steam's only handle on the session: package activation
///         puts the game outside this process's tree, so when Steam stops the shortcut it terminates
///         the wrapper and reaches nothing. Without kill-on-close the game keeps running while Steam
///         shows it as stopped.
///     </para>
///     <para>
///         Only the title's own processes go in. A packaged app shares Windows infrastructure —
///         <c>RuntimeBroker</c> above all — with every other packaged app on the machine, and killing
///         one of those when Steam stops a game would reach well outside this session.
///     </para>
/// </remarks>
internal sealed class GameSessionJob : IDisposable
{
    /// <summary>Shared Windows infrastructure that carries package identity but is not the game.</summary>
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "runtimebroker.exe",
        "applicationframehost.exe",
        "dllhost.exe",
        "gamebarpresencewriter.exe",
        "gamingservices.exe",
        "gamingservicesnet.exe",
        "backgroundtaskhost.exe",
        "wwahost.exe"
    };

    private readonly HashSet<int> _contained = [];
    private IntPtr _job;

    /// <summary>Creates the kill-on-close job, or reports why the session cannot have one.</summary>
    internal GameSessionJob()
    {
        _job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
        {
            PackagedLaunchLog.Warn(
                $"No containment job (error {Marshal.GetLastWin32Error()}); stopping the shortcut in "
                + "Steam will leave the game running.");
            return;
        }

        var information = default(NativeMethods.JobObjectExtendedLimitInformationData);
        information.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
        var size = Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformationData>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, false);
            if (!NativeMethods.SetInformationJobObject(
                    _job, NativeMethods.JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                PackagedLaunchLog.Warn(
                    $"Could not set kill-on-close (error {Marshal.GetLastWin32Error()}).");
                NativeMethods.CloseHandle(_job);
                _job = IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Whether a job exists to contain anything.</summary>
    internal bool Available => _job != IntPtr.Zero;

    /// <summary>Whether this process belongs to the game rather than to shared infrastructure.</summary>
    /// <param name="facts">The process to judge.</param>
    /// <param name="packageFamilyName">The game's package family.</param>
    /// <returns>True when the process is part of this game session.</returns>
    internal static bool BelongsToGame(ProcessFacts facts, string packageFamilyName)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.PackageFamilyName is { } family
               && string.Equals(family, packageFamilyName, StringComparison.OrdinalIgnoreCase)
               && !Shared.Contains(facts.Name);
    }

    /// <summary>Adds one of the game's processes to the job.</summary>
    /// <param name="processId">The process to contain.</param>
    /// <param name="name">Its image name, for the log.</param>
    /// <remarks>
    ///     Assignment can legitimately fail: a packaged app already lives in a system-managed job,
    ///     and nesting is permitted but not guaranteed. The failure is recorded rather than
    ///     swallowed, because it changes what stopping the shortcut will do.
    /// </remarks>
    internal void Contain(int processId, string name)
    {
        if (_job == IntPtr.Zero || !_contained.Add(processId))
        {
            return;
        }

        var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessSetQuota | NativeMethods.ProcessTerminate, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            PackagedLaunchLog.Warn(
                $"Cannot contain {name} ({processId}): access denied (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        try
        {
            if (NativeMethods.AssignProcessToJobObject(_job, process))
            {
                PackagedLaunchLog.Info($"Contained {name} ({processId}).");
            }
            else
            {
                PackagedLaunchLog.Warn(
                    $"Could not contain {name} ({processId}): error {Marshal.GetLastWin32Error()}.");
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>How many contained processes are still running, or null when there is no job.</summary>
    /// <remarks>
    ///     The kernel already counts this, which is cheaper and more accurate than enumerating the
    ///     machine on a timer to find out whether the game is still there.
    /// </remarks>
    internal uint? ActiveProcesses()
    {
        if (_job == IntPtr.Zero)
        {
            return null;
        }

        var size = Marshal.SizeOf<NativeMethods.JobObjectBasicAccountingInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.QueryInformationJobObject(
                    _job, NativeMethods.JobObjectBasicAccountingInformationClass, buffer, (uint)size, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStructure<NativeMethods.JobObjectBasicAccountingInformation>(buffer)
                .ActiveProcesses;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_job != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }
}

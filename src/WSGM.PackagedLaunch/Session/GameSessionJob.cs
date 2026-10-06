using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WSGM.Interop;

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

    private readonly HashSet<(int Id, DateTime? StartedAt)> _contained = [];

    /// <summary>The limits the job was created with, which <see cref="Abandon" /> keeps all but one of.</summary>
    private readonly uint _limits;

    private IntPtr _job;

    /// <summary>Creates the kill-on-close job, or reports why the session cannot have one.</summary>
    /// <param name="recognisedOnly">
    ///     Whether only the processes the supervisor recognises and contains belong in the job. A
    ///     followed game can start a launcher of its own - an Epic title starts Ubisoft Connect - and
    ///     that launcher must neither keep the session alive nor be killed when Steam stops the game.
    ///     So its children leave the job silently, and each game process is contained on its own.
    ///     A packaged game keeps the default: its children carry its identity and belong to it.
    /// </param>
    internal GameSessionJob(bool recognisedOnly = false)
    {
        _job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
        {
            PackagedLaunchLog.Warn(
                $"No containment job (error {Marshal.GetLastWin32Error()}); stopping the shortcut in "
                + "Steam will leave the game running.");
            return;
        }

        _limits = NativeMethods.JobObjectLimitKillOnJobClose
                  | (recognisedOnly ? NativeMethods.JobObjectLimitSilentBreakawayOk : 0);
        var information = default(NativeMethods.JobObjectExtendedLimitInformationData);
        information.BasicLimitInformation.LimitFlags = _limits;
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
                Win32Common.CloseHandle(_job);
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_job != IntPtr.Zero)
        {
            Win32Common.CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }

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

    /// <summary>Whether this process is the GDK launch helper rather than the game itself.</summary>
    /// <param name="facts">The process to judge.</param>
    /// <remarks>
    ///     The helper is contained and supervised like everything else carrying the package
    ///     identity, but it is not what owns the swap chain, so checks about the overlay must not
    ///     settle on it.
    /// </remarks>
    internal static bool IsLaunchHelper(ProcessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Name.Equals("gamelaunchhelper.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds one of the game's processes to the job.</summary>
    /// <param name="facts">The process to contain, known by its id and start time.</param>
    /// <returns>Whether the process is now in the job.</returns>
    /// <remarks>
    ///     <para>
    ///         Assignment can legitimately fail: a packaged app already lives in a system-managed job,
    ///         and nesting is permitted but not guaranteed. The failure is recorded rather than
    ///         swallowed, because it changes what stopping the shortcut will do.
    ///     </para>
    ///     <para>
    ///         A process is remembered by its id and its start time, so a new process that reuses a
    ///         contained one's id is contained in its own right. The handle is checked against that
    ///         start time before it is assigned, so the job never takes a process that merely inherited
    ///         the id since it was seen.
    ///     </para>
    /// </remarks>
    internal bool Contain(ProcessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (_job == IntPtr.Zero || facts.StartedAt is null || !_contained.Add((facts.Id, facts.StartedAt)))
        {
            return false;
        }

        var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessSetQuota | NativeMethods.ProcessTerminate
                                          | NativeMethods.ProcessQueryLimitedInformation, false, (uint)facts.Id);
        if (process == IntPtr.Zero)
        {
            PackagedLaunchLog.Warn(
                $"Cannot contain {facts.Name} ({facts.Id}): access denied (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        try
        {
            var accepted = ProcessContainmentIdentity.TryAssign(facts.StartedAt,
                () => NativeMethods.GetProcessTimes(process, out var creation, out _, out _, out _)
                    ? DateTime.FromFileTimeUtc(creation)
                    : null,
                () => NativeMethods.AssignProcessToJobObject(_job, process));
            if (accepted)
            {
                PackagedLaunchLog.Info($"Contained {facts.Name} ({facts.Id}).");
                return true;
            }

            PackagedLaunchLog.Warn(
                $"Could not contain {facts.Name} ({facts.Id}): creation identity or assignment refused (error {Marshal.GetLastWin32Error()}).");
            return false;
        }
        finally
        {
            Win32Common.CloseHandle(process);
        }
    }

    /// <summary>Gives up the contained processes so closing the job does not end them.</summary>
    /// <remarks>
    ///     The job is kill-on-close, which is what makes Steam's Stop button end the whole game
    ///     tree. A managed cancellation promises the opposite — the log says the game is left
    ///     running — so the limit has to come off before the handle closes, or the promise is a lie
    ///     and Ctrl+C kills the game.
    /// </remarks>
    internal void Abandon()
    {
        if (_job == IntPtr.Zero)
        {
            return;
        }

        var information = default(NativeMethods.JobObjectExtendedLimitInformationData);
        information.BasicLimitInformation.LimitFlags = _limits & ~NativeMethods.JobObjectLimitKillOnJobClose;
        var size = Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformationData>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, false);
            if (!NativeMethods.SetInformationJobObject(
                    _job, NativeMethods.JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                PackagedLaunchLog.Warn(
                    "Could not release the containment job; the game may be ended when this exits "
                    + $"(error {Marshal.GetLastWin32Error()}).");
                return;
            }

            PackagedLaunchLog.Info("Containment released; the game keeps running.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
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
}

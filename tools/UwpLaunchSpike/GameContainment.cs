using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Wsgm.UwpSpike;

/// A kill-on-close job object holding the discovered game processes, so the game dies
/// with the wrapper.
///
/// This is the opposite choice from WSGM.Launch's job, which deliberately lets the tree
/// outlive the wrapper. Here the wrapper is Steam's only handle on the session: package
/// activation puts the game outside the wrapper's process tree, so when Steam stops the
/// shortcut it terminates the wrapper and nothing else, and the game keeps running with
/// Steam showing it as stopped. Kill-on-close makes the kernel finish the job that
/// TerminateProcess on the wrapper cannot.
internal sealed class GameContainment : IDisposable
{
    private readonly SpikeLog log;
    private readonly HashSet<int> contained = [];
    private IntPtr job;

    internal GameContainment(SpikeLog log)
    {
        this.log = log;
        job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            log.Warn($"Could not create the containment job (error {Marshal.GetLastWin32Error()}); "
                + "stopping the shortcut in Steam will leave the game running.");
            return;
        }

        var information = default(Native.JobObjectExtendedLimitInformationData);
        information.BasicLimitInformation.LimitFlags = Native.JobObjectLimitKillOnJobClose;
        var size = Marshal.SizeOf<Native.JobObjectExtendedLimitInformationData>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, false);
            if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                log.Warn($"Could not set kill-on-close on the containment job (error {Marshal.GetLastWin32Error()}).");
                Native.CloseHandle(job);
                job = IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal bool Available => job != IntPtr.Zero;

    /// Adds one discovered process. Assignment can legitimately fail: a packaged app already
    /// lives in a system-managed job, and nesting is allowed but not guaranteed. The failure
    /// is a finding in its own right, so it is recorded rather than swallowed.
    internal void Contain(int pid, string name)
    {
        if (job == IntPtr.Zero || !contained.Add(pid))
        {
            return;
        }

        var process = Native.OpenProcess(Native.ProcessSetQuota | Native.ProcessTerminate, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            log.Warn($"containment: cannot open pid {pid} \"{name}\" for SET_QUOTA|TERMINATE (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        try
        {
            if (Native.AssignProcessToJobObject(job, process))
            {
                log.Info($"containment: pid {pid} \"{name}\" joined the kill-on-close job.");
            }
            else
            {
                log.Warn($"containment: AssignProcessToJobObject failed for pid {pid} \"{name}\" (error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    public void Dispose()
    {
        if (job != IntPtr.Zero)
        {
            Native.CloseHandle(job);
            job = IntPtr.Zero;
        }
    }
}

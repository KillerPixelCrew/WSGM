using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Wsgm.UwpSpike;

/// Takes the packaged title out of Process Lifetime Management so Windows stops
/// suspending it.
///
/// Earlier trials observed suspended game threads after focus loss. Missing window
/// enumeration was a separate manifestation of UWP window filtering, not proof of
/// suspension. This exemption keeps the game available while the attended spike examines
/// overlay and input behavior across foreground transitions.
///
/// IPackageDebugSettings::EnableDebugging is the supported way out: it is what a debugger
/// attaches with, and a package marked for debugging is exempt from suspension. The
/// exemption is released again on exit so the title goes back to normal behaviour.
internal sealed class SuspensionControl : IDisposable
{
    private readonly SpikeLog log;
    private object? settings;
    private string? packageFullName;

    internal SuspensionControl(SpikeLog log) => this.log = log;

    /// Reads the package full name from a running process, which is the identity
    /// IPackageDebugSettings works in (the family name is not enough).
    internal static string? PackageFullNameOf(int pid)
    {
        var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            uint length = 0;
            if (Native.GetPackageFullName(process, ref length, null) != Native.ErrorInsufficientBuffer)
            {
                return null;
            }

            var buffer = new StringBuilder((int)length);
            return Native.GetPackageFullName(process, ref length, buffer) == Native.ErrorSuccess
                ? buffer.ToString()
                : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    /// The installed package full name for a family, needed before anything of that
    /// package is running.
    internal static string? ResolveFullName(string familyName)
    {
        uint count = 0;
        uint bufferLength = 0;
        var status = Native.FindPackagesByPackageFamily(
            familyName,
            Native.PackageFilterHead | Native.PackageFilterDirect,
            ref count,
            IntPtr.Zero,
            ref bufferLength,
            IntPtr.Zero,
            IntPtr.Zero);
        if (status != Native.ErrorInsufficientBuffer || count == 0)
        {
            return null;
        }

        var names = Marshal.AllocHGlobal((int)count * IntPtr.Size);
        var buffer = Marshal.AllocHGlobal((int)bufferLength * sizeof(char));
        try
        {
            status = Native.FindPackagesByPackageFamily(
                familyName,
                Native.PackageFilterHead | Native.PackageFilterDirect,
                ref count,
                names,
                ref bufferLength,
                buffer,
                IntPtr.Zero);
            return status == Native.ErrorSuccess
                ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(names))
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(names);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <param name="fullName">The package to exempt. Environment forwarding is separate.</param>
    internal void ExemptFromSuspension(string fullName)
    {
        if (packageFullName is not null) { return; }
        try
        {
            settings = new Native.PackageDebugSettings();
            var result = ((Native.IPackageDebugSettings)settings).EnableDebugging(fullName, null, IntPtr.Zero);
            if (result < 0)
            {
                log.Warn($"suspension: EnableDebugging({fullName}) failed with 0x{result:X8}.");
                Release();
                return;
            }

            packageFullName = fullName;
            log.Info($"suspension: {fullName} is exempt from PLM suspension for this session.");
        }
        catch (Exception ex)
        {
            log.Warn($"suspension: could not reach IPackageDebugSettings: {ex.Message}");
            Release();
        }
    }
    /// Asks PLM to resume the package, for the case where it was already suspended before
    /// the exemption was in place.
    internal void Resume()
    {
        if (settings is null || packageFullName is null)
        {
            return;
        }

        var hresult = ((Native.IPackageDebugSettings)settings).Resume(packageFullName);
        log.Info($"suspension: Resume({packageFullName}) returned 0x{hresult:X8}");
    }

    /// How many of a process's threads Windows currently has suspended. All of them means
    /// the game is frozen, which is worth seeing in the transcript next to everything else.
    internal static (int Suspended, int Total) SuspendedThreads(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var threads = process.Threads.Cast<ProcessThread>().ToList();
            var suspended = threads.Count(thread =>
                thread.ThreadState == System.Diagnostics.ThreadState.Wait
                && thread.WaitReason == ThreadWaitReason.Suspended);
            return (suspended, threads.Count);
        }
        catch
        {
            return (0, 0);
        }
    }

    public void Dispose()
    {
        if (settings is not null && packageFullName is not null)
        {
            try
            {
                var hresult = ((Native.IPackageDebugSettings)settings).DisableDebugging(packageFullName);
                log.Info($"suspension: DisableDebugging({packageFullName}) returned 0x{hresult:X8}");
            }
            catch (Exception ex)
            {
                log.Warn($"suspension: could not release the exemption: {ex.Message}");
            }
        }

        Release();
    }

    private void Release()
    {
        if (settings is not null && Marshal.IsComObject(settings))
        {
            Marshal.FinalReleaseComObject(settings);
        }

        settings = null;
        packageFullName = null;
    }
}

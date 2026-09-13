using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Wsgm.UwpSpike;

/// Takes the packaged title out of Process Lifetime Management so Windows stops
/// suspending it.
///
/// This is the difference between a UWP title behaving like a game and behaving like a
/// phone app. PLM suspends a packaged app as soon as it loses the foreground: a measured
/// run found all 66 of Moonlighter's threads in Suspended wait the moment focus went
/// elsewhere. While suspended the process has no enumerable window, SetForegroundWindow
/// cannot wake it, and any injected overlay is frozen along with everything else - which
/// accounts for the game not coming back from Steam's Resume and for an injected overlay
/// doing nothing.
///
/// IPackageDebugSettings::EnableDebugging is the supported way out: it is what a debugger
/// attaches with, and a package marked for debugging is exempt from suspension. The
/// exemption is released again on exit so the title goes back to normal behaviour.
internal sealed class SuspensionControl : IDisposable
{
    private readonly SpikeLog log;
    private object? settings;
    private string? packageFullName;
    private bool environmentAccepted = true;

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

    /// <param name="fullName">The package to exempt.</param>
    /// <param name="environment">Variables to hand the package's next launch, as
    /// name=value pairs. This is how Steam's own launch variables reach a title the
    /// Windows broker starts, since such a process inherits nothing from this wrapper.</param>
    internal void ExemptFromSuspension(string fullName, IReadOnlyList<string> environment)
    {
        if (packageFullName is not null)
        {
            return;
        }

        var block = EnvironmentBlock(environment);
        try
        {
            settings = new Native.PackageDebugSettings();
            var debug = (Native.IPackageDebugSettings)settings;
            var hresult = debug.EnableDebugging(fullName, null, block);

            // Separating the two jobs this call does: if it only refuses the environment
            // block, the suspension exemption is still worth having, and knowing which
            // argument was rejected is the finding.
            if (hresult < 0 && block != IntPtr.Zero)
            {
                log.Warn($"suspension: EnableDebugging with an environment block failed with 0x{hresult:X8}; "
                    + "retrying without it to see whether the environment is what it rejects.");
                environmentAccepted = false;
                hresult = debug.EnableDebugging(fullName, null, IntPtr.Zero);
            }

            if (hresult < 0)
            {
                log.Warn($"suspension: EnableDebugging({fullName}) failed with 0x{hresult:X8}; "
                    + "Windows will keep suspending the game whenever it loses the foreground.");
                Release();
                return;
            }

            packageFullName = fullName;
            log.Info($"suspension: {fullName} is exempt from PLM suspension for this session.");
            if (environment.Count > 0 && !environmentAccepted)
            {
                log.Warn("suspension: the environment block was rejected, so Steam's launch variables "
                    + "did NOT reach the game. The renderer will have no session to attach to.");
            }
            else if (environment.Count > 0)
            {
                log.Info($"suspension: handed {environment.Count} variable(s) to the package launch:");
                foreach (var variable in environment)
                {
                    log.Line("            " + variable);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"suspension: could not reach IPackageDebugSettings: {ex.GetType().Name}: {ex.Message}");
            Release();
        }
        finally
        {
            if (block != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(block);
            }
        }
    }

    /// A PZZWSTR: name=value pairs, each NUL-terminated, the whole run closed by a second NUL.
    private static IntPtr EnvironmentBlock(IReadOnlyList<string> variables)
    {
        if (variables.Count == 0)
        {
            return IntPtr.Zero;
        }

        var text = new StringBuilder();
        foreach (var variable in variables)
        {
            text.Append(variable).Append('\0');
        }

        text.Append('\0');
        return Marshal.StringToHGlobalUni(text.ToString());
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

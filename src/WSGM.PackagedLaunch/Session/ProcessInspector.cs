using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.PackagedLaunch;

/// <summary>One process in a snapshot.</summary>
/// <param name="Id">The process id.</param>
/// <param name="ParentId">Its reported parent, which may already have exited.</param>
/// <param name="Name">Its image file name.</param>
internal sealed record ProcessEntry(int Id, int ParentId, string Name);

/// <summary>What the launcher knows about one process it is acting on.</summary>
/// <param name="Id">The process id.</param>
/// <param name="Name">Its image file name, never its full path.</param>
/// <param name="PackageFamilyName">Its package identity, or null when it carries none.</param>
/// <param name="IsAppContainer">Whether it runs in an AppContainer, or null when unreadable.</param>
/// <param name="Integrity">Its integrity level as a word, or empty when unreadable.</param>
/// <param name="StartedAt">When it started, used to tell a reused process id from the same process.</param>
internal sealed record ProcessFacts(
    int Id,
    string Name,
    string? PackageFamilyName,
    bool? IsAppContainer,
    string Integrity,
    DateTime? StartedAt);

/// <summary>One top-level window belonging to, or hosting, a process.</summary>
/// <param name="Handle">The window.</param>
/// <param name="ClassName">Its class, which is how a UWP CoreWindow and its frame are told apart.</param>
/// <param name="Visible">Whether Windows considers it visible.</param>
internal sealed record WindowEntry(IntPtr Handle, string ClassName, bool Visible);

/// <summary>Reads the few process facts this launcher acts on.</summary>
/// <remarks>
///     Deliberately small. The spike described processes exhaustively because it was answering
///     questions; this reads identity, token shape and lifetime, which is what routing, containment
///     and the exit decision need. Nothing here enumerates modules or walks the whole machine on a
///     timer.
/// </remarks>
internal static class ProcessInspector
{
    /// <summary>Every process currently running, or an empty list when the snapshot failed.</summary>
    internal static IReadOnlyList<ProcessEntry> Snapshot()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return [];
        }

        try
        {
            List<ProcessEntry> entries = [];
            NativeMethods.ProcessEntry32W entry = default;
            entry.dwSize = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32W>();
            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return entries;
            }

            do
            {
                entries.Add(new ProcessEntry(
                    (int)entry.th32ProcessID,
                    (int)entry.th32ParentProcessID,
                    entry.szExeFile ?? string.Empty));
            } while (NativeMethods.Process32NextW(snapshot, ref entry));

            return entries;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    /// <summary>Reads one process, or null when it is gone or cannot be opened.</summary>
    /// <param name="processId">The process to read.</param>
    /// <param name="name">Its image name from the snapshot, when one is already known.</param>
    internal static ProcessFacts? Describe(int processId, string? name = null)
    {
        var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return new ProcessFacts(
                processId,
                name is { Length: > 0 } ? name : ImageName(process),
                PackageFamilyNameOf(process),
                IsAppContainer(process),
                IntegrityOf(process),
                StartedAtOf(process));
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>The package family a process carries, or null when it carries none.</summary>
    internal static string? PackageFamilyNameOf(int processId)
    {
        var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return PackageFamilyNameOf(process);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>When a process started, or null when that cannot be read.</summary>
    /// <remarks>
    ///     Process ids are reused, so a recovery record that names one is only trustworthy together
    ///     with the start time of the process that held it.
    /// </remarks>
    internal static DateTime? StartedAt(int processId)
    {
        var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return StartedAtOf(process);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>Whether a process runs as native x64 code, the only kind the overlay components are built for.</summary>
    /// <param name="processId">The process.</param>
    /// <returns>True for a native x64 process on an x64 machine; null when it cannot be read.</returns>
    /// <remarks>
    ///     A 32-bit package runs under WOW64, and an x64 DLL or an x64 code stub written into it cannot
    ///     load or run there. Anything that is not established as x64 is treated as not x64.
    /// </remarks>
    internal static bool? IsNativeX64(int processId)
    {
        var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return NativeMethods.IsWow64Process2(process, out var processMachine, out var nativeMachine)
                ? processMachine == NativeMethods.ImageFileMachineUnknown
                  && nativeMachine == NativeMethods.ImageFileMachineAmd64
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>The windows a process owns, plus the frame hosting its CoreWindow.</summary>
    /// <param name="processId">The process to look for.</param>
    /// <remarks>
    ///     A UWP title's visible window is an <c>ApplicationFrameWindow</c> owned by
    ///     ApplicationFrameHost, not by the game, so looking only at the game's own process finds
    ///     nothing and an empty result would read as "this game has no window". The frame is
    ///     admitted when it actually hosts a CoreWindow belonging to this process, and never
    ///     otherwise: every packaged app on the machine shares that host.
    /// </remarks>
    internal static IReadOnlyList<WindowEntry> WindowsOf(int processId)
    {
        List<WindowEntry> found = [];
        NativeMethods.EnumWindows((handle, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(handle, out var owner);
            var className = ClassNameOf(handle);
            var owned = owner == (uint)processId;
            if (!owned && className.Equals("ApplicationFrameWindow", StringComparison.Ordinal))
            {
                owned = HostsCoreWindowOf(handle, processId);
            }

            if (owned)
            {
                found.Add(new WindowEntry(handle, className, NativeMethods.IsWindowVisible(handle)));
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Whether a frame window hosts a CoreWindow belonging to this process.</summary>
    internal static bool HostsCoreWindowOf(IntPtr frame, int processId)
    {
        var hosts = false;
        NativeMethods.EnumChildWindows(frame, (child, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(child, out var owner);
            if (owner != (uint)processId
                || !ClassNameOf(child).Equals("Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
            {
                return true;
            }

            hosts = true;
            return false;
        }, IntPtr.Zero);
        return hosts;
    }

    private static string ClassNameOf(IntPtr handle)
    {
        StringBuilder className = new(256);
        return NativeMethods.GetClassNameW(handle, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    private static string ImageName(IntPtr process)
    {
        StringBuilder buffer = new(260);
        var size = (uint)buffer.Capacity;
        return NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size)
            ? Path.GetFileName(buffer.ToString())
            : string.Empty;
    }

    private static string? PackageFamilyNameOf(IntPtr process)
    {
        uint length = 0;
        if (NativeMethods.GetPackageFamilyName(process, ref length, null) != NativeMethods.ErrorInsufficientBuffer)
        {
            return null;
        }

        StringBuilder buffer = new((int)length);
        return NativeMethods.GetPackageFamilyName(process, ref length, buffer) == NativeMethods.ErrorSuccess
            ? buffer.ToString()
            : null;
    }

    private static DateTime? StartedAtOf(IntPtr process)
    {
        return NativeMethods.GetProcessTimes(process, out var creation, out _, out _, out _)
            ? DateTime.FromFileTimeUtc(creation)
            : null;
    }

    private static bool? IsAppContainer(IntPtr process)
    {
        return TokenValue<bool>(process, NativeMethods.TokenIsAppContainer, sizeof(uint), buffer =>
            Marshal.ReadInt32(buffer) != 0);
    }

    private static string IntegrityOf(IntPtr process)
    {
        // The integrity level is the last sub-authority of the label SID, and the well-known values
        // are the only ones worth naming; anything else is reported as its number.
        var level = TokenValue(process, NativeMethods.TokenIntegrityLevel, 64, buffer =>
        {
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return (uint?)null;
            }

            var countPointer = NativeMethods.GetSidSubAuthorityCount(sid);
            if (countPointer == IntPtr.Zero)
            {
                return null;
            }

            var count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                return null;
            }

            var last = NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1));
            return last == IntPtr.Zero ? null : (uint)Marshal.ReadInt32(last);
        });

        return level switch
        {
            null => string.Empty,
            < 0x1000 => "untrusted",
            < 0x2000 => "low",
            < 0x3000 => "medium",
            < 0x4000 => "high",
            _ => "system"
        };
    }

    private static T? TokenValue<T>(IntPtr process, int informationClass, int initialSize, Func<IntPtr, T?> read)
        where T : struct
    {
        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token))
        {
            return null;
        }

        var buffer = IntPtr.Zero;
        try
        {
            var size = (uint)initialSize;
            buffer = Marshal.AllocHGlobal((int)size);
            if (!NativeMethods.GetTokenInformation(token, informationClass, buffer, size, out var needed))
            {
                if (needed == 0 || needed > 4096)
                {
                    return null;
                }

                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal((int)needed);
                if (!NativeMethods.GetTokenInformation(token, informationClass, buffer, needed, out _))
                {
                    return null;
                }
            }

            return read(buffer);
        }
        catch (Exception ex) when (ex is OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            NativeMethods.CloseHandle(token);
        }
    }
}

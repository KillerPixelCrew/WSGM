using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Wsgm.UwpSpike;

internal sealed record ProcessEntry(int Pid, int ParentPid, string Name);

internal sealed record WindowEntry(IntPtr Handle, string ClassName, string Title, bool Visible);

/// Everything the spike wants to know about one process, gathered under whatever
/// rights this (deliberately unelevated) wrapper actually has.
internal sealed record ProcessReport(
    int Pid,
    int ParentPid,
    string Name,
    string ParentName,
    string ImagePath,
    DateTime? CreatedAt,
    string? Aumid,
    string? PackageFamily,
    string Integrity,
    bool? IsAppContainer,
    string? AppContainerSid,
    bool? IsElevated,
    string Architecture,
    string Mitigations,
    string HandleRights,
    IReadOnlyList<string> InterestingModules,
    int ModuleCount,
    string ModuleScanNote,
    IReadOnlyList<WindowEntry> Windows);

internal static class ProcessProbe
{
    private static readonly string[] InterestingModuleHints =
    [
        "gameoverlayrenderer",
        "steamclient",
        "steam_api",
        "steamoverlayvulkanlayer",
        "rtsshooks",
    ];

    // ---- Snapshot ----

    internal static IReadOnlyList<ProcessEntry> Snapshot()
    {
        var list = new List<ProcessEntry>(512);
        var snapshot = Native.CreateToolhelp32Snapshot(Native.Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return list;
        }

        try
        {
            var entry = default(Native.ProcessEntry32W);
            entry.dwSize = (uint)Marshal.SizeOf<Native.ProcessEntry32W>();
            if (!Native.Process32FirstW(snapshot, ref entry))
            {
                return list;
            }

            do
            {
                list.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? string.Empty));
            }
            while (Native.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }

        return list;
    }

    /// Transitive children of <paramref name="rootPid"/> inside one snapshot. Windows
    /// reuses process ids, so this is only ever read together with creation times.
    internal static HashSet<int> Descendants(IReadOnlyList<ProcessEntry> snapshot, int rootPid)
    {
        var byParent = new Dictionary<int, List<ProcessEntry>>();
        foreach (var entry in snapshot)
        {
            if (!byParent.TryGetValue(entry.ParentPid, out var bucket))
            {
                bucket = [];
                byParent[entry.ParentPid] = bucket;
            }

            bucket.Add(entry);
        }

        var found = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(rootPid);
        while (pending.Count > 0)
        {
            var pid = pending.Dequeue();
            if (!found.Add(pid) || !byParent.TryGetValue(pid, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Enqueue(child.Pid);
            }
        }

        found.Remove(rootPid);
        found.Add(rootPid);
        return found;
    }

    /// The ancestor chain from this process upwards, which is how the transcript shows
    /// whether Steam is our direct parent.
    internal static IReadOnlyList<ProcessEntry> AncestorChain(IReadOnlyList<ProcessEntry> snapshot, int pid)
    {
        var byPid = snapshot.ToDictionary(entry => entry.Pid);
        var chain = new List<ProcessEntry>();
        var seen = new HashSet<int>();
        var current = pid;
        while (byPid.TryGetValue(current, out var entry) && seen.Add(current))
        {
            chain.Add(entry);
            if (entry.ParentPid == 0 || entry.ParentPid == current)
            {
                break;
            }

            current = entry.ParentPid;
        }

        return chain;
    }

    // ---- Full report ----

    internal static ProcessReport Describe(ProcessEntry entry, string parentName, bool probeRights)
    {
        var limited = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, (uint)entry.Pid);
        var full = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, (uint)entry.Pid);
        try
        {
            var query = limited != IntPtr.Zero ? limited : full;
            var imagePath = query != IntPtr.Zero ? ImagePath(query) : "<no query handle>";
            var created = query != IntPtr.Zero ? CreationTime(query) : null;
            var aumid = query != IntPtr.Zero ? Aumid(query) : null;
            var family = query != IntPtr.Zero ? PackageFamily(query) : null;
            var architecture = query != IntPtr.Zero ? Architecture(query) : "unknown";
            var (integrity, appContainer, appContainerSid, elevated) = TokenFacts(query);
            var mitigations = full != IntPtr.Zero ? Mitigations(full) : "<needs PROCESS_QUERY_INFORMATION>";
            var (modules, moduleCount, note) = Modules(full);
            var rights = probeRights ? HandleRights(entry.Pid) : "<not probed>";

            return new ProcessReport(
                entry.Pid,
                entry.ParentPid,
                entry.Name,
                parentName,
                imagePath,
                created,
                aumid,
                family,
                integrity,
                appContainer,
                appContainerSid,
                elevated,
                architecture,
                mitigations,
                rights,
                modules,
                moduleCount,
                note,
                Windows(entry.Pid));
        }
        finally
        {
            if (limited != IntPtr.Zero) { Native.CloseHandle(limited); }
            if (full != IntPtr.Zero) { Native.CloseHandle(full); }
        }
    }

    internal static void WriteReport(SpikeLog log, string heading, ProcessReport report)
    {
        log.Info($"{heading}: pid {report.Pid} \"{report.Name}\"");
        log.Line($"          parent      : {report.ParentPid} \"{report.ParentName}\"");
        log.Line($"          image       : {report.ImagePath}");
        log.Line($"          created     : {report.CreatedAt?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "unknown"}");
        log.Line($"          aumid       : {report.Aumid ?? "<none>"}");
        log.Line($"          package     : {report.PackageFamily ?? "<none>"}");
        log.Line($"          integrity   : {report.Integrity}   elevated: {Describe(report.IsElevated)}   appcontainer: {Describe(report.IsAppContainer)}{(report.AppContainerSid is null ? string.Empty : " " + report.AppContainerSid)}");
        log.Line($"          arch        : {report.Architecture}");
        log.Line($"          mitigations : {report.Mitigations}");
        log.Line($"          handle      : {report.HandleRights}");
        log.Line($"          modules     : {report.ModuleCount} loaded ({report.ModuleScanNote})");
        if (report.InterestingModules.Count > 0)
        {
            foreach (var module in report.InterestingModules)
            {
                log.Line($"            * {module}");
            }
        }

        if (report.Windows.Count > 0)
        {
            foreach (var window in report.Windows.Take(8))
            {
                log.Line($"          window      : 0x{window.Handle.ToInt64():X} [{window.ClassName}] visible={window.Visible} \"{window.Title}\"");
            }
        }
    }

    private static string Describe(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        _ => "unknown",
    };

    // ---- Individual facts ----

    private static string ImagePath(IntPtr process)
    {
        var size = (uint)32767;
        var buffer = new StringBuilder((int)size);
        return Native.QueryFullProcessImageNameW(process, 0, buffer, ref size)
            ? buffer.ToString()
            : $"<denied: {Marshal.GetLastWin32Error()}>";
    }

    private static DateTime? CreationTime(IntPtr process) =>
        Native.GetProcessTimes(process, out var creation, out _, out _, out _)
            ? DateTime.FromFileTime(creation)
            : null;

    private static string? Aumid(IntPtr process)
    {
        uint length = 0;
        var status = Native.GetApplicationUserModelId(process, ref length, null);
        if (status != Native.ErrorInsufficientBuffer)
        {
            return status is Native.AppModelErrorNoPackage or Native.AppModelErrorNoApplication
                ? null
                : $"<error {status}>";
        }

        var buffer = new StringBuilder((int)length);
        status = Native.GetApplicationUserModelId(process, ref length, buffer);
        return status == Native.ErrorSuccess ? buffer.ToString() : $"<error {status}>";
    }

    internal static string? PackageFamily(IntPtr process)
    {
        uint length = 0;
        var status = Native.GetPackageFamilyName(process, ref length, null);
        if (status != Native.ErrorInsufficientBuffer)
        {
            // APPMODEL_ERROR_NO_PACKAGE for every unpackaged process; anything else is
            // an access problem this probe treats the same way.
            return null;
        }

        var buffer = new StringBuilder((int)length);
        status = Native.GetPackageFamilyName(process, ref length, buffer);
        return status == Native.ErrorSuccess ? buffer.ToString() : null;
    }

    /// Package family of a pid, using only the limited rights an unelevated wrapper
    /// reliably gets. Returns null for every unpackaged process.
    internal static string? PackageFamilyOf(int pid)
    {
        var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return PackageFamily(process);
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static string Architecture(IntPtr process)
    {
        if (!Native.IsWow64Process2(process, out var processMachine, out var nativeMachine))
        {
            return "unknown";
        }

        // IMAGE_FILE_MACHINE_UNKNOWN means "not running under WOW64", i.e. native.
        return processMachine == 0 ? Machine(nativeMachine) + " (native)" : Machine(processMachine) + " on " + Machine(nativeMachine);
    }

    private static string Machine(ushort value) => value switch
    {
        0x014c => "x86",
        0x8664 => "x64",
        0xAA64 => "arm64",
        0x01c4 => "armnt",
        0 => "unknown",
        _ => "0x" + value.ToString("X4", CultureInfo.InvariantCulture),
    };

    private static (string Integrity, bool? AppContainer, string? Sid, bool? Elevated) TokenFacts(IntPtr process)
    {
        if (process == IntPtr.Zero || !Native.OpenProcessToken(process, Native.TokenQuery, out var token))
        {
            return ($"<denied: {Marshal.GetLastWin32Error()}>", null, null, null);
        }

        try
        {
            return (IntegrityLevel(token), Flag(token, Native.TokenIsAppContainer), AppContainerSid(token), Flag(token, Native.TokenElevation));
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    private static string IntegrityLevel(IntPtr token)
    {
        var buffer = QueryToken(token, Native.TokenIntegrityLevel);
        if (buffer == IntPtr.Zero)
        {
            return "<unavailable>";
        }

        try
        {
            var label = Marshal.PtrToStructure<Native.SidAndAttributes>(buffer);
            var countPointer = Native.GetSidSubAuthorityCount(label.Sid);
            if (countPointer == IntPtr.Zero)
            {
                return "<unavailable>";
            }

            var countValue = Marshal.ReadByte(countPointer);
            var last = Native.GetSidSubAuthority(label.Sid, (uint)(countValue - 1));
            var rid = (uint)Marshal.ReadInt32(last);
            return rid switch
            {
                < 0x1000 => $"untrusted (0x{rid:X})",
                < 0x2000 => $"low (0x{rid:X})",
                < 0x3000 => $"medium (0x{rid:X})",
                < 0x4000 => $"high (0x{rid:X})",
                _ => $"system+ (0x{rid:X})",
            };
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? AppContainerSid(IntPtr token)
    {
        var buffer = QueryToken(token, Native.TokenAppContainerSid);
        if (buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var info = Marshal.PtrToStructure<Native.SidAndAttributes>(buffer);
            if (info.Sid == IntPtr.Zero || !Native.ConvertSidToStringSidW(info.Sid, out var text))
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(text);
            }
            finally
            {
                Native.LocalFree(text);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool? Flag(IntPtr token, int informationClass)
    {
        var buffer = QueryToken(token, informationClass);
        if (buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr QueryToken(IntPtr token, int informationClass)
    {
        Native.GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var needed);
        if (needed == 0)
        {
            return IntPtr.Zero;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        if (Native.GetTokenInformation(token, informationClass, buffer, needed, out _))
        {
            return buffer;
        }

        Marshal.FreeHGlobal(buffer);
        return IntPtr.Zero;
    }

    private static string Mitigations(IntPtr process)
    {
        var parts = new List<string>();
        parts.Add(Policy(process, Native.ProcessSignaturePolicy, "signature", value =>
        {
            var flags = new List<string>();
            if ((value & 0x1) != 0) { flags.Add("MicrosoftSignedOnly"); }
            if ((value & 0x2) != 0) { flags.Add("StoreSignedOnly"); }
            if ((value & 0x4) != 0) { flags.Add("MitigationOptIn"); }
            return flags;
        }));
        parts.Add(Policy(process, Native.ProcessDynamicCodePolicy, "dynamic-code", value =>
        {
            var flags = new List<string>();
            if ((value & 0x1) != 0) { flags.Add("ProhibitDynamicCode"); }
            if ((value & 0x2) != 0) { flags.Add("AllowThreadOptOut"); }
            if ((value & 0x4) != 0) { flags.Add("AllowRemoteDowngrade"); }
            return flags;
        }));
        parts.Add(Policy(process, Native.ProcessImageLoadPolicy, "image-load", value =>
        {
            var flags = new List<string>();
            if ((value & 0x1) != 0) { flags.Add("NoRemoteImages"); }
            if ((value & 0x2) != 0) { flags.Add("NoLowMandatoryLabelImages"); }
            if ((value & 0x4) != 0) { flags.Add("PreferSystem32Images"); }
            return flags;
        }));
        parts.Add(Policy(process, Native.ProcessExtensionPointDisablePolicy, "extension-point", value =>
            (value & 0x1) != 0 ? ["DisableExtensionPoints"] : []));

        return string.Join("; ", parts);
    }

    private static string Policy(IntPtr process, int policy, string label, Func<uint, List<string>> decode)
    {
        if (!Native.GetProcessMitigationPolicy(process, policy, out var value, new IntPtr(sizeof(uint))))
        {
            return $"{label}=<err {Marshal.GetLastWin32Error()}>";
        }

        var flags = decode(value);
        return flags.Count == 0 ? $"{label}=none" : $"{label}={string.Join(",", flags)}";
    }

    private static (IReadOnlyList<string> Interesting, int Count, string Note) Modules(IntPtr process)
    {
        if (process == IntPtr.Zero)
        {
            return ([], 0, "no PROCESS_QUERY_INFORMATION|PROCESS_VM_READ handle");
        }

        var handles = new IntPtr[2048];
        if (!Native.K32EnumProcessModulesEx(process, handles, (uint)(handles.Length * IntPtr.Size), out var needed, Native.ListModulesAll))
        {
            return ([], 0, $"EnumProcessModulesEx failed ({Marshal.GetLastWin32Error()})");
        }

        var count = Math.Min(handles.Length, (int)(needed / IntPtr.Size));
        var interesting = new List<string>();
        var buffer = new StringBuilder(Native.MaxPath * 4);
        for (var index = 0; index < count; index++)
        {
            buffer.Clear();
            if (Native.K32GetModuleFileNameExW(process, handles[index], buffer, (uint)buffer.Capacity) == 0)
            {
                continue;
            }

            var path = buffer.ToString();
            var name = path.AsSpan(path.LastIndexOf('\\') + 1).ToString();
            foreach (var hint in InterestingModuleHints)
            {
                if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    interesting.Add(path);
                    break;
                }
            }
        }

        return (interesting, count, "ok");
    }

    /// Which access masks this wrapper can actually obtain on the target. The point is
    /// to separate "Steam never tried" from "Windows would not have let it".
    private static string HandleRights(int pid)
    {
        (string Label, uint Access)[] masks =
        [
            ("QUERY_LIMITED", Native.ProcessQueryLimitedInformation),
            ("QUERY", Native.ProcessQueryInformation),
            ("VM_READ", Native.ProcessVmRead),
            ("VM_WRITE|VM_OPERATION", Native.ProcessVmWrite | Native.ProcessVmOperation),
            ("CREATE_THREAD", Native.ProcessCreateThread),
            ("DUP_HANDLE", Native.ProcessDupHandle),
            ("INJECTOR_SET", Native.InjectorAccess),
            ("ALL_ACCESS", Native.ProcessAllAccess),
        ];

        var results = new List<string>(masks.Length);
        foreach (var (label, access) in masks)
        {
            var handle = Native.OpenProcess(access, false, (uint)pid);
            if (handle != IntPtr.Zero)
            {
                Native.CloseHandle(handle);
                results.Add(label + "=ok");
            }
            else
            {
                results.Add($"{label}=denied({Marshal.GetLastWin32Error()})");
            }
        }

        return string.Join(" ", results);
    }

    private static IReadOnlyList<WindowEntry> Windows(int pid)
    {
        var found = new List<WindowEntry>();
        Native.EnumWindows((handle, _) =>
        {
            Native.GetWindowThreadProcessId(handle, out var owner);
            if (owner != (uint)pid)
            {
                return true;
            }

            var className = new StringBuilder(256);
            Native.GetClassNameW(handle, className, className.Capacity);
            var title = new StringBuilder(512);
            Native.GetWindowTextW(handle, title, title.Capacity);
            found.Add(new WindowEntry(handle, className.ToString(), title.ToString(), Native.IsWindowVisible(handle)));
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

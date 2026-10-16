using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Setup.Engine;

/// <summary>Releases actual users of the exact installation files setup will replace.</summary>
internal static partial class SetupFileOwners
{
    internal readonly record struct Owner(uint ProcessId, ulong Started, string Name, uint ApplicationType);

    /// <summary>A held process handle, so a reused PID can never receive the termination.</summary>
    internal interface IProcess : IDisposable
    {
        ulong Started { get; }
        bool Critical { get; }
        string Image { get; }
        bool Stop(TimeSpan budget);
    }

    internal static string? Release(IReadOnlyList<string> paths)
    {
        return Release(paths, (uint)Environment.ProcessId, ScanPaths, Open, WindowsSetup.ShellAnchorRecoverySettled);
    }

    internal static string? Release(IReadOnlyList<string> paths, uint installer,
        Func<IReadOnlyList<string>, IReadOnlyList<Owner>> scan, Func<Owner, IProcess?> open,
        Func<bool>? anchorSettled = null)
    {
        var locations = string.Join(", ", paths);
        try
        {
            var files = EnumeratePaths(paths);
            if (files.Count == 0)
            {
                return null;
            }

            SetupLog.Info($"Scanning {files.Count} replacement files and directories under: {locations}");

            foreach (var owner in scan(files).Distinct())
            {
                SetupLog.Info($"File usage: {owner.Name}, PID {owner.ProcessId}, started {owner.Started}.");
                if (owner.ProcessId == installer)
                {
                    // Stored setup can be our own executing image. Its existing rename/deferred
                    // deletion path owns that image; setup must never terminate itself.
                    continue;
                }

                if (owner.ProcessId <= 4 || owner.ApplicationType is 1000)
                {
                    return Refusal(owner, "Windows protects this process", locations);
                }

                using var process = open(owner);
                if (process is null || process.Started != owner.Started)
                {
                    // It exited or the PID now identifies somebody else. Only a fresh scan can
                    // establish whether that new process also owns a required file.
                    continue;
                }

                if (process.Critical)
                {
                    return Refusal(owner, "Windows marks this process as critical", locations);
                }

                var image = process.Image;
                if (string.Equals(Path.GetFileName(image), "WSGM.ShellAnchor.exe",
                        StringComparison.OrdinalIgnoreCase) && anchorSettled?.Invoke() != true)
                {
                    return Refusal(owner, "the shell anchor has not confirmed Explorer recovery", locations);
                }

                SetupLog.Info($"Ending file owner {owner.Name}, PID {owner.ProcessId}, image {image}.");
                if (!process.Stop(TimeSpan.FromSeconds(10)))
                {
                    return Refusal(owner, "it did not exit within 10 seconds", locations);
                }
            }

            var remaining = scan(files).Where(owner => owner.ProcessId != installer).ToArray();
            return remaining.Length == 0
                ? null
                : "WSGM files are still in use by "
                  + string.Join(", ", remaining.Select(owner => $"{owner.Name} (PID {owner.ProcessId})"))
                  + $". Required paths: {locations}. Close these programs and run setup again. "
                  + "Setup stopped before changing these paths.";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            SetupLog.Error("Could not release the files setup must replace", ex);
            return "Setup could not release WSGM's files: " + ex.Message
                   + $" Required paths: {locations}. Close programs using WSGM and run setup again. "
                   + "Setup stopped before changing these paths.";
        }
    }

    internal static IReadOnlyList<string> EnumeratePaths(IReadOnlyList<string> paths)
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var absolute = Path.GetFullPath(path);
            // A file itself can be ordinary while a parent junction redirects it outside the
            // installation. Refuse that path before registering any resources with Windows.
            for (var parent = Path.GetDirectoryName(absolute); parent is not null;
                 parent = Path.GetDirectoryName(parent))
            {
                if (Directory.Exists(parent)
                    && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Setup cannot scan a linked installation path: {parent}");
                }
            }

            Add(absolute);
        }

        return [.. files];

        void Add(string path)
        {
            if (!Path.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Setup cannot scan a linked installation path: {path}");
            }

            files.Add(path);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                Add(entry);
            }
        }
    }

    private static string Refusal(Owner owner, string reason, string locations)
    {
        return $"WSGM files are in use by {owner.Name} (PID {owner.ProcessId}); {reason}. "
               + $"Required paths: {locations}. Close it or restart Windows, then run setup again. "
               + "Setup stopped before changing these paths.";
    }

    private static IReadOnlyList<Owner> ScanPaths(IReadOnlyList<string> paths)
    {
        // Restart Manager rejects directories, so a file-only scan misses an empty folder,
        // a working directory or a directory watcher that prevents the App rename/delete.
        var files = paths.Where(File.Exists).ToArray();
        List<Owner> owners = files.Length == 0 ? [] : [.. Scan(files)];
        foreach (var directory in paths.Where(Directory.Exists))
        {
            foreach (var processId in DirectoryOwners(directory))
            {
                if (owners.Any(owner => owner.ProcessId == processId))
                {
                    continue;
                }

                var handle = OpenProcess(0x1000, false, processId);
                if (handle == 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 87)
                    {
                        continue;
                    }

                    throw new Win32Exception(error, $"Directory owner PID {processId}: {directory}");
                }

                using HeldProcess process = new(handle);
                owners.Add(new Owner(processId, process.Started, Path.GetFileName(process.Image), 0));
            }
        }

        return owners;
    }

    private static unsafe IReadOnlyList<uint> DirectoryOwners(string directory)
    {
        // FILE_READ_ATTRIBUTES, all sharing, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS.
        using var handle = CreateFileW(directory, 0x80, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot inspect directory {directory}");
        }

        for (var size = 4096; size <= 1024 * 1024; size *= 2)
        {
            var buffer = new byte[size];
            fixed (byte* information = buffer)
            {
                // FileProcessIdsUsingFileInformation: ULONG count followed by aligned ULONG_PTR IDs.
                var status = NtQueryInformationFile(handle, out _, information, (uint)size, 47);
                if (status is unchecked((int)0x80000005) or unchecked((int)0xC0000004))
                {
                    continue;
                }

                if (status < 0)
                {
                    throw new Win32Exception((int)RtlNtStatusToDosError(status),
                        $"Cannot identify directory owners: {directory}");
                }

                var count = *(uint*)information;
                if (count > (size - IntPtr.Size) / IntPtr.Size)
                {
                    throw new IOException($"Windows returned an invalid directory-owner list: {directory}");
                }

                var identifiers = (nuint*)(information + IntPtr.Size);
                List<uint> result = [];
                for (var index = 0; index < count; index++)
                {
                    result.Add(checked((uint)identifiers[index]));
                }

                return result;
            }
        }

        throw new IOException($"The directory-owner list is too large: {directory}");
    }

    private static unsafe IReadOnlyList<Owner> Scan(IReadOnlyList<string> files)
    {
        var key = stackalloc char[33];
        Check(RmStartSession(out var session, 0, key));
        try
        {
            var names = new nint[files.Count];
            try
            {
                for (var index = 0; index < names.Length; index++)
                {
                    names[index] = Marshal.StringToCoTaskMemUni(files[index]);
                }

                fixed (nint* pointers = names)
                {
                    Check(RmRegisterResources(session, (uint)names.Length, pointers, 0, null, 0, null));
                }
            }
            finally
            {
                foreach (var name in names)
                {
                    Marshal.FreeCoTaskMem(name);
                }
            }

            uint count = 0;
            var result = RmGetList(session, out var needed, ref count, null, out _);
            for (var attempt = 0; result == 234 && attempt < 4; attempt++)
            {
                var information = new ProcessInfo[needed];
                count = needed;
                fixed (ProcessInfo* entries = information)
                {
                    result = RmGetList(session, out needed, ref count, entries, out _);
                    if (result == 0)
                    {
                        List<Owner> owners = [];
                        for (var index = 0; index < count; index++)
                        {
                            var entry = entries + index;
                            owners.Add(new Owner(entry->ProcessId,
                                ((ulong)entry->TimeHigh << 32) | entry->TimeLow,
                                new string(entry->Name), entry->ApplicationType));
                        }

                        return owners;
                    }
                }
            }

            Check(result);
            return [];
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private static IProcess? Open(Owner owner)
    {
        const uint queryLimited = 0x1000;
        const uint terminate = 0x0001;
        const uint synchronize = 0x00100000;
        var handle = OpenProcess(queryLimited | terminate | synchronize, false, owner.ProcessId);
        if (handle != 0)
        {
            return new HeldProcess(handle);
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == 87)
        {
            return null;
        }

        throw new Win32Exception(error, $"{owner.Name} (PID {owner.ProcessId}) could not be opened");
    }

    private static void Check(uint result)
    {
        if (result != 0)
        {
            throw new Win32Exception((int)result);
        }
    }

    private sealed class HeldProcess(nint handle) : IProcess
    {
        public unsafe string Image
        {
            get
            {
                const int capacity = 32768;
                var image = new char[capacity];
                uint length = capacity;
                fixed (char* buffer = image)
                {
                    if (!QueryFullProcessImageNameW(handle, 0, buffer, ref length))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }
                }

                return new string(image, 0, (int)length);
            }
        }

        public ulong Started
        {
            get
            {
                if (!GetProcessTimes(handle, out var started, out _, out _, out _))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return started;
            }
        }

        public bool Critical
        {
            get
            {
                if (!IsProcessCritical(handle, out var critical))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return critical;
            }
        }

        public bool Stop(TimeSpan budget)
        {
            if (WaitForSingleObject(handle, 0) == 0)
            {
                return true;
            }

            if (!TerminateProcess(handle, 1))
            {
                var error = Marshal.GetLastPInvokeError();
                if (WaitForSingleObject(handle, 0) == 0)
                {
                    return true;
                }

                throw new Win32Exception(error);
            }

            return WaitForSingleObject(handle, (uint)budget.TotalMilliseconds) == 0;
        }

        public void Dispose()
        {
            _ = CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessInfo
    {
        internal uint ProcessId;
        internal uint TimeLow;
        internal uint TimeHigh;
        internal fixed char Name[256];
        internal fixed char Service[64];
        internal uint ApplicationType;
        internal uint Status;
        internal uint Session;
        internal int Restartable;
    }

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial uint RmStartSession(out uint session, uint flags, char* key);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial uint RmRegisterResources(uint session, uint files, nint* names,
        uint applications, void* processes, uint services, nint* serviceNames);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial uint RmGetList(uint session, out uint needed, ref uint count,
        ProcessInfo* processes, out uint rebootReasons);

    [LibraryImport("rstrtmgr.dll")]
    private static partial uint RmEndSession(uint session);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal nint Status;
        internal nuint Information;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status,
        void* information, uint length, uint informationClass);

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint handle, out ulong started, out ulong exited,
        out ulong kernel, out ulong user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint handle, uint flags, char* image, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessCritical(nint handle, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint handle, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

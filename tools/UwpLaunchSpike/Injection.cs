using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Wsgm.UwpSpike;

/// Remote DLL load into the discovered game, with enough reporting to tell the three
/// failure modes apart: no handle rights, the AppContainer cannot read the file, or the
/// load itself was refused.
///
/// The AppContainer case is the one that catches people out. A low-integrity packaged
/// process can only map a file whose ACL admits its package, so a DLL sitting in a normal
/// Program Files directory is unreadable to it no matter what rights the injector holds.
/// That is why the ReShade UWP route stages its DLL and grants
/// <c>ALL APPLICATION PACKAGES</c> read and execute on it. This does the same, on a copy,
/// so nothing in Steam's own installation is modified.
internal sealed class Injection(SpikeLog log)
{
    // ALL APPLICATION PACKAGES. Used in SID form because the display name is localised.
    private const string AllApplicationPackagesSid = "S-1-15-2-1";

    private readonly HashSet<string> attempted = new(StringComparer.OrdinalIgnoreCase);

    internal void InjectAll(IReadOnlyList<string> dlls, ProcessReport target)
    {
        foreach (var dll in dlls)
        {
            var key = $"{target.Pid}|{dll}";
            if (!attempted.Add(key))
            {
                continue;
            }

            Inject(dll, target);
        }
    }

    private void Inject(string dll, ProcessReport target)
    {
        log.Section($"Injection: {Path.GetFileName(dll)} -> pid {target.Pid} \"{target.Name}\"");

        var full = Path.GetFullPath(dll);
        if (!File.Exists(full))
        {
            log.Error($"injection: {full} does not exist.");
            return;
        }

        log.Info($"injection: source {full} ({new FileInfo(full).Length} bytes, machine {PeMachine(full)})");
        log.Info($"injection: target is {(target.IsAppContainer == true ? "an AppContainer" : "not an AppContainer")} at integrity {target.Integrity}");
        ReportAcl(full);

        if (TryLoad(full, target, "original path"))
        {
            return;
        }

        // Second attempt: a staged copy the package is allowed to read. If the first
        // attempt failed only because of the file ACL, this one succeeds and the
        // difference between the two is the finding.
        var staged = Stage(full);
        if (staged is null)
        {
            return;
        }

        TryLoad(staged, target, "staged copy readable by ALL APPLICATION PACKAGES");
    }

    private bool TryLoad(string dll, ProcessReport target, string label)
    {
        log.Info($"injection: attempt via {label}: {dll}");

        var process = Native.OpenProcess(Native.InjectorAccess, false, (uint)target.Pid);
        if (process == IntPtr.Zero)
        {
            log.Error($"injection: OpenProcess(injector rights) failed (error {Marshal.GetLastWin32Error()}). "
                + "The obstacle is process access, not the file.");
            return false;
        }

        var remote = IntPtr.Zero;
        var thread = IntPtr.Zero;
        try
        {
            var kernel32 = Native.GetModuleHandleW("kernel32.dll");
            var loadLibrary = kernel32 == IntPtr.Zero ? IntPtr.Zero : Native.GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
            {
                log.Error($"injection: could not resolve LoadLibraryW (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            var bytes = Encoding.Unicode.GetBytes(dll + "\0");
            remote = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)bytes.Length, Native.MemCommit | Native.MemReserve, Native.PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                log.Error($"injection: VirtualAllocEx failed (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            if (!Native.WriteProcessMemory(process, remote, bytes, (UIntPtr)bytes.Length, out _))
            {
                log.Error($"injection: WriteProcessMemory failed (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            thread = Native.CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                log.Error($"injection: CreateRemoteThread failed (error {Marshal.GetLastWin32Error()}). "
                    + "This is the signature of a process that refuses foreign threads.");
                return false;
            }

            var wait = Native.WaitForSingleObject(thread, 15_000);
            if (wait != 0)
            {
                log.Warn($"injection: the remote LoadLibraryW thread did not finish (wait result 0x{wait:X}).");
                return false;
            }

            Native.GetExitCodeThread(thread, out var exitCode);

            // The exit code is the low 32 bits of the returned HMODULE, so it proves a
            // load happened but not where; zero means LoadLibraryW refused the file.
            if (exitCode == 0)
            {
                log.Error("injection: LoadLibraryW returned NULL inside the game. The process could not "
                    + "load this file - unreadable to the package, wrong architecture, or blocked by policy.");
                return false;
            }

            log.Info($"injection: LoadLibraryW returned 0x{exitCode:X8} inside the game.");
            log.Info("injection: SUCCEEDED via " + label);
            return true;
        }
        finally
        {
            if (thread != IntPtr.Zero) { Native.CloseHandle(thread); }
            if (remote != IntPtr.Zero) { Native.VirtualFreeEx(process, remote, UIntPtr.Zero, Native.MemRelease); }
            Native.CloseHandle(process);
        }
    }

    /// Calls a zero-argument export inside the game.
    ///
    /// This is the step that a LoadLibrary cannot cover. Balatro's working overlay comes from
    /// the game itself calling SteamAPI_Init, which loads steamclient64.dll and registers the
    /// process with the running client; both the overlay and Steam Input hang off that
    /// registration. A packaged title that is not a Steam build never makes that call, so
    /// mapping Steam's DLLs into it achieves nothing on its own. CreateRemoteThread can only
    /// pass a single pointer argument, which is exactly enough for an export that takes none.
    internal void CallExport(string dll, string export, ProcessReport target)
    {
        log.Section($"Remote call: {Path.GetFileName(dll)}!{export} in pid {target.Pid}");

        var remoteBase = RemoteModuleBase(target.Pid, Path.GetFileName(dll));
        if (remoteBase == IntPtr.Zero)
        {
            log.Error($"remote call: {Path.GetFileName(dll)} is not loaded in pid {target.Pid}; inject it first.");
            return;
        }

        // The export's address is found locally and translated by relative virtual address,
        // because the same image is mapped at a different base in the target.
        var local = Native.LoadLibraryW(dll);
        if (local == IntPtr.Zero)
        {
            log.Error($"remote call: could not load {dll} locally to resolve {export} (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        IntPtr remoteExport;
        try
        {
            var localExport = Native.GetProcAddress(local, export);
            if (localExport == IntPtr.Zero)
            {
                log.Error($"remote call: {dll} has no export named {export} (error {Marshal.GetLastWin32Error()}).");
                return;
            }

            var rva = localExport.ToInt64() - local.ToInt64();
            remoteExport = new IntPtr(remoteBase.ToInt64() + rva);
            log.Info($"remote call: {export} at rva 0x{rva:X} -> remote 0x{remoteExport.ToInt64():X} (remote base 0x{remoteBase.ToInt64():X})");
        }
        finally
        {
            Native.FreeLibrary(local);
        }

        var process = Native.OpenProcess(Native.InjectorAccess, false, (uint)target.Pid);
        if (process == IntPtr.Zero)
        {
            log.Error($"remote call: OpenProcess failed (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        var thread = IntPtr.Zero;
        try
        {
            thread = Native.CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, remoteExport, IntPtr.Zero, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                log.Error($"remote call: CreateRemoteThread failed (error {Marshal.GetLastWin32Error()}).");
                return;
            }

            var wait = Native.WaitForSingleObject(thread, 20_000);
            if (wait != 0)
            {
                log.Warn($"remote call: {export} did not return within 20s (wait 0x{wait:X}). It may be blocking on the Steam pipe.");
                return;
            }

            Native.GetExitCodeThread(thread, out var exitCode);
            log.Info($"remote call: {export} returned {exitCode} (0x{exitCode:X8}). "
                + (exitCode == 0 ? "Zero is failure for SteamAPI_Init." : "Non-zero is success for SteamAPI_Init."));
        }
        finally
        {
            if (thread != IntPtr.Zero) { Native.CloseHandle(thread); }
            Native.CloseHandle(process);
        }
    }

    private static IntPtr RemoteModuleBase(int pid, string fileName)
    {
        var process = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            var handles = new IntPtr[4096];
            if (!Native.K32EnumProcessModulesEx(process, handles, (uint)(handles.Length * IntPtr.Size), out var needed, Native.ListModulesAll))
            {
                return IntPtr.Zero;
            }

            var count = Math.Min(handles.Length, (int)(needed / IntPtr.Size));
            var buffer = new StringBuilder(Native.MaxPath * 4);
            for (var index = 0; index < count; index++)
            {
                buffer.Clear();
                if (Native.K32GetModuleFileNameExW(process, handles[index], buffer, (uint)buffer.Capacity) == 0)
                {
                    continue;
                }

                var path = buffer.ToString();
                if (Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return handles[index];
                }
            }

            return IntPtr.Zero;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private string? Stage(string source)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WSGM",
                "uwp-spike",
                "inject");
            Directory.CreateDirectory(directory);
            var staged = Path.Combine(directory, Path.GetFileName(source));
            File.Copy(source, staged, overwrite: true);
            log.Info($"injection: staged a copy at {staged}");

            // Grant on the containing directory as well: the loader has to traverse it.
            Grant(directory, "(OI)(CI)(RX)");
            Grant(staged, "(RX)");
            ReportAcl(staged);
            return staged;
        }
        catch (Exception ex)
        {
            log.Error($"injection: could not stage a readable copy: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void Grant(string path, string rights)
    {
        var result = RunIcacls([path, "/grant", $"*{AllApplicationPackagesSid}:{rights}"]);
        log.Info($"injection: icacls grant on {path} -> {result}");
    }

    private void ReportAcl(string path)
    {
        var result = RunIcacls([path]);
        log.Info($"injection: acl of {path}:");
        foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            log.Line("            " + line.TrimEnd());
        }
    }

    private static string RunIcacls(string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("icacls.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return "icacls could not be started";
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);
            return output.Trim();
        }
        catch (Exception ex)
        {
            return $"icacls failed: {ex.Message}";
        }
    }

    /// The PE machine field, so an architecture mismatch is reported as such instead of
    /// showing up as an unexplained NULL from LoadLibraryW.
    private static string PeMachine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D)
            {
                return "not a PE file";
            }

            stream.Position = 0x3C;
            stream.Position = reader.ReadUInt32();
            if (reader.ReadUInt32() != 0x0000_4550)
            {
                return "no PE signature";
            }

            return reader.ReadUInt16() switch
            {
                0x8664 => "x64",
                0x014c => "x86",
                0xAA64 => "arm64",
                var other => $"0x{other:X4}",
            };
        }
        catch (Exception ex)
        {
            return $"unreadable ({ex.Message})";
        }
    }
}

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

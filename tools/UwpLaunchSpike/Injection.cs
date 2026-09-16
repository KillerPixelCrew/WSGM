using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Wsgm.UwpSpike;

/// Remote DLL load into the discovered game, with enough reporting to tell the three
/// failure modes apart: no handle rights, the AppContainer cannot read the file, or the
/// load itself was refused.
///
/// The AppContainer case is the one that catches people out. A low-integrity packaged
/// process needs file access under its package token. The injector's own file access does
/// not establish that the game can load the same path.
/// That is why the ReShade UWP route stages its DLL and grants
/// <c>ALL APPLICATION PACKAGES</c> read and execute on it. This does the same, on a copy,
/// so nothing in Steam's own installation is modified.
internal sealed class Injection(SpikeLog log)
{
    // ALL APPLICATION PACKAGES. Used in SID form because the display name is localised.
    private const string AllApplicationPackagesSid = "S-1-15-2-1";

    private readonly HashSet<string> attempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> pending = [];

    internal void InjectAll(IReadOnlyList<string> dlls, ProcessReport target)
    {
        foreach (var dll in dlls)
        {
            if (pending.Contains(target.Pid)) { break; }
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

        if (pending.Contains(target.Pid)) { return; }

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
        var running = false;
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
                running = true;
                pending.Add(target.Pid);
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
            if (remote != IntPtr.Zero && !running) { Native.VirtualFreeEx(process, remote, UIntPtr.Zero, Native.MemRelease); }
            Native.CloseHandle(process);
        }
    }

    /// Calls a zero-argument export inside the game.
    ///
    /// Used for bridge initialization and explicit comparison exports. Return conventions
    /// belong to the named export: zero means success for the bridge, while SteamAPI_Init
    /// uses a Boolean result. Overlay registration does not inherently require SteamAPI_Init.
    internal uint? CallExport(string dll, string export, ProcessReport target)
    {
        log.Section($"Remote call: {Path.GetFileName(dll)}!{export} in pid {target.Pid}");

        var remoteBase = RemoteModuleBase(target.Pid, Path.GetFileName(dll));
        if (remoteBase == IntPtr.Zero)
        {
            log.Error($"remote call: {Path.GetFileName(dll)} is not loaded in pid {target.Pid}; inject it first.");
            return null;
        }

        // The export's address is found locally and translated by relative virtual address,
        // because the same image is mapped at a different base in the target.
        var local = Native.LoadLibraryW(dll);
        if (local == IntPtr.Zero)
        {
            log.Error($"remote call: could not load {dll} locally to resolve {export} (error {Marshal.GetLastWin32Error()}).");
            return null;
        }

        IntPtr remoteExport;
        try
        {
            var localExport = Native.GetProcAddress(local, export);
            if (localExport == IntPtr.Zero)
            {
                log.Error($"remote call: {dll} has no export named {export} (error {Marshal.GetLastWin32Error()}).");
                return null;
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
            return null;
        }

        var thread = IntPtr.Zero;
        try
        {
            thread = Native.CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, remoteExport, IntPtr.Zero, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                log.Error($"remote call: CreateRemoteThread failed (error {Marshal.GetLastWin32Error()}).");
                return null;
            }

            var wait = Native.WaitForSingleObject(thread, 20_000);
            if (wait != 0)
            {
                log.Warn($"remote call: {export} did not return within 20s (wait 0x{wait:X}). It may be blocking on the Steam pipe.");
                return null;
            }

            Native.GetExitCodeThread(thread, out var exitCode);
            log.Info($"remote call: {export} returned {exitCode} (0x{exitCode:X8}).");
            return exitCode;
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

    /// Sets environment variables inside the target by calling SetEnvironmentVariableW in
    /// it, one remote thread per variable.
    ///
    /// The alternative - replacing ProcessParameters->Environment wholesale - works on a
    /// settled process but kills a starting one about a second later, and starting is
    /// exactly when this has to happen: the overlay renderer hooks device creation, so it
    /// must be loaded before the game builds its swapchain, and it reads SteamGameId when
    /// it loads. Going through the API lets ntdll own the block's allocation as usual.
    ///
    /// CreateRemoteThread passes one argument and this function takes two, so the call is
    /// made by a small stub: the two pointers and the function address are baked in as
    /// immediates, around a standard x64 shadow-space frame.
    internal bool SetRemoteEnvironment(int pid, IReadOnlyList<string> variables)
    {
        if (variables.Count == 0)
        {
            return true;
        }

        log.Section($"Remote environment: pid {pid}");

        var kernel32 = Native.GetModuleHandleW("kernel32.dll");
        var setter = kernel32 == IntPtr.Zero ? IntPtr.Zero : Native.GetProcAddress(kernel32, "SetEnvironmentVariableW");
        if (setter == IntPtr.Zero)
        {
            log.Error($"remote env: could not resolve SetEnvironmentVariableW (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        var process = Native.OpenProcess(Native.InjectorAccess, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            log.Error($"remote env: OpenProcess failed (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        try
        {
            var applied = 0;
            foreach (var variable in variables)
            {
                var separator = variable.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                if (SetOne(process, setter, variable[..separator], variable[(separator + 1)..]))
                {
                    applied++;
                }
            }

            log.Info($"remote env: set {applied} of {variables.Count} variable(s) inside pid {pid}.");
            return applied == variables.Count;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private bool SetOne(IntPtr process, IntPtr setter, string name, string value)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name + "\0");
        var valueBytes = Encoding.Unicode.GetBytes(value + "\0");

        // Layout: [name][value][stub]. The strings go first so their addresses are known
        // before the stub that references them is built.
        var nameOffset = 0;
        var valueOffset = nameBytes.Length;
        var codeOffset = valueOffset + valueBytes.Length;
        var total = codeOffset + 64;

        var block = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)total, Native.MemCommit | Native.MemReserve, Native.PageExecuteReadWrite);
        if (block == IntPtr.Zero)
        {
            log.Error($"remote env: VirtualAllocEx failed for {name} (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        var thread = IntPtr.Zero;
        var running = false;
        try
        {
            var buffer = new byte[total];
            Array.Copy(nameBytes, 0, buffer, nameOffset, nameBytes.Length);
            Array.Copy(valueBytes, 0, buffer, valueOffset, valueBytes.Length);

            var code = new List<byte>(64);
            code.AddRange([0x48, 0x83, 0xEC, 0x28]);                                    // sub rsp, 0x28
            code.AddRange([0x48, 0xB9]);                                                // mov rcx, imm64
            code.AddRange(BitConverter.GetBytes(block.ToInt64() + nameOffset));
            code.AddRange([0x48, 0xBA]);                                                // mov rdx, imm64
            code.AddRange(BitConverter.GetBytes(block.ToInt64() + valueOffset));
            code.AddRange([0x48, 0xB8]);                                                // mov rax, imm64
            code.AddRange(BitConverter.GetBytes(setter.ToInt64()));
            code.AddRange([0xFF, 0xD0]);                                                // call rax
            code.AddRange([0x48, 0x83, 0xC4, 0x28]);                                    // add rsp, 0x28
            code.Add(0xC3);                                                             // ret
            code.CopyTo(buffer, codeOffset);

            if (!Native.WriteProcessMemory(process, block, buffer, (UIntPtr)buffer.Length, out _))
            {
                log.Error($"remote env: WriteProcessMemory failed for {name} (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            thread = Native.CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, new IntPtr(block.ToInt64() + codeOffset), IntPtr.Zero, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                log.Error($"remote env: CreateRemoteThread failed for {name} (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            if (Native.WaitForSingleObject(thread, 10_000) != 0)
            {
                running = true;
                log.Warn($"remote env: the stub for {name} did not finish.");
                return false;
            }

            Native.GetExitCodeThread(thread, out var exitCode);
            if (exitCode == 0)
            {
                log.Warn($"remote env: SetEnvironmentVariableW({name}) returned FALSE inside the game.");
                return false;
            }

            log.Line($"            {name}={value}");
            return true;
        }
        finally
        {
            if (thread != IntPtr.Zero) { Native.CloseHandle(thread); }
            if (!running) { Native.VirtualFreeEx(process, block, UIntPtr.Zero, Native.MemRelease); }
        }
    }

    private string? Stage(string source)
    {
        try
        {
            var directory = Path.Combine(
                // wsgm-allow-live-data-path: the spike's own root beside WSGM's data, never inside it.
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WSGM UwpLaunchSpike",
                "inject");
            Directory.CreateDirectory(directory);
            var staged = Path.Combine(directory, Path.GetFileName(source));
            File.Copy(source, staged, overwrite: true);
            log.Info($"injection: staged a copy at {staged}");

            // Grant on the containing directory as well: the loader has to traverse it.
            Grant(directory, directory: true);
            Grant(staged, directory: false);
            ReportAcl(staged);
            return staged;
        }
        catch (Exception ex)
        {
            log.Error($"injection: could not stage a readable copy: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void Grant(string path, bool directory)
    {
        var sid = new SecurityIdentifier(AllApplicationPackagesSid);
        if (directory)
        {
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        else
        {
            var info = new FileInfo(path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }

        log.Info($"injection: granted package read/execute on {path}");
    }

    private void ReportAcl(string path)
    {
        try
        {
            var acl = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            log.Info($"injection: acl of {path}: {acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access)}");
        }
        catch (Exception ex)
        {
            log.Warn($"injection: ACL inspection failed: {ex.Message}");
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

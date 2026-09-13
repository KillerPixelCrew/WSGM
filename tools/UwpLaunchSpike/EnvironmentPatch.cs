using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Wsgm.UwpSpike;

/// Writes Steam's launch variables into a running game's environment block.
///
/// A packaged title is started by the Windows broker, so it inherits nothing from the
/// wrapper and comes up with none of the variables Steam hands the processes it launches
/// itself. Steam's overlay renderer reads `SteamGameId` / `SteamOverlayGameId` to say
/// which session it belongs to, so injected into a process without them it has nothing to
/// register as.
///
/// IPackageDebugSettings::EnableDebugging looked like the supported way to supply them and
/// is not: it answers E_INVALIDARG for any environment block, because that parameter is
/// the environment for the debugger command line rather than for the app. What is left is
/// the environment block itself, reached through the target's PEB. GetEnvironmentVariableW
/// reads straight out of ProcessParameters->Environment on every call, so repointing it
/// changes what code loaded later sees - which is exactly the renderer, injected after
/// this runs.
internal static class EnvironmentPatch
{
    private const int ProcessBasicInformationClass = 0;

    internal static bool Apply(int pid, IReadOnlyList<string> variables, SpikeLog log)
    {
        if (variables.Count == 0)
        {
            return false;
        }

        log.Section($"Environment patch: pid {pid}");

        var process = Native.OpenProcess(
            Native.ProcessQueryInformation | Native.ProcessVmRead | Native.ProcessVmWrite | Native.ProcessVmOperation,
            false,
            (uint)pid);
        if (process == IntPtr.Zero)
        {
            log.Error($"env patch: OpenProcess failed (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        try
        {
            var information = default(Native.ProcessBasicInformation);
            var status = Native.NtQueryInformationProcess(
                process,
                ProcessBasicInformationClass,
                ref information,
                (uint)Marshal.SizeOf<Native.ProcessBasicInformation>(),
                out _);
            if (status != 0 || information.PebBaseAddress == IntPtr.Zero)
            {
                log.Error($"env patch: NtQueryInformationProcess returned 0x{status:X8}.");
                return false;
            }

            var parameters = ReadPointer(process, information.PebBaseAddress + Native.PebProcessParametersOffset);
            if (parameters == IntPtr.Zero)
            {
                log.Error("env patch: could not read ProcessParameters from the PEB.");
                return false;
            }

            var environmentAddress = ReadPointer(process, parameters + Native.ProcessParametersEnvironmentOffset);
            var environmentSize = ReadPointer(process, parameters + Native.ProcessParametersEnvironmentSizeOffset).ToInt64();
            if (environmentAddress == IntPtr.Zero || environmentSize <= 0 || environmentSize > 1024 * 1024)
            {
                log.Error($"env patch: implausible environment block (address 0x{environmentAddress.ToInt64():X}, size {environmentSize}).");
                return false;
            }

            log.Info($"env patch: peb 0x{information.PebBaseAddress.ToInt64():X}, environment 0x{environmentAddress.ToInt64():X}, {environmentSize} bytes");

            var existing = new byte[environmentSize];
            if (!Native.ReadProcessMemory(process, environmentAddress, existing, (UIntPtr)existing.Length, out _))
            {
                log.Error($"env patch: ReadProcessMemory of the environment failed (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            var merged = Merge(Parse(existing), variables, log);
            var block = Encode(merged);

            var remote = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)block.Length, Native.MemCommit | Native.MemReserve, Native.PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                log.Error($"env patch: VirtualAllocEx failed (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            if (!Native.WriteProcessMemory(process, remote, block, (UIntPtr)block.Length, out _))
            {
                log.Error($"env patch: WriteProcessMemory of the new block failed (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            // Size first, then the pointer: anything reading between the two writes sees a
            // consistent old block rather than the old pointer with the new length.
            if (!WritePointer(process, parameters + Native.ProcessParametersEnvironmentSizeOffset, new IntPtr(block.Length))
                || !WritePointer(process, parameters + Native.ProcessParametersEnvironmentOffset, remote))
            {
                log.Error($"env patch: could not repoint ProcessParameters->Environment (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            log.Info($"env patch: environment replaced at 0x{remote.ToInt64():X} with {merged.Count} variables ({block.Length} bytes).");
            return true;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var buffer = new byte[IntPtr.Size];
        return Native.ReadProcessMemory(process, address, buffer, (UIntPtr)buffer.Length, out _)
            ? new IntPtr(BitConverter.ToInt64(buffer, 0))
            : IntPtr.Zero;
    }

    private static bool WritePointer(IntPtr process, IntPtr address, IntPtr value)
    {
        var buffer = BitConverter.GetBytes(value.ToInt64());
        return Native.WriteProcessMemory(process, address, buffer, (UIntPtr)buffer.Length, out _);
    }

    private static List<string> Parse(byte[] block)
    {
        var text = Encoding.Unicode.GetString(block);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.Contains('='))
            .ToList();
    }

    /// Existing entries keep their original order and position. Nothing is sorted: Windows
    /// looks variables up by linear scan, so reordering gains nothing, and the block opens
    /// with the loader's own `=C:`-style drive entries which are not ordinary variables.
    /// An earlier version sorted the whole block and the game died about a second later.
    private static List<string> Merge(List<string> existing, IReadOnlyList<string> additions, SpikeLog log)
    {
        var merged = new List<string>(existing);
        foreach (var addition in additions)
        {
            var separator = addition.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = addition[..separator];
            var at = merged.FindIndex(entry => entry.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                merged[at] = addition;
            }
            else
            {
                merged.Add(addition);
            }

            log.Line($"            + {addition}");
        }

        return merged;
    }

    private static byte[] Encode(List<string> variables)
    {
        var text = new StringBuilder();
        foreach (var variable in variables)
        {
            text.Append(variable).Append('\0');
        }

        text.Append('\0');
        return Encoding.Unicode.GetBytes(text.ToString());
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace WSGM.Core;

/// <summary>Reads the architecture and version identity of Windows executables and libraries.</summary>
/// <remarks>Shared by the emulator manager and Game Tools so both compare the same PE facts.</remarks>
internal static class PeImage
{
    /// <summary>Receives an imported ASCII DLL name while its PE reader is alive.</summary>
    internal delegate void ImportVisitor(ReadOnlySpan<byte> name);

    /// <summary>Visits bounded native imports without allocating an object per name.</summary>
    /// <param name="path">The executable or library.</param>
    /// <param name="visit">Consumes the borrowed name span.</param>
    internal static unsafe void VisitImports(string path, ImportVisitor visit)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            VisitImportDirectory(pe, pe.PEHeaders.PEHeader?.ImportTableDirectory.RelativeVirtualAddress ?? 0, false, visit);
            VisitImportDirectory(pe, pe.PEHeaders.PEHeader?.DelayImportTableDirectory.RelativeVirtualAddress ?? 0, true, visit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
                                       or ArgumentOutOfRangeException)
        {
            // Unreadable metadata is absent evidence, not a guessed renderer.
        }
    }

    private static unsafe void VisitImportDirectory(PEReader pe, int rva, bool delayed, ImportVisitor visit)
    {
        if (rva == 0)
        {
            return;
        }
        var entries = pe.GetSectionData(rva).GetReader();
        var size = delayed ? 32 : 20;
        for (var index = 0; index < 1024 && entries.RemainingBytes >= size; index++)
        {
            long nameRva;
            if (delayed)
            {
                var attributes = entries.ReadUInt32();
                var name = entries.ReadUInt32();
                entries.Offset += 24;
                if (attributes == 0 && name == 0)
                {
                    break;
                }
                nameRva = (attributes & 1) != 0 ? name : (long)name - (long)(pe.PEHeaders.PEHeader?.ImageBase ?? 0);
            }
            else
            {
                var lookup = entries.ReadUInt32();
                var timestamp = entries.ReadUInt32();
                var forwarder = entries.ReadUInt32();
                var name = entries.ReadUInt32();
                var address = entries.ReadUInt32();
                if ((lookup | timestamp | forwarder | name | address) == 0)
                {
                    break;
                }
                nameRva = name;
            }
            if (nameRva is <= 0 or > int.MaxValue)
            {
                continue;
            }
            var block = pe.GetSectionData((int)nameRva);
            var bytes = new ReadOnlySpan<byte>(block.Pointer, Math.Min(block.Length, 128));
            var end = bytes.IndexOf((byte)0);
            if (end > 0)
            {
                visit(bytes[..end]);
            }
        }
    }

    /// <summary>Reads a DLL's bounded export names as data, without loading executable code.</summary>
    /// <param name="path">The PE file.</param>
    /// <returns>Named exports, or an empty set for missing or malformed export data.</returns>
    internal static unsafe HashSet<string> Exports(string path)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            var rva = pe.PEHeaders.PEHeader?.ExportTableDirectory.RelativeVirtualAddress ?? 0;
            if (rva == 0)
            {
                return names;
            }

            var directory = pe.GetSectionData(rva).GetReader();
            directory.Offset = 24;
            var count = Math.Min(directory.ReadUInt32(), 16384);
            directory.ReadUInt32(); // AddressOfFunctions is not the name table.
            var table = pe.GetSectionData(directory.ReadInt32()).GetReader();
            for (var index = 0; index < count && table.RemainingBytes >= 4; index++)
            {
                var block = pe.GetSectionData(table.ReadInt32());
                var bytes = new ReadOnlySpan<byte>(block.Pointer, Math.Min(block.Length, 512));
                var end = bytes.IndexOf((byte)0);
                if (end > 0)
                {
                    names.Add(Encoding.ASCII.GetString(bytes[..end]));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
                                       or ArgumentOutOfRangeException)
        {
            names.Clear();
        }

        return names;
    }

    /// <summary>The 64-bit x86 architecture name.</summary>
    internal const string X64 = "x64";

    /// <summary>The 32-bit x86 architecture name.</summary>
    internal const string X86 = "x86";

    /// <summary>The 64-bit ARM architecture name.</summary>
    internal const string Arm64 = "arm64";

    /// <summary>The architecture shown to the user.</summary>
    /// <param name="architecture">An architecture name.</param>
    /// <returns><c>64-bit</c>, <c>32-bit</c>, <c>Arm64</c>, or an empty string when unknown.</returns>
    internal static string Label(string architecture)
    {
        return architecture switch
        {
            X64 => "64-bit",
            X86 => "32-bit",
            Arm64 => "Arm64",
            _ => ""
        };
    }

    /// <summary>Reads the COFF machine of a PE file.</summary>
    /// <param name="path">The executable or library.</param>
    /// <returns><c>x64</c>, <c>x86</c>, <c>arm64</c>, or an empty string for any other machine.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="BadImageFormatException">The file is not a PE image.</exception>
    internal static string Architecture(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using PEReader reader = new(stream);
        return Architecture(reader.PEHeaders.CoffHeader.Machine);
    }

    /// <summary>Maps a COFF machine to WSGM's architecture names.</summary>
    /// <param name="machine">The machine field.</param>
    /// <returns>The architecture name, or an empty string for an unsupported machine.</returns>
    internal static string Architecture(Machine machine)
    {
        return machine switch
        {
            Machine.Amd64 => X64,
            Machine.I386 => X86,
            Machine.Arm64 => Arm64,
            (Machine)0xa64e => Arm64, // ARM64X includes a native ARM64 personality.
            (Machine)0xa641 => X64, // ARM64EC is loaded into an emulated x64 process.
            _ => ""
        };
    }

    /// <summary>Reads the architecture a program runs as, without throwing for unreadable or non-PE files.</summary>
    /// <param name="path">The candidate file.</param>
    /// <returns>The architecture name, or null when the file is not a readable PE image.</returns>
    /// <remarks>
    ///     An AnyCPU .NET program carries an x86 COFF header but runs as the operating system's native
    ///     architecture unless it requires or prefers 32-bit, so its process architecture is reported instead.
    /// </remarks>
    internal static string? TryArchitecture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using PEReader reader = new(stream);
            var headers = reader.PEHeaders;
            if (headers.CoffHeader.Machine == Machine.I386 && !headers.IsDll
                                                           && headers.CorHeader is { } cor
                                                           && cor.Flags.HasFlag(CorFlags.ILOnly)
                                                           && !cor.Flags.HasFlag(CorFlags.Requires32Bit)
                                                           && !cor.Flags.HasFlag(CorFlags.Prefers32Bit))
            {
                return RuntimeInformation.OSArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.X64 => X64,
                    System.Runtime.InteropServices.Architecture.Arm64 => Arm64,
                    _ => X86
                };
            }

            return Architecture(headers.CoffHeader.Machine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
                                       or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads the version resource fields used for identity checks.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>Trimmed fields, or null when the version resource cannot be read.</returns>
    internal static PeVersionInfo? TryVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new PeVersionInfo(
                (info.OriginalFilename ?? "").Trim(),
                (info.FileVersion ?? "").Trim(),
                (info.ProductVersion ?? "").Trim(),
                (info.FileDescription ?? "").Trim(),
                (info.ProductName ?? "").Trim(),
                (info.InternalName ?? "").Trim(),
                (info.CompanyName ?? "").Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException
                                       or ArgumentException)
        {
            return null;
        }
    }
}

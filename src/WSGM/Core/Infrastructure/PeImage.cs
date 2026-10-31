using System;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace WSGM.Core;

/// <summary>Reads the architecture and version identity of Windows executables and libraries.</summary>
/// <remarks>Shared by the emulator manager and Game Tools so both compare the same PE facts.</remarks>
internal static class PeImage
{
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

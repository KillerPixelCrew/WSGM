using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace WSGM.Device.Sdk.Packaging;

/// <summary>
///     The archive rules every <c>.wsgmpkg</c> follows. WSGM applies them when it opens a package, and
///     Device Lab and the packers apply the same ones, so a package that builds cleanly also opens.
/// </summary>
/// <remarks>
///     WSGM loads a package from memory and never unpacks it to disk. The two byte bounds refuse an
///     oversized archive instead of truncating it; nothing limits how many files a package holds. A
///     native image cannot be loaded from memory, so a package carries managed assemblies only.
/// </remarks>
public static class PluginPackageLayout
{
    /// <summary>Largest accepted size of one file in a package, in bytes.</summary>
    public const long MaxFileBytes = 128L * 1024 * 1024;

    /// <summary>Largest accepted size of a package, both on disk and uncompressed, in bytes.</summary>
    public const long MaxPackageBytes = 512L * 1024 * 1024;

    /// <summary>
    ///     Assemblies WSGM always supplies from its own copy, whatever a package carries. A packer leaves
    ///     them out, because a packaged copy is never loaded.
    /// </summary>
    public static IReadOnlyList<string> HostProvidedAssemblies { get; } =
    [
        "WSGM.Plugin.Sdk",
        "LibHandheld",
        "LibGPUDriverInteract",
        "WindowsDeviceControl",
        "SteamUiToolkit",
        "WinRT.Runtime",
        "Microsoft.Windows.SDK.NET"
    ];

    /// <summary>Checks that an entry name is relative, <c>/</c>-separated and free of dot segments.</summary>
    /// <param name="name">The entry name as the archive stores it.</param>
    /// <param name="normalized">The accepted name, or empty when it is refused.</param>
    /// <returns>True when the name is acceptable.</returns>
    public static bool TryNormalizeEntryName(string? name, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains(':') || name.StartsWith('/'))
        {
            return false;
        }

        if (name.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        normalized = name;
        return true;
    }

    /// <summary>Whether a file name is a loadable image name: <c>.dll</c>, <c>.exe</c> or <c>.sys</c>.</summary>
    /// <param name="name">A file or entry name.</param>
    /// <returns>True for an image name.</returns>
    public static bool IsImageName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether an image carries a CLR header and metadata, so it can be loaded from memory.</summary>
    /// <param name="image">A seekable stream positioned at the image's start; it is left open.</param>
    /// <returns>True for a managed image; false for a native or unreadable one.</returns>
    public static bool IsManagedImage(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            using PEReader pe = new(image, PEStreamOptions.LeaveOpen);
            return pe.PEHeaders.CorHeader is not null && pe.HasMetadata;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>Reads every file of a package archive into memory under this layout's rules.</summary>
    /// <param name="archive">The package archive; it is left open.</param>
    /// <returns>Each file's bytes keyed by its entry name.</returns>
    /// <exception cref="InvalidDataException">
    ///     The archive is unreadable, oversized, or holds an unsafe or duplicate name or a native image.
    /// </exception>
    public static Dictionary<string, byte[]> ReadEntries(Stream archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.CanSeek && archive.Length > MaxPackageBytes)
        {
            throw new InvalidDataException("The package exceeds the bounded size limit.");
        }

        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        HashSet<string> folded = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(archive, ZipArchiveMode.Read, true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The package is not a readable ZIP archive.", ex);
        }

        using (zip)
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                if (!TryNormalizeEntryName(entry.FullName, out var name)
                    || !string.Equals(name, entry.FullName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The package contains an unsafe path: {entry.FullName}");
                }

                if (!folded.Add(name))
                {
                    throw new InvalidDataException($"The package contains {name} more than once.");
                }

                if (entry.Length > MaxFileBytes || entry.Length > MaxPackageBytes - totalBytes)
                {
                    throw new InvalidDataException("The package exceeds the bounded file or size limit.");
                }

                var bytes = new byte[entry.Length];
                using (var stream = entry.Open())
                {
                    stream.ReadExactly(bytes);
                    if (stream.ReadByte() >= 0)
                    {
                        throw new InvalidDataException($"The package entry {name} is longer than it declares.");
                    }
                }

                if (IsImageName(name))
                {
                    using MemoryStream image = new(bytes, false);
                    if (!IsManagedImage(image))
                    {
                        throw new InvalidDataException(
                            $"The package carries a native image ({name}); packages may hold managed assemblies only.");
                    }
                }

                totalBytes += bytes.Length;
                entries.Add(name, bytes);
            }
        }

        return entries;
    }
}

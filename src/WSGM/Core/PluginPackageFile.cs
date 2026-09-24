using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Packaging;
using WSGM.Plugin.Sdk;
using CommonManifest = WSGM.Plugin.Sdk.PluginManifest;
using CommonManifestReader = WSGM.Plugin.Sdk.PluginManifestReader;
using DeviceManifest = WSGM.Device.Sdk.Packaging.PluginManifest;
using DeviceManifestReader = WSGM.Device.Sdk.Packaging.PluginManifestReader;

namespace WSGM.Core;

/// <summary>One validated <c>.wsgmpkg</c>, held open read-only for as long as its code may run.</summary>
/// <remarks>
///     Nothing is unpacked to disk. Opening reads every entry into memory once, under the same bounds the
///     unpacked slot used to enforce, and keeps the file handle open with <see cref="FileShare.Read" />
///     so the package cannot be replaced or deleted while it is loaded. A package holds managed
///     assemblies, their symbols and data only: a native image cannot be loaded from memory, so one is
///     refused here rather than failing later inside plugin code.
/// </remarks>
internal sealed class PluginPackageFile : IGlyphPackageSource, IDisposable
{
    internal const string Extension = ".wsgmpkg";
    internal const string ManifestName = "plugin.wsgm.json";
    internal const int MaxPackageEntries = 1024;
    internal const int MaxPackageFiles = 512;
    internal const long MaxPackageFileBytes = 128L * 1024 * 1024;
    internal const long MaxPackageBytes = 512L * 1024 * 1024;

    private readonly Dictionary<string, byte[]> _entries;
    private readonly FileStream _handle;
    private bool _disposed;

    private PluginPackageFile(
        string path,
        FileStream handle,
        Dictionary<string, byte[]> entries,
        DeviceManifest? device,
        CommonManifest? common)
    {
        Path = path;
        _handle = handle;
        _entries = entries;
        DeviceManifest = device;
        CommonManifest = common;
    }

    /// <summary>Canonical absolute path of the package file.</summary>
    internal string Path { get; }

    /// <summary>The device manifest, when this is a device package.</summary>
    internal DeviceManifest? DeviceManifest { get; }

    /// <summary>The common manifest, when this is a non-device package.</summary>
    internal CommonManifest? CommonManifest { get; }

    internal string Id => DeviceManifest?.Id ?? CommonManifest!.Id;

    internal string Version => DeviceManifest?.Version ?? CommonManifest!.Version;

    internal string EntryAssembly => DeviceManifest?.EntryAssembly ?? CommonManifest!.EntryAssembly;

    /// <summary>The WSGM version the package was built for, or null when packing did not stamp one.</summary>
    internal string? WsgmVersion => DeviceManifest is { } device ? device.WsgmVersion : CommonManifest!.WsgmVersion;

    /// <summary>This host's version, which a package's <see cref="WsgmVersion" /> must equal.</summary>
    internal static Version HostVersion { get; } =
        Normalize(typeof(PluginPackageFile).Assembly.GetName().Version ?? new Version(0, 0));

    /// <summary>Whether the entry point is an x64 managed assembly, which a device package requires.</summary>
    internal bool EntryIsX64Assembly { get; private init; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateProfileIds()
    {
        const string prefix = "glyphs/profiles/";
        return
        [
            .. _entries.Keys
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
                               && name.EndsWith(".json", StringComparison.Ordinal)
                               && name.AsSpan(prefix.Length).IndexOf('/') < 0)
                .Select(System.IO.Path.GetFileNameWithoutExtension)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                // One past the limit, as the directory source does, so the importer can see an
                // over-limit package instead of a silently truncated one.
                .Take(GlyphProfileLimits.MaxProfiles + 1)
        ];
    }

    /// <inheritdoc />
    public bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (maximumBytes <= 0
            || !TryNormalizeEntryName(relativePath, out var name)
            || !_entries.TryGetValue(name, out var stored)
            || stored.Length == 0
            || stored.Length > maximumBytes)
        {
            return false;
        }

        bytes = [.. stored];
        return true;
    }

    /// <summary>Whether a stamped version names this host, with omitted components read as zero.</summary>
    internal static bool IsForThisHost(string? wsgmVersion)
    {
        return System.Version.TryParse(wsgmVersion, out var parsed) && Normalize(parsed) == HostVersion;
    }

    private static Version Normalize(Version version)
    {
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }

    /// <summary>Returns a fresh stream over a package-root assembly and its symbols, or false.</summary>
    /// <param name="fileName">Assembly file name, optionally below one culture directory.</param>
    /// <param name="assembly">The assembly image.</param>
    /// <param name="symbols">Matching portable symbols, when the package carries them.</param>
    internal bool TryOpenAssembly(string fileName, out MemoryStream assembly, out MemoryStream? symbols)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        assembly = null!;
        symbols = null;
        if (!TryNormalizeEntryName(fileName, out var name) || !_entries.TryGetValue(name, out var image))
        {
            return false;
        }

        assembly = new MemoryStream(image, false);
        var pdbName = System.IO.Path.ChangeExtension(name, ".pdb");
        if (_entries.TryGetValue(pdbName, out var pdb))
        {
            symbols = new MemoryStream(pdb, false);
        }

        return true;
    }

    /// <summary>Opens and fully validates a package without loading any of its code.</summary>
    /// <param name="packagePath">Path of a <c>.wsgmpkg</c> file.</param>
    /// <returns>The open package. The caller owns it and must dispose it.</returns>
    /// <exception cref="InvalidDataException">The package breaks a layout, size or manifest rule.</exception>
    internal static PluginPackageFile Open(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var path = System.IO.Path.GetFullPath(packagePath);
        if (!string.Equals(System.IO.Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Plugin packages must use the {Extension} extension.");
        }

        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new InvalidDataException("A plugin package must be a plain file, not a link or a directory.");
        }

        FileStream handle = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.RandomAccess);
        try
        {
            if (handle.Length > MaxPackageBytes)
            {
                throw new InvalidDataException("The package exceeds the bounded size limit.");
            }

            var entries = ReadEntries(handle);
            if (!entries.TryGetValue(ManifestName, out var manifestBytes))
            {
                throw new InvalidDataException($"The package has no {ManifestName} at its root.");
            }

            var (device, common) = ReadManifest(manifestBytes);
            var entryAssembly = device?.EntryAssembly ?? common!.EntryAssembly;
            if (!TryNormalizeEntryName(entryAssembly, out var entryName)
                || entryName.Contains('/')
                || !entries.TryGetValue(entryName, out var entryImage))
            {
                throw new InvalidDataException("The plugin entry assembly is missing from the package root.");
            }

            return new PluginPackageFile(path, handle, entries, device, common)
            {
                EntryIsX64Assembly = IsX64ManagedAssembly(entryImage)
            };
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static Dictionary<string, byte[]> ReadEntries(FileStream handle)
    {
        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        HashSet<string> folded = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(handle, ZipArchiveMode.Read, true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The package is not a readable ZIP archive.", ex);
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxPackageEntries)
            {
                throw new InvalidDataException("The package exceeds the bounded entry limit.");
            }

            foreach (var entry in archive.Entries)
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

                if (entries.Count >= MaxPackageFiles
                    || entry.Length > MaxPackageFileBytes
                    || entry.Length > MaxPackageBytes - totalBytes)
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

                if (IsImageName(name) && !IsManagedImage(bytes))
                {
                    throw new InvalidDataException(
                        $"The package carries a native image ({name}); packages may hold managed assemblies only.");
                }

                totalBytes += bytes.Length;
                entries.Add(name, bytes);
            }
        }

        return entries;
    }

    /// <summary>Routes the manifest to the reader of its category; a common manifest names one.</summary>
    private static (DeviceManifest? Device, CommonManifest? Common) ReadManifest(byte[] bytes)
    {
        bool hasCategory;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            hasCategory = document.RootElement.ValueKind is JsonValueKind.Object
                          && document.RootElement.TryGetProperty("category", out _);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The plugin manifest is not valid JSON.", ex);
        }

        if (hasCategory)
        {
            if (!CommonManifestReader.TryRead(bytes, out var common, out var errors))
            {
                throw new InvalidDataException(string.Join(" ", errors));
            }

            if (common!.Category == PluginCategories.Device)
            {
                throw new InvalidDataException("Device packages use the device manifest, not a common category.");
            }

            return (null, common);
        }

        var read = DeviceManifestReader.Read(bytes);
        // A package built for another API is still a readable device package: the catalog reports it as
        // api-incompatible so the overlay can say which package it is, instead of a bare folder error.
        if (read.Manifest is not null
            && read.Errors.All(error => error.Code is ManifestValidationCode.InvalidApiVersion))
        {
            return (read.Manifest, null);
        }

        if (!read.IsValid || read.Manifest is null)
        {
            throw new InvalidDataException(string.Join("; ", read.Errors.Select(error => error.Message)));
        }

        return (read.Manifest, null);
    }

    private static bool TryNormalizeEntryName(string? name, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 260 || name.Contains('\\') || name.Contains(':')
            || name.StartsWith('/'))
        {
            return false;
        }

        var segments = name.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        normalized = name;
        return true;
    }

    private static bool IsImageName(string name)
    {
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManagedImage(byte[] bytes)
    {
        try
        {
            using PEReader pe = new(new MemoryStream(bytes, false));
            return pe.PEHeaders.CorHeader is not null && pe.HasMetadata;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsX64ManagedAssembly(byte[] bytes)
    {
        try
        {
            using PEReader pe = new(new MemoryStream(bytes, false));
            return pe.PEHeaders.CoffHeader.Machine is Machine.Amd64
                   && pe.PEHeaders.CorHeader is not null
                   && pe.HasMetadata
                   && pe.GetMetadataReader().IsAssembly;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }
}

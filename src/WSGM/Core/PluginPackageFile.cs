using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using WSGM.Device.Sdk.Packaging;
using WSGM.Plugin.Sdk;
using CommonManifest = WSGM.Plugin.Sdk.PluginManifest;
using CommonManifestReader = WSGM.Plugin.Sdk.PluginManifestReader;

namespace WSGM.Core;

/// <summary>One validated <c>.wsgmpkg</c>, held open read-only for as long as its code may run.</summary>
/// <remarks>
///     Nothing is unpacked to disk. Opening reads every entry into memory once under package layout and
///     size bounds, and keeps the file handle open with <see cref="FileShare.Read" />
///     so the package cannot be replaced or deleted while it is loaded. A package holds managed
///     assemblies, their symbols and data only: a native image cannot be loaded from memory, so one is
///     refused here rather than failing later inside plugin code.
/// </remarks>
internal sealed class PluginPackageFile : IDisposable
{
    /// <summary>Required case-insensitive filename extension for an installed plugin package.</summary>
    internal const string Extension = ".wsgmpkg";

    /// <summary>Required root manifest entry name within the package.</summary>
    internal const string ManifestName = "plugin.wsgm.json";

    private readonly Dictionary<string, byte[]> _entries;
    private readonly FileStream _handle;
    private bool _disposed;

    private PluginPackageFile(
        string path,
        FileStream handle,
        Dictionary<string, byte[]> entries,
        CommonManifest common)
    {
        Path = path;
        _handle = handle;
        _entries = entries;
        CommonManifest = common;
    }

    /// <summary>Canonical absolute path of the package file.</summary>
    internal string Path { get; }

    /// <summary>The validated independent plugin manifest.</summary>
    internal CommonManifest CommonManifest { get; }

    /// <summary>Gets the validated package identity from its device or common manifest.</summary>
    internal string Id => CommonManifest.Id;

    /// <summary>Gets the manifest version string used by catalog selection.</summary>
    internal string Version => CommonManifest.Version;

    /// <summary>Gets the package-root managed entry assembly name.</summary>
    internal string EntryAssembly => CommonManifest.EntryAssembly;

    /// <summary>The WSGM version the package was built for, or null when packing did not stamp one.</summary>
    internal string? WsgmVersion => CommonManifest.WsgmVersion;

    /// <summary>This host's release version, which a package's <see cref="WsgmVersion" /> must equal.</summary>
    /// <remarks>
    ///     The release only: packers stamp <c>WSGM.csproj</c>'s Version, and the assembly's fourth part is the
    ///     build revision (eng/wsgm-revision.targets), which a package built for that release does not name.
    /// </remarks>
    internal static Version HostVersion { get; } =
        Normalize(typeof(PluginPackageFile).Assembly.GetName().Version ?? new Version(0, 0));

    /// <summary>Closes the package file lock; repeated disposal is harmless.</summary>
    /// <remarks>Stop plugin code before disposal. Cached entry bytes remain managed memory until this object is collected.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    /// <summary>Reads a bounded package resource for a common plugin's admitted Steam module.</summary>
    internal bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (maximumBytes <= 0
            || !PluginPackageLayout.TryNormalizeEntryName(relativePath, out var name)
            || !_entries.TryGetValue(name, out var stored)
            || stored.Length == 0
            || stored.Length > maximumBytes)
        {
            return false;
        }

        bytes = [.. stored];
        return true;
    }

    /// <summary>The SHA-256 of the package file, as upper-case hex, read through the handle already open.</summary>
    /// <returns>The full file SHA-256 as uppercase hexadecimal; the shared handle position is advanced to EOF.</returns>
    /// <exception cref="ObjectDisposedException">The file owner has been disposed.</exception>
    /// <remarks>Serialize callers using the shared file handle; disk read failures propagate.</remarks>
    internal string HashFile()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _handle.Position = 0;
        return Convert.ToHexString(SHA256.HashData(_handle));
    }

    /// <summary>Whether a stamped version names this host's release, with an omitted patch read as zero.</summary>
    /// <param name="wsgmVersion">Stamped release version, or null for an unstamped package.</param>
    /// <returns>True only for a parseable matching major/minor/patch release; revision is ignored.</returns>
    internal static bool IsForThisHost(string? wsgmVersion)
    {
        return System.Version.TryParse(wsgmVersion, out var parsed) && Normalize(parsed) == HostVersion;
    }

    private static Version Normalize(Version version)
    {
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <summary>Returns a fresh stream over a package-root assembly and its symbols, or false.</summary>
    /// <param name="fileName">Assembly file name, optionally below one culture directory.</param>
    /// <param name="assembly">The assembly image.</param>
    /// <param name="symbols">Matching portable symbols, when the package carries them.</param>
    /// <returns>True when the normalized entry exists; false leaves both outputs null.</returns>
    /// <remarks>Each returned read-only stream is caller-owned and must be disposed; package bytes are shared without copying.</remarks>
    /// <exception cref="ObjectDisposedException">The package owner has been disposed.</exception>
    internal bool TryOpenAssembly(string fileName, out MemoryStream assembly, out MemoryStream? symbols)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        assembly = null!;
        symbols = null;
        if (!PluginPackageLayout.TryNormalizeEntryName(fileName, out var name)
            || !_entries.TryGetValue(name, out var image))
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
            var entries = PluginPackageLayout.ReadEntries(handle);
            if (!entries.TryGetValue(ManifestName, out var manifestBytes))
            {
                throw new InvalidDataException($"The package has no {ManifestName} at its root.");
            }

            var common = ReadManifest(manifestBytes);
            var entryAssembly = common.EntryAssembly;
            if (!PluginPackageLayout.TryNormalizeEntryName(entryAssembly, out var entryName)
                || entryName.Contains('/')
                || !entries.ContainsKey(entryName))
            {
                throw new InvalidDataException("The plugin entry assembly is missing from the package root.");
            }

            return new PluginPackageFile(path, handle, entries, common);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Reads the independent plugin contract; native hardware libraries have no packages.</summary>
    private static CommonManifest ReadManifest(byte[] bytes)
    {
        if (!CommonManifestReader.TryRead(bytes, out var common, out var errors))
        {
            throw new InvalidDataException(string.Join(" ", errors));
        }

        if (common!.Category == PluginCategories.Device)
        {
            throw new InvalidDataException("Native handheld support is supplied by LibHandheld.");
        }

        return common;
    }
}

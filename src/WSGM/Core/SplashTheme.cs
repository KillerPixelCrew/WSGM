using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>Export/import of <c>.wsgmsplash</c> splash-theme files: a zip archive
/// containing <c>splash.json</c> (the serialized <see cref="SplashConfig"/>) plus the
/// referenced logo/background images bundled under their deterministic names
/// (<c>logo.*</c>/<c>background.*</c>). Theme files are untrusted user-shared
/// content: entry names are strictly whitelisted, decompression is size-bounded,
/// and import stages images into a fresh temp directory — never over the live
/// splash assets — so only a later Save materializes them into the stable copies.
/// Malformed, oversized, or unexpected archives return null with a logged warning —
/// never an exception, so a bad theme file can never break Settings.</summary>
internal static class SplashTheme
{
    private const string ConfigEntryName = "splash.json";
    private const string LogoEntryBaseName = "logo";
    private const string BackgroundEntryBaseName = "background";

    /// <summary>Decompressed-size cap for the <c>splash.json</c> entry.</summary>
    private const long MaxConfigEntryBytes = 1024 * 1024;

    /// <summary>Decompressed-size cap for each bundled image entry.</summary>
    private const long MaxImageEntryBytes = 64L * 1024 * 1024;

    /// <summary>Decompressed-size cap for the whole archive.</summary>
    private const long MaxTotalBytes = 160L * 1024 * 1024;

    /// <summary>Image extensions a theme archive may bundle.</summary>
    private static readonly string[] AllowedImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    /// <summary>Writes a splash theme archive to <paramref name="path"/> atomically:
    /// the archive is built in a sibling temp file and moved over the destination
    /// only once fully written, so a failed export leaves any existing file intact.</summary>
    /// <returns>True when the file was written; false (logged) on any failure.</returns>
    internal static bool Export(SplashConfig splash, string path)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            bool written;
            using (var stream = File.Create(tempPath))
            {
                written = Export(splash, stream);
            }
            if (written)
            {
                File.Move(tempPath, path, overwrite: true);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme export to '{path}' failed: {ex.Message}");
        }
        TryDeleteFile(tempPath);
        return false;
    }

    /// <summary>Writes a splash theme archive to an open stream. The stream is left
    /// open for the caller to dispose.</summary>
    /// <returns>True when the archive was written; false (logged) on any failure.</returns>
    internal static bool Export(SplashConfig splash, Stream destination)
    {
        try
        {
            // The bundled copy gets its image paths rewritten to the archive entry
            // names; the caller's instance is never mutated.
            var bundled = Clone(splash);
            using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            bundled.LogoImagePath = BundleImage(archive, splash.LogoImagePath, LogoEntryBaseName);
            bundled.BackgroundImagePath = BundleImage(archive, splash.BackgroundImagePath, BackgroundEntryBaseName);
            var entry = archive.CreateEntry(ConfigEntryName);
            using var entryStream = entry.Open();
            JsonSerializer.Serialize(entryStream, bundled, ConfigJsonContext.Default.SplashConfig);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme export failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads a splash theme archive, staging any bundled images into a fresh
    /// per-import directory under the user's temp folder — deliberately outside the
    /// live splash assets, which are only touched when a later Save materializes the
    /// staged copies. Older staging directories are swept best-effort only AFTER a
    /// successful import: a failed import leaves the caller pointing at the previous
    /// import's staged files, which must survive.</summary>
    /// <returns>The imported configuration, or null (logged) when the file is not an
    /// acceptable splash theme.</returns>
    internal static SplashConfig? Import(string path)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "WSGM.splash-import");
        var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        var imported = Import(path, stagingDirectory);
        if (imported is not null)
        {
            CleanUpStaleStagingDirectories(stagingRoot, keep: stagingDirectory);
        }
        return imported;
    }

    /// <summary>Reads a splash theme archive, extracting any bundled images into
    /// <paramref name="targetImageDirectory"/> and rewriting the returned config's
    /// image paths to the extracted copies. Every entry must be one of the
    /// whitelisted names within its size cap; extraction is bounded so a lying
    /// central directory cannot decompress past the caps. A failed import removes
    /// anything it staged.</summary>
    /// <returns>The imported configuration, or null (logged) when the file is not an
    /// acceptable splash theme.</returns>
    internal static SplashConfig? Import(string path, string targetImageDirectory)
    {
        var targetExistedBefore = Directory.Exists(targetImageDirectory);
        var extractedFiles = new List<string>();
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (!EntriesAreAcceptable(archive, path))
            {
                return null;
            }

            var configEntry = FindConfigEntry(archive);
            if (configEntry is null)
            {
                Log.Warn($"Splash theme '{path}' contains no {ConfigEntryName} — not a splash theme file.");
                return null;
            }

            SplashConfig? splash;
            using (var buffer = new MemoryStream())
            {
                using (var entryStream = configEntry.Open())
                {
                    CopyBounded(entryStream, buffer, MaxConfigEntryBytes, ConfigEntryName);
                }
                buffer.Position = 0;
                splash = JsonSerializer.Deserialize(buffer, ConfigJsonContext.Default.SplashConfig);
            }
            if (splash is null)
            {
                Log.Warn($"Splash theme '{path}' has an empty {ConfigEntryName}.");
                return null;
            }

            // Same explicit-null repairs a loaded config.json gets — the archive
            // contents are untrusted.
            ConfigStore.NormalizeSplash(splash);
            splash.LogoImagePath =
                ExtractImage(archive, LogoEntryBaseName, targetImageDirectory, extractedFiles)
                ?? splash.LogoImagePath;
            splash.BackgroundImagePath =
                ExtractImage(archive, BackgroundEntryBaseName, targetImageDirectory, extractedFiles)
                ?? splash.BackgroundImagePath;
            return splash;
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme import from '{path}' failed: {ex.Message}");
            CleanUpFailedImport(targetImageDirectory, targetExistedBefore, extractedFiles);
            return null;
        }
    }

    /// <summary>Validates every archive entry against the whitelist (exactly
    /// <c>splash.json</c>, <c>logo.&lt;image-ext&gt;</c>, or
    /// <c>background.&lt;image-ext&gt;</c>; no directories, separators, or traversal)
    /// and the per-entry/total declared-size caps.</summary>
    private static bool EntriesAreAcceptable(ZipArchive archive, string path)
    {
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var limit = AllowedEntryBytes(entry.FullName);
            if (limit is null)
            {
                Log.Warn($"Splash theme '{path}' rejected: unexpected entry '{entry.FullName}'.");
                return false;
            }
            if (entry.Length > limit)
            {
                Log.Warn(
                    $"Splash theme '{path}' rejected: entry '{entry.FullName}' declares {entry.Length} bytes (limit {limit})."
                );
                return false;
            }
            totalBytes += entry.Length;
            if (totalBytes > MaxTotalBytes)
            {
                Log.Warn($"Splash theme '{path}' rejected: total declared size exceeds {MaxTotalBytes} bytes.");
                return false;
            }
        }
        return true;
    }

    /// <summary>Returns the decompressed-size cap for a whitelisted entry name, or
    /// null when the name is not acceptable (unknown name, disallowed extension, or
    /// any path separator — entry names must be bare file names).</summary>
    private static long? AllowedEntryBytes(string entryName)
    {
        if (entryName.Contains('/') || entryName.Contains('\\'))
        {
            return null;
        }
        if (string.Equals(entryName, ConfigEntryName, StringComparison.OrdinalIgnoreCase))
        {
            return MaxConfigEntryBytes;
        }
        var stem = Path.GetFileNameWithoutExtension(entryName);
        var extension = Path.GetExtension(entryName).ToLowerInvariant();
        if (
            (
                string.Equals(stem, LogoEntryBaseName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, BackgroundEntryBaseName, StringComparison.OrdinalIgnoreCase)
            )
            && Array.IndexOf(AllowedImageExtensions, extension) >= 0
        )
        {
            return MaxImageEntryBytes;
        }
        return null;
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/>,
    /// aborting once more than <paramref name="limit"/> bytes actually decompress —
    /// the declared entry length in the central directory can lie.</summary>
    private static void CopyBounded(Stream source, Stream destination, long limit, string entryName)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException($"entry '{entryName}' exceeds {limit} bytes when decompressed");
            }
            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>Copies the referenced image into the archive under its deterministic
    /// entry name and returns that name; a blank or missing source keeps the
    /// original path string and bundles nothing.</summary>
    private static string BundleImage(ZipArchive archive, string sourcePath, string entryBaseName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return sourcePath ?? "";
        }
        var entryName = entryBaseName + Path.GetExtension(sourcePath).ToLowerInvariant();
        var entry = archive.CreateEntry(entryName);
        using var target = entry.Open();
        using var source = File.OpenRead(sourcePath);
        source.CopyTo(target);
        return entryName;
    }

    /// <summary>Extracts the (already whitelisted) image entry with the given base
    /// name into the target directory through the bounded copy and returns the
    /// extracted file's full path, or null when the archive has no such entry.
    /// The destination is recorded in <paramref name="extractedFiles"/> before the
    /// copy starts so a partial file is cleaned up on failure.</summary>
    private static string? ExtractImage(
        ZipArchive archive, string entryBaseName, string targetDirectory, List<string> extractedFiles)
    {
        foreach (var entry in archive.Entries)
        {
            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(entry.FullName),
                    entryBaseName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Directory.CreateDirectory(targetDirectory);
            var destination = Path.Combine(targetDirectory, entry.FullName.ToLowerInvariant());
            extractedFiles.Add(destination);
            using var source = entry.Open();
            using var target = File.Create(destination);
            CopyBounded(source, target, MaxImageEntryBytes, entry.FullName);
            return destination;
        }
        return null;
    }

    /// <summary>Removes whatever a failed import staged: a directory the import
    /// created is deleted wholesale, while a pre-existing directory only loses the
    /// files this import wrote. Best effort — never throws.</summary>
    private static void CleanUpFailedImport(
        string targetImageDirectory, bool targetExistedBefore, List<string> extractedFiles)
    {
        try
        {
            if (!targetExistedBefore)
            {
                if (Directory.Exists(targetImageDirectory))
                {
                    Directory.Delete(targetImageDirectory, recursive: true);
                }
                return;
            }
            foreach (var file in extractedFiles)
            {
                TryDeleteFile(file);
            }
        }
        catch
        {
            // Cleanup after a failed import is best effort.
        }
    }

    /// <summary>Best-effort removal of staging directories left behind by earlier
    /// imports (e.g. imports that were never saved), sparing the one the current
    /// import just produced. Never throws.</summary>
    private static void CleanUpStaleStagingDirectories(string stagingRoot, string keep)
    {
        try
        {
            if (!Directory.Exists(stagingRoot))
            {
                return;
            }
            foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
            {
                if (string.Equals(
                        Path.GetFullPath(directory),
                        Path.GetFullPath(keep),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // A locked or in-use staging dir just stays for the next sweep.
                }
            }
        }
        catch
        {
            // Staging cleanup is best effort.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort.
        }
    }

    private static ZipArchiveEntry? FindConfigEntry(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, ConfigEntryName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    private static SplashConfig Clone(SplashConfig splash)
    {
        var json = JsonSerializer.Serialize(splash, ConfigJsonContext.Default.SplashConfig);
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.SplashConfig) ?? new SplashConfig();
    }
}

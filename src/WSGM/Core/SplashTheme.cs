using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace WSGM.Core;

/// <summary>
///     Export/import of <c>.wsgmsplash</c> splash-theme files: a zip archive
///     containing <c>splash.json</c> (the serialized <see cref="SplashConfig" />) plus the
///     referenced logo/background images bundled under their deterministic names
///     (<c>logo.*</c>/<c>background.*</c>). Theme files are untrusted user-shared
///     content: entry names are strictly whitelisted, decompression is size-bounded,
///     and import stages images into a fresh temp directory — never over the live
///     splash assets — so only a later Save materializes them into the stable copies.
///     Malformed, oversized, or unexpected archives return null with a logged warning —
///     never an exception, so a bad theme file can never break Settings.
/// </summary>
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

    private static readonly Lock SessionGate = new();

    /// <summary>
    ///     How many windows that can still display staged imports are open. The shell opens
    ///     Settings IN-PROCESS and more than one window can be open at a time, so this process's
    ///     staged imports are deleted when the last one closes, not the first. Guarded by
    ///     <see cref="SessionGate" />.
    /// </summary>
    private static int _openImportSessions;

    /// <summary>
    ///     This process's directory name under the staging root: its id and start time, so a reused
    ///     process id never claims what a dead process staged.
    /// </summary>
    internal static string ProcessDirectoryName { get; } = CurrentProcessDirectoryName();

    /// <summary>
    ///     Parent directory holding one directory per process, each with one directory per import,
    ///     under the user's temp folder: deliberately outside the live splash assets.
    /// </summary>
    private static string StagingRoot => Path.Combine(Path.GetTempPath(), "WSGM.splash-import");

    /// <summary>
    ///     Writes a splash theme archive to <paramref name="path" /> atomically:
    ///     the archive is built in a sibling temp file and moved over the destination
    ///     only once fully written, so a failed export leaves any existing file intact.
    /// </summary>
    /// <returns>True when the file was written; false (logged) on any failure.</returns>
    /// <param name="splash">Splash configuration whose referenced images are included when valid.</param>
    /// <param name="path">Destination archive path, replaced only after a complete temporary archive is written.</param>
    internal static bool Export(SplashConfig splash, string path)
    {
        try
        {
            return AtomicFile.Write(path, stream => Export(splash, stream), false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme export to '{path}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Writes a splash theme archive to an open stream. The stream is left
    ///     open for the caller to dispose.
    /// </summary>
    /// <returns>
    ///     True when the archive was written; false (logged) on any failure,
    ///     including a selected image above the per-image import cap.
    /// </returns>
    private static bool Export(SplashConfig splash, Stream destination)
    {
        try
        {
            // Refuse up front — before a single byte is written — what the importer
            // would always reject, so an oversized image can never produce (or
            // replace) an archive nobody can import.
            if (!ImageIsBundleable(splash.LogoImagePath) || !ImageIsBundleable(splash.BackgroundImagePath))
            {
                return false;
            }

            // The bundled copy gets its image paths rewritten to the archive entry
            // names; the caller's instance is never mutated.
            var bundled = ConfigJson.Clone(splash, ConfigJsonContext.Tolerant.SplashConfig);
            using var archive = new ZipArchive(destination, ZipArchiveMode.Create, true);
            bundled.LogoImagePath = BundleImage(archive, splash.LogoImagePath, LogoEntryBaseName);
            bundled.BackgroundImagePath = BundleImage(archive, splash.BackgroundImagePath, BackgroundEntryBaseName);
            var entry = archive.CreateEntry(ConfigEntryName);
            using var entryStream = entry.Open();
            JsonSerializer.Serialize(entryStream, bundled, ConfigJsonContext.Tolerant.SplashConfig);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme export failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Reads a splash theme archive, staging any bundled images into a fresh per-import
    ///     directory under this process's staging directory: deliberately outside the live splash
    ///     assets, which are only touched when a later Save materializes the staged copies. The
    ///     staged images stay until the last import session in this process ends
    ///     (<see cref="EndImportSession()" />), so closing a second Settings window cannot free an
    ///     unsaved import still on screen in the first.
    /// </summary>
    /// <returns>
    ///     The imported configuration, or null (logged) when the file is not an
    ///     acceptable splash theme.
    /// </returns>
    /// <param name="path">Archive to read; caller must hold an import session while using returned staged image paths.</param>
    internal static SplashConfig? Import(string path)
    {
        return Import(path, Path.Combine(StagingRoot, ProcessDirectoryName, Guid.NewGuid().ToString("N")));
    }

    /// <summary>
    ///     Opens an import session: called by every window that can display the images an import
    ///     staged (Settings), for its whole lifetime. Opening one also deletes what processes that no
    ///     longer run left staged, so a crashed session's imports do not wait for another import.
    /// </summary>
    internal static void BeginImportSession()
    {
        BeginImportSession(StagingRoot);
    }

    /// <summary>
    ///     Test seam for <see cref="BeginImportSession()" /> against a temp
    ///     staging root instead of the real one.
    /// </summary>
    /// <param name="stagingRoot">The staging root to sweep.</param>
    internal static void BeginImportSession(string stagingRoot)
    {
        lock (SessionGate)
        {
            _openImportSessions++;
        }

        CleanUpStagingDirectories(stagingRoot, true);
    }

    /// <summary>
    ///     Closes an import session. When it was the last one, nothing in this process can be
    ///     pointing at its staged images any more, so they are deleted, up to
    ///     <see cref="MaxTotalBytes" /> per import.
    /// </summary>
    internal static void EndImportSession()
    {
        EndImportSession(StagingRoot);
    }

    /// <summary>
    ///     Test seam for <see cref="EndImportSession()" /> against a temp staging
    ///     root instead of the real one.
    /// </summary>
    /// <param name="stagingRoot">The staging root to sweep.</param>
    internal static void EndImportSession(string stagingRoot)
    {
        lock (SessionGate)
        {
            if (_openImportSessions > 0)
            {
                _openImportSessions--;
            }

            // Inside the gate, so a window opening right now cannot import into the directory
            // this deletes.
            if (_openImportSessions == 0)
            {
                CleanUpStagingDirectories(stagingRoot, false);
            }
        }
    }

    /// <summary>
    ///     Reads a splash theme archive, extracting any bundled images into
    ///     <paramref name="targetImageDirectory" /> and rewriting the returned config's
    ///     image paths to the extracted copies. Every entry must be one of the
    ///     whitelisted names within its size cap; extraction is bounded so a lying
    ///     central directory cannot decompress past the caps. Failed imports attempt to remove
    ///     their staged files; filesystem cleanup failures are tolerated.
    /// </summary>
    /// <returns>
    ///     The imported configuration, or null (logged) when the file is not an
    ///     acceptable splash theme.
    /// </returns>
    /// <param name="path">Splash archive file to read under the import limits.</param>
    /// <param name="targetImageDirectory">Caller-owned staging directory for bundled images; keep it alive while the returned config refers to it.</param>
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
                splash = JsonSerializer.Deserialize(buffer, ConfigJsonContext.Tolerant.SplashConfig);
            }

            if (splash is null)
            {
                Log.Warn($"Splash theme '{path}' has an empty {ConfigEntryName}.");
                return null;
            }

            // Apply the same explicit-null repairs as a loaded config.json.
            SplashRules.Normalize(splash);
            // Archive paths never escape the import transaction: only files staged by this import
            // are returned, or an empty path when the archive omits the image.
            splash.LogoImagePath = ExtractImage(archive, LogoEntryBaseName, targetImageDirectory, extractedFiles) ?? "";
            splash.BackgroundImagePath =
                ExtractImage(archive, BackgroundEntryBaseName, targetImageDirectory, extractedFiles) ?? "";
            return splash;
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme import from '{path}' failed: {ex.Message}");
            CleanUpFailedImport(targetImageDirectory, targetExistedBefore, extractedFiles);
            return null;
        }
    }

    /// <summary>
    ///     Validates every archive entry against the whitelist (exactly
    ///     <c>splash.json</c>, <c>logo.&lt;image-ext&gt;</c>, or
    ///     <c>background.&lt;image-ext&gt;</c>; no directories, separators, or traversal)
    ///     and the per-entry/total declared-size caps.
    /// </summary>
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
            if (totalBytes <= MaxTotalBytes)
            {
                continue;
            }

            Log.Warn($"Splash theme '{path}' rejected: total declared size exceeds {MaxTotalBytes} bytes.");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Returns the decompressed-size cap for a whitelisted entry name, or
    ///     null when the name is not acceptable (unknown name, disallowed extension, or
    ///     anything that is not a bare file name).
    /// </summary>
    private static long? AllowedEntryBytes(string entryName)
    {
        // Not just separators: a drive-relative name like "D:logo.png" is rooted,
        // and Path.Combine would then DISCARD the staging directory and write it
        // wherever that drive's current directory points. Requiring the name to
        // equal its own file name rejects separators, roots and volume-relative
        // forms in one check.
        if (entryName.Length == 0
            || !string.Equals(entryName, Path.GetFileName(entryName), StringComparison.Ordinal))
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

    /// <summary>
    ///     Copies <paramref name="source" /> to <paramref name="destination" />,
    ///     aborting once more than <paramref name="limit" /> bytes actually decompress —
    ///     the declared entry length in the central directory can lie.
    /// </summary>
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

    /// <summary>
    ///     Checks a to-be-bundled image against the very rules
    ///     <see cref="Import(string, string)" /> enforces — its size cap AND its
    ///     extension whitelist — so an export can never yield an archive the importer
    ///     rejects. A blank, missing or unreadable source bundles nothing and passes.
    /// </summary>
    private static bool ImageIsBundleable(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return true;
        }

        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            return true;
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (Array.IndexOf(AllowedImageExtensions, extension) < 0)
        {
            Log.Warn(
                $"Splash theme export refused: image '{sourcePath}' has an unsupported extension "
                + $"('{extension}'; allowed: {string.Join(", ", AllowedImageExtensions)})."
            );
            return false;
        }

        if (info.Length <= MaxImageEntryBytes)
        {
            return true;
        }

        Log.Warn(
            $"Splash theme export refused: image '{sourcePath}' is {info.Length} bytes (limit {MaxImageEntryBytes})."
        );
        return false;
    }

    /// <summary>
    ///     Copies the referenced image into the archive under its deterministic
    ///     entry name and returns that name; a blank or missing source bundles nothing
    ///     and returns "". Returning the source path instead would leak the author's
    ///     absolute local path into a file meant to be shared, for an image the archive
    ///     does not even contain; the importer treats an absent entry as "no image"
    ///     either way.
    /// </summary>
    private static string BundleImage(ZipArchive archive, string sourcePath, string entryBaseName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return "";
        }

        var entryName = entryBaseName + Path.GetExtension(sourcePath).ToLowerInvariant();
        var entry = archive.CreateEntry(entryName);
        using var target = entry.Open();
        using var source = File.OpenRead(sourcePath);
        source.CopyTo(target);
        return entryName;
    }

    /// <summary>
    ///     Extracts the (already whitelisted) image entry with the given base
    ///     name into the target directory through the bounded copy and returns the
    ///     extracted file's full path, or null when the archive has no such entry.
    ///     The destination is recorded in <paramref name="extractedFiles" /> before the
    ///     copy starts so a partial file is cleaned up on failure.
    /// </summary>
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
            var destination = Path.Combine(targetDirectory, Path.GetFileName(entry.FullName).ToLowerInvariant());
            // Belt and braces behind the name whitelist: never write outside the
            // staging directory, whatever the archive claims an entry is called.
            if (!Path.GetFullPath(destination).StartsWith(
                    Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"entry '{entry.FullName}' escapes the staging directory");
            }

            extractedFiles.Add(destination);
            using var source = entry.Open();
            using var target = File.Create(destination);
            CopyBounded(source, target, MaxImageEntryBytes, entry.FullName);
            return destination;
        }

        return null;
    }

    /// <summary>
    ///     Removes whatever a failed import staged: a directory the import
    ///     created is deleted wholesale, while a pre-existing directory only loses the
    ///     files this import wrote. Best effort — never throws.
    /// </summary>
    private static void CleanUpFailedImport(
        string targetImageDirectory, bool targetExistedBefore, List<string> extractedFiles)
    {
        try
        {
            if (!targetExistedBefore)
            {
                if (Directory.Exists(targetImageDirectory))
                {
                    Directory.Delete(targetImageDirectory, true);
                }

                return;
            }

            foreach (var file in extractedFiles)
            {
                SplashAssets.TryDelete(file);
            }
        }
        catch
        {
            // Cleanup after a failed import is best effort.
        }
    }

    /// <summary>
    ///     Best-effort removal of staged imports nobody can be looking at any more: every process
    ///     directory whose process is gone, crashed ones included, and this process's own unless
    ///     <paramref name="keepOwn" />. Never throws.
    /// </summary>
    /// <param name="stagingRoot">The parent directory holding every process's staging directory.</param>
    /// <param name="keepOwn">Whether this process's directory stays, as it does while a session is open.</param>
    internal static void CleanUpStagingDirectories(string stagingRoot, bool keepOwn)
    {
        try
        {
            if (!Directory.Exists(stagingRoot))
            {
                return;
            }

            // A reparse point where our staging root should be is an attack surface:
            // a same-user process can repoint %TEMP%\WSGM.splash-import at any
            // directory before an elevated sweep, which would then enumerate and
            // recursively delete the junction target's children. The root is always a
            // plain directory WSGM created; never follow a junction through it.
            if (IsReparsePoint(stagingRoot))
            {
                Log.Warn($"Splash staging root '{stagingRoot}' is a reparse point; skipping cleanup.");
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
            {
                var name = Path.GetFileName(directory);
                var keep = string.Equals(name, ProcessDirectoryName, StringComparison.Ordinal)
                    ? keepOwn
                    : IsRunningProcessDirectory(name);
                if (keep)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, true);
                }
                catch
                {
                    // A locked or in-use staging directory just stays for the next sweep.
                }
            }
        }
        catch
        {
            // Staging cleanup is best effort.
        }
    }

    /// <summary>
    ///     Whether a staging directory is named for a process that still runs, matched by id and
    ///     start time. A process whose start time cannot be read counts as running: never deleting
    ///     is the safe side, and the directory goes once that id is free again.
    /// </summary>
    /// <param name="name">The staging directory's name.</param>
    /// <returns>True for a matching live process or an unreadable live start time; false for invalid/stale identities.</returns>
    internal static bool IsRunningProcessDirectory(string name)
    {
        var separator = name.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0
            || !int.TryParse(name.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture,
                out var processId)
            || !long.TryParse(name.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                out var startTicks))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Not running, or it exited while being asked.
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    /// <summary>
    ///     True when the path is a reparse point (junction/symlink), so the
    ///     cleanup sweep never follows a repointed staging root. An unreadable attribute
    ///     set counts as a reparse point: the safe answer for a best-effort sweep is to
    ///     skip rather than risk following a link.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static string CurrentProcessDirectoryName()
    {
        using var current = Process.GetCurrentProcess();
        return $"{current.Id}-{current.StartTime.ToUniversalTime().Ticks}";
    }

    private static ZipArchiveEntry? FindConfigEntry(ZipArchive archive)
    {
        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName, ConfigEntryName, StringComparison.OrdinalIgnoreCase));
    }
}

using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>Export/import of <c>.wsgmsplash</c> splash-theme files: a zip archive
/// containing <c>splash.json</c> (the serialized <see cref="SplashConfig"/>) plus the
/// referenced logo/background images bundled under their deterministic names
/// (<c>logo.*</c>/<c>background.*</c>). Import extracts the images next to the user's
/// other splash assets and rewrites the config's image paths to the extracted copies;
/// malformed or incomplete archives return null with a logged warning — never an
/// exception, so a bad theme file can never break Settings.</summary>
internal static class SplashTheme
{
    private const string ConfigEntryName = "splash.json";
    private const string LogoEntryBaseName = "logo";
    private const string BackgroundEntryBaseName = "background";

    /// <summary>Writes a splash theme archive to <paramref name="path"/>.</summary>
    /// <returns>True when the file was written; false (logged) on any failure.</returns>
    internal static bool Export(SplashConfig splash, string path)
    {
        try
        {
            using var stream = File.Create(path);
            return Export(splash, stream);
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme export to '{path}' failed: {ex.Message}");
            return false;
        }
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

    /// <summary>Reads a splash theme archive, extracting any bundled images into
    /// <paramref name="targetImageDirectory"/> and rewriting the returned config's
    /// image paths to the extracted copies.</summary>
    /// <returns>The imported configuration, or null (logged) when the file is not a
    /// readable splash theme.</returns>
    internal static SplashConfig? Import(string path, string targetImageDirectory)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var configEntry = FindConfigEntry(archive);
            if (configEntry is null)
            {
                Log.Warn($"Splash theme '{path}' contains no {ConfigEntryName} — not a splash theme file.");
                return null;
            }

            SplashConfig? splash;
            using (var entryStream = configEntry.Open())
            {
                splash = JsonSerializer.Deserialize(entryStream, ConfigJsonContext.Default.SplashConfig);
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
                ExtractImage(archive, LogoEntryBaseName, targetImageDirectory) ?? splash.LogoImagePath;
            splash.BackgroundImagePath =
                ExtractImage(archive, BackgroundEntryBaseName, targetImageDirectory) ?? splash.BackgroundImagePath;
            return splash;
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme import from '{path}' failed: {ex.Message}");
            return null;
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

    /// <summary>Extracts the image entry with the given base name (any extension)
    /// into the target directory and returns the extracted file's full path, or null
    /// when the archive has no such entry.</summary>
    private static string? ExtractImage(ZipArchive archive, string entryBaseName, string targetDirectory)
    {
        foreach (var entry in archive.Entries)
        {
            // Entry names are untrusted: build the destination from the file-name
            // part only, so a crafted "..\..\logo.png" cannot escape the target dir.
            var fileName = Path.GetFileName(entry.FullName);
            if (!string.Equals(Path.GetFileNameWithoutExtension(fileName), entryBaseName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Directory.CreateDirectory(targetDirectory);
            var destination = Path.Combine(targetDirectory, fileName.ToLowerInvariant());
            entry.ExtractToFile(destination, overwrite: true);
            return destination;
        }
        return null;
    }

    private static ZipArchiveEntry? FindConfigEntry(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(Path.GetFileName(entry.FullName), ConfigEntryName, StringComparison.OrdinalIgnoreCase))
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

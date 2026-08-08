using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Copies user-picked splash images into WSGM's per-user splash asset
/// directory at save time, so the boot splash never depends on the originally
/// picked file staying in place (removable drive, Downloads cleanup, …).</summary>
public static class SplashAssets
{
    /// <summary>Gets the per-user directory that holds the materialized splash images.</summary>
    public static string Directory => Path.Combine(Log.Directory, "splash");

    /// <summary>Materializes the images referenced by <paramref name="splash"/> into
    /// <see cref="Directory"/>, rewriting the config paths to the stable copies.
    /// Never throws; IO failures are logged and leave the original path in place.</summary>
    /// <param name="splash">The splash section whose image paths are materialized in place.</param>
    internal static void Materialize(SplashConfig splash) => Materialize(splash, Directory);

    /// <summary>Materializes into an explicit target directory (test seam).</summary>
    /// <param name="splash">The splash section whose image paths are materialized in place.</param>
    /// <param name="targetDirectory">The directory that receives the stable copies.</param>
    internal static void Materialize(SplashConfig splash, string targetDirectory)
    {
        splash.LogoImagePath = MaterializeSlot(splash.LogoImagePath, "logo", targetDirectory);
        splash.BackgroundImagePath = MaterializeSlot(
            splash.BackgroundImagePath,
            "background",
            targetDirectory
        );
    }

    /// <summary>Brings one image slot into the target directory: an empty path removes
    /// stale copies, a path already inside the directory is left untouched, and any
    /// other path is copied to <c>{baseName}{ext}</c> (overwriting, stale sibling
    /// extensions deleted) with the copy's path returned as the new config value.</summary>
    private static string MaterializeSlot(string sourcePath, string baseName, string targetDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                DeleteCopies(baseName, targetDirectory, keep: null);
                return sourcePath ?? "";
            }

            var fullSource = Path.GetFullPath(sourcePath);
            var fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            if (
                string.Equals(
                    Path.GetDirectoryName(fullSource),
                    fullTarget,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return sourcePath; // Already a materialized copy — idempotent.
            }

            var destination = Path.Combine(fullTarget, baseName + Path.GetExtension(fullSource));
            System.IO.Directory.CreateDirectory(fullTarget);
            File.Copy(fullSource, destination, overwrite: true);
            DeleteCopies(baseName, fullTarget, keep: destination);
            return destination;
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"Couldn't copy splash image '{sourcePath}' into '{targetDirectory}', keeping the original path: {ex.Message}"
            );
            return sourcePath ?? "";
        }
    }

    /// <summary>Deletes every file in the target directory named <c>{baseName}.*</c>
    /// (any extension, including none) except <paramref name="keep"/>.</summary>
    private static void DeleteCopies(string baseName, string targetDirectory, string? keep)
    {
        if (!System.IO.Directory.Exists(targetDirectory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(targetDirectory))
        {
            if (
                !string.Equals(
                    Path.GetFileNameWithoutExtension(file),
                    baseName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            if (
                keep is not null
                && string.Equals(Path.GetFullPath(file), keep, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't delete stale splash image '{file}': {ex.Message}");
            }
        }
    }
}

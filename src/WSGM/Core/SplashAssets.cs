using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.Core;

/// <summary>Copies user-picked splash images into WSGM's per-user splash asset
/// directory at save time, so the boot splash never depends on the originally
/// picked file staying in place (removable drive, Downloads cleanup, …).
/// The copy is a two-phase transaction (<see cref="Prepare(SplashConfig)"/> →
/// <see cref="Transaction.Commit"/>): the live files are only replaced once the
/// config write that points at them succeeded, so a failed save can never leave
/// the persisted config referring to already-replaced images.</summary>
public static class SplashAssets
{
    // Suffix of the staged sidecar copies Prepare writes next to the live files.
    // Deterministic (no pid), and every Prepare first sweeps this slot's leftover
    // sidecars: the name embeds the source extension, so a crashed save's
    // "logo.jpg.wsgmnew" would otherwise survive a later ".png" pick forever.
    private const string StagedSuffix = ".wsgmnew";

    /// <summary>Gets the per-user directory that holds the materialized splash images.</summary>
    public static string Directory => Path.Combine(Log.Directory, "splash");

    /// <summary>Prepare + immediate commit, for callers with nothing that can fail
    /// in between. The save path deliberately does NOT use this: it must commit
    /// only after the config write succeeded.
    /// Never throws; IO failures are logged and leave the original path in place.</summary>
    /// <param name="splash">The splash section whose image paths are materialized in place.</param>
    /// <param name="targetDirectory">The directory that receives the stable copies.</param>
    internal static void Materialize(SplashConfig splash, string targetDirectory)
    {
        using var staged = Prepare(splash, targetDirectory);
        staged.Commit();
    }

    /// <summary>Stages the images referenced by <paramref name="splash"/> as sidecar
    /// files inside <see cref="Directory"/> and rewrites the config paths to the FINAL
    /// names the sidecars will take on commit. The live files stay untouched until
    /// <see cref="Transaction.Commit"/>; disposing or rolling back deletes the sidecars.
    /// Never throws; IO failures are logged and leave the original path in place.</summary>
    /// <param name="splash">The splash section whose image paths are rewritten in place.</param>
    /// <returns>The handle that commits or rolls back the staged copies.</returns>
    internal static Transaction Prepare(SplashConfig splash) => Prepare(splash, Directory);

    /// <summary>Stages into an explicit target directory (test seam).</summary>
    /// <param name="splash">The splash section whose image paths are rewritten in place.</param>
    /// <param name="targetDirectory">The directory that receives the stable copies.</param>
    /// <returns>The handle that commits or rolls back the staged copies.</returns>
    internal static Transaction Prepare(SplashConfig splash, string targetDirectory)
    {
        var transaction = new Transaction();
        splash.LogoImagePath = PrepareSlot(transaction, splash.LogoImagePath, "logo", targetDirectory);
        splash.BackgroundImagePath = PrepareSlot(
            transaction,
            splash.BackgroundImagePath,
            "background",
            targetDirectory
        );
        return transaction;
    }

    /// <summary>Stages one image slot: an empty path queues the removal of stale
    /// copies, a path already inside the directory is left untouched, and any other
    /// path is copied to <c>{baseName}{ext}{StagedSuffix}</c> with the FINAL
    /// <c>{baseName}{ext}</c> path returned as the new config value.</summary>
    private static string PrepareSlot(
        Transaction transaction,
        string sourcePath,
        string baseName,
        string targetDirectory
    )
    {
        string? stagedPath = null;
        try
        {
            var fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            DeleteStaleSidecars(baseName, fullTarget);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                // Cleared slot: the live copies only go away once the save succeeded.
                transaction.AddClear(baseName, fullTarget);
                return sourcePath ?? "";
            }

            var fullSource = Path.GetFullPath(sourcePath);
            if (
                string.Equals(
                    Path.GetDirectoryName(fullSource),
                    fullTarget,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return sourcePath; // Already a materialized copy — idempotent, nothing to stage.
            }

            var destination = Path.Combine(fullTarget, baseName + Path.GetExtension(fullSource));
            stagedPath = destination + StagedSuffix;
            System.IO.Directory.CreateDirectory(fullTarget);
            File.Copy(fullSource, stagedPath, overwrite: true);
            transaction.AddStaged(baseName, fullTarget, stagedPath, destination);
            return destination;
        }
        catch (Exception ex)
        {
            if (stagedPath is not null)
            {
                TryDelete(stagedPath); // A half-written sidecar must never survive.
            }
            Log.Warn(
                $"Couldn't copy splash image '{sourcePath}' into '{targetDirectory}', keeping the original path: {ex.Message}"
            );
            return sourcePath ?? "";
        }
    }

    /// <summary>Removes sidecars orphaned by an earlier crashed or killed save
    /// (<c>{baseName}.*{StagedSuffix}</c>), which <see cref="DeleteCopies"/> cannot
    /// match. Best effort — a locked leftover just waits for the next Prepare.</summary>
    private static void DeleteStaleSidecars(string baseName, string targetDirectory)
    {
        if (!System.IO.Directory.Exists(targetDirectory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(targetDirectory, baseName + ".*" + StagedSuffix))
        {
            TryDelete(file);
        }
    }

    /// <summary>Deletes every file in the target directory named <c>{baseName}.*</c>
    /// (any extension, including none) except <paramref name="keep"/>. Staged
    /// sidecars are never matched — their name carries the full live file name.</summary>
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

            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't delete stale splash image '{path}': {ex.Message}");
        }
    }

    /// <summary>The handle returned by <see cref="Prepare(SplashConfig)"/>: it owns the
    /// staged sidecar copies until the caller either commits them over the live files
    /// or throws them away. Neither operation throws — a commit failure is logged and
    /// covered by the splash's existing missing-image fallback.</summary>
    internal sealed class Transaction : IDisposable
    {
        private readonly List<PendingSlot> _pending = [];
        private bool _completed;

        /// <summary>Queues the removal of a slot's live copies (the slot was cleared).</summary>
        /// <param name="baseName">The slot's file base name.</param>
        /// <param name="targetDirectory">The directory holding the live copies.</param>
        internal void AddClear(string baseName, string targetDirectory) =>
            _pending.Add(new PendingSlot(baseName, targetDirectory, null, null));

        /// <summary>Queues a staged sidecar for promotion over the live file.</summary>
        /// <param name="baseName">The slot's file base name.</param>
        /// <param name="targetDirectory">The directory holding the live copies.</param>
        /// <param name="stagedPath">The sidecar written by Prepare.</param>
        /// <param name="livePath">The final path the sidecar is moved to.</param>
        internal void AddStaged(
            string baseName,
            string targetDirectory,
            string stagedPath,
            string livePath
        ) => _pending.Add(new PendingSlot(baseName, targetDirectory, stagedPath, livePath));

        /// <summary>Atomically moves every staged sidecar over its live file and drops
        /// the copies of cleared slots. Call only after the config write succeeded.</summary>
        internal void Commit()
        {
            if (_completed)
            {
                return;
            }
            _completed = true;

            foreach (var slot in _pending)
            {
                try
                {
                    if (slot.StagedPath is null || slot.LivePath is null)
                    {
                        DeleteCopies(slot.BaseName, slot.TargetDirectory, keep: null);
                        continue;
                    }

                    // Atomic replace (MoveFileEx REPLACE_EXISTING) — same directory,
                    // so the live file is never observed half-written.
                    File.Move(slot.StagedPath, slot.LivePath, overwrite: true);
                    DeleteCopies(slot.BaseName, slot.TargetDirectory, keep: slot.LivePath);
                }
                catch (Exception ex)
                {
                    // A half-committed slot leaves the config pointing at a missing or
                    // stale image, which the splash renders without it.
                    Log.Warn(
                        $"Couldn't apply the new splash image for '{slot.BaseName}' in '{slot.TargetDirectory}': {ex.Message}"
                    );
                    if (slot.StagedPath is not null)
                    {
                        TryDelete(slot.StagedPath);
                    }
                }
            }
            _pending.Clear();
        }

        /// <summary>Discards the staged sidecars, leaving every live file untouched.
        /// Idempotent, and a no-op once <see cref="Commit"/> ran.</summary>
        internal void Rollback()
        {
            if (_completed)
            {
                return;
            }
            _completed = true;

            foreach (var slot in _pending)
            {
                if (slot.StagedPath is not null)
                {
                    TryDelete(slot.StagedPath);
                }
            }
            _pending.Clear();
        }

        /// <summary>Rolls back unless the transaction was already committed.</summary>
        public void Dispose() => Rollback();

        private sealed record PendingSlot(
            string BaseName,
            string TargetDirectory,
            string? StagedPath,
            string? LivePath
        );
    }
}

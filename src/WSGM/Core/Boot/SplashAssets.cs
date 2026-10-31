using System;
using System.IO;

namespace WSGM.Core;

/// <summary>
///     Copies user-picked splash images into WSGM's per-user splash asset
///     directory at save time, so the boot splash never depends on the originally
///     picked file staying in place (removable drive, Downloads cleanup, …).
///     The copy is a two-phase transaction (<see cref="Prepare(SplashConfig, UserDataContext)" /> →
///     <see cref="SplashAssetTransaction.Commit" />): the live files are only replaced once the
///     config write that points at them succeeded, so a failed save can never leave
///     the persisted config referring to already-replaced images.
///     <para>
///         <see cref="Prepare(SplashConfig, UserDataContext)" /> is the SLOW half (a picked image may be
///         tens of megabytes) and is deliberately safe to run WITHOUT the cross-process
///         config lock: every staged sidecar carries a per-transaction GUID, so two savers
///         can never write the same sidecar. Only <see cref="SplashAssetTransaction.Commit" /> — a pair
///         of same-directory <c>File.Move</c> calls — has to run under that lock, together
///         with the config write it belongs to.
///     </para>
/// </summary>
public static class SplashAssets
{
    // Suffix of the staged sidecar copies Prepare writes next to the live files.
    // The full name is "{baseName}{ext}.{guid}.wsgmnew": the extension keeps the
    // source's, and the GUID makes the name unique per transaction so concurrent
    // savers (two Settings windows, a shell + an elevated one-shot) can stage at
    // the same time without sharing — and therefore corrupting — a sidecar.
    private const string StagedSuffix = ".wsgmnew";

    /// <summary>
    ///     Base file name of the logo slot — also the name
    ///     <see cref="SplashAssetTransaction.Commit" /> reports a failed promotion under.
    /// </summary>
    internal const string LogoSlot = "logo";

    /// <summary>
    ///     Base file name of the background slot — also the name
    ///     <see cref="SplashAssetTransaction.Commit" /> reports a failed promotion under.
    /// </summary>
    internal const string BackgroundSlot = "background";

    /// <summary>
    ///     Stages the images referenced by <paramref name="splash" /> as sidecar
    ///     files inside the owner's splash directory and rewrites the config paths to the FINAL
    ///     names the sidecars will take on commit. The live files stay untouched until
    ///     <see cref="SplashAssetTransaction.Commit" />; disposing or rolling back deletes the sidecars.
    ///     Never throws: an IO failure is logged, leaves the original path in place AND
    ///     marks that slot as failed, so <see cref="SplashAssetTransaction.Commit" /> reports it and
    ///     the save cannot claim a success that only exists in memory.
    /// </summary>
    /// <param name="splash">The splash section whose image paths are rewritten in place.</param>
    /// <returns>The handle that commits or rolls back the staged copies.</returns>
    /// <param name="context">The owner's explicit data directory.</param>
    internal static SplashAssetTransaction Prepare(SplashConfig splash, UserDataContext context)
    {
        return Prepare(splash, Path.Combine(context.Root, "splash"));
    }

    /// <summary>Stages into an explicit target directory (test seam).</summary>
    /// <param name="splash">The splash section whose image paths are rewritten in place.</param>
    /// <param name="targetDirectory">The directory that receives the stable copies.</param>
    /// <returns>The handle that commits or rolls back the staged copies.</returns>
    internal static SplashAssetTransaction Prepare(SplashConfig splash, string targetDirectory)
    {
        var transaction = new SplashAssetTransaction();
        splash.LogoImagePath = PrepareSlot(transaction, splash.LogoImagePath, LogoSlot, targetDirectory);
        splash.BackgroundImagePath = PrepareSlot(
            transaction,
            splash.BackgroundImagePath,
            BackgroundSlot,
            targetDirectory
        );
        return transaction;
    }

    /// <summary>
    ///     Stages one image slot: an empty path queues the removal of stale
    ///     copies, THIS slot's own live copy is left untouched, and any other path is
    ///     copied to <c>{baseName}{ext}.{guid}{StagedSuffix}</c> with the FINAL
    ///     <c>{baseName}{ext}</c> path returned as the new config value.
    /// </summary>
    private static string PrepareSlot(
        SplashAssetTransaction transaction,
        string? sourcePath,
        string baseName,
        string targetDirectory
    )
    {
        string? stagedPath = null;
        FileStream? stagedHandle = null;
        // A slot with a picked source MUST end up as a copy inside the target
        // directory. If it does not, the config would keep naming the user's volatile
        // pick (Downloads, a removable drive) — exactly what materialization exists to
        // eliminate — so the failure has to be REPORTED, not just logged (see the
        // catch below). A cleared slot has nothing to stage and never reports.
        var mustStage = !string.IsNullOrWhiteSpace(sourcePath);
        try
        {
            var fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            DeleteStaleSidecars(baseName, fullTarget);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                // Cleared slot: the live copies only go away once the save succeeded.
                // A whitespace-only path is a cleared slot too — every consumer treats
                // it as "no image" (IsNullOrWhiteSpace) — so it is normalized to ""
                // here rather than persisted verbatim, which would put a path nobody
                // can act on into config.json and into every shared splash theme.
                transaction.AddClear(baseName, fullTarget);
                return "";
            }

            var fullSource = Path.GetFullPath(sourcePath);
            if (IsThisSlotsOwnCopy(fullSource, baseName, fullTarget))
            {
                if (File.Exists(fullSource))
                {
                    return sourcePath; // Already a materialized copy — idempotent, nothing to stage.
                }

                // This slot's own copy is GONE (deleted by a cleanup tool, AV, or a
                // user emptying the splash folder). Short-circuiting on the path alone
                // would keep re-persisting the name of a file that no longer exists,
                // forever. It is NOT a save failure: the user changed nothing, nothing
                // could be written, and the splash already skips an element whose image
                // is missing — the honest outcome is a config that stops naming it.
                Log.Warn(
                    $"Splash image '{sourcePath}' is gone from WSGM's splash folder — clearing the '{baseName}' slot."
                );
                transaction.AddClear(baseName, fullTarget);
                return "";
            }

            var destination = Path.Combine(fullTarget, baseName + Path.GetExtension(fullSource));
            stagedPath = $"{destination}.{Guid.NewGuid():N}{StagedSuffix}";
            Directory.CreateDirectory(fullTarget);
            stagedHandle = CopyToSidecar(fullSource, stagedPath);
            transaction.AddStaged(
                baseName,
                fullTarget,
                stagedPath,
                destination,
                stagedHandle,
                fullSource
            );
            return destination;
        }
        catch (Exception ex)
        {
            stagedHandle?.Dispose();
            if (stagedPath is not null)
            {
                TryDelete(stagedPath); // A half-written sidecar must never survive.
            }

            Log.Warn(
                $"Couldn't copy splash image '{sourcePath}' into '{targetDirectory}', keeping the original path: {ex.Message}"
            );
            if (mustStage)
            {
                // Same reported-failure path a failed PROMOTION takes: the caller puts
                // the previously persisted path back into the config and fails the save
                // instead of reporting a success it did not achieve. Without this a
                // denied/full target directory or an unreadable source silently
                // persisted the volatile source path under a "Settings saved." line.
                transaction.AddFailed(baseName);
            }

            return sourcePath ?? "";
        }
    }

    /// <summary>
    ///     True only when <paramref name="fullSource" /> is the live copy THIS
    ///     slot owns: inside the splash directory AND named <c>{baseName}.{ext}</c>, the
    ///     exact shape <see cref="DeleteCopies" /> claims for the slot.
    ///     <para>
    ///         Being inside the directory is not enough. A config whose background slot
    ///         points at the LOGO slot's copy (<c>…\splash\logo.png</c> — reachable through the
    ///         file picker, a hand-edited config, or an imported theme) used to short-circuit
    ///         on the directory alone: nothing was staged, the path was persisted as-is, and
    ///         the logo slot's stale-sibling cleanup then deleted that very file on commit,
    ///         leaving the saved config naming a deleted image with no failure reported.
    ///         Anything that is not this slot's own copy is therefore staged like any other
    ///         source, which gives the slot its own independent copy.
    ///     </para>
    /// </summary>
    private static bool IsThisSlotsOwnCopy(string fullSource, string baseName, string fullTarget)
    {
        return string.Equals(
                   Path.GetDirectoryName(fullSource),
                   fullTarget,
                   StringComparison.OrdinalIgnoreCase
               )
               && string.Equals(
                   Path.GetFileNameWithoutExtension(fullSource),
                   baseName,
                   StringComparison.OrdinalIgnoreCase
               );
    }

    /// <summary>
    ///     Copies the picked image into its sidecar and KEEPS the destination
    ///     handle open until AFTER <see cref="SplashAssetTransaction.Commit" /> renamed it over the
    ///     live file. That open handle is what tells a concurrent
    ///     <see cref="DeleteStaleSidecars" /> sweep in another process (or another save in
    ///     this one) that the sidecar belongs to a LIVE transaction and must not be swept
    ///     — a GUID in the name cannot express that. Windows closes the handle if the
    ///     process dies, which is exactly when the sidecar really has become an orphan
    ///     the sweep should collect.
    ///     <para>
    ///         The share mode includes <see cref="FileShare.Delete" /> because Windows
    ///         only permits renaming a file while a handle to it is open when every open
    ///         handle granted delete sharing — and holding the handle ACROSS the promoting
    ///         rename is what closes the micro-race that releasing it first opened (a
    ///         concurrent sweep could delete the sidecar between the release and the move,
    ///         turning a healthy save into a spurious "splash image not updated"). Granting
    ///         delete sharing means a plain <c>File.Delete</c> would now succeed on a live
    ///         sidecar, so the sweep no longer probes liveness by trying to delete: it opens
    ///         the candidate with <see cref="FileShare.None" /> first (see
    ///         <see cref="DeleteStaleSidecars" />).
    ///     </para>
    /// </summary>
    private static FileStream CopyToSidecar(string sourcePath, string stagedPath)
    {
        var staged = new FileStream(
            stagedPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete
        );
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            );
            source.CopyTo(staged);
            // Flush here, not at close: after Commit's rename the handle points at the
            // LIVE file, so a first flush at that point could fail (full volume) with
            // the live image already half-written.
            staged.Flush();
            return staged;
        }
        catch
        {
            staged.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Removes sidecars orphaned by an earlier crashed or killed save
    ///     (<c>{baseName}.{unique}{StagedSuffix}</c>), which <see cref="DeleteCopies" />
    ///     cannot match. Matching is done in managed code rather than through a Win32
    ///     search pattern so 8.3 short names can never widen it. A sidecar still held open
    ///     by a live transaction is skipped; that is expected and deliberately silent,
    ///     unlike the other delete paths.
    /// </summary>
    private static void DeleteStaleSidecars(string baseName, string targetDirectory)
    {
        if (!Directory.Exists(targetDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(targetDirectory))
        {
            if (!IsSidecarOf(Path.GetFileName(file), baseName) || IsHeldByALiveTransaction(file))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch
            {
                // Locked by something else entirely: leaving it costs one stale file
                // until the next Prepare, and warning about it would put a routine
                // condition on the remote-diagnosis log.
            }
        }
    }

    /// <summary>
    ///     Tells a staged sidecar of <paramref name="baseName" /> from every other
    ///     file in the directory. The name has to be <c>{baseName}.{something}{suffix}</c>
    ///     with a NON-EMPTY middle segment: a live materialized copy that merely happens to
    ///     be named <c>{baseName}{StagedSuffix}</c> (a user picking an image with that
    ///     extension) is not a sidecar and must never be swept.
    /// </summary>
    private static bool IsSidecarOf(string name, string baseName)
    {
        return name.Length > baseName.Length + 1 + StagedSuffix.Length
               && name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(StagedSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Probes whether a sidecar is still owned by a live transaction — in
    ///     this process or another one — by opening it with no sharing at all: that only
    ///     succeeds when nobody else holds a handle. It replaces the old "the delete
    ///     failed, so it must be live" test, which stopped being a liveness signal when
    ///     <see cref="CopyToSidecar" /> started granting <see cref="FileShare.Delete" /> so
    ///     the promoting rename can run with the handle still open. A failure to open for
    ///     any other reason is treated as live too — never deleting is the safe side.
    /// </summary>
    private static bool IsHeldByALiveTransaction(string path)
    {
        try
        {
            using var probe = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None
            );
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    ///     Deletes every file in the target directory named <c>{baseName}.*</c>
    ///     (any extension, including none) except <paramref name="keep" />. Staged
    ///     sidecars are never matched — their name carries the full live file name.
    /// </summary>
    internal static void DeleteCopies(string baseName, string targetDirectory, string? keep)
    {
        if (!Directory.Exists(targetDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(targetDirectory))
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

    /// <summary>
    ///     Best-effort delete shared with the splash-theme import/export
    ///     cleanup paths: a failure is logged, never thrown.
    /// </summary>
    /// <param name="path">The file to delete.</param>
    internal static void TryDelete(string path)
    {
        FileCleanup.TryDelete(path, ex => Log.Warn($"Couldn't delete stale splash file '{path}': {ex.Message}"));
    }
}

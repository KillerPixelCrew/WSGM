using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.Core;

/// <summary>
///     The handle returned by <see cref="SplashAssets.Prepare(SplashConfig, UserDataContext)" />: it owns the
///     staged sidecar copies until the caller either commits them over the live files
///     or throws them away. Neither operation throws — a commit failure is logged AND
///     returned per slot, so the save that owns the transaction can put the previously
///     persisted path back instead of reporting a success it did not achieve.
/// </summary>
internal sealed class SplashAssetTransaction : IDisposable
{
    private readonly List<PendingSlot> _pending = [];
    private bool _completed;

    /// <summary>
    ///     Test seam replacing the stale-copy cleanup (base name, directory,
    ///     path to keep). Production never sets it; it exists so a test can prove that a
    ///     THROWING cleanup cannot turn a successful promotion into a reported failure,
    ///     which no arrangement of real files can force.
    /// </summary>
    internal Action<string, string, string?>? CleanUpStaleCopiesOverride { get; set; }

    /// <summary>Rolls back unless the transaction was already committed.</summary>
    public void Dispose()
    {
        Rollback();
    }

    /// <summary>Queues the removal of a slot's live copies (the slot was cleared).</summary>
    /// <param name="baseName">The slot's file base name.</param>
    /// <param name="targetDirectory">The directory holding the live copies.</param>
    internal void AddClear(string baseName, string targetDirectory)
    {
        _pending.Add(new PendingSlot(baseName, targetDirectory, null, null, null));
    }

    /// <summary>
    ///     Records a slot whose STAGING already failed, so
    ///     <see cref="Commit" /> reports it exactly like a failed promotion. Nothing
    ///     was written for it, so there is nothing to promote or roll back — but the
    ///     config path for this slot still names the user's volatile pick instead of a
    ///     copy inside the splash directory, which the caller must not persist as a
    ///     success.
    /// </summary>
    /// <param name="baseName">The slot's file base name.</param>
    internal void AddFailed(string baseName)
    {
        _pending.Add(new PendingSlot(baseName, "", null, null, null, StagingFailed: true));
    }

    /// <summary>Queues a staged sidecar for promotion over the live file.</summary>
    /// <param name="baseName">The slot's file base name.</param>
    /// <param name="targetDirectory">The directory holding the live copies.</param>
    /// <param name="stagedPath">The sidecar written by Prepare.</param>
    /// <param name="livePath">The final path the sidecar is moved to.</param>
    /// <param name="stagedHandle">
    ///     The still-open write handle that marks the sidecar
    ///     as owned by this live transaction; released after the promoting move.
    /// </param>
    /// <param name="sourcePath">
    ///     The picked image the sidecar was copied from — kept
    ///     so <see cref="Commit" /> can still produce the live file if the sidecar itself
    ///     is deleted by an outside actor (see <see cref="TryCopyFromSource" />).
    /// </param>
    internal void AddStaged(
        string baseName,
        string targetDirectory,
        string stagedPath,
        string livePath,
        FileStream stagedHandle,
        string sourcePath
    )
    {
        _pending.Add(
            new PendingSlot(
                baseName,
                targetDirectory,
                stagedPath,
                livePath,
                stagedHandle,
                sourcePath
            )
        );
    }

    /// <summary>
    ///     Atomically moves every staged sidecar over its live file and drops
    ///     the copies of cleared slots. Call only after the config write succeeded, and
    ///     under the same cross-process config lock as that write. Never throws.
    /// </summary>
    /// <returns>
    ///     The base names (<see cref="SplashAssets.LogoSlot" />, <see cref="SplashAssets.BackgroundSlot" />)
    ///     of the slots that did NOT end up as a live copy — empty when every slot went
    ///     live. That covers both halves of the transaction: a failed promotion (a
    ///     locked file, an AV hold, a permission error), whose slot still holds its
    ///     previous content, and a slot whose STAGING already failed in
    ///     <see cref="SplashAssets.Prepare(SplashConfig, UserDataContext)" /> (unreadable source, uncreatable or full
    ///     target directory), whose config path is still the user's picked path. In
    ///     both cases the caller must put that slot's previously persisted path back
    ///     into the config and fail the save instead of reporting success: the
    ///     persisted config must never name an image that was never written, nor the
    ///     volatile source path that materialization exists to eliminate.
    ///     <para>
    ///         For a slot that WAS staged, only the promoting move — or, when the
    ///         sidecar vanished under it, <see cref="TryCopyFromSource" /> — decides this: the stale-sibling cleanup that
    ///         follows it (an old <c>logo.jpg</c> left behind by a new <c>logo.png</c>) is
    ///         best effort and is logged, never reported — the new image IS live by then,
    ///         and reporting a failure would make the caller revert the persisted path to
    ///         an image that is no longer on disk.
    ///     </para>
    /// </returns>
    internal IReadOnlyList<string> Commit()
    {
        if (_completed)
        {
            return [];
        }

        _completed = true;

        List<string>? failed = null;
        foreach (var slot in _pending)
        {
            if (slot.StagingFailed)
            {
                // Never staged (unreadable source, uncreatable/full target): the
                // config path for this slot is still the user's picked path, so it
                // is reported the same way a failed promotion is.
                (failed ??= []).Add(slot.BaseName);
                continue;
            }

            if (slot.StagedPath is null || slot.LivePath is null)
            {
                // Cleared slot: the config now names no image, so leftover copies are
                // garbage, not a broken promotion — never reported as a failure.
                CleanUpStaleCopies(slot.BaseName, slot.TargetDirectory, null);
                continue;
            }

            try
            {
                // Atomic replace (MoveFileEx REPLACE_EXISTING) — same directory,
                // so the live file is never observed half-written. Deliberately
                // performed with the sidecar's own handle STILL OPEN (it granted
                // FileShare.Delete for exactly this): releasing first left a window
                // in which a concurrent saver's orphan sweep could delete the
                // sidecar and turn this move into a spurious failure report.
                File.Move(slot.StagedPath, slot.LivePath, true);
            }
            catch (Exception ex)
            {
                // Close before probing or deleting: a delete against a still-open
                // handle only marks the file for deletion, so the sidecar stays
                // visible — and the promoting move keeps failing — until the last
                // handle closes.
                ReleaseHandle(slot);
                if (TryCopyFromSource(slot))
                {
                    CleanUpStaleCopies(slot.BaseName, slot.TargetDirectory, slot.LivePath);
                    continue;
                }

                // Reported to the caller: this slot's live file is still the OLD
                // image, so the path this save is about to persist would point at
                // content that never landed.
                Log.Warn(
                    $"Couldn't apply the new splash image for '{slot.BaseName}' in '{slot.TargetDirectory}': {ex.Message}"
                );
                (failed ??= []).Add(slot.BaseName);
                SplashAssets.TryDelete(slot.StagedPath);
                continue;
            }

            // The handle now refers to the live file; nothing is written through it
            // any more (Prepare already flushed), so closing it is pure cleanup.
            ReleaseHandle(slot);
            CleanUpStaleCopies(slot.BaseName, slot.TargetDirectory, slot.LivePath);
        }

        _pending.Clear();
        return failed ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    ///     Last resort for a promotion whose sidecar VANISHED: copies the
    ///     originally picked image straight to the live path.
    ///     <para>
    ///         Why this exists: the sidecar's handle grants <see cref="FileShare.Delete" />
    ///         (that is what makes renaming a held-open file legal), so a plain delete
    ///         against it now succeeds — AV, a cleanup tool, or an older WSGM build's
    ///         orphan sweep can remove a perfectly healthy in-flight sidecar and turn a
    ///         good save into a reported "splash image not updated". The source the sidecar
    ///         was copied from is still on disk in that case, so the save can simply finish
    ///         the job from it.
    ///     </para>
    ///     <para>
    ///         Deliberately narrow: it runs ONLY when the sidecar is really gone
    ///         (called after the handle is closed, so a delete-pending name is resolved by
    ///         then). Every other promotion failure — a locked, denied, or occupied live
    ///         path — would fail this copy too and must keep being reported. The copy
    ///         writes the live file directly and is therefore not the atomic replace the
    ///         rename gives: with the sidecar gone that transaction is already lost, and a
    ///         half-written live image is caught by the splash's own load fallback, whereas
    ///         a false failure report reverts the user's pick.
    ///     </para>
    /// </summary>
    /// <param name="slot">The slot whose promotion just failed.</param>
    /// <returns>True when the live file now holds the new image.</returns>
    private static bool TryCopyFromSource(PendingSlot slot)
    {
        if (slot.SourcePath is null || slot.LivePath is null || File.Exists(slot.StagedPath))
        {
            return false;
        }

        try
        {
            File.Copy(slot.SourcePath, slot.LivePath, true);
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"Couldn't re-copy the splash image for '{slot.BaseName}' from '{slot.SourcePath}': {ex.Message}"
            );
            return false;
        }

        Log.Warn(
            $"Staged splash image for '{slot.BaseName}' disappeared before it could be applied — "
            + $"copied '{slot.SourcePath}' directly to '{slot.LivePath}' instead."
        );
        return true;
    }

    /// <summary>
    ///     Closes the sidecar's own write handle. Disposing a
    ///     <see cref="FileStream" /> flushes, so it CAN throw (a full or disconnected
    ///     volume) — and both <see cref="Commit" /> and <see cref="Rollback" /> promise
    ///     never to. The bytes were already flushed when the sidecar was written, so a
    ///     failure here cannot have lost image content; the handle itself is closed by
    ///     <see cref="FileStream" /> either way.
    /// </summary>
    private static void ReleaseHandle(PendingSlot slot)
    {
        try
        {
            slot.StagedHandle?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"Couldn't finish writing the staged splash image '{slot.StagedPath}': {ex.Message}"
            );
        }
    }

    /// <summary>
    ///     Drops the slot's other-extension leftovers. Best effort by contract:
    ///     the promotion already succeeded, so a failure here is logged and swallowed.
    /// </summary>
    private void CleanUpStaleCopies(string baseName, string targetDirectory, string? keep)
    {
        try
        {
            (CleanUpStaleCopiesOverride ?? SplashAssets.DeleteCopies)(baseName, targetDirectory, keep);
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"Couldn't clean up stale splash copies for '{baseName}' in '{targetDirectory}': {ex.Message}"
            );
        }
    }

    /// <summary>
    ///     Discards the staged sidecars, leaving every live file untouched.
    ///     Idempotent, and a no-op once <see cref="Commit" /> ran.
    /// </summary>
    internal void Rollback()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;

        foreach (var slot in _pending)
        {
            ReleaseHandle(slot);
            if (slot.StagedPath is not null)
            {
                SplashAssets.TryDelete(slot.StagedPath);
            }
        }

        _pending.Clear();
    }

    private sealed record PendingSlot(
        string BaseName,
        string TargetDirectory,
        string? StagedPath,
        string? LivePath,
        FileStream? StagedHandle,
        string? SourcePath = null,
        bool StagingFailed = false
    );
}

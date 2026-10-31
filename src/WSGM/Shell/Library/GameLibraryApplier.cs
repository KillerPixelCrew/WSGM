using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Writes a reviewed selection to Steam: re-reads each shortcut right before changing it, records
///     every confirmed write before decorating it with the controller override and artwork, and then
///     brings the imported titles' Steam collections in step.
/// </summary>
/// <remarks>
///     A failure stops the run without rolling back completed writes, and a write Steam did not confirm is
///     never retried. The run's phase, notes and progress are published through the owner's lifecycle.
/// </remarks>
/// <param name="owner">The service whose review and lifecycle the run reports to.</param>
/// <param name="store">Where this run's records are kept.</param>
/// <param name="writer">Opens a shortcut writer over the live client, or null when unreachable.</param>
/// <param name="readLibrary">Reads every shortcut Steam holds; throws when the library cannot be read whole.</param>
/// <param name="readShortcut">Reads one shortcut again, or answers null when Steam no longer has it.</param>
/// <param name="applyArtwork">Applies images to a confirmed app id, or null to skip.</param>
/// <param name="setControllerTarget">Writes the per-game controller override, or null to skip.</param>
/// <param name="controllerManaged">Whether anything switches the controller for a running title.</param>
/// <param name="syncCollection">Brings one Steam collection in step, or null without collections.</param>
/// <param name="readCollections">Reads collections while verifying the captured Steam account, or null.</param>
/// <param name="sources">The sources a scan can read now, which name the collections.</param>
/// <param name="artwork">The artwork stage, or null to offer only the sources' own images.</param>
/// <param name="storage">The removable storage imported content is listed on.</param>
/// <param name="emulators">The emulator view whose parser systems name the ROM collections.</param>
internal sealed class GameLibraryApplier(
    GameLibraryService owner,
    ImportStateStore store,
    Func<SteamShortcutWriter?> writer,
    Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> readLibrary,
    Func<uint, CancellationToken, Task<ExistingShortcut?>> readShortcut,
    Func<uint, IReadOnlyList<(ArtworkAsset Asset, string Url)>, CancellationToken,
        Task<IReadOnlyList<ArtworkResult>>>? applyArtwork,
    Func<string, string, ManagedControllerTarget?, bool, CancellationToken, Task<bool>>? setControllerTarget,
    Func<bool> controllerManaged,
    Func<string?, string, IReadOnlyCollection<uint>, IReadOnlyCollection<uint>, CancellationToken,
        Task<SteamCollectionSyncResult>>? syncCollection,
    Func<uint, CancellationToken, Task<IReadOnlyList<ImportedCollection>>>? readCollections,
    Func<IReadOnlyList<ILibrarySource>> sources,
    GameLibraryArtwork? artwork,
    GameLibraryCardStorage storage,
    GameLibraryEmulators emulators)
{
    private readonly SemaphoreSlim _collectionSync = new(1, 1);

    /// <summary>Whether this session can keep Steam collections for imported titles.</summary>
    internal bool CollectionsSupported => syncCollection is not null;

    /// <summary>Applies the selected entries in order, then brings the collections in step.</summary>
    /// <param name="generation">The service run the apply belongs to.</param>
    /// <param name="selected">The entries to apply, in the order they are applied.</param>
    /// <param name="launcher">The launcher a generated packaged entry points at.</param>
    /// <param name="cancellationToken">The run's own token.</param>
    /// <returns>Completion once the run finished, stopped or failed and published how.</returns>
    internal async Task ApplyAsync(
        long generation, IReadOnlyList<GameLibraryEntryState> selected, string launcher,
        CancellationToken cancellationToken)
    {
        var shortcuts = writer();
        if (shortcuts is null)
        {
            owner.Fail(generation, "Steam is not reachable, so nothing was imported.", []);
            return;
        }

        // Read once for the run. Each entry's own shortcut is read again right before its write, and
        // this run's own writes are folded in as they happen.
        var snapshot = await readLibrary(cancellationToken).ConfigureAwait(false);
        var run = new GameLibraryApplyRun(generation, shortcuts, launcher, CaptureAccount(snapshot), snapshot,
            store.Entries());
        foreach (var entry in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireAccount(run.AccountId);
            var identity = (entry.Plan.Source, entry.Plan.Key);
            var record = run.Records.GetValueOrDefault(identity);
            if (record is { HasUnresolvedLegacyMapping: true })
            {
                if (record.SteamMappings.Any(mapping => mapping.AccountId is null
                        && (mapping.AppId == 0 && !entry.Plan.Unconfirmed
                            || mapping.AppId > 0 && run.Existing.Any(shortcut => shortcut.AppId == mapping.AppId))))
                {
                    run.Problems.Add($"{entry.Plan.Name}: legacy shortcut ownership is unresolved. This title was preserved.");
                    continue;
                }

                // A new account receipt may be created, but the unknown receipt remains canonical and untouched.
                record = null;
            }
            // Re-read immediately before the write: the user may have changed the shortcut in Steam
            // since the scan, and acting on stale state is how the wrong entry is changed.
            ExistingShortcut? live = null;
            if (entry.Plan.AppId > 0)
            {
                live = await readShortcut(entry.Plan.AppId, cancellationToken).ConfigureAwait(false);
                run.Replace(entry.Plan.AppId, live);
            }

            if (Revalidate(entry, run.Existing, launcher, record, live, run.ClaimedByOthers(identity))
                is not { } current)
            {
                owner.Note(generation, $"{entry.Plan.Name} changed since the scan and was left alone.");
                continue;
            }

            if (current.Action is ImportAction.Remove)
            {
                await RemoveEntryAsync(run, entry, current, record, live, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (await WriteEntryAsync(run, entry, current, record, live, cancellationToken)
                        .ConfigureAwait(false) is not { } written)
                {
                    if (run.Stopped)
                    {
                        return;
                    }

                    continue;
                }

                await DecorateEntryAsync(run, entry, current.Action, record, written.Saved, written.AppId,
                    cancellationToken).ConfigureAwait(false);
            }

            if (run.Stopped)
            {
                return;
            }

            run.Applied++;
            owner.Progress(generation, run.Applied);
        }

        // Decoration like the artwork: a collection that cannot be brought in step is a problem the
        // user sees, and the shortcuts stay as written.
        await SyncCollectionsAsync(run.Problems, cancellationToken).ConfigureAwait(false);
        owner.Complete(generation, $"Applied {run.Applied} of {selected.Count} selected changes.", run.Problems);
    }

    /// <summary>
    ///     Keeps one Steam collection per group of imported titles in step with WSGM's records, when the
    ///     switch is on: created with the group's first confirmed titles, given the ones imported since,
    ///     and cleared of the ones WSGM no longer has. A group whose source is unticked is left alone.
    /// </summary>
    /// <param name="problems">Where a collection that could not be brought in step is reported.</param>
    /// <param name="cancellationToken">Cancels waiting on Steam.</param>
    /// <returns>Completion once every group was brought in step or reported.</returns>
    internal async Task SyncCollectionsAsync(List<string> problems, CancellationToken cancellationToken)
    {
        if (syncCollection is null || !owner.ReadSettings().CreateCollections)
        {
            return;
        }

        await _collectionSync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accountSnapshot = await readLibrary(cancellationToken).ConfigureAwait(false);
            var accountId = CaptureAccount(accountSnapshot);
            if (readCollections is null && store.LegacyCollections().Count > 0)
            {
                problems.Add("Steam collections could not be checked. Legacy collection changes remain pending.");
                return;
            }

            await ObserveLegacyCollectionsAsync(accountId, cancellationToken).ConfigureAwait(false);

            var settings = owner.ReadSettings();
            var names = GameLibraryScanner.SourceNames(sources());
            foreach (var system in emulators.Read().Systems)
            {
                names.TryAdd("rom-system:" + system.Id, system.Name);
            }

            names.TryAdd("manual", "Manual games");
            HashSet<string> unticked = new(settings.DisabledSources, StringComparer.OrdinalIgnoreCase);
            foreach (var source in settings.RomSources.Where(source => unticked.Contains(source.Id)))
            {
                unticked.Add("rom-system:" + source.SystemId);
            }

            if (unticked.Contains("manual"))
            {
                foreach (var manual in settings.ManualSources.Where(source => source.RomPath is not null))
                {
                    unticked.Add("rom-system:" + EmulatorStorage.NormalizeSystemId(manual.SystemId));
                }
            }

            var recorded = store.Collections()
                .ToDictionary(collection => collection.Group, StringComparer.OrdinalIgnoreCase);
            var importedEntries = store.Entries();
            foreach (var entry in importedEntries.Where(entry =>
                         unticked.Contains(entry.Source) && entry.Content?.SourceKind == LibrarySourceKind.Rom))
            {
                unticked.Add("rom-system:" + entry.Content!.SystemId);
            }

            var imported = importedEntries
                .Where(entry => entry.AppId != 0 && !entry.RemovedFromSteam)
                .GroupBy(CollectionGroup, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(entry => entry.AppId).Distinct().ToList(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (var group in imported.Keys.Union(recorded.Keys, StringComparer.OrdinalIgnoreCase)
                         .Order(StringComparer.Ordinal).ToList())
            {
                if (unticked.Contains(group))
                {
                    continue;
                }

                recorded.TryGetValue(group, out var record);
                var want = imported.GetValueOrDefault(group) ?? [];
                var name = record?.Name is { Length: > 0 } kept ? kept : names.GetValueOrDefault(group, group);
                List<uint> takeBack = record is null ? [] : [.. record.AppIds.Except(want)];
                if (want.Count == 0 && record is null)
                {
                    continue;
                }

                var result = await syncCollection(record?.Id, name, want, takeBack, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Outcome == SteamClientWriteOutcome.NotSent)
                {
                    problems.Add("Steam could not be reached, so the collections were not updated.");
                    return;
                }

                if (!result.Succeeded)
                {
                    // A collection Steam created before a later step failed is still WSGM's: its id is
                    // kept, so the next sync changes it instead of creating a second one.
                    if (result.Id is { Length: > 0 } createdId)
                    {
                        RequireAccount(accountId);
                        store.SaveCollection(new ImportedCollection
                        {
                            AccountId = accountId, Group = group, Id = createdId, Name = name, AppIds = [.. want]
                        });
                    }

                    problems.Add(result.Outcome == SteamClientWriteOutcome.Unknown
                        ? $"Steam did not answer while the {name} collection was being updated."
                        : $"The {name} collection could not be updated: {result.Error}");
                    continue;
                }

                RequireAccount(accountId);
                store.SaveCollection(new ImportedCollection
                {
                    AccountId = accountId, Group = group, Id = result.Id ?? string.Empty, Name = name, AppIds = [.. want]
                });
            }
        }
        catch (Exception ex) when (ex is ImportStateException or InvalidOperationException
                                      or IOException or UnauthorizedAccessException or JsonException)
        {
            problems.Add(ex.Message);
        }
        finally
        {
            _collectionSync.Release();
        }
    }

    /// <summary>Binds legacy collection receipts to the collections the confirmed account holds.</summary>
    /// <param name="accountId">The account a scan confirmed.</param>
    /// <param name="cancellationToken">Cancels waiting on Steam.</param>
    /// <returns>Completion once the receipts were bound, or at once when there are none to bind.</returns>
    internal async Task ObserveLegacyCollectionsAsync(uint accountId, CancellationToken cancellationToken)
    {
        if (readCollections is null || store.LegacyCollections().Count == 0)
        {
            return;
        }

        var observed = await readCollections(accountId, cancellationToken).ConfigureAwait(false);
        RequireAccount(accountId);
        store.BindLegacyCollections(accountId, observed);
        // Absent IDs remain unbound; an independent active-account collection may be created without erasing them.
    }

    /// <summary>Waits until no collection sync runs; every writer uses the cancelled lifetime or run token.</summary>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>Completion once the running sync, if any, released the lane.</returns>
    internal async Task JoinCollectionSyncAsync(CancellationToken cancellationToken)
    {
        await _collectionSync.WaitAsync(cancellationToken).ConfigureAwait(false);
        _collectionSync.Release();
    }

    /// <summary>Confirms the account a whole-library read belongs to is the import state's.</summary>
    /// <param name="shortcuts">The read.</param>
    /// <returns>The confirmed account.</returns>
    /// <exception cref="InvalidOperationException">The read named no account, or another one.</exception>
    internal uint CaptureAccount(IReadOnlyList<ExistingShortcut> shortcuts)
    {
        if (shortcuts is not SteamShortcutSnapshot snapshot)
        {
            throw new InvalidOperationException("Steam's account could not be confirmed. Recheck before importing.");
        }
        RequireAccount(snapshot.AccountId);
        return snapshot.AccountId;
    }

    /// <summary>The collection an imported title belongs in: its source's.</summary>
    /// <remarks>
    ///     One per launcher and one per shortcuts folder. An emulator source gives each system its
    ///     own group here once ROMs are imported.
    /// </remarks>
    private static string CollectionGroup(ImportedEntry entry)
    {
        return entry.Content is { SourceKind: LibrarySourceKind.Rom } content
            ? "rom-system:" + content.SystemId
            : entry.Source;
    }

    /// <summary>Re-checks one selected entry against the library as it is right now.</summary>
    /// <param name="entry">The entry the user selected.</param>
    /// <param name="existing">The shortcuts Steam holds, with this entry's own read a moment ago.</param>
    /// <param name="launcher">The launcher a generated packaged entry points at.</param>
    /// <param name="record">What WSGM wrote for the title before, if anything.</param>
    /// <param name="live">The entry's own shortcut as Steam has it now, when it has one.</param>
    /// <param name="claimedByOthers">The app ids other records already name, which this entry may not adopt.</param>
    /// <returns>What to do now, or null when Steam has moved and it should be left alone.</returns>
    /// <remarks>
    ///     The user's chosen action is kept; only its premise is rechecked. An Add whose entry has
    ///     appeared in the meantime becomes an Update rather than a duplicate, and anything that
    ///     names a live entry has to still find it, still own it, and for anything WSGM recorded,
    ///     still hold exactly what was recorded: a launch option the user edited between the scan and
    ///     the apply is not overwritten.
    /// </remarks>
    private static ImportPlanEntry? Revalidate(
        GameLibraryEntryState entry, IReadOnlyList<ExistingShortcut> existing, string launcher, ImportedEntry? record,
        ExistingShortcut? live, IReadOnlySet<uint> claimedByOthers)
    {
        var plan = entry.Plan;
        if (entry.Action is ImportAction.Add)
        {
            return existing.FirstOrDefault(shortcut => !claimedByOthers.Contains(shortcut.AppId)
                                                       && ImportPlan.Owns(shortcut, entry.Game, launcher))
                is { } appeared
                ? plan with { Action = ImportAction.Update, AppId = appeared.AppId }
                : plan with { Action = ImportAction.Add, AppId = 0 };
        }

        // A removal whose entry Steam no longer has still has a record and an override to clear,
        // and there is nothing left there to change under us.
        if (live is null)
        {
            return entry.Action is ImportAction.Remove ? plan : null;
        }

        if (record is not null)
        {
            return ImportPlan.OwnsRecorded(live, record) ? plan with { Action = entry.Action } : null;
        }

        // Nothing recorded: an adoption, or an adoption the user moved to another route, which has
        // only ownership to go on.
        return entry.Action is ImportAction.Adopt or ImportAction.Update && ImportPlan.Owns(live, entry.Game, launcher)
            ? plan with { Action = entry.Action }
            : null;
    }

    /// <summary>Composes what an entry's shortcut should run, without throwing.</summary>
    private static bool TryCompose(GameLibraryEntryState entry, string launcher, out ShortcutFields fields,
        out string refusal)
    {
        if (!entry.Packaged)
        {
            var route = entry.Game.CommandRoutes.FirstOrDefault(candidate => candidate.Id == entry.Route)
                        ?? entry.Game.CommandRoutes[0];
            return CommandShortcut.TryCompose(route, launcher, out fields, out refusal);
        }

        fields = new ShortcutFields(string.Empty, string.Empty, string.Empty);
        if (launcher.Length == 0)
        {
            refusal = "WSGM.PackagedLaunch is missing from this install.";
            return false;
        }

        try
        {
            fields = PackagedLauncherShortcut.Compose(launcher, entry.Plan.Key, entry.Mode,
                entry.Game.Multiplayer is MultiplayerVerdict.Multiplayer, entry.Acknowledged);
            refusal = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            refusal = ex.Message;
            return false;
        }
    }

    /// <summary>Removes one entry's shortcut, then its record, override and card listing.</summary>
    /// <remarks>Marks the run stopped when Steam did not confirm the removal.</remarks>
    private async Task RemoveEntryAsync(GameLibraryApplyRun run, GameLibraryEntryState entry, ImportPlanEntry current,
        ImportedEntry? record, ExistingShortcut? live, CancellationToken cancellationToken)
    {
        // A record whose shortcut Steam no longer has: there is nothing to ask the client to
        // delete, so nothing is asked, and only the record and its override go.
        var removed = live is null
            ? new ShortcutWriteResult(current.AppId, true, null)
            : await run.Writer.RemoveAsync(current.AppId, cancellationToken).ConfigureAwait(false);
        if (!removed.Confirmed)
        {
            Stop(run, $"{entry.Plan.Name}: {removed.Error}");
            return;
        }

        // Not cancellable: the shortcut is gone, and stopping before the record is dropped
        // would leave a removal the next scan offers again.
        await ReleaseControllerTargetAsync(current.AppId, record?.OwnsProfile == true)
            .ConfigureAwait(false);
        RequireAccount(run.AccountId);
        store.Forget(entry.Plan.Source, entry.Plan.Key, run.AccountId);
        if (record?.Content is { } removedContent
            && await storage.RemoveFromCardAsync(removedContent, CancellationToken.None).ConfigureAwait(false) is
                { } cardProblem)
        {
            run.Problems.Add(cardProblem);
        }

        run.Records.Remove((entry.Plan.Source, entry.Plan.Key));
        run.Replace(current.AppId, null);
        lock (owner.Gate)
        {
            owner.Entries.Remove(entry.Id);
        }
    }

    /// <summary>Writes one entry's shortcut and records it once Steam confirmed it.</summary>
    /// <returns>
    ///     The saved record and the confirmed app id; null when the entry was left out of the run, or when the
    ///     run stopped at it.
    /// </returns>
    private async Task<(ImportedEntry Saved, uint AppId)?> WriteEntryAsync(GameLibraryApplyRun run,
        GameLibraryEntryState entry, ImportPlanEntry current, ImportedEntry? record, ExistingShortcut? live,
        CancellationToken cancellationToken)
    {
        var artworkOnly = entry.ArtworkOnly;
        if (!artworkOnly && entry.Managed)
        {
            try
            {
                await storage.PublishImportedContentAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (GameLibraryCardStorage.IsStorageFailure(ex) && ex is not ImportStateException)
            {
                run.Problems.Add($"{entry.Plan.Name}: its storage inventory could not be saved. Unlock or reconnect the card and retry this title. {ex.Message}");
                return null;
            }
        }

        var confirmedName = current.Action == ImportAction.Add ? entry.Plan.Name
            : live is { Name.Length: > 0 } named ? named.Name : record?.Name ?? entry.Plan.Name;
        if (entry.RenameRequested && current.AppId > 0)
        {
            var renamed = live?.Name == entry.Plan.Name
                ? new ShortcutWriteResult(current.AppId, true, null)
                : await run.Writer.RenameAsync(current.AppId, entry.Plan.Name, cancellationToken)
                    .ConfigureAwait(false);
            if (!renamed.Confirmed)
            {
                Stop(run, $"{entry.Plan.Name}: {renamed.Error}");
                return null;
            }

            confirmedName = entry.Plan.Name;
        }

        ShortcutFields fields;
        ShortcutWriteResult result;
        if (artworkOnly && record is not null)
        {
            fields = new ShortcutFields(record.Target, record.StartDirectory, record.LaunchOptions);
            result = new ShortcutWriteResult(current.AppId, true, null);
        }
        else if (current.Action is ImportAction.Adopt && live is not null)
        {
            // Adoption writes nothing, so it records what the shortcut actually says. Recording
            // freshly composed fields instead would make the very next scan report that somebody
            // had changed the command.
            fields = new ShortcutFields(live.Target, live.StartDirectory, live.LaunchOptions);
            result = new ShortcutWriteResult(current.AppId, true, null);
        }
        else
        {
            if (!TryCompose(entry, run.Launcher, out fields, out var refusal))
            {
                // Nothing was written, so the run goes on to the next title.
                run.Problems.Add($"{entry.Plan.Name}: {refusal}");
                return null;
            }

            result = current.Action is ImportAction.Add
                ? await run.Writer.AddAsync(entry.Plan.Name, fields, cancellationToken).ConfigureAwait(false)
                : live is not null && live.Target == fields.Target && live.LaunchOptions == fields.LaunchOptions
                  && live.StartDirectory == fields.StartDirectory
                    ? new ShortcutWriteResult(current.AppId, true, null)
                    : await run.Writer.UpdateAsync(current.AppId, fields, cancellationToken).ConfigureAwait(false);
        }

        var saved = artworkOnly && record is not null
            ? record
            : new ImportedEntry
            {
                AccountId = run.AccountId,
                Source = entry.Plan.Source,
                Key = entry.Plan.Key,
                AppId = result.AppId,
                Name = confirmedName,
                Target = fields.Target,
                LaunchOptions = fields.LaunchOptions,
                StartDirectory = fields.StartDirectory,
                Content = entry.Managed ? entry.Game.Content?.Copy() : null,
                Mode = entry.Mode.ToString(),
                Route = entry.Packaged ? string.Empty : entry.Route,
                Acknowledged = entry.Acknowledged,
                ImportedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ConfirmedUtc = result.Confirmed
                    ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    : string.Empty,
                ArtworkApplied = record is not null && record.AppId == result.AppId ? record.ArtworkApplied : 0,
                SettledArtwork = record is null || record.AppId != result.AppId
                    ? [] : new Dictionary<ArtworkAsset, AppliedArtworkChoice>(record.SettledArtwork),

                // Whether an earlier run created this title's profile. Only such a profile may be
                // removed when its override is cleared; one the user made, or had before an
                // adoption, is theirs.
                OwnsProfile = record is { OwnsProfile: true } && record.AppId == result.AppId
            };

        if (!result.Confirmed)
        {
            if (current.Action is ImportAction.Update)
            {
                try
                {
                    var observed = await readShortcut(current.AppId, cancellationToken).ConfigureAwait(false);
                    if (observed is not null && observed.AppId == current.AppId
                                             && CommandShortcut.Same(observed, fields.Target, fields.LaunchOptions))
                    {
                        saved.Target = observed.Target;
                        saved.LaunchOptions = observed.LaunchOptions;
                        saved.ConfirmedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                        RequireAccount(run.AccountId);
                        store.Save(saved);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException
                                           && !(ex is OperationCanceledException &&
                                                cancellationToken.IsCancellationRequested))
                {
                    run.Problems.Add($"The shortcut update could not be reconciled: {ex.Message}");
                }
            }

            // An add Steam did not confirm may still exist, so it is recorded as unconfirmed and
            // never retried; the next scan offers it for the user to check. An update is recorded
            // only when the single read above observed the composed command. Otherwise the old
            // record remains, including after a partial update. Neither path retries the write.
            if (current.Action is ImportAction.Add && result.AppId > 0)
            {
                RequireAccount(run.AccountId);
                store.Save(saved);
            }

            Stop(run, $"{entry.Plan.Name}: {result.Error} The run stopped here; {run.Applied} entry/entries were "
                      + "applied and nothing was retried.");
            return null;
        }

        // Persist confirmed ownership before any slower decoration or controller-profile work.
        RequireAccount(run.AccountId);
        store.SaveApplied(saved);
        run.Records[(entry.Plan.Source, entry.Plan.Key)] = saved;
        run.Replace(result.AppId, new ExistingShortcut(result.AppId, fields.Target, fields.LaunchOptions,
            fields.StartDirectory, confirmedName));
        if (result.Mismatch is { } mismatch)
        {
            run.Problems.Add($"{entry.Plan.Name}: {mismatch}");
        }

        return (saved, result.AppId);
    }

    /// <summary>Gives a recorded shortcut its controller override and artwork, then settles its entry.</summary>
    /// <remarks>
    ///     Everything past the recorded shortcut is decoration or policy the user can redo by hand, so a
    ///     failure here is noted and the run carries on.
    /// </remarks>
    private async Task DecorateEntryAsync(GameLibraryApplyRun run, GameLibraryEntryState entry, ImportAction action,
        ImportedEntry? record, ImportedEntry saved, uint appId, CancellationToken cancellationToken)
    {
        // A shortcut the user deleted and this run replaced: its override and profile go with it.
        if (action is ImportAction.Add && entry.Plan.ReplacedAppId > 0)
        {
            await ReleaseControllerTargetAsync(entry.Plan.ReplacedAppId, record?.OwnsProfile == true)
                .ConfigureAwait(false);
            saved.OwnsProfile = false;
        }

        var ownsProfile = await WriteControllerTargetAsync(entry, action, appId, saved.OwnsProfile, run.Problems)
            .ConfigureAwait(false);
        saved.OwnsProfile = ownsProfile;
        RequireAccount(run.AccountId);
        store.SaveApplied(saved);
        var applied = 0;
        try
        {
            applied = await ApplyImagesAsync(entry, saved, action is ImportAction.Add, appId, run.Problems,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Saved even when Stop lands mid-artwork: the override and the images already applied
            // are facts about Steam now.
            saved.OwnsProfile = ownsProfile;
            saved.ArtworkApplied = Math.Max(saved.ArtworkApplied, applied);
            RequireAccount(run.AccountId);
            store.SaveApplied(saved);
        }

        lock (owner.Gate)
        {
            entry.Settle(appId, saved.ArtworkApplied, saved.SettledArtwork);
        }
    }

    /// <summary>Writes the controller override a packaged entry launches with.</summary>
    /// <returns>Whether an import now owns the title's profile.</returns>
    /// <remarks>
    ///     Only a packaged title that was written has an input mode to pin. Adopting writes nothing,
    ///     so it never touches a profile that may be the user's own, and a command title is launched
    ///     by Steam like any other non-Steam game. The write is not cancellable: it pairs with the
    ///     record just saved.
    /// </remarks>
    private async Task<bool> WriteControllerTargetAsync(
        GameLibraryEntryState entry, ImportAction action, uint appId, bool ownsProfile, List<string> problems)
    {
        if (!entry.Packaged || entry.ArtworkOnly || action is not (ImportAction.Add or ImportAction.Update)
            || setControllerTarget is null)
        {
            return ownsProfile;
        }

        if (entry.Mode is ImportMode.ControllerOnly && !controllerManaged())
        {
            // The override is still written, so it takes effect as soon as management is on, but
            // until then nothing switches the controller and the title has no controller route.
            problems.Add($"{entry.Plan.Name} launches controller only, but controller management is off, so "
                         + "nothing will switch the controller. Turn on Device Integration and controller "
                         + "management in Settings.");
        }

        try
        {
            return ownsProfile | await setControllerTarget(
                    ProfileResolver.SteamId(appId), entry.Plan.Name,
                    entry.Mode is ImportMode.ControllerOnly ? ManagedControllerTarget.Xbox360 : null,
                    ownsProfile, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warn($"Game Library: the controller override for {entry.Plan.Name} failed: {exception.Message}");
            problems.Add($"The controller override could not be written for {entry.Plan.Name}. Set Xbox 360 on "
                         + "its per-game profile in Quick Access, or save it again.");
            return ownsProfile;
        }
    }

    /// <summary>Applies an entry's artwork to its confirmed shortcut.</summary>
    /// <returns>How many images the shortcut now has from WSGM.</returns>
    private async Task<int> ApplyImagesAsync(GameLibraryEntryState entry, ImportedEntry receipt, bool created,
        uint appId, List<string> problems, CancellationToken cancellationToken)
    {
        IReadOnlyList<(ArtworkAsset Asset, string Url)> images;
        Dictionary<ArtworkAsset, ArtworkPick> picks;
        lock (owner.Gate)
        {
            images = Images(entry, created);
            picks = new Dictionary<ArtworkAsset, ArtworkPick>(entry.Picks);
        }

        var before = receipt.ArtworkApplied;
        if (images.Count == 0 || applyArtwork is null)
        {
            return before;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var results = await applyArtwork(appId, images, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < Math.Min(images.Count, results.Count); index++)
        {
            if (results[index].Succeeded && picks.TryGetValue(images[index].Asset, out var pick))
            {
                receipt.SettledArtwork[images[index].Asset] = new AppliedArtworkChoice(images[index].Url, pick.Revision);
            }
        }
        var failed = results.Where(result => !result.Succeeded).Select(result => result.Detail).Distinct().ToList();
        if (failed.Count > 0)
        {
            problems.Add($"{entry.Plan.Name}: some images could not be applied ({string.Join(" ", failed)}). "
                         + "Pick others and save again.");
        }

        return Math.Max(before, results.Count(result => result.Succeeded));
    }

    /// <summary>The images an apply writes for one entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="created">Whether this run created its shortcut, so the defaults apply too.</param>
    /// <returns>One image per artwork type at most.</returns>
    /// <remarks>
    ///     A shortcut this run created gets every slot that shows an image, the defaults included. An
    ///     entry Steam already had keeps the artwork it has, apart from what the user picked here.
    /// </remarks>
    private List<(ArtworkAsset Asset, string Url)> Images(GameLibraryEntryState entry, bool created)
    {
        var preference = owner.ReadSettings().ArtworkPreference;
        List<(ArtworkAsset Asset, string Url)> images = [];
        foreach (var type in GameLibraryArtwork.Assets)
        {
            if (entry.Picks.TryGetValue(type, out var pick))
            {
                images.Add((type, pick.Url));

                continue;
            }

            if (created && GameLibraryProjection.Preferred(GameLibraryProjection.Options(entry, type, artwork),
                    preference, true) is { } option)
            {
                images.Add((type, option.Url));
            }
        }

        return images;
    }

    /// <summary>Clears a controller override left by an earlier import. Never cancelled: it pairs with a record.</summary>
    /// <param name="appId">The app id whose override to release.</param>
    /// <param name="ownsProfile">Whether an import created the profile, so it may go once empty.</param>
    private async Task ReleaseControllerTargetAsync(uint appId, bool ownsProfile)
    {
        if (setControllerTarget is null)
        {
            return;
        }

        try
        {
            await setControllerTarget(ProfileResolver.SteamId(appId), string.Empty, null, ownsProfile, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The shortcut is already gone; a stale override is a profile the user can delete.
            Log.Warn($"Game Library: the controller override for {appId} could not be released: {exception.Message}");
        }
    }

    /// <summary>Requires the import state's account to still be the one the run confirmed.</summary>
    private void RequireAccount(uint accountId)
    {
        if (store.ActiveAccountId != accountId)
        {
            throw new InvalidOperationException("The Steam account changed. Recheck before continuing.");
        }
    }

    /// <summary>Stops the run at an entry Steam did not confirm, publishing why.</summary>
    private void Stop(GameLibraryApplyRun run, string error)
    {
        run.Stopped = true;
        owner.Fail(run.Generation, error, run.Problems);
    }
}

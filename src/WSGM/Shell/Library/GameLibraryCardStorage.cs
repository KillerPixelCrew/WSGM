using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The Game Library's removable storage: binds discovered titles to the cards they are on, offers
///     each connected card's titles as a source, keeps each card's game list in step with the titles
///     imported to and removed from it, and runs the immediate Repair and Use this card commands.
/// </summary>
/// <remarks>
///     Card writes take the availability tracker's storage gate; the immediate commands also take the
///     owner's storage lane, which scans and applies hold for their whole run.
/// </remarks>
/// <param name="owner">The service whose review and lifecycle the immediate commands follow.</param>
/// <param name="store">The import records, libraries and inventory the cards are matched against.</param>
/// <param name="tracker">The session's shortcut availability tracker, or null in an isolated importer.</param>
internal sealed class GameLibraryCardStorage(
    GameLibraryService owner,
    ImportStateStore store,
    ShortcutAvailabilityTracker? tracker)
{
    /// <summary>The session's availability tracker, or null in an isolated importer.</summary>
    internal ShortcutAvailabilityTracker? Tracker { get; } = tracker;

    /// <summary>The tracker's latest result, or an empty one in an isolated importer.</summary>
    internal ShortcutAvailabilitySnapshot Tracked => Tracker?.Current ?? ShortcutAvailabilitySnapshot.Empty;

    /// <summary>Whether an exception is one a card, a store file or its contents can raise.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>True for an I/O, access, format or import-state failure.</returns>
    internal static bool IsStorageFailure(Exception exception) => exception is IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or ArgumentException or ImportStateException;

    /// <summary>Asks the tracker to check every shortcut again.</summary>
    internal void RequestCheck()
    {
        Tracker?.Request();
    }

    /// <summary>Offers each connected card's titles for import, on this device or another.</summary>
    /// <returns>One source per connected card.</returns>
    internal IEnumerable<ILibrarySource> ManifestSources()
    {
        var cards = Tracked.Cards;
        if (cards.Count == 0)
        {
            return [];
        }

        var local = store.InventoryEntries().Select(entry => entry.Content).OfType<ManagedContentRecord>()
            .DistinctBy(content => content.Id).ToDictionary(content => content.Id, StringComparer.Ordinal);
        return cards.Select(pair => (ILibrarySource)new ManifestLibrarySource(new StorageLibraryRecord
        {
            Id = pair.Key, Name = pair.Value.Volume.Name, VolumeId = pair.Value.Volume.VolumeId,
            ManifestRevision = pair.Value.Manifest.Revision
        }, local)).ToArray();
    }

    /// <summary>Binds discovered portable content to the card it is on, adopting the card's own title id.</summary>
    /// <param name="game">The title as its source found it.</param>
    /// <param name="launcher">Whether its source is a third-party launcher, whose titles may gain the managed route.</param>
    /// <param name="known">The device's records by content id, so a known title keeps its identity.</param>
    /// <returns>The title bound to its card, or unchanged when it is on none.</returns>
    internal DiscoveredGame BindDiscoveredContent(DiscoveredGame game, bool launcher,
        IReadOnlyDictionary<string, ImportedEntry> known)
    {
        if (launcher)
        {
            game = TrackLauncherStorage(game);
        }

        if (game.Content is not { } content || !StorageLibraryStorage.IsPortable(content))
        {
            return game;
        }

        var card = Tracked.Cards.Values.FirstOrDefault(item => SameId(item.Volume.VolumeId, content.BackingPath.VolumeId));
        if (card is null)
        {
            return game;
        }

        var copy = content.Copy();
        StorageLibraryContent? match;
        try
        {
            match = StorageLibraryStorage.Match(card.Manifest, copy);
        }
        catch (InvalidDataException)
        {
            match = null;
        }

        if (match is not null)
        {
            copy.Id = match.Id;
        }

        copy.LibraryId = card.Manifest.Id;
        copy.Location = card.Volume.Name;
        var record = known.GetValueOrDefault(copy.Id);
        var source = record?.Source ?? (match is null ? game.SourceId : "manifest:" + card.Manifest.Id.ToLowerInvariant());
        var key = record?.Key ?? (match is null ? game.Key : copy.Id);
        copy.SourceId = source;
        copy.SourceKey = key;
        return game with
        {
            SourceId = source, Key = key, Content = copy,
            Routes = game.CommandRoutes.Select(route => route.IsManaged ? route with { ManagedId = copy.Id } : route).ToArray()
        };
    }

    /// <summary>Adds an imported title to its card's list before its shortcut is written.</summary>
    /// <param name="entry">The title being imported through the managed route.</param>
    /// <param name="token">Cancels the card write.</param>
    /// <remarks>
    ///     A card without a list gets one. A card whose list this device does not use yet refuses until the user
    ///     chooses Use this card. Only the managed route stores content.
    /// </remarks>
    internal async Task PublishImportedContentAsync(GameLibraryEntryState entry, CancellationToken token)
    {
        var content = entry.Game.Content!;
        var volume = Tracked.Volumes.FirstOrDefault(item => item.Removable
                                                            && SameId(item.VolumeId, content.BackingPath.VolumeId));
        if (Tracker is null || !StorageLibraryStorage.IsPortable(content) || volume is null)
        {
            return;
        }

        if (Tracked.Ambiguous.Contains(content.LibraryId))
        {
            throw new InvalidDataException("Two connected cards carry this library. Remove one before importing.");
        }

        await Tracker.StorageGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var volumeId = volume.VolumeId;
            var libraries = store.Libraries();
            var known = libraries.FirstOrDefault(item => SameId(item.VolumeId, volumeId));
            StorageLibraryManifest manifest;
            long? revision;
            var state = StorageLibraryStorage.InspectPath(volumeId, @".wsgm\library.json");
            if (state == ContentPathState.Missing && known is null)
            {
                manifest = new StorageLibraryManifest { Id = Guid.NewGuid().ToString("D"), Name = volume.Name };
                revision = null;
            }
            else
            {
                // Re-read the card right before writing: whatever is in the drive now must be the accepted card.
                manifest = StorageLibraryStorage.Read(volumeId, known?.Id);
                if (known is null)
                {
                    throw new InvalidDataException("Choose Use this card under Storage before importing to it.");
                }

                revision = manifest.Revision;
            }

            var match = StorageLibraryStorage.Match(manifest, content);
            if (match is not null && match.Id != content.Id)
            {
                throw new InvalidDataException("The library identity changed. Scan again before importing.");
            }

            var portable = StorageLibraryStorage.FromManaged(content, volumeId, content.Location);
            if (!StorageLibraryStorage.FilesPresent(portable, volumeId))
            {
                throw new IOException("The game files could not be found on the card.");
            }

            var library = known ?? new StorageLibraryRecord { Id = manifest.Id, VolumeId = volumeId };
            var existing = manifest.Entries.FirstOrDefault(item => item.Id == content.Id);
            if (existing is null || !StorageLibraryStorage.SameEntry(existing, portable))
            {
                manifest.Entries.RemoveAll(item => item.Id == content.Id);
                manifest.Entries.Add(portable);
                manifest.Name = volume.Name;
                manifest.Revision = revision.HasValue ? checked(revision.Value + 1) : 1;
                token.ThrowIfCancellationRequested();
                StorageLibraryStorage.Publish(volumeId, manifest, revision);
            }

            library.ManifestRevision = manifest.Revision;
            library.Name = volume.Name;
            store.SaveLibrary(library);
            content.LibraryId = library.Id;
            content.Location = volume.Name;
            store.SaveInventory(new ImportedEntry
            {
                Source = entry.Plan.Source, Key = entry.Plan.Key, Name = content.Name, Content = content.Copy()
            });
        }
        finally
        {
            Tracker.StorageGate.Release();
        }
    }

    /// <summary>Takes a removed title off its card's list, when the accepted card is in and nothing else uses it.</summary>
    /// <param name="content">The removed title's managed content.</param>
    /// <param name="token">Cancels the card write.</param>
    /// <returns>A problem to report, or null; a card that is out keeps its entry.</returns>
    internal async Task<string?> RemoveFromCardAsync(ManagedContentRecord content, CancellationToken token)
    {
        if (Tracker is null || content.LibraryId.Length == 0
                            || store.InventoryEntries().Any(entry => entry.Content?.Id == content.Id))
        {
            return null;
        }

        await Tracker.StorageGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (store.Libraries().FirstOrDefault(item => SameId(item.Id, content.LibraryId)) is not { } library
                || !SameId(library.VolumeId, content.BackingPath.VolumeId)
                || ManagedContentStorage.MountOf(library.VolumeId) is null)
            {
                return null;
            }

            var manifest = StorageLibraryStorage.Read(library.VolumeId, library.Id);
            if (manifest.Entries.RemoveAll(item => item.Id == content.Id) == 0)
            {
                return null;
            }

            var revision = manifest.Revision;
            manifest.Revision = checked(revision + 1);
            StorageLibraryStorage.Publish(library.VolumeId, manifest, revision);
            library.ManifestRevision = manifest.Revision;
            store.SaveLibrary(library);
            return null;
        }
        catch (Exception ex) when (IsStorageFailure(ex) && ex is not ImportStateException)
        {
            return $"{content.Name} was removed from Steam, but its card's game list could not be updated: {ex.Message}";
        }
        finally
        {
            Tracker.StorageGate.Release();
        }
    }

    /// <summary>Checks every shortcut again now, and answers once that pass has finished.</summary>
    /// <param name="cancellationToken">Cancels waiting for the pass.</param>
    /// <returns>The pass's outcome, or a refusal in an isolated importer.</returns>
    internal Task<SteamUiCommandResult> RecheckAsync(CancellationToken cancellationToken)
    {
        return Tracker?.RecheckAsync(cancellationToken)
               ?? Task.FromResult(new SteamUiCommandResult(false, "Storage checks are not available in this session."));
    }

    /// <summary>The Steam details page's Recheck: one pass, then whether the shortcut is tracked at all.</summary>
    /// <param name="appId">The shortcut's app id.</param>
    /// <param name="cancellationToken">Cancels waiting for the pass.</param>
    /// <returns>The pass's outcome, or why the shortcut has nothing to check.</returns>
    internal async Task<SteamUiCommandResult> RecheckShortcutAsync(uint appId, CancellationToken cancellationToken)
    {
        var result = await RecheckAsync(cancellationToken).ConfigureAwait(false);
        return result.Succeeded && Tracked.Find(appId) is null
            ? new SteamUiCommandResult(false, "This shortcut has nothing on local storage to check.")
            : result;
    }

    /// <summary>Rebuilds a mounted card's missing or damaged game list from the titles this device knows, now.</summary>
    /// <param name="id">The card library's UUID.</param>
    /// <param name="cancellationToken">Cancels request admission and the lane wait.</param>
    /// <returns>The recheck after the repair, or why it failed.</returns>
    internal Task<SteamUiCommandResult> RepairLibraryAsync(string id, CancellationToken cancellationToken)
    {
        return StorageCommandAsync(id, "Repair library", token =>
        {
            var known = store.Libraries().FirstOrDefault(item => SameId(item.Id, id))
                        ?? throw new InvalidDataException("That library is no longer known. Choose Recheck.");
            if (ManagedContentStorage.MountOf(known.VolumeId) is null)
            {
                throw new InvalidDataException($"Insert {known.Name} before repairing it.");
            }

            var manifest = new StorageLibraryManifest
            {
                Id = known.Id, Name = known.Name, Revision = checked(known.ManifestRevision + 1),
                Entries =
                [
                    .. store.InventoryEntries().Select(entry => entry.Content).OfType<ManagedContentRecord>()
                        .Where(content => SameId(content.LibraryId, id) && SameId(content.BackingPath.VolumeId, known.VolumeId))
                        .DistinctBy(content => content.Id)
                        .Select(content => StorageLibraryStorage.FromManaged(content, known.VolumeId, content.Location))
                ]
            };
            known.RepairRequired = true;
            token.ThrowIfCancellationRequested();
            StorageLibraryStorage.Repair(known, manifest);
            known.ManifestRevision = manifest.Revision;
            known.RepairRequired = false;
            store.SaveLibrary(known);
        }, cancellationToken);
    }

    /// <summary>Launches a connected card's titles from the volume it is on now, accepting that binding immediately.</summary>
    /// <param name="id">The card library's UUID.</param>
    /// <param name="cancellationToken">Cancels request admission and the lane wait.</param>
    /// <returns>The recheck after the binding, or why it failed.</returns>
    internal Task<SteamUiCommandResult> UseLibraryAsync(string id, CancellationToken cancellationToken)
    {
        return StorageCommandAsync(id, "Use this card", token =>
        {
            if (!Tracked.Cards.TryGetValue(id, out var card) || Tracked.Ambiguous.Contains(id))
            {
                throw new InvalidDataException("Insert the card, then choose Recheck before using it.");
            }

            // Re-read the card through its volume right before acting on what the last pass saw.
            var manifest = StorageLibraryStorage.Read(card.Volume.VolumeId, id);
            var candidate = new StorageLibraryRecord
            {
                Id = manifest.Id, Name = card.Volume.Name, VolumeId = card.Volume.VolumeId,
                ManifestRevision = manifest.Revision
            };
            var recipes = manifest.Entries.ToDictionary(item => item.Id, StringComparer.Ordinal);
            List<ImportedEntry> rebound = [];
            foreach (var entry in store.InventoryEntries().Where(entry => entry.Content is { } content
                         && SameId(content.LibraryId, id)))
            {
                var local = entry.Content!.Copy();
                RebindVolume(local, candidate.VolumeId);
                var updated = recipes.GetValueOrDefault(local.Id) is { } recipe
                    ? StorageLibraryStorage.ToManaged(recipe, candidate, entry.Source, local)
                    : local;
                updated.SourceKey = entry.Key;
                if (recipes.GetValueOrDefault(local.Id)?.SourceRoot is null)
                {
                    updated.SourceRoot = local.SourceRoot.Copy();
                }

                entry.Content = updated;
                rebound.Add(entry);
            }

            token.ThrowIfCancellationRequested();
            store.SaveInventories(rebound);
            store.SaveLibrary(candidate);
            lock (owner.Gate)
            {
                foreach (var entry in owner.Entries.Values)
                {
                    if (rebound.FirstOrDefault(item => item.Source == entry.Plan.Source && item.Key == entry.Plan.Key)
                        is { Content: { } accepted })
                    {
                        entry.Game = entry.Game with { Content = accepted.Copy() };
                    }
                }
            }
        }, cancellationToken);
    }

    /// <summary>Runs one immediate storage command under the storage lane and the card gate, then rechecks.</summary>
    private async Task<SteamUiCommandResult> StorageCommandAsync(string id, string action,
        Action<CancellationToken> command, CancellationToken cancellationToken)
    {
        lock (owner.Gate)
        {
            if (owner.Guard() is { } refusal)
            {
                return refusal;
            }
        }

        if (Tracker is null || id.Length == 0)
        {
            return new SteamUiCommandResult(false, "That storage is no longer listed.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.ShutdownToken);
        try
        {
            await owner.StorageLane.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await Tracker.StorageGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await Task.Run(() => command(linked.Token), linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    Tracker.StorageGate.Release();
                }
            }
            finally
            {
                owner.StorageLane.Release();
            }
        }
        catch (Exception ex) when (IsStorageFailure(ex) || ex is InvalidOperationException)
        {
            Log.Change("library.storage.action." + id, $"{action} failed: {ex.Message}");
            return new SteamUiCommandResult(false, ex.Message);
        }

        Log.Change("library.storage.action." + id, $"{action} done.");
        owner.NotifyManagedEntriesChanged();
        return await Tracker.RecheckAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives a launcher title on removable storage its own executable as the managed route.</summary>
    /// <remarks>
    ///     Like Steam ROM Manager: the game's executable becomes a portable recipe that enters the card's list,
    ///     launches through the content check and follows its volume across drive letters. Every other route keeps
    ///     no content record; its shortcut's own folder decides its availability.
    /// </remarks>
    private DiscoveredGame TrackLauncherStorage(DiscoveredGame game)
    {
        if (game.Content is not null || game.InstallPath.Length == 0 || !Path.IsPathFullyQualified(game.InstallPath)
            || game.CommandRoutes.FirstOrDefault(route => route is { Id: "direct", Follows: false }) is not { } direct
            || !StoragePaths.IsUnder(game.InstallPath, direct.Target))
        {
            return game;
        }

        var root = ManagedContentStorage.CapturePath(game.InstallPath, true);
        if (Tracked.Volumes.FirstOrDefault(volume => volume.Removable && SameId(volume.VolumeId, root.VolumeId))
            is not { } storage)
        {
            return game;
        }

        var directory = direct.StartDirectory.Length > 0
            ? direct.StartDirectory : Path.GetDirectoryName(direct.Target) ?? "";
        var managed = ManagedContentStorage.CreateRecord(game.SourceId, game.Key, game.Name,
            LibrarySourceKind.Folder, storage.Name, direct.Target, binding: ManagedContentStorage.BindPath(root, direct.Target));
        managed.SourceRoot = root;
        managed.Program = managed.BackingPath.Copy();
        managed.WorkingDirectory = ManagedContentStorage.BindPath(root, directory, true);
        managed.RawArguments = direct.LaunchOptions;
        return game with
        {
            Content = managed,
            Routes = game.CommandRoutes.Select(route => ReferenceEquals(route, direct)
                ? route with
                {
                    Id = "managed", ManagedId = managed.Id,
                    Evidence = route.Evidence + " WSGM checks its storage first, so it follows the card across drive letters."
                }
                : route).ToArray()
        };
    }

    private static bool SameId(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static void RebindVolume(ManagedContentRecord content, string volumeId)
    {
        var previous = content.BackingPath.VolumeId;
        foreach (var path in content.Paths().Append(content.SourceRoot))
        {
            if (!SameId(path.VolumeId, previous))
            {
                continue;
            }
            path.RelativePath = ManagedContentStorage.NormalizeLocalRelativePath(path.RelativePath);
            path.VolumeId = volumeId;
            path.AbsolutePath = ManagedContentStorage.ResolveLibraryPath(volumeId, path.RelativePath);
        }
    }
}

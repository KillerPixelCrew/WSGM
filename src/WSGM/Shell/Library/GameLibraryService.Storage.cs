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

internal sealed partial class GameLibraryService
{
    // Scan, apply and the immediate storage commands share this lane; card writes also take the tracker's gate.
    private readonly SemaphoreSlim _storageLane = new(1, 1);
    private readonly ShortcutAvailabilityTracker? _tracker;

    /// <summary>The tracker's latest result, or an empty one in an isolated importer.</summary>
    private ShortcutAvailabilitySnapshot Tracked => _tracker?.Current ?? ShortcutAvailabilitySnapshot.Empty;

    private uint CaptureAccount(IReadOnlyList<ExistingShortcut> shortcuts)
    {
        if (shortcuts is not SteamShortcutSnapshot snapshot)
        {
            throw new InvalidOperationException("Steam's account could not be confirmed. Recheck before importing.");
        }
        RequireAccount(snapshot.AccountId);
        return snapshot.AccountId;
    }

    private void RequireAccount(uint accountId)
    {
        if (_store.ActiveAccountId != accountId)
        {
            throw new InvalidOperationException("The Steam account changed. Recheck before continuing.");
        }
    }

    /// <summary>Whether a record's content can travel on its card: a directly launched recipe bound to a volume.</summary>
    private static bool PortableContent(ManagedContentRecord content)
    {
        return content.SourceKind is LibrarySourceKind.Rom or LibrarySourceKind.Folder or LibrarySourceKind.Manual
               && content.BackingPath.VolumeId.Length > 0;
    }

    private static bool StorageFailure(Exception exception) => exception is IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or ArgumentException or ImportStateException;

    private static bool SameId(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private void OnAvailabilityChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // The connected cards are sources too; the next read lists the ones in the drives now.
            _sourceCache = null;
            Publish();
        }

        Notify();
    }

    /// <summary>The tracker's status for an entry's shortcut, by app id, else by its managed content id.</summary>
    private ShortcutStatus? StatusOf(Entry entry)
    {
        var tracked = Tracked;
        return tracked.Find(entry.AppId)
               ?? (entry.Game.Content is { } content
                   ? tracked.Shortcuts.FirstOrDefault(status => status.ContentId == content.Id)
                   : null);
    }

    /// <summary>Offers each connected card's titles for import, on this device or another.</summary>
    private IEnumerable<ILibrarySource> ManifestSources()
    {
        var cards = Tracked.Cards;
        if (cards.Count == 0)
        {
            return [];
        }

        var local = _store.InventoryEntries().Select(entry => entry.Content).OfType<ManagedContentRecord>()
            .DistinctBy(content => content.Id).ToDictionary(content => content.Id, StringComparer.Ordinal);
        return cards.Select(pair => (ILibrarySource)new ManifestLibrarySource(new StorageLibraryRecord
        {
            Id = pair.Key, Name = pair.Value.Volume.Name, VolumeId = pair.Value.Volume.VolumeId,
            ManifestRevision = pair.Value.Manifest.Revision
        }, local)).ToArray();
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

    /// <summary>Binds discovered portable content to the card it is on, adopting the card's own title id.</summary>
    /// <param name="game">The title as its source found it.</param>
    /// <param name="launcher">Whether its source is a third-party launcher, whose titles may gain the managed route.</param>
    /// <param name="known">The device's records by content id, so a known title keeps its identity.</param>
    private DiscoveredGame BindDiscoveredContent(DiscoveredGame game, bool launcher,
        IReadOnlyDictionary<string, ImportedEntry> known)
    {
        if (launcher)
        {
            game = TrackLauncherStorage(game);
        }

        if (game.Content is not { } content || !PortableContent(content))
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
    /// <remarks>
    ///     A card without a list gets one. A card whose list this device does not use yet refuses until the user
    ///     chooses Use this card. Only the managed route stores content.
    /// </remarks>
    private async Task PublishImportedContentAsync(Entry entry, CancellationToken token)
    {
        var content = entry.Game.Content!;
        var volume = Tracked.Volumes.FirstOrDefault(item => item.Removable
                                                            && SameId(item.VolumeId, content.BackingPath.VolumeId));
        if (_tracker is null || !PortableContent(content) || volume is null)
        {
            return;
        }

        if (Tracked.Ambiguous.Contains(content.LibraryId))
        {
            throw new InvalidDataException("Two connected cards carry this library. Remove one before importing.");
        }

        await _tracker.StorageGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var volumeId = volume.VolumeId;
            var libraries = _store.Libraries();
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
            foreach (var path in new[] { portable.BackingPath }.Concat(portable.RequiredPaths))
            {
                if (StorageLibraryStorage.InspectPath(volumeId, path.RelativePath, path.Directory)
                    != ContentPathState.Present)
                {
                    throw new IOException("The game files could not be found on the card.");
                }
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
            _store.SaveLibrary(library);
            content.LibraryId = library.Id;
            content.Location = volume.Name;
            _store.SaveInventory(new ImportedEntry
            {
                Source = entry.Plan.Source, Key = entry.Plan.Key, Name = content.Name, Content = content.Copy()
            });
        }
        finally
        {
            _tracker.StorageGate.Release();
        }
    }

    /// <summary>Takes a removed title off its card's list, when the accepted card is in and nothing else uses it.</summary>
    /// <returns>A problem to report, or null; a card that is out keeps its entry.</returns>
    private async Task<string?> RemoveFromCardAsync(ManagedContentRecord content, CancellationToken token)
    {
        if (_tracker is null || content.LibraryId.Length == 0
                             || _store.InventoryEntries().Any(entry => entry.Content?.Id == content.Id))
        {
            return null;
        }

        await _tracker.StorageGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_store.Libraries().FirstOrDefault(item => SameId(item.Id, content.LibraryId)) is not { } library
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
            _store.SaveLibrary(library);
            return null;
        }
        catch (Exception ex) when (StorageFailure(ex) && ex is not ImportStateException)
        {
            return $"{content.Name} was removed from Steam, but its card's game list could not be updated: {ex.Message}";
        }
        finally
        {
            _tracker.StorageGate.Release();
        }
    }

    /// <summary>Checks every shortcut again now, and answers once that pass has finished.</summary>
    public Task<SteamUiCommandResult> RecheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        return _tracker?.RecheckAsync(cancellationToken)
               ?? Task.FromResult(new SteamUiCommandResult(false, "Storage checks are not available in this session."));
    }

    /// <summary>The Steam details page's Recheck: one pass, then whether the shortcut is tracked at all.</summary>
    internal async Task<SteamUiCommandResult> RecheckShortcutAsync(uint appId, CancellationToken cancellationToken)
    {
        var result = await RecheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        return result.Succeeded && Tracked.Find(appId) is null
            ? new SteamUiCommandResult(false, "This shortcut has nothing on local storage to check.")
            : result;
    }

    /// <summary>Rebuilds a mounted card's missing or damaged game list from the titles this device knows, now.</summary>
    public Task<SteamUiCommandResult> RepairLibraryAsync(string id, CancellationToken cancellationToken)
    {
        return StorageCommandAsync(id, "Repair library", token =>
        {
            var known = _store.Libraries().FirstOrDefault(item => SameId(item.Id, id))
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
                    .. _store.InventoryEntries().Select(entry => entry.Content).OfType<ManagedContentRecord>()
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
            _store.SaveLibrary(known);
        }, cancellationToken);
    }

    /// <summary>Launches a connected card's titles from the volume it is on now, accepting that binding immediately.</summary>
    public Task<SteamUiCommandResult> UseLibraryAsync(string id, CancellationToken cancellationToken)
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
            foreach (var entry in _store.InventoryEntries().Where(entry => entry.Content is { } content
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
            _store.SaveInventories(rebound);
            _store.SaveLibrary(candidate);
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
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
        lock (_gate)
        {
            if (Guard() is { } refusal)
            {
                return refusal;
            }
        }

        if (_tracker is null || id.Length == 0)
        {
            return new SteamUiCommandResult(false, "That storage is no longer listed.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            await _storageLane.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await _tracker.StorageGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await Task.Run(() => command(linked.Token), linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    _tracker.StorageGate.Release();
                }
            }
            finally
            {
                _storageLane.Release();
            }
        }
        catch (Exception ex) when (StorageFailure(ex) || ex is InvalidOperationException)
        {
            Log.Change("library.storage.action." + id, $"{action} failed: {ex.Message}");
            return new SteamUiCommandResult(false, ex.Message);
        }

        Log.Change("library.storage.action." + id, $"{action} done.");
        NotifyManagedEntriesChanged();
        return await _tracker.RecheckAsync(cancellationToken).ConfigureAwait(false);
    }

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

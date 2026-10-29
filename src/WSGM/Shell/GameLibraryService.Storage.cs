using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

internal sealed partial class GameLibraryService
{
    // Scan metadata, manifest publication, imports and automatic cleanup share this lane.
    private readonly SemaphoreSlim _storageLane = new(1, 1);
    private readonly Func<uint, CancellationToken, Task<bool?>> _isShortcutRunning;
    private readonly Dictionary<string, string> _storageActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StorageLibraryRecord> _stagedStorage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameLibraryStorage> _storagePublished = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StorageLibraryRecord> _storageCandidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StorageLibraryManifest> _storageManifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _eligibleVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguousLibraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _storageMounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImportedEntry> _knownContent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StorageLibraryManifest> _manifestPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _storageObservation = new();
    private string? _storageError;

    private CancellationTokenSource LinkStorageObservation(CancellationToken token)
    {
        lock (_gate)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token, _storageObservation.Token);
        }
    }

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

    private static bool PortableContent(ManagedContentRecord content)
    {
        return content.LaunchKind == ManagedLaunchKind.Direct
               && content.SourceKind is LibrarySourceKind.Rom or LibrarySourceKind.Folder or LibrarySourceKind.Manual
               && content.BackingPath.VolumeId.Length > 0;
    }

    private static bool StorageFailure(Exception exception) => exception is IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or ArgumentException or ImportStateException;

    private static bool SameVolume(string first, string second) =>
        string.Equals(first.TrimEnd('\\'), second.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool SameLibrary(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private void RefreshStorage(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var inventory = StorageInventory.Read();
        Dictionary<string, string> volumes = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> mounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in inventory.ReadyExternalVolumes())
        {
            token.ThrowIfCancellationRequested();
            if (!NativeStorage.TryGetVolumeGuidPath(volume.Letter, out var guid) || guid is null)
            {
                continue;
            }
            using var handle = NativeStorage.OpenVolumeForQueryPath(guid.TrimEnd('\\'));
            if (handle.IsInvalid || !NativeStorage.TryGetDeviceNumber(handle, out var type, out var disk)
                || type != NativeStorage.FileDeviceDisk || inventory.FindDisk(disk) is null)
            {
                continue;
            }
            var label = NativeStorage.TryGetVolumeInformation(guid, out var currentLabel, out _)
                ? currentLabel : volume.Label;
            volumes[guid] = StorageName(guid, label);
            mounts[guid] = volume.MountPath;
        }
        MigrateStorage(volumes, mounts, token);
        var known = _store.Libraries();
        var records = _store.InventoryEntries();
        Dictionary<string, StorageLibraryRecord> candidates = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, StorageLibraryManifest> manifests = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ambiguous = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GameLibraryStorage> published = new(StringComparer.OrdinalIgnoreCase);
        foreach (var library in known)
        {
            published[library.Id] = new GameLibraryStorage(library.Id, library.Name, "Disconnected",
                "Connect the expected storage to check its games.",
                records.Count(record => record.Content?.LibraryId == library.Id && !record.Content.InventoryRemoved),
                false, false);
        }
        foreach (var (volumeId, name) in volumes)
        {
            token.ThrowIfCancellationRequested();
            var expected = known.FirstOrDefault(library => SameVolume(library.VolumeId, volumeId));
            try
            {
                var manifest = StorageLibraryStorage.Read(volumeId);
                if (candidates.ContainsKey(manifest.Id))
                {
                    ambiguous.Add(manifest.Id);
                    published[manifest.Id] = new GameLibraryStorage(manifest.Id, name, "Ambiguous storage",
                        "Two connected volumes claim this library. Disconnect the duplicate before checking.",
                        manifest.Entries.Count, false, false);
                    continue;
                }
                var accepted = known.FirstOrDefault(library => SameLibrary(library.Id, manifest.Id));
                var candidate = new StorageLibraryRecord
                {
                    Id = manifest.Id, Name = name, VolumeId = volumeId, LastMountPath = mounts[volumeId],
                    ManifestRevision = manifest.Revision
                };
                candidates[manifest.Id] = candidate;
                manifests[manifest.Id] = manifest;
                var binding = accepted is not null && SameVolume(accepted.VolumeId, volumeId);
                published[manifest.Id] = new GameLibraryStorage(manifest.Id, name,
                    binding ? "Available" : "Review storage",
                    binding ? "" : "Review this library and Apply to accept its storage on this device.",
                    manifest.Entries.Count, false, !binding);
            }
            catch (StorageLibraryUnsupportedVersionException ex)
            {
                if (expected is { RepairRequired: false })
                {
                    expected.RepairRequired = true;
                    token.ThrowIfCancellationRequested();
                    _store.SaveLibrary(expected);
                }
                var libraryId = expected?.Id ?? "volume:" + volumeId;
                published[libraryId] = new GameLibraryStorage(libraryId, expected?.Name ?? name,
                    "Update WSGM", ex.Message,
                    expected is null ? 0 : records.Count(record => record.Content?.LibraryId == expected.Id), false, false);
            }
            catch (Exception ex) when (StorageFailure(ex) && ex is not ImportStateException)
            {
                if (expected is null)
                {
                    continue;
                }
                var repair = ex is FileNotFoundException or DirectoryNotFoundException or JsonException
                    or InvalidDataException;
                // A failed read cannot make cached recipes trustworthy. Only a successful
                // reconciliation clears this launch guard; repair remains an explicit action.
                if (!expected.RepairRequired)
                {
                    expected.RepairRequired = true;
                    token.ThrowIfCancellationRequested();
                    _store.SaveLibrary(expected);
                }
                published[expected.Id] = new GameLibraryStorage(expected.Id, expected.Name, "Check failed",
                    ex.Message, records.Count(record => record.Content?.LibraryId == expected.Id
                                                       && !record.Content.InventoryRemoved), repair, false);
            }
        }
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }
            _eligibleVolumes.Clear();
            foreach (var pair in volumes)
            {
                _eligibleVolumes.Add(pair.Key, pair.Value);
            }
            _storageCandidates.Clear();
            _storageManifests.Clear();
            _manifestPaths.Clear();
            _knownContent.Clear();
            foreach (var record in records.Where(record => record.Content is not null))
            {
                _knownContent.TryAdd(record.Content!.Id, record);
            }
            _ambiguousLibraries.Clear();
            foreach (var libraryId in ambiguous)
            {
                _ambiguousLibraries.Add(libraryId);
            }
            foreach (var pair in candidates.Where(pair => !ambiguous.Contains(pair.Key)))
            {
                _storageCandidates.Add(pair.Key, pair.Value);
                _storageManifests.Add(pair.Key, manifests[pair.Key]);
                foreach (var group in manifests[pair.Key].Entries.GroupBy(entry => entry.BackingPath.RelativePath,
                             StringComparer.OrdinalIgnoreCase))
                {
                    _manifestPaths[pair.Key + ":" + group.Key.Replace('/', '\\')] = new StorageLibraryManifest
                    {
                        Id = manifests[pair.Key].Id, Name = manifests[pair.Key].Name,
                        Revision = manifests[pair.Key].Revision, Entries = group.ToList()
                    };
                }
            }
            var previous = _storagePublished.Values.OrderBy(library => library.Id, StringComparer.Ordinal).ToArray();
            _storageMounts.Clear();
            foreach (var pair in mounts)
            {
                _storageMounts.Add(pair.Key, pair.Value);
            }
            foreach (var (id, action) in _storageActions.ToArray())
            {
                if (!published.TryGetValue(id, out var row) || ambiguous.Contains(id)
                    || action == "repair" && !row.CanRepair
                    || action == "acceptBinding" && (!row.CanAcceptBinding || !candidates.ContainsKey(id)))
                {
                    _storageActions.Remove(id);
                    _stagedStorage.Remove(id);
                    _notes.Add("A staged storage action was cleared because its storage or preconditions changed. Recheck and review it again.");
                }
            }
            _storagePublished.Clear();
            foreach (var pair in published)
            {
                _storagePublished[pair.Key] = pair.Value with
                {
                    StagedAction = _storageActions.GetValueOrDefault(pair.Key, "")
                };
            }
            _sourceCache = null;
            if (!previous.SequenceEqual(_storagePublished.Values.OrderBy(library => library.Id, StringComparer.Ordinal)))
            {
                Publish();
            }
        }
    }

    private static string StorageName(string volumeId, string fallback)
    {
        foreach (var relative in new[] { SteamLibraryVdf.CardFolderName, "" })
        {
            try
            {
                var marker = Path.Combine(relative, "libraryfolder.vdf");
                if (StorageLibraryStorage.InspectPath(volumeId, marker).State == StorageLibraryPathState.Present
                    && SteamLibraryMarker.TryRead(ManagedContentStorage.ResolveLibraryPath(volumeId, relative),
                        out _, out var label) && !string.IsNullOrWhiteSpace(label))
                {
                    return label;
                }
            }
            catch (Exception ex) when (StorageFailure(ex))
            {
                Log.Change("library.storage.label", "The storage label could not be read: " + ex.Message);
            }
        }
        return string.IsNullOrWhiteSpace(fallback) ? "Game library" : fallback;
    }

    private void MigrateStorage(IReadOnlyDictionary<string, string> volumes,
        IReadOnlyDictionary<string, string> mounts, CancellationToken token)
    {
        var libraries = _store.Libraries();
        var legacy = _store.InventoryEntries().Where(entry => entry.Content is { LibraryId.Length: 0 } content
            && PortableContent(content) && !content.InventoryRemoved)
            .GroupBy(entry => entry.Content!.BackingPath.VolumeId, StringComparer.OrdinalIgnoreCase);
        foreach (var group in legacy)
        {
            if (!volumes.TryGetValue(group.Key, out var name))
            {
                continue;
            }
            token.ThrowIfCancellationRequested();
            var existing = libraries.FirstOrDefault(library => SameVolume(library.VolumeId, group.Key));
            if (existing is { MigrationPending: false })
            {
                continue;
            }
            try
            {
                StorageLibraryManifest manifest;
                var found = StorageLibraryStorage.InspectPath(group.Key, @".wsgm\library.json");
                if (found.State == StorageLibraryPathState.Missing && existing is null)
                {
                    manifest = new StorageLibraryManifest
                    {
                        Id = Guid.NewGuid().ToString("D"), Name = name,
                        Entries = group.Select(entry => StorageLibraryStorage.FromManaged(entry.Content!, group.Key,
                            entry.Content!.Location)).ToList()
                    };
                    token.ThrowIfCancellationRequested();
                    StorageLibraryStorage.Publish(group.Key, manifest, expectedRevision: null);
                }
                else if (found.State == StorageLibraryPathState.Present && existing is not null)
                {
                    manifest = StorageLibraryStorage.Read(group.Key, existing?.Id);
                }
                else if (found.State == StorageLibraryPathState.Present)
                {
                    manifest = StorageLibraryStorage.Read(group.Key);
                    var expectedRecipes = group.Select(entry => StorageLibraryStorage.FromManaged(entry.Content!, group.Key,
                            entry.Content!.Location)).OrderBy(entry => entry.Id, StringComparer.Ordinal)
                        .Select(entry => JsonSerializer.Serialize(entry, StorageLibraryJsonContext.Default.StorageLibraryContent));
                    var observedRecipes = manifest.Entries.OrderBy(entry => entry.Id, StringComparer.Ordinal)
                        .Select(entry => JsonSerializer.Serialize(entry, StorageLibraryJsonContext.Default.StorageLibraryContent));
                    if (manifest.Revision != 1 || !observedRecipes.SequenceEqual(expectedRecipes))
                    {
                        continue;
                    }
                    // Recover only our exact interrupted initial publication; foreign inventories still need review.
                }
                else
                {
                    continue;
                }
                var library = existing ?? new StorageLibraryRecord
                {
                    Id = manifest.Id, Name = name, VolumeId = group.Key, LastMountPath = mounts[group.Key]
                };
                var manifestIds = manifest.Entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                if (existing is null && !group.All(entry => manifestIds.Contains(entry.Content!.Id)))
                {
                    continue;
                }
                List<ImportedEntry> migrated = [];
                foreach (var entry in group)
                {
                    var content = entry.Content!;
                    if (!manifestIds.Contains(content.Id))
                    {
                        continue;
                    }
                    content.LibraryId = library.Id;
                    content.LibraryBindingAccepted = true;
                    content.Location = name;
                    token.ThrowIfCancellationRequested();
                    migrated.Add(entry);
                }
                if (migrated.Count == 0)
                {
                    continue;
                }
                library.ManifestRevision = manifest.Revision;
                library.MigrationPending = true;
                _store.SaveLibrary(library);
                _store.SaveInventories(migrated);
                library.MigrationPending = false;
                _store.SaveLibrary(library);
            }
            catch (Exception ex) when (StorageFailure(ex) && ex is not ImportStateException)
            {
                Log.Change("library.migration." + group.Key, "Library migration is pending: " + ex.Message);
                lock (_gate)
                {
                    _notes.Add("Library migration is pending: " + ex.Message);
                }
            }
        }
    }

    private IEnumerable<ILibrarySource> ManifestSources()
    {
        var local = _store.InventoryEntries().Where(entry => entry.Content is { InventoryRemoved: false })
            .GroupBy(entry => entry.Content!.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Content!, StringComparer.Ordinal);
        return _storageCandidates.Values.Select(library => (ILibrarySource)new ManifestLibrarySource(library, local))
            .ToArray();
    }

    private DiscoveredGame BindDiscoveredContent(DiscoveredGame game)
    {
        if (game.Content is not { } content || !PortableContent(content))
        {
            return game;
        }
        var copy = content.Copy();
        var library = _storageCandidates.Values.FirstOrDefault(item => SameVolume(item.VolumeId, copy.BackingPath.VolumeId));
        if (library is null)
        {
            return game;
        }
        var pathKey = library.Id + ":" + copy.BackingPath.RelativePath.Replace('/', '\\');
        var match = _manifestPaths.TryGetValue(pathKey, out var matching)
            ? StorageLibraryStorage.Match(matching, copy) : null;
        if (match is not null)
        {
            copy.Id = match.Id;
        }
        copy.LibraryId = library.Id;
        copy.Location = library.Name;
        copy.LibraryBindingAccepted = _store.Libraries().Any(item => SameLibrary(item.Id, library.Id)
            && SameVolume(item.VolumeId, library.VolumeId));
        var known = _knownContent.GetValueOrDefault(copy.Id);
        var source = known?.Source ?? (match is null ? game.SourceId : "manifest:" + library.Id.ToLowerInvariant());
        var key = known?.Key ?? (match is null ? game.Key : copy.Id);
        copy.SourceId = source;
        copy.SourceKey = key;
        return game with
        {
            SourceId = source, Key = key, Content = copy,
            Routes = game.CommandRoutes.Select(route => route.IsManaged ? route with { ManagedId = copy.Id } : route).ToArray()
        };
    }

    internal async Task<SteamUiCommandResult> RecheckStorageChangesAsync(CancellationToken token)
    {
        using var linked = LinkStorageObservation(token);
        var admitted = false;
        try
        {
            await _storageLane.WaitAsync(linked.Token).ConfigureAwait(false);
            admitted = true;
            return await Task.Run(async () =>
            {
                var before = new Dictionary<string, string>(_storageMounts, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, GameLibraryStorage> previousLibraries;
                lock (_gate)
                {
                    previousLibraries = new Dictionary<string, GameLibraryStorage>(_storagePublished,
                        StringComparer.OrdinalIgnoreCase);
                }
                RefreshStorage(linked.Token);
                var changed = before.Keys.Union(_storageMounts.Keys, StringComparer.OrdinalIgnoreCase)
                    .Where(volume => !before.TryGetValue(volume, out var oldMount)
                        || !_storageMounts.TryGetValue(volume, out var newMount) || oldMount != newMount)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var dependencies = _store.InventoryEntries().Where(entry => entry.Content is { } content
                    && new[] { content.BackingPath, content.Program, content.WorkingDirectory }
                        .Concat(content.RequiredPaths).Any(path => changed.Contains(path.VolumeId)))
                    .Select(entry => entry.Content!.LibraryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var requested = _store.Libraries().Where(library => changed.Contains(library.VolumeId)
                    || dependencies.Contains(library.Id)
                    || previousLibraries.GetValueOrDefault(library.Id)?.Status == "Check failed"
                    || previousLibraries.GetValueOrDefault(library.Id)?.Status
                    != _storagePublished.GetValueOrDefault(library.Id)?.Status)
                    .Select(library => library.Id).ToArray();
                List<string> problems = [];
                foreach (var libraryId in requested)
                {
                    var result = await ReconcileStorageAsync("library:" + libraryId, linked.Token, refresh: false)
                        .ConfigureAwait(false);
                    if (!result.Succeeded && result.Error is { } error)
                    {
                        problems.Add(error);
                    }
                }
                foreach (var entry in _store.InventoryEntries().Where(entry => entry.Content is { LibraryId.Length: 0 } content
                             && new[] { content.BackingPath, content.Program, content.WorkingDirectory }
                                 .Concat(content.RequiredPaths).Any(path => changed.Contains(path.VolumeId))))
                {
                    var result = await ReconcileStorageAsync(EntryId(entry.Source, entry.Key), linked.Token, refresh: false)
                        .ConfigureAwait(false);
                    if (!result.Succeeded && result.Error is { } error)
                    {
                        problems.Add(error);
                    }
                }
                Notify();
                return problems.Count == 0 ? SteamUiCommandResult.Applied
                    : new SteamUiCommandResult(false, string.Join(" ", problems));
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            return new SteamUiCommandResult(false, "The library changed during its check. It will be checked again.");
        }
        finally
        {
            if (admitted)
            {
                _storageLane.Release();
            }
        }
    }

    private ManagedContentCheck CheckStorageContent(ManagedContentRecord content, EmulatorStore emulators,
        ManagedContentCheckContext context)
    {
        if (content.InventoryRemoved)
        {
            return new ManagedContentCheck(ManagedContentAvailability.ContentMissing,
                "Game files were removed. Steam cleanup is pending.", Content: content);
        }
        if (content.LibraryId.Length > 0)
        {
            if (_ambiguousLibraries.Contains(content.LibraryId))
            {
                return new ManagedContentCheck(ManagedContentAvailability.Unreadable,
                    "Two connected volumes claim this library. Disconnect the duplicate and recheck.", Content: content);
            }
            if (!_storageCandidates.TryGetValue(content.LibraryId, out var candidate))
            {
                var row = _storagePublished.GetValueOrDefault(content.LibraryId);
                return new ManagedContentCheck(row?.Status is "Check failed" or "Update WSGM"
                        ? ManagedContentAvailability.Unreadable : ManagedContentAvailability.StorageUnavailable,
                    row?.Detail ?? "Connect the expected library storage.", Content: content);
            }
            if (!SameVolume(candidate.VolumeId, content.BackingPath.VolumeId))
            {
                return new ManagedContentCheck(ManagedContentAvailability.StorageUnavailable,
                    "Review and Apply this library's new storage binding before launching it.", Content: content);
            }
        }
        return ManagedContentStorage.Check(content, emulators, context);
    }

    private void PublishImportedContent(Entry entry, CancellationToken token)
    {
        var content = entry.Game.Content!;
        if (!PortableContent(content) || !_eligibleVolumes.TryGetValue(content.BackingPath.VolumeId, out var name))
        {
            return;
        }
        if (_ambiguousLibraries.Contains(content.LibraryId))
        {
            throw new InvalidDataException("Duplicate storage identity. Disconnect the duplicate before importing.");
        }
        var volumeId = content.BackingPath.VolumeId;
        var known = _store.Libraries().FirstOrDefault(item => SameVolume(item.VolumeId, volumeId));
        StorageLibraryManifest manifest;
        long? revision;
        var state = StorageLibraryStorage.InspectPath(volumeId, @".wsgm\library.json");
        if (state.State == StorageLibraryPathState.Missing && known is null)
        {
            manifest = new StorageLibraryManifest { Id = Guid.NewGuid().ToString("D"), Name = name };
            revision = null;
        }
        else
        {
            manifest = StorageLibraryStorage.Read(volumeId, known?.Id);
            if (known is null)
            {
                throw new InvalidDataException("Review and Apply this library's storage binding before importing.");
            }
            revision = manifest.Revision;
        }
        var match = StorageLibraryStorage.Match(manifest, content);
        if (match is not null && match.Id != content.Id)
        {
            throw new InvalidDataException("The library identity changed. Scan again before importing.");
        }
        var library = known ?? new StorageLibraryRecord { Id = manifest.Id, VolumeId = volumeId, Name = name };
        var acceptedElsewhere = _store.Libraries().FirstOrDefault(item => SameLibrary(item.Id, manifest.Id));
        if (acceptedElsewhere is not null && !SameVolume(acceptedElsewhere.VolumeId, volumeId))
        {
            throw new InvalidDataException("Review and Apply this library's new storage binding first.");
        }
        var backing = InspectContent(StorageLibraryStorage.FromManaged(content, volumeId), volumeId);
        if (backing.State != StorageLibraryPathState.Present)
        {
            throw new IOException(backing.Detail.Length > 0 ? backing.Detail : "The backing files could not be confirmed.");
        }
        var portable = StorageLibraryStorage.FromManaged(content, volumeId, content.Location);
        var existingRecipe = manifest.Entries.FirstOrDefault(item => item.Id == content.Id);
        var unchanged = existingRecipe is not null
            && JsonSerializer.Serialize(existingRecipe, StorageLibraryJsonContext.Default.StorageLibraryContent)
            == JsonSerializer.Serialize(portable, StorageLibraryJsonContext.Default.StorageLibraryContent);
        if (!unchanged)
        {
            manifest.Entries.RemoveAll(item => item.Id == content.Id);
            manifest.Entries.Add(portable);
            manifest.Name = name;
            manifest.Revision = revision.HasValue ? checked(revision.Value + 1) : 1;
            token.ThrowIfCancellationRequested();
            StorageLibraryStorage.Publish(volumeId, manifest, revision);
        }
        if (!unchanged || known is null)
        {
            library.ManifestRevision = manifest.Revision;
        }
        library.Name = name;
        if (known is null)
        {
            library.LastValidatedUtc = DateTimeOffset.UtcNow;
        }
        token.ThrowIfCancellationRequested();
        _store.SaveLibrary(library);
        content.LibraryId = library.Id;
        content.LibraryBindingAccepted = true;
        content.Location = name;
        _store.SaveInventory(new ImportedEntry
        {
            Source = entry.Plan.Source, Key = entry.Plan.Key, Name = content.Name, Content = content.Copy()
        });
    }

    public Task<SteamUiCommandResult> StageStorageActionAsync(string id, string action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (Guard() is { } refusal)
            {
                return Task.FromResult(refusal);
            }
            if (!_storagePublished.TryGetValue(id, out var library))
            {
                if (action.Length == 0)
                {
                    _storageActions.Remove(id);
                    _stagedStorage.Remove(id);
                    return Task.FromResult(SteamUiCommandResult.Applied);
                }
                return Refuse("That storage is no longer listed.");
            }
            if (action is not ("" or "repair" or "acceptBinding"))
            {
                return Refuse("That storage action is not supported.");
            }
            if (action == "repair" && !library.CanRepair || action == "acceptBinding" && !library.CanAcceptBinding)
            {
                return Refuse("Recheck this storage before choosing that action.");
            }
            if (action.Length == 0)
            {
                _storageActions.Remove(id);
                _stagedStorage.Remove(id);
            }
            else
            {
                _storageActions[id] = action;
                var checkpoint = action == "acceptBinding" ? _storageCandidates.GetValueOrDefault(id)
                    : _store.Libraries().FirstOrDefault(item => SameLibrary(item.Id, id));
                if (checkpoint is null)
                {
                    _storageActions.Remove(id);
                    return Refuse("Recheck this storage before choosing that action.");
                }
                _stagedStorage[id] = checkpoint.Copy();
            }
            _storagePublished[id] = library with { StagedAction = action };
            Publish();
        }
        Notify();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private async Task<(int Applied, List<string> Problems)> ApplyStorageActionsAsync(CancellationToken token)
    {
        List<string> problems = [];
        var applied = 0;
        (string Id, string Action, StorageLibraryRecord? Checkpoint)[] actions;
        lock (_gate)
        {
            actions = _storageActions.Select(pair => (pair.Key, pair.Value,
                _stagedStorage.GetValueOrDefault(pair.Key)?.Copy())).ToArray();
        }
        foreach (var (id, action, checkpoint) in actions)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (checkpoint is null)
                {
                    throw new InvalidDataException("Recheck this storage before choosing that action.");
                }
                if (_ambiguousLibraries.Contains(id))
                {
                    throw new InvalidDataException("The library identity is ambiguous.");
                }
                var known = _store.Libraries().FirstOrDefault(item => SameLibrary(item.Id, id));
                if (action == "acceptBinding")
                {
                    if (!_storageCandidates.TryGetValue(id, out var candidate))
                    {
                        throw new InvalidDataException("Reconnect and recheck this storage before accepting it.");
                    }
                    var manifest = StorageLibraryStorage.Read(candidate.VolumeId, id);
                    if (manifest.Revision != checkpoint.ManifestRevision || !SameVolume(candidate.VolumeId, checkpoint.VolumeId))
                    {
                        throw new InvalidDataException("The library changed. Review its storage again.");
                    }
                    candidate.RepairRequired = false;
                    var recipes = manifest.Entries.ToDictionary(item => item.Id, StringComparer.Ordinal);
                    List<ImportedEntry> rebound = [];
                    foreach (var entry in _store.InventoryEntries().Where(entry => entry.Content is { } content
                                 && SameLibrary(content.LibraryId, id)))
                    {
                        var recipe = recipes.GetValueOrDefault(entry.Content!.Id);
                        var local = entry.Content!.Copy();
                        RebindVolume(local, candidate.VolumeId);
                        var updated = recipe is null || local.InventoryRemoved ? local
                            : StorageLibraryStorage.ToManaged(recipe, candidate, entry.Source,
                                bindingAccepted: true, localRecipe: local);
                        updated.SourceKey = entry.Key;
                        if (recipe?.SourceRoot is null)
                        {
                            updated.SourceRoot = local.SourceRoot.Copy();
                        }
                        entry.Content = updated;
                        rebound.Add(entry);
                    }
                    token.ThrowIfCancellationRequested();
                    _store.SaveInventories(rebound);
                    _store.SaveLibrary(candidate);
                }
                else if (action == "repair")
                {
                    if (known is null || !_eligibleVolumes.ContainsKey(known.VolumeId)
                        || !SameVolume(known.VolumeId, checkpoint.VolumeId) || known.ManifestRevision != checkpoint.ManifestRevision)
                    {
                        throw new InvalidDataException("Connect the exact expected storage before repairing its library.");
                    }
                    var entries = _store.InventoryEntries().Where(entry => entry.Content is { InventoryRemoved: false } content
                        && SameLibrary(content.LibraryId, id)).ToArray();
                    var manifest = new StorageLibraryManifest
                    {
                        Id = id, Name = known.Name, Revision = checked(known.ManifestRevision + 1),
                        Entries = entries.Select(entry => StorageLibraryStorage.FromManaged(entry.Content!, known.VolumeId)).ToList()
                    };
                    foreach (var entry in manifest.Entries)
                    {
                        var check = InspectContent(entry, known.VolumeId);
                        if (check.State is StorageLibraryPathState.Unreadable or StorageLibraryPathState.StorageUnavailable)
                        {
                            throw new IOException(check.Detail);
                        }
                    }
                    known.RepairRequired = true;
                    token.ThrowIfCancellationRequested();
                    StorageLibraryStorage.Repair(known, manifest);
                    known.ManifestRevision = manifest.Revision;
                    known.RepairRequired = false;
                    _store.SaveLibrary(known);
                }
                applied++;
            }
            catch (Exception ex) when (StorageFailure(ex) && ex is not ImportStateException)
            {
                problems.Add("Storage " + id + ": " + ex.Message + " Recheck and review the action again.");
            }
            lock (_gate)
            {
                _storageActions.Remove(id);
                _stagedStorage.Remove(id);
                if (_storagePublished.TryGetValue(id, out var row))
                {
                    _storagePublished[id] = row with { StagedAction = "" };
                }
            }
        }
        if (actions.Length > 0)
        {
            RefreshStorage(token);
            var result = await ReconcileStorageAsync("", token, refresh: false).ConfigureAwait(false);
            if (!result.Succeeded && result.Error is { } error)
            {
                problems.Add(error);
            }
            var records = _store.Entries();
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    var record = records.FirstOrDefault(item => item.Source == entry.Plan.Source && item.Key == entry.Plan.Key);
                    if (record?.Content is { } accepted)
                    {
                        entry.Game = entry.Game with { Content = accepted.Copy() };
                    }
                }
            }
        }
        return (applied, problems);
    }

    private static void RebindVolume(ManagedContentRecord content, string volumeId)
    {
        var previous = content.BackingPath.VolumeId;
        foreach (var path in new[] { content.BackingPath, content.SourceRoot, content.Program, content.WorkingDirectory }
                     .Concat(content.RequiredPaths))
        {
            if (!SameVolume(path.VolumeId, previous))
            {
                continue;
            }
            path.RelativePath = ManagedContentStorage.NormalizeLocalRelativePath(path.RelativePath);
            path.VolumeId = volumeId;
            path.AbsolutePath = ManagedContentStorage.ResolveLibraryPath(volumeId, path.RelativePath);
        }
        content.LibraryBindingAccepted = true;
    }

    private static StorageLibraryPathCheck InspectContent(StorageLibraryContent content, string volumeId)
    {
        var checks = new[] { content.BackingPath }.Concat(content.RequiredPaths)
            .Select(path => StorageLibraryStorage.InspectPath(volumeId, path.RelativePath, path.Directory)).ToArray();
        return checks.FirstOrDefault(check => check.State is StorageLibraryPathState.Unreadable
            or StorageLibraryPathState.StorageUnavailable)
            ?? checks.FirstOrDefault(check => check.State == StorageLibraryPathState.Missing) ?? checks[0];
    }

    private async Task<SteamUiCommandResult> ReconcileStorageAsync(string id, CancellationToken token, bool refresh = true)
    {
        long generation;
        lock (_gate)
        {
            generation = _generation;
        }
        if (refresh)
        {
            RefreshStorage(token);
        }
        var requestedLibrary = id.StartsWith("library:", StringComparison.Ordinal) ? id[8..] : "";
        var canonical = _store.InventoryEntries();
        var requestedContent = id.Length > 0 && requestedLibrary.Length == 0
            ? canonical.FirstOrDefault(entry => entry.Content?.Id == id || EntryId(entry.Source, entry.Key) == id)?.Content?.Id
                ?? id
            : "";
        var libraries = _store.Libraries();
        var checkedCount = 0;
        var missing = 0;
        HashSet<string> removedContent = new(StringComparer.Ordinal);
        List<string> problems = [];
        foreach (var library in libraries)
        {
            if (requestedLibrary.Length > 0 && !SameLibrary(requestedLibrary, library.Id))
            {
                continue;
            }
            if (id.Length > 0 && requestedLibrary.Length == 0 && !canonical.Any(entry => entry.Content?.LibraryId == library.Id
                && (entry.Content.Id == id || EntryId(entry.Source, entry.Key) == id)))
            {
                continue;
            }
            if (!_storageCandidates.TryGetValue(library.Id, out var candidate)
                || !SameVolume(candidate.VolumeId, library.VolumeId) || _ambiguousLibraries.Contains(library.Id))
            {
                continue;
            }
            try
            {
                var manifest = StorageLibraryStorage.Read(library.VolumeId, library.Id);
                var revision = manifest.Revision;
                var manifestIds = manifest.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
                var cached = canonical.Where(entry => entry.Content is { InventoryRemoved: false } content
                    && SameLibrary(content.LibraryId, library.Id) && PortableContent(content)
                    && (requestedContent.Length == 0 || content.Id == requestedContent)).ToArray();
                HashSet<string> removed = new(StringComparer.Ordinal);
                var changed = false;
                var failed = false;
                foreach (var portable in manifest.Entries.Where(entry => requestedContent.Length == 0
                             || entry.Id == requestedContent).ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    var check = InspectContent(portable, library.VolumeId);
                    checkedCount++;
                    if (check.State == StorageLibraryPathState.Missing)
                    {
                        removed.Add(portable.Id);
                    }
                    else if (check.State != StorageLibraryPathState.Present)
                    {
                        failed = true;
                        problems.Add(library.Name + ": " + check.Detail);
                    }
                }
                foreach (var entry in cached)
                {
                    try
                    {
                        var portable = StorageLibraryStorage.FromManaged(entry.Content!, library.VolumeId);
                        var check = InspectContent(portable, library.VolumeId);
                        checkedCount++;
                        if (check.State == StorageLibraryPathState.Missing)
                        {
                            removed.Add(portable.Id);
                        }
                        else if (check.State == StorageLibraryPathState.Present)
                        {
                            var replacesMissing = removed.Remove(portable.Id);
                            if (replacesMissing || !manifestIds.Contains(portable.Id))
                            {
                                if (replacesMissing)
                                {
                                    manifest.Entries.RemoveAll(item => item.Id == portable.Id);
                                }
                                manifest.Entries.Add(portable);
                                changed = true;
                            }
                        }
                        else
                        {
                            failed = true;
                            removed.Remove(portable.Id);
                            problems.Add(library.Name + ": " + check.Detail);
                        }
                    }
                    catch (Exception ex) when (StorageFailure(ex) && ex is not ImportStateException)
                    {
                        failed = true;
                        removed.Remove(entry.Content!.Id);
                        problems.Add(entry.Name + ": " + ex.Message);
                    }
                }
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_disposed || generation != _generation)
                    {
                        return SteamUiCommandResult.Applied;
                    }
                }
                if (removed.Count > 0 || changed)
                {
                    manifest.Entries.RemoveAll(item => removed.Contains(item.Id));
                    manifest.Revision = checked(revision + 1);
                    StorageLibraryStorage.Publish(library.VolumeId, manifest, revision);
                }
                else if (StorageLibraryStorage.Read(library.VolumeId, library.Id).Revision != revision)
                {
                    throw new InvalidDataException("The storage changed while its content was checked.");
                }
                List<ImportedEntry> pruned = [];
                foreach (var entry in cached.Where(entry => removed.Contains(entry.Content!.Id)))
                {
                    entry.Content!.InventoryRemoved = true;
                    entry.Content.Availability = ManagedContentAvailability.ContentMissing;
                    entry.Content.AvailabilityDetail = "Game files were removed. Steam cleanup is pending.";
                    pruned.Add(entry);
                    removedContent.Add(entry.Content.Id);
                    missing++;
                }
                _store.SaveInventories(pruned);
                var checkpoint = requestedContent.Length == 0 && !failed && (library.LastValidatedUtc is null
                    || DateTimeOffset.UtcNow - library.LastValidatedUtc > TimeSpan.FromMinutes(5));
                if (requestedContent.Length == 0 && (library.ManifestRevision != manifest.Revision || library.Name != candidate.Name
                    || library.LastMountPath != candidate.LastMountPath || library.RepairRequired
                    || library.MigrationPending || checkpoint))
                {
                    library.ManifestRevision = manifest.Revision;
                    library.Name = candidate.Name;
                    library.LastMountPath = candidate.LastMountPath;
                    library.RepairRequired = false;
                    library.MigrationPending = false;
                    if (checkpoint)
                    {
                        library.LastValidatedUtc = DateTimeOffset.UtcNow;
                    }
                    _store.SaveLibrary(library);
                }
            }
            catch (Exception ex) when (StorageFailure(ex) && ex is not ImportStateException)
            {
                problems.Add(library.Name + ": " + ex.Message);
            }
        }
        try
        {
            await CleanRemovedContentAsync(token, problems, requestedLibrary, requestedContent).ConfigureAwait(false);
        }
        catch (Exception ex) when ((StorageFailure(ex) || ex is InvalidOperationException) && ex is not ImportStateException)
        {
            problems.Add("Steam cleanup is pending: " + ex.Message);
        }
        Dictionary<string, ManagedContentCheck> checks = [];
        Dictionary<string, ManagedContentRecord> expected = [];
        var emulators = ReadEmulatorStore();
        var context = new ManagedContentCheckContext();
        var current = _store.Entries();
        var currentByIdentity = current.ToDictionary(entry => (entry.Source, entry.Key), ImportPlan.Identity);
        removedContent.UnionWith(canonical.Where(entry => entry.Content?.InventoryRemoved == true
                && (requestedLibrary.Length == 0 || SameLibrary(entry.Content.LibraryId, requestedLibrary))
                && (requestedContent.Length == 0 || entry.Content.Id == requestedContent))
            .Select(entry => entry.Content!.Id));
        foreach (var entry in _store.InventoryEntries().Where(entry => entry.Content is not null))
        {
            var content = entry.Content!;
            if (requestedLibrary.Length > 0 && !SameLibrary(content.LibraryId, requestedLibrary))
            {
                continue;
            }
            if (id.Length > 0 && requestedLibrary.Length == 0 && content.Id != id && EntryId(entry.Source, entry.Key) != id)
            {
                continue;
            }
            token.ThrowIfCancellationRequested();
            expected[content.Id] = content;
            checks[content.Id] = CheckStorageContent(content, emulators, context);
        }
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation)
            {
                return SteamUiCommandResult.Applied;
            }
            foreach (var entry in _entries.Values.ToArray())
            {
                if (entry.Game.Content is not { } scoped
                    || requestedContent.Length > 0 && scoped.Id != requestedContent
                    || requestedLibrary.Length > 0 && !SameLibrary(scoped.LibraryId, requestedLibrary))
                {
                    continue;
                }
                var stored = currentByIdentity.GetValueOrDefault((entry.Plan.Source, entry.Plan.Key));
                if (entry.Game.Content is { } content && removedContent.Contains(content.Id)
                    && (stored is null || stored.Content?.InventoryRemoved == true && !stored.HasSteamMapping))
                {
                    _entries.Remove(entry.Id);
                }
                else if (stored?.Content?.InventoryRemoved == true)
                {
                    entry.Game = entry.Game with { Content = stored.Content.Copy() };
                    entry.Selected = false;
                }
            }
            if (removedContent.Count > 0)
            {
                Publish();
            }
            var error = problems.Count == 0 ? null : string.Join(" ", problems.Distinct());
            if (id.Length == 0 && _storageError != error)
            {
                _storageError = error;
                Publish();
            }
        }
        ApplyAvailability(checks, expected, token);
        RefreshStorage(token);
        Log.Change("library.reconcile", $"Game libraries: checked {checkedCount}, removed {missing}, check failures {problems.Count}, cleanup pending {_store.InventoryEntries().Count(entry => entry.Content?.InventoryRemoved == true)}.");
        Notify();
        return problems.Count == 0 ? SteamUiCommandResult.Applied : new SteamUiCommandResult(false, string.Join(" ", problems.Distinct()));
    }

    private async Task CleanRemovedContentAsync(CancellationToken token, List<string> problems,
        string requestedLibrary, string requestedContent)
    {
        IReadOnlyList<ExistingShortcut> snapshot;
        try
        {
            snapshot = await _readLibrary(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            if (_store.InventoryEntries().Any(entry => entry.Content?.InventoryRemoved == true))
            {
                problems.Add("Steam cleanup is pending: " + ex.Message);
            }
            else
            {
                Log.Change("library.steam-state.pending", "Steam shortcut state is pending: " + ex.Message);
            }
            return;
        }
        var accountId = CaptureAccount(snapshot);
        foreach (var entry in _store.Entries().Where(entry => entry.Content?.InventoryRemoved == true && entry.HasSteamMapping
                     && (requestedLibrary.Length == 0 || SameLibrary(entry.Content.LibraryId, requestedLibrary))
                     && (requestedContent.Length == 0 || entry.Content.Id == requestedContent)))
        {
            token.ThrowIfCancellationRequested();
            RequireAccount(accountId);
            if (entry.AppId == 0)
            {
                problems.Add(entry.Name + ": shortcut creation is unconfirmed. Its cleanup receipt was preserved.");
                continue;
            }
            var live = await _readShortcut(entry.AppId, token).ConfigureAwait(false);
            RequireAccount(accountId);
            if (live is not null && !ImportPlan.OwnsRecorded(live, entry))
            {
                _store.Forget(entry.Source, entry.Key, accountId);
                continue;
            }
            if (await _isShortcutRunning(entry.AppId, token).ConfigureAwait(false) != false)
            {
                continue;
            }
            if (live is not null)
            {
                if (entry.DeletionDispatched)
                {
                    problems.Add("Steam did not confirm a previous cleanup. Check the shortcut in Steam, then Recheck.");
                    continue;
                }
                var writer = _writer();
                if (writer is null)
                {
                    continue;
                }
                _store.MarkDeletionDispatched(entry.Source, entry.Key, accountId);
                var result = await writer.RemoveAsync(entry.AppId, CancellationToken.None).ConfigureAwait(false);
                RequireAccount(accountId);
                if (!result.Confirmed)
                {
                    problems.Add("Steam did not confirm a cleanup. It will be observed again without retrying the write.");
                    break;
                }
            }
            await ReleaseControllerTargetAsync(entry.AppId, entry.OwnsProfile).ConfigureAwait(false);
            RequireAccount(accountId);
            _store.Forget(entry.Source, entry.Key, accountId);
        }
        // Collection creation and additions belong to explicit Apply/settings actions, never reconciliation.
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Tracks whether every non-Steam shortcut in Steam's list can start, and where its files live.</summary>
/// <remarks>
///     <para>
///         One session-owned worker covers WSGM's imports, emulator interfaces and the user's own shortcuts alike.
///         Triggers only set a pending flag; a single debounced pass then reads Steam's list, the store and the
///         volumes once, probes each distinct path once, and publishes one list when something changed.
///     </para>
///     <para>
///         It reports and never removes. The only writes are adding a missing entry for WSGM's own managed content
///         to its card's manifest, the one-time adoption of pre-library records onto their card, the library
///         binding's name, revision and repair flag, and the per-shortcut card bindings of literal paths.
///     </para>
/// </remarks>
internal sealed class ShortcutAvailabilityTracker : IAsyncDisposable
{
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MountSettle = TimeSpan.FromMilliseconds(750);
    private readonly RemovableDriveManager? _drives;
    private readonly EmulatorManager? _emulators;
    private readonly Lock _gate = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> _readLibrary;
    private readonly Func<EmulatorStore> _readEmulators;
    private readonly CancellationTokenSource _stop = new();
    private readonly ImportStateStore _store;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly MessageWindow? _window;
    private ShortcutAvailabilitySnapshot _current = ShortcutAvailabilitySnapshot.Empty;
    private Task? _dispose;
    private bool _disposed;
    private string? _lastError;
    private TaskCompletionSource<string?> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _settle;
    private Task _worker = Task.CompletedTask;

    /// <summary>Creates the tracker; <see cref="Start" /> begins observing.</summary>
    /// <param name="store">The device ledger of imports, libraries and shortcut bindings.</param>
    /// <param name="readLibrary">Reads Steam's complete, account-tagged shortcut list.</param>
    /// <param name="readEmulators">Reads a fresh emulator snapshot for managed ROM and interface checks.</param>
    /// <param name="drives">The shared volume inventory and its topology notifications, or null.</param>
    /// <param name="window">The session message window for resume notifications, or null.</param>
    /// <param name="emulators">The emulator manager, whose installations changing triggers a pass, or null.</param>
    internal ShortcutAvailabilityTracker(ImportStateStore store,
        Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> readLibrary, Func<EmulatorStore> readEmulators,
        RemovableDriveManager? drives = null, MessageWindow? window = null, EmulatorManager? emulators = null)
    {
        _store = store;
        _readLibrary = readLibrary;
        _readEmulators = readEmulators;
        _drives = drives;
        _window = window;
        _emulators = emulators;
    }

    /// <summary>Serializes every card manifest and library binding write, here and in Game Library commands.</summary>
    internal SemaphoreSlim StorageGate { get; } = new(1, 1);

    /// <summary>The latest published result.</summary>
    internal ShortcutAvailabilitySnapshot Current => Volatile.Read(ref _current);

    /// <summary>Raised on the worker after a pass changed the published statuses or libraries.</summary>
    internal event Action? Changed;

    /// <summary>Subscribes to its triggers and runs the first pass. Call once, on the UI thread.</summary>
    internal void Start()
    {
        if (_drives is not null)
        {
            _drives.TopologyChanged += OnTopologyChanged;
        }

        if (_window is not null)
        {
            _window.SystemResumed += Request;
        }

        if (_emulators is not null)
        {
            _emulators.InstallationsChanged += Request;
        }

        _worker = Task.Run(RunAsync);
        Request();
    }

    /// <summary>Asks for one pass soon; repeated requests before it starts collapse into it.</summary>
    internal void Request()
    {
        lock (_gate)
        {
            if (!_disposed && _wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
    }

    /// <summary>Requests a pass and waits for it, as the Recheck commands do.</summary>
    /// <returns>Applied, or the reason the pass could not read Steam or the storage.</returns>
    internal async Task<SteamUiCommandResult> RecheckAsync(CancellationToken cancellationToken)
    {
        Task<string?> pass;
        lock (_gate)
        {
            if (_disposed)
            {
                return new SteamUiCommandResult(false, "The session is shutting down.");
            }

            pass = _next.Task;
        }

        Request();
        var error = await pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        return error is null ? SteamUiCommandResult.Applied : new SteamUiCommandResult(false, error);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _dispose ??= DisposeCoreAsync();
            return new ValueTask(_dispose);
        }
    }

    private void OnTopologyChanged()
    {
        lock (_gate)
        {
            _settle = true;
        }

        Request();
    }

    private async Task RunAsync()
    {
        try
        {
            if (_emulators is not null)
            {
                // A ROM check before the installations load would call every emulator missing. A failed load is
                // the emulator manager's to report; checks then go ahead with whatever it holds.
                await _emulators.Initialization.ContinueWith(static _ => { }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                    .WaitAsync(_stop.Token).ConfigureAwait(false);
            }

            while (true)
            {
                _ = await _wake.WaitAsync(RecoveryInterval, _stop.Token).ConfigureAwait(false);
                TaskCompletionSource<string?> completion;
                bool settle;
                lock (_gate)
                {
                    completion = _next;
                    _next = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    settle = _settle;
                    _settle = false;
                }

                if (settle)
                {
                    // A volume notification precedes the mount being ready; the inventory has settled once already.
                    await Task.Delay(MountSettle, _stop.Token).ConfigureAwait(false);
                }

                string? error;
                try
                {
                    error = await PassAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    completion.TrySetResult("The session is shutting down.");
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                                               or UnauthorizedAccessException or InvalidOperationException
                                               or ImportStateException or ArgumentException)
                {
                    error = ex.Message;
                }

                if (error != _lastError)
                {
                    _lastError = error;
                    if (error is not null)
                    {
                        Log.Change("library.availability.error", "Shortcut availability is pending: " + error);
                    }
                }

                completion.TrySetResult(error);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                   && _stop.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                _next.TrySetResult("The session is shutting down.");
            }
        }
    }

    /// <summary>Runs one complete pass and publishes its result when anything changed.</summary>
    /// <returns>Null, or why Steam's shortcuts could not be checked; storage results are published either way.</returns>
    private async Task<string?> PassAsync(CancellationToken token)
    {
        var context = new ManagedContentCheckContext();
        var volumes = ObserveVolumes(context, token);
        var reads = volumes.Where(volume => volume.Removable)
            .ToDictionary(volume => volume.VolumeId, ReadCard, StringComparer.OrdinalIgnoreCase);
        volumes = [.. volumes.Select(volume => reads.TryGetValue(volume.VolumeId, out var read)
                                               && read.Manifest is { } manifest
            ? volume with { CardId = manifest.Id }
            : volume)];
        var cards = volumes.Where(volume => reads.GetValueOrDefault(volume.VolumeId)?.Manifest is not null)
            .GroupBy(volume => reads[volume.VolumeId].Manifest!.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ambiguous = cards.Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connected = cards.Where(group => group.Count() == 1).ToDictionary(group => group.Key,
            group => new ConnectedCard(reads[group.Single().VolumeId].Manifest!, group.Single()),
            StringComparer.OrdinalIgnoreCase);

        await StorageGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Reconcile(volumes, reads, connected, ambiguous, context, token);
        }
        finally
        {
            StorageGate.Release();
        }

        var libraries = _store.Libraries();
        var inventory = _store.InventoryEntries();
        var rows = Rows(libraries, inventory, volumes, reads, connected, ambiguous);

        string? error = null;
        var previous = Current;
        var statuses = previous.Shortcuts;
        var checkedOnce = previous.Checked;
        try
        {
            statuses = await CheckShortcutsAsync(libraries, inventory, volumes, connected, ambiguous, context, token)
                .ConfigureAwait(false);
            checkedOnce = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ImportStateException)
        {
            error = ex.Message;
        }

        token.ThrowIfCancellationRequested();
        var changed = !checkedOnce.Equals(previous.Checked) || !statuses.SequenceEqual(previous.Shortcuts)
                      || !rows.SequenceEqual(previous.Libraries);
        Volatile.Write(ref _current, new ShortcutAvailabilitySnapshot(previous.Revision + (changed ? 1 : 0),
            checkedOnce, statuses, rows, volumes, connected, ambiguous));
        if (changed)
        {
            LibraryBadges.UpdateShortcuts([
                .. statuses.Select(status => new SteamShortcutAvailability(status.AppId, status.Available,
                    status.Location, status.Detail))
            ]);
            Log.Change("library.availability",
                $"Shortcut availability: {statuses.Count} tracked, "
                + $"{statuses.Count(status => status.State == ContentAvailability.LibraryMissing)} library missing, "
                + $"{statuses.Count(status => status.State == ContentAvailability.FileMissing)} files missing; "
                + $"{rows.Count} card libraries.");
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Shortcut availability: a change subscriber failed: {ex.Message}");
            }
        }

        return error;
    }

    /// <summary>Identifies every mounted local volume through its GUID path, naming cards by their own marker.</summary>
    private List<ObservedVolume> ObserveVolumes(ManagedContentCheckContext context, CancellationToken token)
    {
        var inventory = _drives?.Inventory is { Volumes.Count: > 0 } shared ? shared : StorageInventory.Read();
        List<ObservedVolume> volumes = [];
        foreach (var volume in inventory.Volumes.Where(volume => volume is { Ready: true, VolumeId.Length: > 0 }))
        {
            token.ThrowIfCancellationRequested();
            var removable = inventory.FindDisk(volume.Disk) is not null || volume.DriveType == DriveType.Removable;
            var label = volume.Label.Length > 0 || !NativeStorage.TryGetVolumeInformation(volume.MountPath,
                out var read, out _)
                ? volume.Label
                : read;
            var (name, contentId) = removable ? CardName(volume.VolumeId, label)
                : (label.Length > 0 ? label : volume.MountPath.TrimEnd('\\'), "");
            context.UseMount(volume.VolumeId, volume.MountPath);
            volumes.Add(new ObservedVolume(volume.VolumeId, volume.MountPath, name, contentId, removable));
        }

        return volumes;
    }

    /// <summary>Names a card from its own Steam library marker, else its volume label.</summary>
    /// <remarks>Steam's libraryfolders.vdf label belongs to a path and survives a card swap, so it never names a card.</remarks>
    private static (string Name, string ContentId) CardName(string volumeId, string label)
    {
        foreach (var relative in new[] { SteamLibraryVdf.CardFolderName, "" })
        {
            try
            {
                if (SteamLibraryMarker.TryRead(ManagedContentStorage.ResolveLibraryPath(volumeId, relative),
                        out var contentId, out var marked))
                {
                    return (string.IsNullOrWhiteSpace(marked) ? Fallback() : marked, contentId ?? "");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Change("library.storage.label", "A card's Steam library marker could not be read: " + ex.Message);
            }
        }

        return (Fallback(), "");

        string Fallback()
        {
            return string.IsNullOrWhiteSpace(label) ? "Removable storage" : label;
        }
    }

    /// <summary>One card's manifest read, keeping missing, damaged, unsupported and unreadable apart.</summary>
    private static CardRead ReadCard(ObservedVolume volume)
    {
        try
        {
            return new CardRead(StorageLibraryStorage.Read(volume.VolumeId), CardState.Present, "");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new CardRead(null, CardState.None, "");
        }
        catch (StorageLibraryUnsupportedVersionException ex)
        {
            return new CardRead(null, CardState.Unsupported, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return new CardRead(null, CardState.Damaged, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new CardRead(null, CardState.Unreadable, ex.Message);
        }
    }

    /// <summary>Keeps card manifests and library bindings current. Only ever adds; never removes a title.</summary>
    private void Reconcile(IReadOnlyList<ObservedVolume> volumes, IReadOnlyDictionary<string, CardRead> reads,
        Dictionary<string, ConnectedCard> connected, IReadOnlySet<string> ambiguous,
        ManagedContentCheckContext context, CancellationToken token)
    {
        AdoptLegacyContent(volumes, reads, connected, context, token);
        var inventory = _store.InventoryEntries();
        foreach (var library in _store.Libraries())
        {
            token.ThrowIfCancellationRequested();
            if (ambiguous.Contains(library.Id))
            {
                continue;
            }

            var updated = library.Copy();
            if (connected.TryGetValue(library.Id, out var card) && Same(card.Volume.VolumeId, library.VolumeId))
            {
                var manifest = card.Manifest;
                var known = manifest.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
                List<StorageLibraryContent> added = [];
                foreach (var content in inventory.Select(entry => entry.Content).OfType<ManagedContentRecord>()
                             .Where(content => Same(content.LibraryId, library.Id)
                                               && Same(content.BackingPath.VolumeId, library.VolumeId)
                                               && !known.Contains(content.Id))
                             .DistinctBy(content => content.Id))
                {
                    if (Portable(content, library.VolumeId, context) is { } entry)
                    {
                        added.Add(entry);
                        known.Add(entry.Id);
                    }
                }

                if (added.Count > 0)
                {
                    var next = new StorageLibraryManifest
                    {
                        Id = manifest.Id, Name = manifest.Name, Revision = checked(manifest.Revision + 1),
                        Entries = [.. manifest.Entries, .. added]
                    };
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        StorageLibraryStorage.Publish(library.VolumeId, next, manifest.Revision);
                        connected[library.Id] = card with { Manifest = next };
                        manifest = next;
                        Log.Change("library.card." + library.Id,
                            $"Added {added.Count} title(s) to {card.Volume.Name}'s game list.");
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        Log.Change("library.card." + library.Id,
                            $"{card.Volume.Name}'s game list was not updated: {ex.Message}");
                    }
                }

                updated.Name = card.Volume.Name;
                updated.ManifestRevision = manifest.Revision;
                updated.RepairRequired = false;
            }
            else if (reads.TryGetValue(library.VolumeId, out var read) && read.State != CardState.Present
                     && read.State != CardState.Unreadable)
            {
                // The accepted card is mounted, but its game list is gone or damaged: launching waits for repair.
                updated.RepairRequired = true;
            }

            if (updated.Name != library.Name || updated.ManifestRevision != library.ManifestRevision
                                             || updated.RepairRequired != library.RepairRequired)
            {
                _store.SaveLibrary(updated);
            }
        }
    }

    /// <summary>
    ///     Brings records imported before card libraries onto their card, once: afterwards they carry its library id.
    /// </summary>
    /// <remarks>
    ///     A card without a game list gets a new one holding these records. A card whose list this device already
    ///     uses gains them through the ordinary add. Any other card's list is left for the user to review.
    /// </remarks>
    private void AdoptLegacyContent(IReadOnlyList<ObservedVolume> volumes, IReadOnlyDictionary<string, CardRead> reads,
        Dictionary<string, ConnectedCard> connected, ManagedContentCheckContext context, CancellationToken token)
    {
        var legacy = _store.InventoryEntries().Where(entry => entry.Content is
            {
                LibraryId.Length: 0, BackingPath.VolumeId.Length: > 0,
                SourceKind: LibrarySourceKind.Rom or LibrarySourceKind.Folder or LibrarySourceKind.Manual
            })
            .GroupBy(entry => entry.Content!.BackingPath.VolumeId, StringComparer.OrdinalIgnoreCase);
        foreach (var group in legacy)
        {
            token.ThrowIfCancellationRequested();
            if (volumes.FirstOrDefault(volume => volume.Removable && Same(volume.VolumeId, group.Key)) is not { } volume
                || !reads.TryGetValue(volume.VolumeId, out var read))
            {
                continue;
            }

            var libraries = _store.Libraries();
            StorageLibraryRecord? library;
            try
            {
                if (read.State == CardState.None && libraries.All(known => !Same(known.VolumeId, volume.VolumeId)))
                {
                    var manifest = new StorageLibraryManifest
                    {
                        Id = Guid.NewGuid().ToString("D"), Name = volume.Name,
                        Entries =
                        [
                            .. group.Select(entry => Portable(entry.Content!, volume.VolumeId, context))
                                .OfType<StorageLibraryContent>().DistinctBy(entry => entry.Id)
                        ]
                    };
                    StorageLibraryStorage.Publish(volume.VolumeId, manifest, expectedRevision: null);
                    library = new StorageLibraryRecord
                    {
                        Id = manifest.Id, Name = volume.Name, VolumeId = volume.VolumeId,
                        ManifestRevision = manifest.Revision
                    };
                    _store.SaveLibrary(library);
                    connected[manifest.Id] = new ConnectedCard(manifest, volume with { CardId = manifest.Id });
                }
                else if (read.Manifest is { } existing
                         && libraries.FirstOrDefault(known => Same(known.Id, existing.Id)) is { } accepted
                         && Same(accepted.VolumeId, volume.VolumeId))
                {
                    library = accepted;
                }
                else
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Change("library.migration." + volume.VolumeId, "Library adoption is pending: " + ex.Message);
                continue;
            }

            List<ImportedEntry> adopted = [];
            foreach (var entry in group)
            {
                entry.Content!.LibraryId = library.Id;
                entry.Content.Location = library.Name;
                adopted.Add(entry);
            }

            _store.SaveInventories(adopted);
            Log.Change("library.migration." + volume.VolumeId,
                $"Moved {adopted.Count} earlier title(s) into {library.Name}'s game list.");
        }
    }

    /// <summary>The card entry for content whose files are present on that card, or null.</summary>
    private static StorageLibraryContent? Portable(ManagedContentRecord content, string volumeId,
        ManagedContentCheckContext context)
    {
        try
        {
            var entry = StorageLibraryStorage.FromManaged(content, volumeId, content.Location);
            return new[] { entry.BackingPath }.Concat(entry.RequiredPaths).All(path =>
                StorageLibraryStorage.InspectPath(volumeId, path.RelativePath, path.Directory, context)
                == ContentPathState.Present)
                ? entry
                : null;
        }
        catch (InvalidDataException ex)
        {
            Log.Change("library.card.entry." + content.Id, $"{content.Name} stays off its card's list: {ex.Message}");
            return null;
        }
    }

    /// <summary>The Storage page's library rows: every known binding and every card this device does not use yet.</summary>
    private static List<GameLibraryStorage> Rows(IReadOnlyList<StorageLibraryRecord> libraries,
        IReadOnlyList<ImportedEntry> inventory, IReadOnlyList<ObservedVolume> volumes,
        IReadOnlyDictionary<string, CardRead> reads, IReadOnlyDictionary<string, ConnectedCard> connected,
        IReadOnlySet<string> ambiguous)
    {
        List<GameLibraryStorage> rows = [];
        foreach (var library in libraries)
        {
            var count = inventory.Count(entry => Same(entry.Content?.LibraryId ?? "", library.Id));
            if (ambiguous.Contains(library.Id))
            {
                rows.Add(new GameLibraryStorage(library.Id, library.Name, "Two cards claim it",
                    "Two connected cards carry this library. Remove one, then choose Recheck.", count, false, false));
            }
            else if (connected.TryGetValue(library.Id, out var card))
            {
                rows.Add(Same(card.Volume.VolumeId, library.VolumeId)
                    ? new GameLibraryStorage(library.Id, card.Volume.Name, "Connected", "",
                        card.Manifest.Entries.Count, false, false)
                    : new GameLibraryStorage(library.Id, card.Volume.Name, "Not in use here",
                        "This card is connected on a volume this device has not used for it. Choose Use this card to "
                        + "launch its games from it.", card.Manifest.Entries.Count, false, true));
            }
            else if (reads.TryGetValue(library.VolumeId, out var read))
            {
                rows.Add(read.State switch
                {
                    CardState.Unsupported => new GameLibraryStorage(library.Id, library.Name, "Update WSGM",
                        read.Detail, count, false, false),
                    CardState.Unreadable => new GameLibraryStorage(library.Id, library.Name, "Cannot read",
                        read.Detail.Length > 0 ? read.Detail : "The card could not be read. Check it, then choose Recheck.",
                        count, false, false),
                    CardState.Present => new GameLibraryStorage(library.Id, library.Name, "Different card",
                        "A different library's card is connected here. Insert this library's card.", count, false, false),
                    _ => new GameLibraryStorage(library.Id, library.Name, "Needs repair",
                        "This card's game list is missing or damaged, so its games cannot start. Repair library "
                        + "rebuilds it from the titles WSGM knows.", count, true, false)
                });
            }
            else
            {
                rows.Add(new GameLibraryStorage(library.Id, library.Name, "Disconnected",
                    $"Insert {library.Name} to check its games.", count, false, false));
            }
        }

        foreach (var (id, card) in connected.Where(pair => libraries.All(library => !Same(library.Id, pair.Key))))
        {
            rows.Add(new GameLibraryStorage(id, card.Volume.Name, "Not in use here",
                "Choose Use this card to launch this card's games on this device, then scan to import them.",
                card.Manifest.Entries.Count, false, true));
        }

        foreach (var id in ambiguous.Where(id => libraries.All(library => !Same(library.Id, id))))
        {
            var name = volumes.FirstOrDefault(volume => Same(volume.CardId, id))?.Name ?? "Game library";
            rows.Add(new GameLibraryStorage(id, name, "Two cards claim it",
                "Two connected cards carry the same library. Remove one, then choose Recheck.", 0, false, false));
        }

        rows.Sort((left, right) => StringComparer.CurrentCulture.Compare(left.Name, right.Name));
        return rows;
    }

    /// <summary>Reads Steam's list once and checks every shortcut with something local to check.</summary>
    private async Task<IReadOnlyList<ShortcutStatus>> CheckShortcutsAsync(IReadOnlyList<StorageLibraryRecord> libraries,
        IReadOnlyList<ImportedEntry> inventory, IReadOnlyList<ObservedVolume> volumes,
        IReadOnlyDictionary<string, ConnectedCard> connected, IReadOnlySet<string> ambiguous,
        ManagedContentCheckContext context, CancellationToken token)
    {
        var shortcuts = await _readLibrary(token).ConfigureAwait(false);
        if (shortcuts is not SteamShortcutSnapshot snapshot)
        {
            throw new InvalidOperationException("Steam's account could not be confirmed.");
        }

        var owned = _store.Entries().Where(entry => entry is { AppId: > 0, HasSteamMapping: true })
            .Select(entry => entry.AppId).ToHashSet();
        var contents = inventory.Select(entry => entry.Content).OfType<ManagedContentRecord>()
            .DistinctBy(content => content.Id).ToDictionary(content => content.Id, StringComparer.Ordinal);
        var bindings = _store.ShortcutBindings(snapshot.AccountId);
        var emulators = _readEmulators();
        List<ShortcutBinding> kept = [];
        List<ShortcutStatus> statuses = [];
        foreach (var shortcut in snapshot)
        {
            token.ThrowIfCancellationRequested();
            if (ShortcutRequirements.Resolve(shortcut) is not { } requirement)
            {
                continue;
            }

            var name = shortcut.Name.Length > 0 ? shortcut.Name : "Shortcut " + shortcut.AppId;
            statuses.Add(requirement.Managed
                ? CheckManaged(shortcut.AppId, name, owned.Contains(shortcut.AppId),
                    contents.GetValueOrDefault(requirement.ContentId), requirement.ContentId, libraries, volumes,
                    connected, ambiguous, emulators, context)
                : CheckLiteral(shortcut.AppId, name, owned.Contains(shortcut.AppId), requirement.LiteralPaths,
                    bindings, kept, volumes, context));
        }

        _store.SaveShortcutBindings(snapshot.AccountId, kept);
        statuses.Sort((left, right) =>
        {
            var location = StringComparer.CurrentCulture.Compare(left.Location, right.Location);
            return location != 0 ? location : StringComparer.CurrentCulture.Compare(left.Name, right.Name);
        });
        return statuses;
    }

    /// <summary>Checks a shortcut whose WSGM helper Target names a managed record, through its volume GUID.</summary>
    private static ShortcutStatus CheckManaged(uint appId, string name, bool owned, ManagedContentRecord? content,
        string contentId, IReadOnlyList<StorageLibraryRecord> libraries, IReadOnlyList<ObservedVolume> volumes,
        IReadOnlyDictionary<string, ConnectedCard> connected, IReadOnlySet<string> ambiguous, EmulatorStore emulators,
        ManagedContentCheckContext context)
    {
        if (content is null)
        {
            return new ShortcutStatus(appId, name, owned, contentId, "", ContentAvailability.FileMissing, "",
                "WSGM's launch record for this shortcut is missing. Scan in Game Library to import it again.");
        }

        var location = volumes.FirstOrDefault(volume => Same(volume.VolumeId, content.BackingPath.VolumeId))?.Name
                       ?? content.Location;
        if (content.LibraryId.Length > 0)
        {
            var library = libraries.FirstOrDefault(known => Same(known.Id, content.LibraryId));
            location = library?.Name ?? content.Location;
            ShortcutStatus Missing(string detail)
            {
                return new ShortcutStatus(appId, name, owned, content.Id, content.LibraryId,
                    ContentAvailability.LibraryMissing, location, detail);
            }

            if (ambiguous.Contains(content.LibraryId))
            {
                return Missing($"Two connected cards carry {location}. Remove one, then choose Recheck.");
            }

            if (library is null || !Same(library.VolumeId, content.BackingPath.VolumeId))
            {
                return Missing(connected.ContainsKey(content.LibraryId)
                    ? $"Choose Use this card for {location} under Storage in Game Library."
                    : $"Insert {location}.");
            }

            if (library.RepairRequired && ManagedContentStorage.MountOf(library.VolumeId, context) is not null)
            {
                return Missing($"{location}'s game list needs repair. Choose Repair library under Storage in Game Library.");
            }
        }

        var record = content.Copy();
        record.Location = location;
        var check = ManagedContentStorage.Check(record, emulators, context);
        return new ShortcutStatus(appId, name, owned, content.Id, content.LibraryId, check.Availability, location,
            check.Detail);
    }

    /// <summary>Checks a shortcut's literal paths, which mean whatever is in that drive now.</summary>
    private static ShortcutStatus CheckLiteral(uint appId, string name, bool owned,
        IReadOnlyList<ManagedContentPath> paths, IReadOnlyList<ShortcutBinding> bindings, List<ShortcutBinding> kept,
        IReadOnlyList<ObservedVolume> volumes, ManagedContentCheckContext context)
    {
        LiteralVerdict? first = null;
        LiteralVerdict? worst = null;
        foreach (var path in paths)
        {
            var root = Path.GetPathRoot(path.AbsolutePath) ?? "";
            var atDrive = volumes.FirstOrDefault(volume => string.Equals(volume.Mount, root,
                              StringComparison.OrdinalIgnoreCase))
                          ?? (Directory.Exists(root)
                              ? new ObservedVolume(ManagedContentStorage.VolumeAt(root) ?? "", root,
                                  root.TrimEnd('\\', '/'), "", false)
                              : null);
            var binding = bindings.FirstOrDefault(item => item.AppId == appId
                                                          && string.Equals(item.Path, path.AbsolutePath,
                                                              StringComparison.OrdinalIgnoreCase));
            var bound = binding is null ? null : volumes.FirstOrDefault(volume => volume.Holds(binding));
            var verdict = ShortcutRequirements.DecideLiteral(appId, path.AbsolutePath,
                ManagedContentStorage.Probe(path, context), atDrive, binding, bound);
            if (verdict.Binding is { } keep && !kept.Contains(keep))
            {
                kept.Add(keep);
            }

            first ??= verdict;
            if (verdict.State != ContentAvailability.Available
                && (worst is null || verdict.State == ContentAvailability.LibraryMissing
                    && worst.State != ContentAvailability.LibraryMissing))
            {
                worst = verdict;
            }
        }

        var result = worst ?? first!;
        return new ShortcutStatus(appId, name, owned, "", "", result.State, result.Location, result.Detail);
    }

    private static bool Same(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DisposeCoreAsync()
    {
        if (_drives is not null)
        {
            _drives.TopologyChanged -= OnTopologyChanged;
        }

        if (_emulators is not null)
        {
            _emulators.InstallationsChanged -= Request;
        }

        if (_window is { } window)
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.SystemResumed -= Request);
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        LibraryBadges.UpdateShortcuts([]);
        _wake.Dispose();
        _stop.Dispose();
    }

    private enum CardState
    {
        None,
        Present,
        Damaged,
        Unsupported,
        Unreadable
    }

    /// <summary>One card manifest read in a pass.</summary>
    private sealed record CardRead(StorageLibraryManifest? Manifest, CardState State, string Detail);
}

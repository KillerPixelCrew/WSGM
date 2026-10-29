using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WSGM.Core;

/// <summary>Everything WSGM remembers about the entries it created, and what the user decided.</summary>
internal sealed class ImportState
{
    /// <summary>The file format this build writes.</summary>
    internal const int CurrentVersion = ManagedContentStorage.FormatVersion;

    /// <summary>The format the file was written in. Zero for a file from before versions were recorded.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>One record per generated entry, oldest first.</summary>
    public List<ImportedEntry> Entries { get; set; } = [];

    /// <summary>One choice per title the user has decided something about, oldest first.</summary>
    public List<ImportChoice> Choices { get; set; } = [];

    /// <summary>The Steam collections WSGM made for its imports, one per group.</summary>
    public List<ImportedCollection> Collections { get; set; } = [];

    /// <summary>Accepted local storage identities and their validation checkpoints.</summary>
    public List<StorageLibraryRecord> Libraries { get; set; } = [];
}

/// <summary>One Steam collection WSGM made and keeps for a group of imported titles.</summary>
/// <remarks>
///     WSGM owns the collection by <see cref="Id" />, never by its name, and changes only the apps it
///     put in: <see cref="AppIds" /> is what the last sync added, so a title the user put in the
///     collection stays, and a title WSGM no longer imports is taken back out.
/// </remarks>
public sealed class ImportedCollection
{
    /// <summary>The owning userdata account, null until explicitly observed for legacy collections.</summary>
    public uint? AccountId { get; set; }

    /// <summary>The group it holds: a source's id.</summary>
    public string Group { get; set; } = "";

    /// <summary>Steam's id for the collection.</summary>
    public string Id { get; set; } = "";

    /// <summary>The name it was created with.</summary>
    public string Name { get; set; } = "";

    /// <summary>The imported titles the last sync put in it.</summary>
    public List<uint> AppIds { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ImportState))]
[JsonSerializable(typeof(ImportAccountMapping))]
internal sealed partial class ImportStateJsonContext : JsonSerializerContext;

/// <summary>The import records could not be read or written; nothing was changed on their account.</summary>
/// <param name="message">What went wrong, in words the page shows.</param>
/// <param name="inner">The underlying failure.</param>
public sealed class ImportStateException(string message, Exception inner) : Exception(message, inner);

/// <summary>Remembers which Steam entries WSGM created, and from what.</summary>
/// <remarks>
///     <para>
///         The Target and Launch Arguments are stored exactly as they were written, which is what
///         makes "has the user edited this?" an exact comparison rather than a guess.
///     </para>
///     <para>
///         <c>ImportedUtc</c> records when an entry was requested and <c>ConfirmedUtc</c> when its
///         id was actually observed in Steam's library. They are separate because Steam holds
///         shortcuts in memory and writes them out on exit: an entry can be requested and not
///         survive, and nothing should report as persisted on the strength of a request.
///     </para>
///     <para>
///         The file is the only memory of which shortcuts are WSGM's, so it is never replaced on the
///         strength of a failed read. A file that cannot be opened right now (antivirus holding it, a
///         sharing violation) fails the operation and is read again next time; a file that opens
///         and does not parse is moved aside, the way the configuration's recovery keeps a broken
///         copy, before an empty state takes its place. A write that fails throws: an apply that
///         carried on would leave a shortcut in Steam with no record of whose it is.
///     </para>
/// </remarks>
public sealed class ImportStateStore
{
    private readonly Lock _gate = new();
    private readonly string _path;
    private ImportState? _state;
    private uint? _activeAccount;
    private bool _needsLegacyBackup;

    /// <summary>Creates the store over the owner's explicit per-user directory.</summary>
    /// <param name="context">The directory context supplied by the owner.</param>
    public ImportStateStore(UserDataContext context)
        : this(Path.Combine(context.Root, "library-import.json"))
    {
    }

    /// <summary>Creates the store over one file.</summary>
    /// <param name="path">Where the state lives.</param>
    public ImportStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Sets the account established by a settled Steam observation; null or zero means unknown.</summary>
    public void SetActiveAccount(uint? accountId)
    {
        lock (_gate)
        {
            _activeAccount = accountId is > 0 ? accountId : null;
        }
    }

    /// <summary>The last settled account observation, null while unknown.</summary>
    public uint? ActiveAccountId
    {
        get
        {
            lock (_gate)
            {
                return _activeAccount;
            }
        }
    }

    /// <summary>Binds unresolved legacy receipts only to exact shortcuts observed in the active account.</summary>
    /// <returns>The number of receipts bound. Missing shortcuts never settle another account's receipt.</returns>
    public int BindLegacyAccount(uint accountId, IReadOnlyList<ExistingShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        var bound = 0;
        Mutate(state =>
        {
            RequireAccount(accountId);
            foreach (var entry in state.Entries)
            {
                if (entry.SteamMappings.Any(mapping => mapping.AccountId == accountId))
                {
                    continue;
                }

                foreach (var mapping in entry.SteamMappings.Where(mapping => mapping.AccountId is null))
                {
                    var recorded = entry.Copy();
                    mapping.Project(recorded);
                    var matches = shortcuts.Where(shortcut => (mapping.AppId == 0 || shortcut.AppId == mapping.AppId)
                        && ImportPlan.OwnsRecorded(shortcut, recorded)).Take(2).ToArray();
                    if (matches is [{ } match])
                    {
                        mapping.AccountId = accountId;
                        mapping.AppId = match.AppId;
                        bound++;
                        break;
                    }
                }
            }

            return bound > 0;
        });
        return bound;
    }

    /// <summary>Records shortcut absence only for confirmed receipts belonging to the fully read account.</summary>
    public void ObserveShortcutPresence(uint accountId, IReadOnlyList<ExistingShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        var present = shortcuts.Select(shortcut => shortcut.AppId).ToHashSet();
        Mutate(state =>
        {
            RequireAccount(accountId);
            var changed = false;
            foreach (var entry in state.Entries)
            {
                var mapping = entry.SteamMappings.FirstOrDefault(item => item.AccountId == accountId);
                if (mapping is not { AppId: > 0, ConfirmedUtc.Length: > 0, PendingDeletion: false })
                {
                    continue;
                }

                var removed = !present.Contains(mapping.AppId);
                if (mapping.RemovedFromSteam != removed)
                {
                    mapping.RemovedFromSteam = removed;
                    changed = true;
                }
            }

            return changed;
        });
    }

    /// <summary>Binds legacy collection ownership only after observing its exact id in the confirmed account.</summary>
    public int BindLegacyCollections(uint accountId, IReadOnlyList<ImportedCollection> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        var bound = 0;
        Mutate(state =>
        {
            RequireAccount(accountId);
            foreach (var collection in state.Collections.Where(item => item.AccountId is null))
            {
                if (state.Collections.Any(item => item.AccountId == accountId
                        && string.Equals(item.Group, collection.Group, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (observed.Any(item => item.Id == collection.Id))
                {
                    collection.AccountId = accountId;
                    bound++;
                }
            }

            return bound > 0;
        });
        return bound;
    }

    /// <summary>Returns complete detached title inventory, including all accounts' cleanup receipts.</summary>
    public IReadOnlyList<ImportedEntry> InventoryEntries()
    {
        lock (_gate)
        {
            return [.. Read().Entries.Select(entry => entry.Copy())];
        }
    }

    /// <summary>Returns detached snapshots of accepted local library bindings.</summary>
    public IReadOnlyList<StorageLibraryRecord> Libraries()
    {
        lock (_gate)
        {
            return [.. Read().Libraries.Select(CopyLibrary)];
        }
    }

    /// <summary>Upserts a library checkpoint; callers own binding acceptance and manifest validation.</summary>
    public void SaveLibrary(StorageLibraryRecord library)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (LibraryDefect(library) is { } defect)
        {
            throw new ArgumentException(defect, nameof(library));
        }

        Mutate(state =>
        {
            state.Libraries.RemoveAll(existing => string.Equals(existing.Id, library.Id, StringComparison.OrdinalIgnoreCase));
            state.Libraries.Add(CopyLibrary(library));
        });
    }

    /// <summary>Updates title metadata without changing any account receipt or shared choice.</summary>
    public void SaveInventory(ImportedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        SaveInventories([entry]);
    }

    /// <summary>Publishes one reconciliation batch without rewriting the complete index for each title.</summary>
    public void SaveInventories(IReadOnlyCollection<ImportedEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        Mutate(state =>
        {
            var inventory = state.Entries.ToDictionary(item => (item.Source, item.Key), ImportPlan.Identity);
            HashSet<(string Source, string Key)> removedChoices = new(ImportPlan.Identity);
            foreach (var entry in entries)
            {
                var identity = (entry.Source, entry.Key);
                var previous = inventory.GetValueOrDefault(identity);
                if (previous?.Content?.InventoryRemoved == true && entry.Content?.InventoryRemoved != true)
                {
                    throw new InvalidOperationException("Removed inventory must settle all account receipts before reimport.");
                }

                var saved = new ImportedEntry
                {
                    Source = entry.Source, Key = entry.Key, Name = entry.Name, Content = entry.Content?.Copy(),
                    SteamMappings = previous is null ? [] : [.. previous.SteamMappings.Select(mapping => mapping.Copy())]
                };
                if (saved.Content?.InventoryRemoved == true)
                {
                    foreach (var mapping in saved.SteamMappings)
                    {
                        mapping.PendingDeletion = true;
                    }
                }

                if (EntryDefect(saved) is { } defect)
                {
                    throw new ArgumentException(defect, nameof(entries));
                }

                if (saved.Content?.InventoryRemoved != true || saved.SteamMappings.Count > 0)
                {
                    inventory[identity] = saved;
                }
                else
                {
                    inventory.Remove(identity);
                    removedChoices.Add(identity);
                }
            }

            state.Entries = [.. inventory.Values];
            state.Choices.RemoveAll(choice => removedChoices.Contains((choice.Source, choice.Key)));
        });
    }

    /// <summary>Applies observations in memory and persists only actual availability transitions.</summary>
    /// <param name="checks">The completed observations, keyed by stable managed content identity.</param>
    /// <param name="expectedContents">The original launch records inspected, or null for an unconditional owner update.</param>
    public void UpdateAvailability(IReadOnlyDictionary<string, ManagedContentCheck> checks,
        IReadOnlyDictionary<string, ManagedContentRecord>? expectedContents = null)
    {
        Mutate(state =>
        {
            var changed = false;
            for (var index = 0; index < state.Entries.Count; index++)
            {
                var previous = state.Entries[index];
                if (previous.Content is not { InventoryRemoved: false } content || !checks.TryGetValue(content.Id, out var check))
                {
                    continue;
                }

                if (expectedContents is not null && (!expectedContents.TryGetValue(content.Id, out var expected)
                                                     || !ManagedContentStorage.SameLaunch(content, expected)))
                {
                    continue;
                }

                var transition = content.Availability != check.Availability ||
                                 content.AvailabilityDetail != check.Detail;
                if (!transition)
                {
                    continue;
                }

                var entry = previous.Copy();
                entry.Content!.Availability = check.Availability;
                entry.Content.AvailabilityDetail = check.Detail;
                state.Entries[index] = entry;
                changed |= transition;
            }

            return changed;
        });
    }

    /// <summary>Every entry WSGM remembers creating.</summary>
    /// <exception cref="ImportStateException">The records could not be read.</exception>
    /// <returns>Detached copies of the current persisted import records.</returns>
    public IReadOnlyList<ImportedEntry> Entries()
    {
        lock (_gate)
        {
            return [.. Read().Entries.Select(Project)];
        }
    }

    /// <summary>Reads an already-loaded snapshot without starting disk I/O from a UI publication.</summary>
    internal IReadOnlyList<ImportedEntry>? EntriesIfLoaded()
    {
        lock (_gate)
        {
            return _state is null ? null : [.. _state.Entries.Select(Project)];
        }
    }

    /// <summary>Counts installed-emulator dependencies without copying content or starting a disk read.</summary>
    internal Dictionary<string, int>? CountEmulatorDependencies(EmulatorStore emulators)
    {
        lock (_gate)
        {
            if (_state is null)
            {
                return null;
            }

            Dictionary<string, string> preferences = new(StringComparer.Ordinal);
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            foreach (var entry in _state.Entries)
            {
                if (entry.Content is not { SourceKind: LibrarySourceKind.Rom, InventoryRemoved: false } content)
                {
                    continue;
                }

                var installation = content.EmulatorInstallationId;
                if (content.FollowSystemPreference)
                {
                    var system = EmulatorStorage.NormalizeSystemId(content.SystemId);
                    if (!preferences.TryGetValue(system, out installation))
                    {
                        installation = ManagedContentStorage.PreferredSystem(emulators, system)?.InstallationId ?? "";
                        preferences[system] = installation;
                    }
                }

                counts[installation] = counts.GetValueOrDefault(installation) + 1;
            }

            return counts;
        }
    }

    /// <summary>Every choice the user has made.</summary>
    /// <exception cref="ImportStateException">The records could not be read.</exception>
    /// <returns>Choices read from disk for this call; modifying them does not persist changes.</returns>
    public IReadOnlyList<ImportChoice> Choices()
    {
        lock (_gate)
        {
            return [.. Read().Choices.Select(choice => choice.Copy())];
        }
    }

    /// <summary>Every collection WSGM made for its imports.</summary>
    /// <exception cref="ImportStateException">The records could not be read.</exception>
    /// <returns>Collections read from disk for this call; modifying them does not persist changes.</returns>
    public IReadOnlyList<ImportedCollection> Collections()
    {
        lock (_gate)
        {
            return _activeAccount is null ? [] : [.. Read().Collections
                .Where(collection => collection.AccountId == _activeAccount).Select(CopyCollection)];
        }
    }

    /// <summary>Returns detached legacy collection receipts whose account ownership is not yet confirmed.</summary>
    public IReadOnlyList<ImportedCollection> LegacyCollections()
    {
        lock (_gate)
        {
            return [.. Read().Collections.Where(collection => collection.AccountId is null).Select(CopyCollection)];
        }
    }

    /// <summary>Records one group's collection, replacing the earlier record; one with no id is forgotten.</summary>
    /// <param name="collection">What Steam now has for the group.</param>
    /// <exception cref="ImportStateException">The record could not be written.</exception>
    public void SaveCollection(ImportedCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection is not { Group.Length: > 0, Id: not null, Name: not null, AppIds: not null })
        {
            throw new ArgumentException("An incomplete collection receipt.", nameof(collection));
        }

        Mutate(state =>
        {
            if (collection.AccountId is null)
            {
                throw new InvalidOperationException("Collection saves require the captured Steam account.");
            }

            var accountId = RequireAccount(collection.AccountId);
            state.Collections.RemoveAll(existing =>
                existing.AccountId == accountId && string.Equals(existing.Group, collection.Group, StringComparison.OrdinalIgnoreCase));
            if (collection.Id.Length > 0)
            {
                var saved = CopyCollection(collection);
                saved.AccountId = accountId;
                state.Collections.Add(saved);
            }
        });
    }

    /// <summary>Records one entry, replacing any earlier record for the same title.</summary>
    /// <param name="entry">What was created.</param>
    /// <exception cref="ImportStateException">The record could not be written.</exception>
    public void Save(ImportedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Mutate(state => Replace(state, entry, RequireEntryAccount(entry)));
    }

    /// <summary>Records one applied entry and settles its choice in a single write.</summary>
    /// <param name="entry">What Steam now has.</param>
    /// <exception cref="ImportStateException">The record could not be written.</exception>
    /// <remarks>Shared choices remain user intent across accounts; this records only the applied account's receipt.</remarks>
    public void SaveApplied(ImportedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Choices are device-wide user intent. Applying in one account cannot settle another
        // account's unapplied choices; the account receipt records the resulting Steam state.
        Mutate(state =>
        {
            var accountId = RequireEntryAccount(entry);
            var previous = state.Entries.FirstOrDefault(item => Same(item.Source, item.Key, entry.Source, entry.Key));
            var mapping = previous?.SteamMappings.FirstOrDefault(item => item.AccountId == accountId);
            if (previous is not null && mapping is not null && previous.Name == entry.Name
                && mapping.SameAs(ImportAccountMapping.From(entry, accountId))
                && ManagedContentStorage.SameLaunch(previous.Content, entry.Content)
                && previous.Content?.Name == entry.Content?.Name
                && previous.Content?.Location == entry.Content?.Location
                && previous.Content?.LibraryBindingAccepted == entry.Content?.LibraryBindingAccepted
                && previous.Content?.InventoryRemoved == entry.Content?.InventoryRemoved)
            {
                return false;
            }

            Replace(state, entry, accountId);
            return true;
        });
    }

    /// <summary>Forgets only the active account's mapping once its shortcut is gone.</summary>
    /// <param name="source">Which source it came from.</param>
    /// <param name="key">Its identity in that source.</param>
    /// <exception cref="ImportStateException">The change could not be written.</exception>
    [Obsolete("Pass the captured Steam account to Forget.", error: true)]
    public void Forget(string source, string key)
    {
        throw new InvalidOperationException("Forgetting a shortcut requires its captured Steam account. Use the account-scoped overload.");
    }

    /// <summary>Settles only the named active account's receipt after an account-checked observation.</summary>
    public void Forget(string source, string key, uint accountId)
    {
        Mutate(state => ForgetMapping(state, source, key, RequireAccount(accountId)));
    }

    /// <summary>Durably admits one cleanup removal in the exact observed account before dispatch.</summary>
    public void MarkDeletionDispatched(string source, string key, uint accountId)
    {
        Mutate(state =>
        {
            var owner = RequireAccount(accountId);
            var entry = state.Entries.FirstOrDefault(item => Same(item.Source, item.Key, source, key));
            var mapping = entry?.SteamMappings.FirstOrDefault(item => item.AccountId == owner);
            if (entry?.Content?.InventoryRemoved != true || mapping is not { PendingDeletion: true })
            {
                throw new InvalidOperationException("The title is not pending cleanup in this Steam account.");
            }

            if (mapping.DeletionDispatched)
            {
                return false;
            }

            mapping.DeletionDispatched = true;
            return true;
        });
    }

    /// <summary>Records one title's choice, replacing any earlier one.</summary>
    /// <param name="choice">What the user decided.</param>
    /// <exception cref="ImportStateException">The choice could not be written.</exception>
    /// <remarks>A choice that decides nothing is removed rather than kept.</remarks>
    public void SaveChoice(ImportChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        SaveChoices([choice]);
    }

    /// <summary>Records several titles' choices in one write.</summary>
    /// <param name="choices">What the user decided, one per title.</param>
    /// <exception cref="ImportStateException">The choices could not be written.</exception>
    public void SaveChoices(IReadOnlyCollection<ImportChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
        {
            return;
        }

        Mutate(state =>
        {
            foreach (var choice in choices)
            {
                if (ChoiceDefect(choice) is { } defect)
                {
                    throw new ArgumentException(defect, nameof(choices));
                }

                state.Choices.RemoveAll(existing => Same(existing.Source, existing.Key, choice.Source, choice.Key));
                if (!choice.IsEmpty())
                {
                    state.Choices.Add(choice.Copy());
                }
            }
        });
    }

    /// <summary>Drops the choices of titles a complete scan no longer lists.</summary>
    /// <param name="listed">Every title the scan listed.</param>
    /// <param name="read">Whether a source was read in full, so its missing titles are really gone.</param>
    /// <exception cref="ImportStateException">The change could not be written.</exception>
    /// <remarks>
    ///     Only a source read in full can say a title is gone; a choice for a title of an unticked or
    ///     failed source waits for that source to be read again.
    /// </remarks>
    public void PruneChoices(IReadOnlySet<(string Source, string Key)> listed, Func<string, bool> read)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            var state = Read();
            if (!state.Choices.Any(Stale))
            {
                return;
            }

            var working = Copy(state);
            working.Choices.RemoveAll(Stale);
            Write(working);
            _state = working;
        }

        bool Stale(ImportChoice choice)
        {
            // Retained inventory and other accounts' mappings keep their shared choices.
            return !Read().Entries.Any(entry => Same(entry.Source, entry.Key, choice.Source, choice.Key))
                   && read(choice.Source) && !listed.Contains((choice.Source, choice.Key));
        }
    }

    private static void Replace(ImportState state, ImportedEntry entry, uint accountId)
    {
        var previous = state.Entries.FirstOrDefault(item => Same(item.Source, item.Key, entry.Source, entry.Key));
        if (previous?.Content?.InventoryRemoved == true)
        {
            throw new InvalidOperationException("Removed inventory must settle its cleanup before it can be imported again.");
        }

        var saved = new ImportedEntry
        {
            Source = entry.Source, Key = entry.Key, Name = entry.Name, Content = entry.Content?.Copy(),
            SteamMappings = previous is null ? [] : [.. previous.SteamMappings.Select(mapping => mapping.Copy())]
        };
        if (saved.Content is null && previous?.Content is { } previousContent)
        {
            saved.Content = previousContent.Copy();
        }

        if (previous?.Content is { } observed && saved.Content is { } content
            && ManagedContentStorage.SameLaunch(observed, content))
        {
            content.Availability = observed.Availability;
            content.AvailabilityDetail = observed.AvailabilityDetail;
        }

        saved.SteamMappings.RemoveAll(mapping => mapping.AccountId == accountId);
        saved.SteamMappings.Add(ImportAccountMapping.From(entry, accountId));
        if (EntryDefect(saved) is { } defect)
        {
            throw new ArgumentException(defect, nameof(entry));
        }

        state.Entries.RemoveAll(existing => Same(existing.Source, existing.Key, entry.Source, entry.Key));
        state.Entries.Add(saved);
    }

    private static void ForgetMapping(ImportState state, string source, string key, uint accountId)
    {
        var entry = state.Entries.FirstOrDefault(item => Same(item.Source, item.Key, source, key));
        if (entry is null)
        {
            return;
        }

        entry.SteamMappings.RemoveAll(mapping => mapping.AccountId == accountId);
        if (entry.SteamMappings.Count == 0 && (entry.Content is null || entry.Content.InventoryRemoved
                || entry.Content.LibraryId.Length == 0))
        {
            state.Entries.Remove(entry);
            // A local shortcut removal never erases shared choices. Only inventory deletion does.
            if (entry.Content?.InventoryRemoved == true)
            {
                state.Choices.RemoveAll(choice => Same(choice.Source, choice.Key, source, key));
            }
        }
    }

    private uint RequireAccount(uint? expected = null)
    {
        if (_activeAccount is not { } accountId || expected is not null && expected != accountId)
        {
            throw new InvalidOperationException("Steam's active account is unknown or changed; no import records were changed.");
        }

        return accountId;
    }

    private uint RequireEntryAccount(ImportedEntry entry)
    {
        if (entry.AccountId is null || entry.AccountUnknown || entry.HasUnresolvedLegacyMapping)
        {
            throw new InvalidOperationException("This title needs an exact Steam account observation before it can be changed.");
        }

        return RequireAccount(entry.AccountId);
    }

    private ImportedEntry Project(ImportedEntry entry)
    {
        var projection = entry.Copy();
        projection.AccountId = _activeAccount;
        projection.AccountUnknown = _activeAccount is null;
        var mapping = _activeAccount is null ? null : entry.SteamMappings.FirstOrDefault(item => item.AccountId == _activeAccount);
        projection.HasUnresolvedLegacyMapping = mapping is null && entry.SteamMappings.Any(item => item.AccountId is null);
        mapping?.Project(projection);
        if (projection.Content is { LibraryId.Length: > 0 } content
            && Read().Libraries.FirstOrDefault(library => string.Equals(library.Id, content.LibraryId, StringComparison.OrdinalIgnoreCase)) is { } library)
        {
            content.Location = library.Name;
        }

        return projection;
    }

    private static bool Same(string sourceA, string keyA, string sourceB, string keyB)
    {
        return ImportPlan.Identity.Equals((sourceA, keyA), (sourceB, keyB));
    }

    /// <summary>Applies one change to a copy, writes it, and only then makes it the state.</summary>
    /// <remarks>A write that fails leaves the remembered state as the file still has it.</remarks>
    private void Mutate(Action<ImportState> change)
    {
        Mutate(state =>
        {
            change(state);
            return true;
        });
    }

    private void Mutate(Func<ImportState, bool> change)
    {
        lock (_gate)
        {
            var working = Copy(Read());
            if (change(working))
            {
                Write(working);
            }

            _state = working;
        }
    }

    private static ImportState Copy(ImportState state)
    {
        return new ImportState
        {
            Version = ImportState.CurrentVersion,
            Entries = [.. state.Entries.Select(entry => entry.Copy())],
            Choices = [.. state.Choices.Select(choice => choice.Copy())],
            Collections = [.. state.Collections.Select(CopyCollection)],
            Libraries = [.. state.Libraries.Select(CopyLibrary)]
        };
    }

    private ImportState Read()
    {
        if (_state is not null)
        {
            return _state;
        }

        ImportState? loaded = null;
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The import index must be an object.");
            }

            var version = 0;
            if (document.RootElement.TryGetProperty("Version", out var writtenVersion)
                && (writtenVersion.ValueKind != JsonValueKind.Number || !writtenVersion.TryGetInt32(out version)))
            {
                throw new JsonException("The import index version is invalid.");
            }

            // Inspect the version before decoding fields an older build cannot understand.
            if (version > ImportState.CurrentVersion)
            {
                throw new ImportStateException(
                    "WSGM's record of imported titles was written by a newer WSGM, so this one leaves it alone.",
                    new InvalidDataException($"Import state version {version}."));
            }

            loaded = JsonSerializer.Deserialize(document.RootElement, ImportStateJsonContext.Default.ImportState)
                     ?? throw new JsonException("Import state is null.");
            loaded.Version = version;
            if (version < 2)
            {
                _needsLegacyBackup = true;
                if (document.RootElement.TryGetProperty("Entries", out var legacyEntries)
                    && legacyEntries.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var legacy in legacyEntries.EnumerateArray())
                    {
                        if (loaded.Entries is not null && index < loaded.Entries.Count && loaded.Entries[index] is { } entry
                            && legacy.ValueKind == JsonValueKind.Object)
                        {
                            var mapping = JsonSerializer.Deserialize(legacy, ImportStateJsonContext.Default.ImportAccountMapping)
                                          ?? throw new JsonException("Legacy shortcut receipt is null.");
                            mapping.AccountId = null;
                            if (MappingDefect(mapping) is { } defect)
                            {
                                // Preserve v1's row-local recovery without weakening v2 ownership validation.
                                Log.Warn($"Library import legacy record {Describe(entry)} dropped: {defect}.");
                                entry.Source = "";
                                entry.SteamMappings = [];
                            }
                            else
                            {
                                entry.SteamMappings = [mapping];
                            }
                        }

                        index++;
                    }
                }
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The library import records could not be read: {ex.Message}");
            throw new ImportStateException(
                "WSGM's record of imported titles could not be read right now, so nothing was changed. "
                + "Try again in a moment.", ex);
        }
        catch (JsonException ex)
        {
            Quarantine(ex);
            loaded = null;
            _needsLegacyBackup = false;
        }

        _state = Sanitize(loaded ?? new ImportState());
        return _state;
    }

    /// <summary>Moves an unreadable file aside, keeping it for recovery rather than writing over it.</summary>
    private void Quarantine(JsonException failure)
    {
        var aside = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        try
        {
            File.Move(_path, aside, false);
            Log.Warn($"The library import records did not parse ({failure.Message}); kept as {aside}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportStateException(
                "WSGM's record of imported titles is damaged and could not be set aside, so nothing was changed.",
                ex);
        }
    }

    /// <summary>Rejects malformed record shapes while preserving authored strings in full.</summary>
    /// <remarks>
    ///     A hand-edited or corrupted record must not become a launch command or a removal target. Only a
    ///     row that fails a shape check is dropped, never one for its length, and each one is logged with
    ///     its reason, because the next write persists the loss.
    /// </remarks>
    private static ImportState Sanitize(ImportState state)
    {
        List<ImportedEntry> entries = [];
        foreach (var entry in state.Entries ?? [])
        {
            if (EntryDefect(entry) is { } reason)
            {
                Log.Warn($"Library import record {Describe(entry)} dropped: {reason}.");
                continue;
            }

            // A later title snapshot replaces metadata, but must not erase another account's receipt.
            var earlier = entries.FirstOrDefault(item => Same(item.Source, item.Key, entry.Source, entry.Key));
            if (earlier is not null)
            {
                foreach (var mapping in earlier.SteamMappings)
                {
                    if (!entry.SteamMappings.Any(item => item.AccountId == mapping.AccountId))
                    {
                        entry.SteamMappings.Add(mapping);
                    }
                }

                entries.Remove(earlier);
            }

            entries.Add(entry);
        }

        List<ImportChoice> choices = [];
        foreach (var choice in state.Choices ?? [])
        {
            if (ChoiceDefect(choice) is { } reason)
            {
                Log.Warn($"Library import choice {Describe(choice)} dropped: {reason}.");
                continue;
            }

            // One pick per artwork type, and only an image the apply could download: an https URL, or
            // empty for a slot the user cleared.
            List<ArtworkPick> picks = [];
            foreach (var pick in choice.Artwork ?? [])
            {
                if (PickDefect(pick, picks) is { } pickReason)
                {
                    Log.Warn($"Library import choice {Describe(choice)} artwork pick dropped: {pickReason}.");
                    continue;
                }

                picks.Add(pick);
            }

            choice.Artwork = picks;
            choices.RemoveAll(existing => Same(existing.Source, existing.Key, choice.Source, choice.Key));
            choices.Add(choice);
        }

        // The last record for a group wins, as SaveCollection replaces the earlier one.
        List<ImportedCollection> collections = [];
        foreach (var collection in state.Collections ?? [])
        {
            if (collection?.AccountId == 0)
            {
                throw new ImportStateException("A stored collection account is invalid; the index was left unchanged.",
                    new InvalidDataException("Steam account zero is not an ownership identity."));
            }

            if (collection is not { Group.Length: > 0, Id.Length: > 0, Name: not null, AppIds: not null })
            {
                Log.Warn($"Library import collection {Describe(collection)} dropped: "
                         + "no group or id, or a missing field.");
                continue;
            }

            var earlier = collections.FindIndex(existing =>
                existing.AccountId == collection.AccountId
                && string.Equals(existing.Group, collection.Group, StringComparison.OrdinalIgnoreCase));
            if (earlier >= 0)
            {
                Log.Warn($"Library import collection {Describe(collections[earlier])} dropped: "
                         + "a later record for the same group replaces it.");
                collections.RemoveAt(earlier);
            }

            collection.AppIds = [.. collection.AppIds.Where(id => id != 0).Distinct()];
            collections.Add(collection);
        }

        List<StorageLibraryRecord> libraries = [];
        foreach (var library in state.Libraries ?? [])
        {
            if (LibraryDefect(library) is { } defect)
            {
                throw new ImportStateException("A stored library binding is invalid; the index was left unchanged.",
                    new InvalidDataException(defect));
            }

            libraries.RemoveAll(existing => string.Equals(existing.Id, library.Id, StringComparison.OrdinalIgnoreCase));
            libraries.Add(CopyLibrary(library));
        }

        return new ImportState
        {
            Version = ImportState.CurrentVersion, Entries = entries, Choices = choices, Collections = collections,
            Libraries = libraries
        };
    }

    /// <summary>Why a loaded entry cannot be kept, or null when it can.</summary>
    /// <remarks>
    ///     Every string is matched through a property pattern, which is null-safe. A state file with a JSON
    ///     null in any of them would otherwise throw out of the scan, and every later scan too.
    /// </remarks>
    private static string? EntryDefect(ImportedEntry? entry)
    {
        if (entry is not { Source.Length: > 0, Key.Length: > 0 })
        {
            return "no source or key";
        }

        if (entry is not { Name: not null, SteamMappings: not null })
        {
            return "a missing field";
        }

        if (entry.Content is { } content && ManagedContentStorage.RecordDefect(content) is { } defect)
        {
            return defect;
        }

        HashSet<uint?> accounts = [];
        foreach (var mapping in entry.SteamMappings)
        {
            if (MappingDefect(mapping) is not null || !accounts.Add(mapping.AccountId))
            {
                // Losing an account receipt could authorize a duplicate or hide pending deletion.
                throw new ImportStateException("A stored Steam ownership receipt is invalid; the index was left unchanged.",
                    new InvalidDataException(MappingDefect(mapping) ?? "Duplicate account receipt."));
            }
        }

        return null;
    }

    private static string? MappingDefect(ImportAccountMapping? mapping)
    {
        if (mapping is not
            {
                Target: not null, LaunchOptions: not null, StartDirectory: not null, Route: not null,
                ImportedUtc: not null, ConfirmedUtc: not null
            } || mapping.AccountId == 0)
        {
            return "an incomplete account shortcut receipt";
        }

        if (mapping.PendingLaunch is { } pending
            && pending is not { Target: not null, StartDirectory: not null, LaunchOptions: not null })
        {
            return "an incomplete pending shortcut command";
        }

        return mapping.Mode is nameof(ImportMode.ControllerOnly) or nameof(ImportMode.SteamIntegration)
            ? null : $"unknown mode '{mapping.Mode}'";
    }

    /// <summary>Why a loaded choice cannot be kept, or null when it can.</summary>
    private static string? ChoiceDefect(ImportChoice? choice)
    {
        if (choice is not { Source.Length: > 0, Key.Length: > 0 })
        {
            return "no source or key";
        }

        if (choice is not
            {
                Mode: not null, Route: not null, MatchProvider: not null, MatchId: not null, MatchName: not null,
                TitleName: not null, EmulatorInstallationId: not null, CoreId: not null, Arguments: not null,
                Artwork: not null
            })
        {
            return "a missing field";
        }

        return choice.Mode.Length == 0 || choice.PickedMode() is not null
            ? null
            : $"unknown mode '{choice.Mode}'";
    }

    /// <summary>Why a loaded artwork pick cannot be kept beside the ones already kept, or null when it can.</summary>
    private static string? PickDefect(ArtworkPick? pick, List<ArtworkPick> kept)
    {
        if (pick is not { Url: not null, Thumb: not null, Provider: not null })
        {
            return "a missing field";
        }

        if (!Enum.IsDefined(pick.Asset))
        {
            return $"unknown artwork type {(int)pick.Asset}";
        }

        if (pick.Url.Length > 0 && !HttpUrls.IsHttps(pick.Url))
        {
            return "not an https image";
        }

        return kept.Exists(existing => existing.Asset == pick.Asset)
            ? $"a second pick for {pick.Asset}"
            : null;
    }

    private static string Describe(ImportedEntry? entry)
    {
        return entry is null ? "(empty row)" : $"{entry.Source}/{entry.Key}";
    }

    private static string Describe(ImportChoice? choice)
    {
        return choice is null ? "(empty row)" : $"{choice.Source}/{choice.Key}";
    }

    private static string Describe(ImportedCollection? collection)
    {
        return collection is null ? "(empty row)" : $"{collection.Group}/{collection.Id}";
    }

    private static ImportedCollection CopyCollection(ImportedCollection collection)
    {
        return new ImportedCollection
        {
            AccountId = collection.AccountId, Group = collection.Group, Id = collection.Id,
            Name = collection.Name, AppIds = [.. collection.AppIds]
        };
    }

    private static StorageLibraryRecord CopyLibrary(StorageLibraryRecord library)
    {
        return library.Copy();
    }

    private static string? LibraryDefect(StorageLibraryRecord? library)
    {
        if (library is not { Id: not null, Name: not null, VolumeId: not null, LastMountPath: not null,
                ManifestRevision: >= 0 } || !Guid.TryParseExact(library.Id, "D", out var id) || id == Guid.Empty)
        {
            return "An invalid library identity or checkpoint.";
        }

        try
        {
            _ = ManagedContentStorage.ResolveLibraryPath(library.VolumeId, "");
            return null;
        }
        catch (InvalidDataException)
        {
            return "An invalid library volume binding.";
        }
    }

    private void Write(ImportState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (_needsLegacyBackup)
            {
                // Publish only a complete durable copy, and never replace an existing v1 backup.
                var backup = _path + ".v1.bak";
                if (!File.Exists(backup))
                {
                    var temporary = backup + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        using (var original = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            original.CopyTo(output);
                            output.Flush(flushToDisk: true);
                        }

                        File.Move(temporary, backup, overwrite: false);
                    }
                    finally
                    {
                        File.Delete(temporary);
                    }
                }
            }

            // Durable, so a power loss cannot leave a renamed empty file.
            AtomicFile.Write(_path, stream =>
            {
                JsonSerializer.Serialize(stream, state, ImportStateJsonContext.Default.ImportState);
                return true;
            }, true);
            _needsLegacyBackup = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The library import records could not be saved: {ex.Message}");
            throw new ImportStateException(
                "WSGM could not save its record of imported titles: " + ex.Message, ex);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace WSGM.Core;

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
                if (mapping is not { AppId: > 0, ConfirmedUtc.Length: > 0 })
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

    /// <summary>Returns complete detached title inventory, including every account's receipts.</summary>
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
            return [.. Read().Libraries.Select(library => library.Copy())];
        }
    }

    /// <summary>Upserts a library binding; callers own its acceptance and manifest validation.</summary>
    public void SaveLibrary(StorageLibraryRecord library)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (ImportStateSanitizer.LibraryDefect(library) is { } defect)
        {
            throw new ArgumentException(defect, nameof(library));
        }

        Mutate(state =>
        {
            state.Libraries.RemoveAll(existing => string.Equals(existing.Id, library.Id, StringComparison.OrdinalIgnoreCase));
            state.Libraries.Add(library.Copy());
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
            foreach (var entry in entries)
            {
                var identity = (entry.Source, entry.Key);
                var previous = inventory.GetValueOrDefault(identity);
                var saved = new ImportedEntry
                {
                    Source = entry.Source, Key = entry.Key, Name = entry.Name, Content = entry.Content?.Copy(),
                    SteamMappings = previous is null ? [] : [.. previous.SteamMappings.Select(mapping => mapping.Copy())]
                };
                if (ImportStateSanitizer.EntryDefect(saved) is { } defect)
                {
                    throw new ArgumentException(defect, nameof(entries));
                }

                inventory[identity] = saved;
            }

            state.Entries = [.. inventory.Values];
        });
    }

    /// <summary>Returns the account's recorded shortcut bindings.</summary>
    public IReadOnlyList<ShortcutBinding> ShortcutBindings(uint accountId)
    {
        lock (_gate)
        {
            return [.. Read().ShortcutBindings.Where(binding => binding.AccountId == accountId)];
        }
    }

    /// <summary>Replaces one account's shortcut bindings, writing only when they changed.</summary>
    /// <param name="accountId">The account whose complete shortcut list produced the bindings.</param>
    /// <param name="bindings">Every binding that account's shortcuts still need.</param>
    public void SaveShortcutBindings(uint accountId, IReadOnlyCollection<ShortcutBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        Mutate(state =>
        {
            RequireAccount(accountId);
            var current = state.ShortcutBindings.Where(binding => binding.AccountId == accountId).ToArray();
            if (current.Length == bindings.Count && current.All(bindings.Contains))
            {
                return false;
            }

            state.ShortcutBindings.RemoveAll(binding => binding.AccountId == accountId);
            state.ShortcutBindings.AddRange(bindings.Select(binding => binding with { AccountId = accountId }));
            return true;
        });
    }

    /// <summary>Every entry WSGM remembers creating.</summary>
    /// <exception cref="ImportStateException">The records could not be read.</exception>
    /// <returns>Detached copies of the current persisted import records.</returns>
    public IReadOnlyList<ImportedEntry> Entries()
    {
        lock (_gate)
        {
            var state = Read();
            var names = LibraryNames(state);
            return [.. state.Entries.Select(entry => Project(entry, names))];
        }
    }

    /// <summary>Reads an already-loaded snapshot without starting disk I/O from a UI publication.</summary>
    internal IReadOnlyList<ImportedEntry>? EntriesIfLoaded()
    {
        lock (_gate)
        {
            if (_state is null)
            {
                return null;
            }

            var names = LibraryNames(_state);
            return [.. _state.Entries.Select(entry => Project(entry, names))];
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
                if (entry.Content is not { SourceKind: LibrarySourceKind.Rom } content)
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
                .Where(collection => collection.AccountId == _activeAccount).Select(collection => collection.Copy())];
        }
    }

    /// <summary>Returns detached legacy collection receipts whose account ownership is not yet confirmed.</summary>
    public IReadOnlyList<ImportedCollection> LegacyCollections()
    {
        lock (_gate)
        {
            return [.. Read().Collections.Where(collection => collection.AccountId is null).Select(collection => collection.Copy())];
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
                var saved = collection.Copy();
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
                && previous.Content?.Location == entry.Content?.Location)
            {
                return false;
            }

            Replace(state, entry, accountId);
            return true;
        });
    }

    /// <summary>Settles only the named active account's receipt after an account-checked observation.</summary>
    public void Forget(string source, string key, uint accountId)
    {
        Mutate(state => ForgetMapping(state, source, key, RequireAccount(accountId)));
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
                if (ImportStateSanitizer.ChoiceDefect(choice) is { } defect)
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
        Mutate(state => state.Choices.RemoveAll(Stale) > 0);

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
        var saved = new ImportedEntry
        {
            Source = entry.Source, Key = entry.Key, Name = entry.Name, Content = entry.Content?.Copy(),
            SteamMappings = previous is null ? [] : [.. previous.SteamMappings.Select(mapping => mapping.Copy())]
        };
        // The saved content replaces the earlier record: only the managed route stores one, so a title moved to
        // another route stops carrying it.
        saved.SteamMappings.RemoveAll(mapping => mapping.AccountId == accountId);
        saved.SteamMappings.Add(ImportAccountMapping.From(entry, accountId));
        if (ImportStateSanitizer.EntryDefect(saved) is { } defect)
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
        // A local shortcut removal never erases shared choices or another account's receipt.
        if (entry.SteamMappings.Count == 0)
        {
            state.Entries.Remove(entry);
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

    private static Dictionary<string, string> LibraryNames(ImportState state)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var library in state.Libraries)
        {
            names.TryAdd(library.Id, library.Name);
        }

        return names;
    }

    private ImportedEntry Project(ImportedEntry entry, Dictionary<string, string> libraryNames)
    {
        var projection = entry.Copy();
        projection.AccountId = _activeAccount;
        projection.AccountUnknown = _activeAccount is null;
        var mapping = _activeAccount is null ? null : entry.SteamMappings.FirstOrDefault(item => item.AccountId == _activeAccount);
        projection.HasUnresolvedLegacyMapping = mapping is null && entry.SteamMappings.Any(item => item.AccountId is null);
        mapping?.Project(projection);
        if (projection.Content is { LibraryId.Length: > 0 } content
            && libraryNames.TryGetValue(content.LibraryId, out var libraryName))
        {
            content.Location = libraryName;
        }

        return projection;
    }

    private static bool Same(string sourceA, string keyA, string sourceB, string keyB)
    {
        return ImportPlan.Identity.Equals((sourceA, keyA), (sourceB, keyB));
    }

    /// <summary>Applies one change to the remembered state and writes it.</summary>
    /// <remarks>
    ///     A change or write that fails forgets the remembered state, so the next read takes it from the
    ///     file again and the remembered state never holds what the file does not.
    /// </remarks>
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
            var state = Read();
            try
            {
                if (change(state))
                {
                    Write(state);
                }
            }
            catch
            {
                _state = null;
                throw;
            }
        }
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
            DropLauncherRouteContent(document.RootElement, loaded);
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
                            if (ImportStateSanitizer.MappingDefect(mapping) is { } defect)
                            {
                                // Preserve v1's row-local recovery without weakening v2 ownership validation.
                                Log.Warn($"Library import legacy record {ImportStateSanitizer.Describe(entry)} dropped: {defect}.");
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

        _state = ImportStateSanitizer.Sanitize(loaded ?? new ImportState());
        return _state;
    }

    /// <summary>Forgets content an earlier build recorded for a launcher's own route.</summary>
    /// <remarks>
    ///     Only the managed route carries a content record now. Shortcuts on other routes are checked from their
    ///     own Target, so a stored launcher-route record would only mislead the plan.
    /// </remarks>
    private static void DropLauncherRouteContent(JsonElement root, ImportState loaded)
    {
        if (!root.TryGetProperty("Entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var element in entries.EnumerateArray())
        {
            if (loaded.Entries is not null && index < loaded.Entries.Count && loaded.Entries[index] is { } entry
                && element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("Content", out var content) && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("LaunchKind", out var kind) && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "Native")
            {
                entry.Content = null;
            }

            index++;
        }
    }

    /// <summary>Moves an unreadable file aside, keeping it for recovery rather than writing over it.</summary>
    private void Quarantine(JsonException failure)
    {
        try
        {
            var aside = AtomicFile.SetAside(_path);
            Log.Warn($"The library import records did not parse ({failure.Message}); kept as {aside}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportStateException(
                "WSGM's record of imported titles is damaged and could not be set aside, so nothing was changed.",
                ex);
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

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
    internal const int CurrentVersion = 1;

    /// <summary>The format the file was written in. Zero for a file from before versions were recorded.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>One record per generated entry, oldest first.</summary>
    public List<ImportedEntry> Entries { get; set; } = [];

    /// <summary>One choice per title the user has decided something about, oldest first.</summary>
    public List<ImportChoice> Choices { get; set; } = [];

    /// <summary>The Steam collections WSGM made for its imports, one per group.</summary>
    public List<ImportedCollection> Collections { get; set; } = [];
}

/// <summary>One Steam collection WSGM made and keeps for a group of imported titles.</summary>
/// <remarks>
///     WSGM owns the collection by <see cref="Id" />, never by its name, and changes only the apps it
///     put in: <see cref="AppIds" /> is what the last sync added, so a title the user put in the
///     collection stays, and a title WSGM no longer imports is taken back out.
/// </remarks>
public sealed class ImportedCollection
{
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

    /// <summary>Every entry WSGM remembers creating.</summary>
    /// <exception cref="ImportStateException">The records could not be read.</exception>
    public IReadOnlyList<ImportedEntry> Entries()
    {
        lock (_gate)
        {
            return [.. Read().Entries.Select(entry => entry.Copy())];
        }
    }

    /// <summary>Every choice the user has made.</summary>
    /// <exception cref="ImportStateException">The records could not be read.</exception>
    public IReadOnlyList<ImportChoice> Choices()
    {
        lock (_gate)
        {
            return [.. Read().Choices];
        }
    }

    /// <summary>Every collection WSGM made for its imports.</summary>
    /// <exception cref="ImportStateException">The records could not be read.</exception>
    public IReadOnlyList<ImportedCollection> Collections()
    {
        lock (_gate)
        {
            return [.. Read().Collections];
        }
    }

    /// <summary>Records one group's collection, replacing the earlier record; one with no id is forgotten.</summary>
    /// <param name="collection">What Steam now has for the group.</param>
    /// <exception cref="ImportStateException">The record could not be written.</exception>
    public void SaveCollection(ImportedCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        Mutate(state =>
        {
            state.Collections.RemoveAll(existing =>
                string.Equals(existing.Group, collection.Group, StringComparison.OrdinalIgnoreCase));
            if (collection.Id.Length > 0)
            {
                state.Collections.Add(collection);
            }
        });
    }

    /// <summary>Records one entry, replacing any earlier record for the same title.</summary>
    /// <param name="entry">What was created.</param>
    /// <exception cref="ImportStateException">The record could not be written.</exception>
    public void Save(ImportedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Mutate(state => Replace(state, entry));
    }

    /// <summary>Records one applied entry and settles its choice in a single write.</summary>
    /// <param name="entry">What Steam now has.</param>
    /// <exception cref="ImportStateException">The record could not be written.</exception>
    /// <remarks>
    ///     The record now says what Steam has, so the choice's mode, route, artwork and exclusion
    ///     would only be a second, stale answer to the same question and go. A match the user fixed
    ///     stays: it is not something Steam records, and the next scan's artwork would otherwise go
    ///     back to the wrong automatic match.
    /// </remarks>
    public void SaveApplied(ImportedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Mutate(state =>
        {
            Replace(state, entry);
            var index = state.Choices.FindIndex(choice => Same(choice.Source, choice.Key, entry.Source, entry.Key));
            if (index < 0)
            {
                return;
            }

            // A new object, not an edit: the remembered state shares its choices with this copy until
            // the write succeeds.
            var kept = state.Choices[index];
            ImportChoice settled = new()
            {
                Source = kept.Source,
                Key = kept.Key,
                MatchProvider = kept.MatchProvider,
                MatchId = kept.MatchId,
                MatchName = kept.MatchName
            };
            if (settled.IsEmpty())
            {
                state.Choices.RemoveAt(index);
            }
            else
            {
                state.Choices[index] = settled;
            }
        });
    }

    /// <summary>Forgets one title's record and choice together, once its shortcut is gone.</summary>
    /// <param name="source">Which source it came from.</param>
    /// <param name="key">Its identity in that source.</param>
    /// <exception cref="ImportStateException">The change could not be written.</exception>
    public void Forget(string source, string key)
    {
        Mutate(state =>
        {
            state.Entries.RemoveAll(entry => Same(entry.Source, entry.Key, source, key));
            state.Choices.RemoveAll(choice => Same(choice.Source, choice.Key, source, key));
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
                state.Choices.RemoveAll(existing => Same(existing.Source, existing.Key, choice.Source, choice.Key));
                if (!choice.IsEmpty())
                {
                    state.Choices.Add(choice);
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
            return read(choice.Source) && !listed.Contains((choice.Source, choice.Key));
        }
    }

    private static void Replace(ImportState state, ImportedEntry entry)
    {
        state.Entries.RemoveAll(existing => Same(existing.Source, existing.Key, entry.Source, entry.Key));
        state.Entries.Add(entry.Copy());
    }

    private static bool Same(string sourceA, string keyA, string sourceB, string keyB)
    {
        return ImportPlan.Identity.Equals((sourceA, keyA), (sourceB, keyB));
    }

    /// <summary>Applies one change to a copy, writes it, and only then makes it the state.</summary>
    /// <remarks>A write that fails leaves the remembered state as the file still has it.</remarks>
    private void Mutate(Action<ImportState> change)
    {
        lock (_gate)
        {
            var working = Copy(Read());
            change(working);
            Write(working);
            _state = working;
        }
    }

    private static ImportState Copy(ImportState state)
    {
        return new ImportState
        {
            Version = ImportState.CurrentVersion,
            Entries = [.. state.Entries],
            Choices = [.. state.Choices],
            Collections = [.. state.Collections]
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
            loaded = JsonSerializer.Deserialize(stream, ImportStateJsonContext.Default.ImportState)
                     ?? throw new JsonException("Import state is null.");
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
        }

        if (loaded is { Version: > ImportState.CurrentVersion })
        {
            throw new ImportStateException(
                "WSGM's record of imported titles was written by a newer WSGM, so this one leaves it alone.",
                new InvalidDataException($"Import state version {loaded.Version}."));
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
            choices.Add(choice);
        }

        // The last record for a group wins, as SaveCollection replaces the earlier one.
        List<ImportedCollection> collections = [];
        foreach (var collection in state.Collections ?? [])
        {
            if (collection is not { Group.Length: > 0, Id.Length: > 0, Name: not null, AppIds: not null })
            {
                Log.Warn($"Library import collection {Describe(collection)} dropped: "
                         + "no group or id, or a missing field.");
                continue;
            }

            var earlier = collections.FindIndex(existing =>
                string.Equals(existing.Group, collection.Group, StringComparison.OrdinalIgnoreCase));
            if (earlier >= 0)
            {
                Log.Warn($"Library import collection {Describe(collections[earlier])} dropped: "
                         + "a later record for the same group replaces it.");
                collections.RemoveAt(earlier);
            }

            collection.AppIds = [.. collection.AppIds.Where(id => id != 0).Distinct()];
            collections.Add(collection);
        }

        return new ImportState
        {
            Version = ImportState.CurrentVersion, Entries = entries, Choices = choices, Collections = collections
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

        if (entry is not { Name: not null, Target: not null, LaunchOptions: not null, Route: not null })
        {
            return "a missing field";
        }

        return entry.Mode is nameof(ImportMode.ControllerOnly) or nameof(ImportMode.SteamIntegration)
            ? null
            : $"unknown mode '{entry.Mode}'";
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
                Mode: not null, Route: not null, MatchProvider: not null, MatchId: not null, MatchName: not null
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

        if (pick.Url.Length > 0 && !pick.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
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

    private void Write(ImportState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Durable, so a power loss cannot leave a renamed empty file.
            AtomicFile.Write(_path, stream =>
            {
                JsonSerializer.Serialize(stream, state, ImportStateJsonContext.Default.ImportState);
                return true;
            }, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The library import records could not be saved: {ex.Message}");
            throw new ImportStateException(
                "WSGM could not save its record of imported titles: " + ex.Message, ex);
        }
    }
}

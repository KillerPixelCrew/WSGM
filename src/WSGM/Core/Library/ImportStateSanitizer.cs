using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Validates import records as they are loaded and before they are saved.</summary>
/// <remarks>
///     A hand-edited or corrupted record must not become a launch command or a removal target. Only a
///     row that fails a shape check is dropped, never one for its length, and each one is logged with
///     its reason, because the next write persists the loss.
/// </remarks>
internal static class ImportStateSanitizer
{
    /// <summary>Rejects malformed record shapes while preserving authored strings in full.</summary>
    /// <param name="state">The state as it was read.</param>
    /// <returns>The state with every defective row dropped and each identity kept once.</returns>
    /// <exception cref="ImportStateException">A record whose loss would change ownership is invalid.</exception>
    internal static ImportState Sanitize(ImportState state)
    {
        // The last record for a title wins at its own position, so each list keeps a slot per row and
        // an index per identity rather than searching the rows kept so far.
        List<ImportedEntry?> entries = [];
        Dictionary<(string Source, string Key), int> entryAt = new(ImportPlan.Identity);
        foreach (var entry in state.Entries ?? [])
        {
            if (EntryDefect(entry) is { } reason)
            {
                Log.Warn($"Library import record {Describe(entry)} dropped: {reason}.");
                continue;
            }

            // A later title snapshot replaces metadata, but must not erase another account's receipt.
            if (entryAt.TryGetValue((entry.Source, entry.Key), out var earlierAt) && entries[earlierAt] is { } earlier)
            {
                foreach (var mapping in earlier.SteamMappings)
                {
                    if (!entry.SteamMappings.Any(item => item.AccountId == mapping.AccountId))
                    {
                        entry.SteamMappings.Add(mapping);
                    }
                }

                entries[earlierAt] = null;
            }

            entryAt[(entry.Source, entry.Key)] = entries.Count;
            entries.Add(entry);
        }

        List<ImportChoice?> choices = [];
        Dictionary<(string Source, string Key), int> choiceAt = new(ImportPlan.Identity);
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
            if (choiceAt.TryGetValue((choice.Source, choice.Key), out var earlierAt))
            {
                choices[earlierAt] = null;
            }

            choiceAt[(choice.Source, choice.Key)] = choices.Count;
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
            libraries.Add(library.Copy());
        }

        List<ShortcutBinding> bindings = [];
        foreach (var binding in state.ShortcutBindings ?? [])
        {
            if (binding is not { AccountId: > 0, AppId: > 0, Path.Length: > 0, VolumeId.Length: > 0,
                    CardId: not null, Name: not null })
            {
                Log.Warn("Library import shortcut binding dropped: a missing field.");
                continue;
            }

            bindings.RemoveAll(existing => existing.AccountId == binding.AccountId && existing.AppId == binding.AppId
                                           && string.Equals(existing.Path, binding.Path, StringComparison.OrdinalIgnoreCase));
            bindings.Add(binding);
        }

        return new ImportState
        {
            Version = ImportState.CurrentVersion, Entries = [.. entries.OfType<ImportedEntry>()],
            Choices = [.. choices.OfType<ImportChoice>()], Collections = collections, Libraries = libraries,
            ShortcutBindings = bindings
        };
    }

    /// <summary>Why a loaded entry cannot be kept, or null when it can.</summary>
    /// <remarks>
    ///     Every string is matched through a property pattern, which is null-safe. A state file with a JSON
    ///     null in any of them would otherwise throw out of the scan, and every later scan too.
    /// </remarks>
    internal static string? EntryDefect(ImportedEntry? entry)
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

    /// <summary>Why an account's shortcut receipt cannot be kept, or null when it can.</summary>
    internal static string? MappingDefect(ImportAccountMapping? mapping)
    {
        if (mapping is not
            {
                Target: not null, LaunchOptions: not null, StartDirectory: not null, Route: not null,
                ImportedUtc: not null, ConfirmedUtc: not null, SettledArtwork: not null
            } || mapping.AccountId == 0
              || mapping.SettledArtwork.Any(pair => !Enum.IsDefined(pair.Key)
                  || pair.Value is not { Url: not null, Revision: not null }))
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
    internal static string? ChoiceDefect(ImportChoice? choice)
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

    /// <summary>Why a library binding cannot be kept, or null when it can.</summary>
    internal static string? LibraryDefect(StorageLibraryRecord? library)
    {
        if (library is not { Id: not null, Name: not null, VolumeId: not null, ManifestRevision: >= 0 }
            || !Guid.TryParseExact(library.Id, "D", out var id) || id == Guid.Empty)
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

    /// <summary>Names an entry in a log line.</summary>
    internal static string Describe(ImportedEntry? entry)
    {
        return entry is null ? "(empty row)" : $"{entry.Source}/{entry.Key}";
    }

    /// <summary>Why a loaded artwork pick cannot be kept beside the ones already kept, or null when it can.</summary>
    private static string? PickDefect(ArtworkPick? pick, List<ArtworkPick> kept)
    {
        if (pick is not { Url: not null, Thumb: not null, Provider: not null, Revision: not null })
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

    private static string Describe(ImportChoice? choice)
    {
        return choice is null ? "(empty row)" : $"{choice.Source}/{choice.Key}";
    }

    private static string Describe(ImportedCollection? collection)
    {
        return collection is null ? "(empty row)" : $"{collection.Group}/{collection.Id}";
    }
}

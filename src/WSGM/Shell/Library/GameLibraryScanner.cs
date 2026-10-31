using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The Game Library's sources and scans: which sources exist now, what each one's detection found,
///     and the scan that reads the ticked ones, plans against Steam and WSGM's records, and lays the
///     user's stored choices and selection over the review. A scan writes nothing to Steam.
/// </summary>
/// <remarks>
///     Its source cache, detections and counts are guarded by the owner's gate, the same lock that guards
///     the review the scan replaces.
/// </remarks>
/// <param name="owner">The service whose review and lifecycle the scan reports to.</param>
/// <param name="launchers">The launchers games are discovered in, in the order they are listed.</param>
/// <param name="readPrograms">Reads Windows' installed-programs list, once per detection or scan.</param>
/// <param name="resolveLauncher">Finds the packaged-game launcher, or null when it is missing.</param>
/// <param name="readLibrary">Reads every shortcut Steam holds; throws when the library cannot be read whole.</param>
/// <param name="folderSource">Creates the source for one shortcuts folder, or null without folders.</param>
/// <param name="store">The import records and choices the plan is laid over.</param>
/// <param name="storage">The removable storage connected cards and their titles come from.</param>
/// <param name="emulators">The emulator view the ROM sources parse with.</param>
/// <param name="applier">The applier whose account check and legacy collections a scan settles first.</param>
internal sealed class GameLibraryScanner(
    GameLibraryService owner,
    IReadOnlyList<ILibrarySource> launchers,
    Func<IReadOnlyList<UninstallEntry>> readPrograms,
    Func<string?> resolveLauncher,
    Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> readLibrary,
    Func<ShortcutFolderConfig, ILibrarySource>? folderSource,
    ImportStateStore store,
    GameLibraryCardStorage storage,
    GameLibraryEmulators emulators,
    GameLibraryApplier applier)
{
    /// <summary>What each source's last detection found.</summary>
    private readonly Dictionary<string, SourceAvailability> _availability = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many titles each source's last scan found; a source not read in full is absent.</summary>
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ILibrarySource>? _sourceCache;

    /// <summary>Whether this session can offer shortcuts folders as sources.</summary>
    internal bool FoldersSupported => folderSource is not null;

    /// <summary>What each source's last detection found. The caller holds the owner's gate.</summary>
    internal IReadOnlyDictionary<string, SourceAvailability> Availability => _availability;

    /// <summary>How many titles each source's last scan found. The caller holds the owner's gate.</summary>
    internal IReadOnlyDictionary<string, int> Counts => _counts;

    /// <summary>The sources a scan can read now: the launchers, then the configured folders, ROMs and cards.</summary>
    /// <returns>The cached list, built again after anything it depends on changed.</returns>
    internal IReadOnlyList<ILibrarySource> Sources()
    {
        lock (owner.Gate)
        {
            if (_sourceCache is not null)
            {
                return _sourceCache;
            }

            var settings = owner.ReadSettings();
            var folders = settings.ShortcutFolders;
            var existing = folderSource is null
                ? launchers
                : [.. launchers, .. folders.Select(folderSource)];
            var (_, systems) = emulators.Read();
            return _sourceCache =
            [
                .. existing,
                .. settings.RomSources.Select(source => new RomLibrarySource(source, systems)),
                new ManualLibrarySource(settings.ManualSources, systems),
                .. storage.ManifestSources()
            ];
        }
    }

    /// <summary>Drops the cached sources, so the next read builds them again. The caller holds the owner's gate.</summary>
    internal void InvalidateSources()
    {
        _sourceCache = null;
    }

    /// <summary>Forgets how many titles a source's last scan found. The caller holds the owner's gate.</summary>
    /// <param name="sourceId">The source.</param>
    internal void ForgetCount(string sourceId)
    {
        _counts.Remove(sourceId);
    }

    /// <summary>Forgets what a source's last detection found. The caller holds the owner's gate.</summary>
    /// <param name="sourceId">The source.</param>
    internal void ForgetDetection(string sourceId)
    {
        _availability.Remove(sourceId);
    }

    /// <summary>The sources' display names by id; the first source with an id names it.</summary>
    /// <param name="sources">The sources.</param>
    /// <returns>A new case-insensitive dictionary.</returns>
    internal static Dictionary<string, string> SourceNames(IReadOnlyList<ILibrarySource> sources)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            names.TryAdd(source.Id, source.DisplayName);
        }

        return names;
    }

    /// <summary>Detects every source in the background, so the sidebar knows what is installed.</summary>
    internal void StartDetection()
    {
        _ = Task.Run(() =>
        {
            var launcher = resolveLauncher();
            var detected = DetectSources(Sources(), readPrograms());
            lock (owner.Gate)
            {
                if (owner.Closed)
                {
                    return;
                }

                owner.RecordLauncher(launcher);
                Record(detected);
                owner.Publish();
            }

            owner.Notify();
        });
    }

    /// <summary>Starts a scan now, or after the apply that is running. The caller holds the owner's gate.</summary>
    internal void ScanOrQueue()
    {
        if (owner.Phase is GameLibraryPhases.Applying)
        {
            owner.QueueRescan();
            return;
        }

        StartScan();
    }

    /// <summary>Starts a scan, replacing one that runs. The caller holds the owner's gate.</summary>
    internal void StartScan()
    {
        var (generation, token) = owner.Begin(GameLibraryPhases.Scanning);
        owner.Run(work => ScanAsync(generation, work), generation, token);
    }

    /// <summary>Runs every source's detection: each one reads the registry or disk, so never under the lock.</summary>
    /// <param name="sources">The sources to detect.</param>
    /// <param name="programs">Windows' installed-programs list, read once for all of them.</param>
    private static List<(string Id, SourceAvailability Found)> DetectSources(
        IReadOnlyList<ILibrarySource> sources, IReadOnlyList<UninstallEntry> programs)
    {
        List<(string Id, SourceAvailability Found)> results = [];
        foreach (var source in sources)
        {
            SourceAvailability found;
            try
            {
                found = source.Detect(programs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Game Library source {source.Id} could not be detected: {ex.Message}");
                found = new SourceAvailability(false, "Could not be checked");
            }

            results.Add((source.Id, found));
        }

        return results;
    }

    private static DiscoveredGame ApplyContentChoice(DiscoveredGame game, ImportChoice? choice)
    {
        if (choice is null || game.Content is not { SourceKind: LibrarySourceKind.Rom } content)
        {
            return game;
        }

        if (choice.TitleName.Length > 0)
        {
            content.Name = choice.TitleName;
            game = game with { Name = choice.TitleName };
        }

        if (choice.EmulatorInstallationId.Length > 0)
        {
            content.EmulatorInstallationId = choice.EmulatorInstallationId;
            content.CoreId = choice.CoreId;
            content.FollowSystemPreference = false;
        }

        if (choice.Arguments.Count > 0)
        {
            content.Arguments = [.. choice.Arguments];
        }

        return game;
    }

    /// <summary>Lays what the user decided and what was recorded over one planned title.</summary>
    private static GameLibraryEntryState Create(
        ImportPlanEntry planned, DiscoveredGame? game, ImportedEntry? record, ImportChoice? choice)
    {
        var created = new GameLibraryEntryState(GameLibraryEntryState.IdOf(planned.Source, planned.Key), planned,
            game ?? GameLibraryProjection.Placeholder(planned, record))
        {
            RecordedTitleName = record?.Content?.Name ?? record?.Name ?? game?.Name ?? planned.Name,
            Mode = planned.Mode,
            Route = planned.Route,

            // An acknowledgement the user already gave is theirs, and losing it here is not cosmetic:
            // an acknowledged multiplayer title whose launch fields changed becomes an Update, and
            // composing that update without the acknowledgement throws.
            Acknowledged = planned.Mode is ImportMode.SteamIntegration && (record?.Acknowledged ?? false),
            ArtworkApplied = record is { AppId: > 0 } ? record.ArtworkApplied : null,
            ContentChanged = record?.Content is { } before && game?.Content is { } after &&
                             !ManagedContentStorage.SameLaunch(before, after)
        };

        if (choice is null)
        {
            return created;
        }

        created.TitleName = choice.TitleName;
        created.EmulatorOverride = choice.EmulatorInstallationId;
        created.CoreOverride = choice.CoreId;
        created.Cleanup = choice.Cleanup;
        created.ArgumentsOverride = [.. choice.Arguments];
        if (choice.Cleanup && record?.Content is not null && planned.Action is not ImportAction.Conflict)
        {
            created.BeforeCleanup = planned;
            created.Plan = planned with
            {
                Action = ImportAction.Remove, Selectable = true, Preselect = false,
                Reason = "You selected cleanup. Apply removes this shortcut, not its ROM or save data."
            };
        }

        // The user's own decisions, laid over what the plan derived. A picked mode the plan would now
        // refuse - the title lost its validated route, or became multiplayer without the risk having
        // been accepted - is not honoured, and the plan's stands. A picked route the source no longer
        // offers is dropped the same way.
        if (choice.PickedMode() is { } picked && created.Packaged
                                              && GameLibraryEntryState.ModeRefusal(planned, picked,
                                                  choice.Acknowledged) is null)
        {
            created.Mode = picked;
            created.Acknowledged = picked is ImportMode.SteamIntegration && choice.Acknowledged;
        }

        if (choice is { Route.Length: > 0 } && created.Game.CommandRoutes.Any(route => route.Id == choice.Route))
        {
            created.Route = choice.Route;
        }

        if (planned.Action is not ImportAction.Remove)
        {
            foreach (var pick in choice.Artwork)
            {
                if (record is null || record.AppId != planned.AppId
                    || !record.SettledArtwork.TryGetValue(pick.Asset, out var applied)
                    || applied.Url != pick.Url || applied.Revision != pick.Revision)
                {
                    created.Picks[pick.Asset] = pick;
                }
                else
                {
                    created.SettledPicks[pick.Asset] = pick;
                }
            }

            if (choice.MatchId.Length > 0)
            {
                created.Match = new ArtworkGameMatch(choice.MatchProvider, choice.MatchId, choice.MatchName, true);
            }
        }

        created.Excluded = choice.Excluded && GameLibraryEntryState.Excludable(planned);
        return created;
    }

    private void Record(List<(string Id, SourceAvailability Found)> detected)
    {
        foreach (var (id, found) in detected)
        {
            _availability[id] = found;
        }
    }

    private async Task ScanAsync(long generation, CancellationToken cancellationToken)
    {
        if (storage.Tracker is { } tracker)
        {
            // One fresh pass gives the scan the cards in the drives now and each shortcut's state.
            var checkedStorage = await tracker.RecheckAsync(cancellationToken).ConfigureAwait(false);
            if (!checkedStorage.Succeeded && checkedStorage.Error is { } pending)
            {
                owner.Note(generation, "Storage could not be checked, so titles on missing storage may be offered for removal. " + pending);
            }

            lock (owner.Gate)
            {
                _sourceCache = null;
            }
        }

        var launcher = resolveLauncher();
        var settings = owner.ReadSettings();
        var sources = Sources();
        var programs = readPrograms();
        var detected = DetectSources(sources, programs);
        lock (owner.Gate)
        {
            if (!owner.IsCurrent(generation))
            {
                return;
            }

            owner.RecordLauncher(launcher);
            Record(detected);
        }

        var disabled = new HashSet<string>(settings.DisabledSources, StringComparer.OrdinalIgnoreCase);
        var installed = detected.Where(found => found.Found.Installed).Select(found => found.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reading = sources.Where(source => installed.Contains(source.Id) && !disabled.Contains(source.Id)).ToList();

        // Read together: the local sources no longer wait behind the Store lookups of the Xbox one.
        var answers = await Task.WhenAll(reading.Select(async source =>
        {
            try
            {
                var games = await source.DiscoverAsync(programs, cancellationToken).ConfigureAwait(false);
                return (Source: source, Games: games, Failure: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // One launcher's broken data does not hide every other launcher's games. Its records
                // are treated as unread, so nothing of it is offered for removal either.
                Log.Warn($"Game Library source {source.Id} could not be read: {ex.Message}");
                return (Source: source, Games: (IReadOnlyList<DiscoveredGame>)[],
                    Failure: (string?)$"{source.DisplayName} could not be read: {ex.Message}");
            }
        })).ConfigureAwait(false);

        var existing = await readLibrary(cancellationToken).ConfigureAwait(false);
        var scanAccount = applier.CaptureAccount(existing);
        try
        {
            await applier.ObserveLegacyCollectionsAsync(scanAccount, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when ((GameLibraryCardStorage.IsStorageFailure(ex) || ex is InvalidOperationException)
                                   && ex is not ImportStateException)
        {
            owner.Note(generation, "Collection ownership could not be checked; its changes remain pending. " + ex.Message);
        }
        // Emulator UI shortcuts share the ownership ledger, but are reconciled by their manager.
        var recorded = store.Entries().Where(entry => entry.Source != EmulatorShortcutSync.Source).ToArray();
        var scanChoices = store.Choices().ToDictionary(choice => (choice.Source, choice.Key), ImportPlan.Identity);
        var recordedSources = recorded.Select(record => record.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var emulatorSnapshot = emulators.ReadStore();
        var contentChecks = new ManagedContentCheckContext();
        var knownContent = store.InventoryEntries().Where(entry => entry.Content is not null)
            .GroupBy(entry => entry.Content!.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        List<string> notes = [];
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        List<DiscoveredGame> discovered = [];
        HashSet<(string Source, string Key)> discoveredIdentities = new(ImportPlan.Identity);
        foreach (var (source, found, failure) in answers)
        {
            if (failure is not null)
            {
                notes.Add(failure);
                continue;
            }

            foreach (var foundGame in found)
            {
                var bound = storage.BindDiscoveredContent(foundGame, source.Kind == LibrarySourceKind.Launcher,
                    knownContent);
                var game = ApplyContentChoice(bound, scanChoices.GetValueOrDefault((bound.SourceId, bound.Key)));
                if (!discoveredIdentities.Add((game.SourceId, game.Key)))
                {
                    continue;
                }
                if (game.Content is { SourceKind: LibrarySourceKind.Rom } observed)
                {
                    // Only the resolved emulator command is shown; availability belongs to the tracker.
                    if (ManagedContentStorage.Check(observed, emulatorSnapshot, contentChecks).Launch is { } launch)
                    {
                        game = game with
                        {
                            Notes =
                            [
                                .. game.Notes,
                                "Launch command: " + WindowsCommandLine.Quote(launch.Executable) + " " +
                                LaunchArguments.Join(launch.Arguments),
                                "Working directory: " + launch.WorkingDirectory
                            ]
                        };
                    }
                }

                discovered.Add(game);
            }

            counts[source.Id] = found.Count;
            if (found.Count == 0 && recordedSources.Contains(source.Id))
            {
                // Nothing at all from a source titles were imported from is far likelier to be a
                // launcher that could not be read properly than every game uninstalled at once.
                notes.Add($"{source.DisplayName} reported no titles, so none of the titles imported from it "
                          + "are offered for removal.");
            }
        }

        var known = sources.Select(source => source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        ImportSourceState StateOf(string sourceId)
        {
            if (counts.TryGetValue(sourceId, out var count))
            {
                return count > 0 ? ImportSourceState.Read : ImportSourceState.Unread;
            }

            // Not read: unticked or failed stays unread; a launcher no longer installed, or a folder
            // no longer configured, is gone, and what was imported from it can be removed.
            return !known.Contains(sourceId) || !installed.Contains(sourceId)
                ? ImportSourceState.Gone
                : ImportSourceState.Unread;
        }

        var tracked = storage.Tracked;
        var plan = ImportPlan.Build(
            discovered, recorded, existing, launcher ?? string.Empty, settings.DefaultMode, settings.ImportUnroutable,
            StateOf, appId => tracked.Find(appId)?.State).ToList();

        Dictionary<(string Source, string Key), DiscoveredGame> games = new(ImportPlan.Identity);
        foreach (var game in discovered)
        {
            games.TryAdd((game.SourceId, game.Key), game);
        }

        Dictionary<(string Source, string Key), ImportedEntry> records = new(ImportPlan.Identity);
        foreach (var record in recorded)
        {
            records[(record.Source, record.Key)] = record;
        }

        HashSet<(string Source, string Key)> listed = new(ImportPlan.Identity);
        foreach (var entry in plan)
        {
            listed.Add((entry.Source, entry.Key));
        }

        lock (owner.Gate)
        {
            if (owner.Closed || !owner.IsCurrent(generation))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                store.PruneChoices(listed, source => StateOf(source) is ImportSourceState.Read);
            }
            catch (ImportStateException ex)
            {
                notes.Add(ex.Message);
            }

            IReadOnlyList<ImportChoice> choices;
            try
            {
                // Read here, under the lock, so a choice made while the scan ran is laid over it too.
                choices = store.Choices();
            }
            catch (ImportStateException ex)
            {
                choices = [];
                notes.Add(ex.Message);
            }

            Dictionary<(string Source, string Key), ImportChoice> chosen = new(ImportPlan.Identity);
            foreach (var choice in choices)
            {
                chosen[(choice.Source, choice.Key)] = choice;
            }

            var entries = owner.Entries;
            var previous = new Dictionary<string, GameLibraryEntryState>(entries, StringComparer.Ordinal);
            entries.Clear();
            foreach (var planned in plan)
            {
                var identity = (planned.Source, planned.Key);
                var created = Create(planned, games.GetValueOrDefault(identity), records.GetValueOrDefault(identity),
                    chosen.GetValueOrDefault(identity));

                // The user's selection survives a rescan: ids are stable, so a title they deselected
                // stays deselected and one they ticked stays ticked, while it can still be ticked.
                created.Selected = previous.TryGetValue(created.Id, out var before)
                    ? before.Selected && created.Selectable
                    : created.Selectable && (planned.Preselect || created.PendingChange);
                entries[created.Id] = created;
            }

            _counts.Clear();
            foreach (var (id, count) in counts)
            {
                _counts[id] = count;
            }

            if (plan.Count == 0 && notes.Count == 0)
            {
                notes.Add(reading.Count == 0 ? "No source is ticked and installed." : "No games were found.");
            }

            owner.ShowReview(notes);
        }

        owner.ResetArtwork(sources);
        owner.UpdateEmulatorDependencies();
        owner.Notify();
    }
}

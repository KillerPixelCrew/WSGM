using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The Game Library: the one backend both of its surfaces drive.</summary>
/// <remarks>
///     <para>
///         Sources discover games, the plan decides what a sync would do, the user's stored choices
///         are laid over it, and an apply writes shortcuts, records, controller overrides and Store
///         artwork. The Steam page and the overlay view both render <see cref="ReadState" /> and call
///         the same methods, so a change made in one is what the other shows next.
///     </para>
///     <para>
///         A scan writes nothing. It discovers, classifies, matches against Steam and the state
///         file, and publishes a plan; the dry run is the default rather than a mode.
///     </para>
///     <para>
///         Applies are serialized, one write at a time, and each entry is re-matched immediately
///         before its own write so a library that changed between scan and apply cannot be acted on
///         from stale state. A failure stops the run and reports how far it got; it does not roll
///         back, because removing a batch of somebody's shortcuts over one failed write is a worse
///         outcome than stopping.
///     </para>
/// </remarks>
internal sealed class GameLibraryService : IGameLibraryBackend, IDisposable
{
    /// <summary>How many entries one apply may write, so a mistake has a bounded blast radius.</summary>
    private const int MaximumPerRun = 50;

    private readonly Func<uint, IReadOnlyList<DiscoveredArtwork>, CancellationToken, Task<int>>? _applyArtwork;
    private readonly Func<ImportMode> _defaultMode;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();
    private readonly Func<bool> _includeUnroutable;
    private readonly Func<uint, string, CancellationToken, Task<SteamUiCommandResult>>? _openArtwork;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> _readLibrary;
    private readonly Func<string?> _resolveLauncher;
    private readonly Func<string, string, ManagedControllerTarget?, CancellationToken, Task>? _setControllerTarget;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IReadOnlyList<ILibrarySource> _sources;
    private readonly ImportStateStore _store;
    private readonly Func<SteamShortcutWriter?> _writer;
    private bool _artworkMissing;

    /// <summary>Titles whose controller override could not be written in this run.</summary>
    private readonly List<string> _controllerFailures = [];

    /// <summary>The answer to any change to the list while an apply is working through it.</summary>
    /// <remarks>
    ///     An apply composes a title's shortcut, writes it, and then records the mode and writes the
    ///     controller override. A mode changed in between would be recorded and pinned while the
    ///     shortcut still launched the old way, so the list is read-only until the run ends.
    /// </remarks>
    private static readonly SteamUiCommandResult Frozen =
        new(false, "An import is running. Wait for it to finish, or stop it, before changing the list.");
    private bool _disposed;
    private string? _error;
    private long _generation;
    private string? _notice;
    private string _phase = "idle";
    private int _progress;
    private int _progressTotal;
    private long _revision;
    private CancellationTokenSource? _work;

    /// <summary>Creates the backend over its sources and the client calls it drives.</summary>
    /// <param name="sources">Where games are discovered, in the order they are listed.</param>
    /// <param name="store">Where this run's records are kept.</param>
    /// <param name="writer">Opens a shortcut writer over the live client, or null when unreachable.</param>
    /// <param name="readLibrary">Reads the shortcuts Steam currently holds.</param>
    /// <param name="defaultMode">The mode an entry starts on.</param>
    /// <param name="includeUnroutable">Whether titles with no validated launch route are offered.</param>
    /// <param name="applyArtwork">Applies catalog images to a confirmed app id, or null to skip.</param>
    /// <param name="setControllerTarget">Writes the per-game controller override, or null to skip.</param>
    /// <param name="resolveLauncher">Finds the packaged-game launcher, or null for the real one.</param>
    /// <param name="openArtwork">Opens the artwork page for an app id and title, or null without one.</param>
    internal GameLibraryService(
        IReadOnlyList<ILibrarySource> sources,
        ImportStateStore store,
        Func<SteamShortcutWriter?> writer,
        Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> readLibrary,
        Func<ImportMode> defaultMode,
        Func<bool> includeUnroutable,
        Func<uint, IReadOnlyList<DiscoveredArtwork>, CancellationToken, Task<int>>? applyArtwork = null,
        Func<string, string, ManagedControllerTarget?, CancellationToken, Task>? setControllerTarget = null,
        Func<string?>? resolveLauncher = null,
        Func<uint, string, CancellationToken, Task<SteamUiCommandResult>>? openArtwork = null)
    {
        _sources = sources;
        _store = store;
        _writer = writer;
        _readLibrary = readLibrary;
        _defaultMode = defaultMode;
        _includeUnroutable = includeUnroutable;
        _applyArtwork = applyArtwork;
        _setControllerTarget = setControllerTarget;
        _resolveLauncher = resolveLauncher ?? PackagedLauncherShortcut.ResolveLauncher;
        _openArtwork = openArtwork;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        _work?.Dispose();
        _shutdown.Dispose();
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_phase == "applying")
            {
                return Task.FromResult(new SteamUiCommandResult(
                    false, "An import is already running."));
            }

            var generation = Begin("scanning");
            _ = RunAsync(token => ScanCoreAsync(generation, token), generation);
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CancelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _work?.Cancel();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ToggleEntryAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_phase == "applying")
            {
                return Task.FromResult(Frozen);
            }

            if (!_entries.TryGetValue(id, out var entry))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That entry is no longer listed."));
            }

            if (!entry.Selectable)
            {
                return Task.FromResult(new SteamUiCommandResult(
                    false, entry.Reason));
            }

            entry.Selected = !entry.Selected;
            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SelectAllAsync(bool selected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_phase == "applying")
            {
                return Task.FromResult(Frozen);
            }

            foreach (var entry in _entries.Values.Where(entry => entry.Selectable))
            {
                entry.Selected = selected;
            }

            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetModeAsync(
        string id, string mode, bool acknowledged, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_phase == "applying")
            {
                return Task.FromResult(Frozen);
            }

            if (!_entries.TryGetValue(id, out var entry))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That entry is no longer listed."));
            }

            if (!Enum.TryParse<ImportMode>(mode, true, out var wanted))
            {
                return Task.FromResult(new SteamUiCommandResult(false, $"'{mode}' is not a launch mode."));
            }

            // Checked here, not only in the page. A page defect must not be able to put a
            // multiplayer title on the route that injects into it.
            if (Refusal(entry.Plan, wanted, acknowledged) is { } refusal)
            {
                return Task.FromResult(new SteamUiCommandResult(false, refusal));
            }

            entry.Mode = wanted;
            entry.Acknowledged = wanted is ImportMode.SteamIntegration && acknowledged;
            Remember(entry, wanted);
            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ExcludeAsync(string id, CancellationToken cancellationToken)
    {
        return SetExcludedAsync(id, true, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> IncludeAsync(string id, CancellationToken cancellationToken)
    {
        return SetExcludedAsync(id, false, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The artwork stage of the pipeline. Store art is applied when a write is confirmed; this is
    ///     where the user picks something else, per entry, with the entry's own title as the search
    ///     so a shortcut Steam has not listed yet is not searched for as "App N".
    /// </remarks>
    public async Task<SteamUiCommandResult> OpenArtworkAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint appId;
        string title;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return new SteamUiCommandResult(false, "That entry is no longer listed.");
            }

            appId = entry.AppId;
            title = entry.Plan.Name;
            if (appId == 0 || entry.Action is ImportAction.Remove)
            {
                return new SteamUiCommandResult(false,
                    "This title is not in Steam yet. Import it first, then change its artwork.");
            }
        }

        if (_openArtwork is null)
        {
            return new SteamUiCommandResult(false, "Artwork is unavailable in this session.");
        }

        var opened = await _openArtwork(appId, title, cancellationToken).ConfigureAwait(false);
        if (!opened.Succeeded)
        {
            return opened;
        }

        // The route travels in the answer, the same contract the game menu's Change Artwork uses,
        // so the page follows it the way every other page-opening action is followed.
        return new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { ["route"] = SteamArtworkBrowserSurface.RouteFor(appId) }));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ApplyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_phase is "scanning" or "applying")
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Something is already running."));
            }

            if (_resolveLauncher() is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "The packaged-game launcher is missing from this install, so an imported entry "
                    + "would point at nothing."));
            }

            var selected = _entries.Values.Where(entry => entry.Selected && entry.Selectable).ToList();
            if (selected.Count == 0)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Nothing is selected."));
            }

            var generation = Begin("applying");
            _progressTotal = Math.Min(selected.Count, MaximumPerRun);
            _ = RunAsync(token => ApplyCoreAsync(generation, selected, token), generation);
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Raised when the published state changed.</summary>
    internal event Action? Changed;

    /// <summary>The state Steam should currently render.</summary>
    internal GameLibraryState ReadState()
    {
        lock (_gate)
        {
            var launcher = _resolveLauncher();
            var entries = _entries.Values.OrderBy(entry => entry.Plan.Name, StringComparer.CurrentCulture)
                .Select(Project).ToList();

            return new GameLibraryState(
                [.. _sources.Select(source => source.DisplayName)],
                _phase,
                entries,
                entries.Count(entry => entry.Selected),
                Count(ImportAction.Add),
                Count(ImportAction.Update),
                Count(ImportAction.Remove),
                Count(ImportAction.Skip),
                Count(ImportAction.Conflict),
                _entries.Values.Count(entry => !entry.Game.Launch.Validated),
                _progress,
                _progressTotal,
                launcher is not null,
                launcher is null
                    ? "The packaged-game launcher is missing from this install, so nothing can be imported."
                    : null,
                _phase is "scanning" or "applying",
                _notice,
                _error,
                _revision);
        }
    }

    private async Task ScanCoreAsync(long generation, CancellationToken cancellationToken)
    {
        List<DiscoveredGame> discovered = [];
        foreach (var source in _sources)
        {
            discovered.AddRange(await source.DiscoverAsync(cancellationToken).ConfigureAwait(false));
        }

        var existing = await _readLibrary(cancellationToken).ConfigureAwait(false);
        var launcher = _resolveLauncher() ?? string.Empty;
        var recorded = _store.Entries();
        var choices = _store.Choices();
        var plan = ImportPlan.Build(
            discovered, recorded, existing, launcher, _defaultMode(), _includeUnroutable());

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _entries.Clear();
            var index = 0;
            foreach (var entry in plan)
            {
                var game = discovered.FirstOrDefault(candidate =>
                    string.Equals(candidate.SourceId, entry.Source, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Key, entry.Key, StringComparison.OrdinalIgnoreCase));

                // Opaque per-publication ids, so a page rendered against an older scan cannot
                // address an entry by guessing a title's identity.
                var id = index++.ToString(CultureInfo.InvariantCulture);

                // An acknowledgement the user already gave is theirs, and losing it here is not
                // cosmetic: an acknowledged multiplayer title whose launch fields changed becomes an
                // Update, and composing that update without the acknowledgement throws.
                var record = recorded.FirstOrDefault(saved =>
                    ImportPlan.Matches(saved, entry.Source, entry.Key));
                var created = new Entry(id, entry, game ?? Placeholder(entry))
                {
                    Mode = entry.Mode,
                    Acknowledged = entry.Mode is ImportMode.SteamIntegration
                                   && (record?.Acknowledged ?? false),
                    ArtworkApplied = record is { AppId: > 0 } ? record.ArtworkApplied : null
                };

                // The user's own decisions, laid over what the plan derived. A picked mode the plan
                // would now refuse - the title lost its validated route, or became multiplayer
                // without the risk having been accepted - is not honoured, and the plan's stands.
                var choice = choices.FirstOrDefault(saved =>
                    string.Equals(saved.Source, entry.Source, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(saved.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
                if (choice?.PickedMode() is { } picked && Refusal(entry, picked, choice.Acknowledged) is null)
                {
                    created.Mode = picked;
                    created.Acknowledged = picked is ImportMode.SteamIntegration && choice.Acknowledged;
                }

                created.Excluded = choice?.Excluded == true && Excludable(entry);

                // Never pre-tick something the sources could not vouch for, a deletion, or a title
                // the user said to leave out.
                created.Selected = created.Selectable
                                   && created.Action is ImportAction.Add or ImportAction.Update
                                   && (game?.IsGame ?? false);
                _entries[id] = created;
            }

            _phase = "review";
            _notice = plan.Count == 0 ? "No games were found." : null;
            Publish();
        }
    }

    private async Task ApplyCoreAsync(
        long generation, IReadOnlyList<Entry> selected, CancellationToken cancellationToken)
    {
        _artworkMissing = false;
        _controllerFailures.Clear();
        var writer = _writer();
        if (writer is null)
        {
            Fail(generation, "Steam is not reachable, so nothing was imported.");
            return;
        }

        var launcher = _resolveLauncher();
        if (launcher is null)
        {
            Fail(generation, "The packaged-game launcher is missing from this install.");
            return;
        }

        var applied = 0;
        foreach (var entry in selected.Take(MaximumPerRun))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-read immediately before each write: the user may have added or removed something
            // in Steam since the scan, and acting on stale state is how the wrong entry is changed.
            // This re-checks Steam, not the user's decision: rebuilding the whole plan here would
            // derive the action from the recorded mode again and quietly discard the route they
            // just chose, and would read a removal's placeholder as a discovered title.
            var existing = await _readLibrary(cancellationToken).ConfigureAwait(false);
            if (Revalidate(entry, existing, launcher) is not { } current)
            {
                Note(generation, $"{entry.Plan.Name} changed since the scan and was left alone.");
                continue;
            }

            var fields = PackagedLauncherShortcut.Compose(
                launcher, entry.Game.Key, entry.Mode,
                entry.Game.Multiplayer is MultiplayerVerdict.Multiplayer, entry.Acknowledged);

            // Adoption writes nothing, so it must record what the shortcut actually says. Recording
            // freshly composed fields instead would make the very next scan report that somebody
            // had changed the command.
            if (current.Action is ImportAction.Adopt
                && existing.FirstOrDefault(shortcut => shortcut.AppId == current.AppId) is { } adopted)
            {
                fields = fields with { Target = adopted.Target, LaunchOptions = adopted.LaunchOptions };
            }

            var result = current.Action switch
            {
                ImportAction.Add => await writer.AddAsync(entry.Plan.Name, fields, cancellationToken)
                    .ConfigureAwait(false),
                ImportAction.Adopt => new ShortcutWriteResult(current.AppId, true, null),
                ImportAction.Update => await writer.UpdateAsync(current.AppId, fields, cancellationToken)
                    .ConfigureAwait(false),
                // A record whose shortcut Steam no longer has: there is nothing to ask the client
                // to delete, so nothing is asked, and only the record and its override go.
                ImportAction.Remove => existing.All(shortcut => shortcut.AppId != current.AppId)
                    ? new ShortcutWriteResult(current.AppId, true, null)
                    : await writer.RemoveAsync(current.AppId, cancellationToken).ConfigureAwait(false),
                _ => new ShortcutWriteResult(0, false, "Nothing to do.")
            };

            if (current.Action is ImportAction.Remove)
            {
                if (result.Confirmed)
                {
                    // The override outlives the shortcut otherwise, and would then match nothing
                    // while still showing up as a profile the user never made.
                    await ReleaseControllerTargetAsync(current.AppId, cancellationToken)
                        .ConfigureAwait(false);
                    _store.Forget(entry.Game.SourceId, entry.Game.Key);
                    _store.ForgetChoice(entry.Game.SourceId, entry.Game.Key);
                    lock (_gate)
                    {
                        entry.Selected = false;
                    }

                    applied++;
                    Progress(generation, applied);
                    continue;
                }

                Fail(generation, $"{entry.Plan.Name}: {result.Error}");
                return;
            }

            ImportedEntry saved = new()
            {
                Source = entry.Game.SourceId,
                Key = entry.Game.Key,
                AppId = result.AppId,
                Name = entry.Plan.Name,
                Target = fields.Target,
                LaunchOptions = fields.LaunchOptions,
                Mode = entry.Mode.ToString(),
                Acknowledged = entry.Acknowledged,
                ImportedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ConfirmedUtc = result.Confirmed
                    ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    : string.Empty
            };
            _store.Save(saved);

            // The record now says what Steam has, so a choice for this title would only be a second,
            // stale answer to the same question.
            _store.ForgetChoice(entry.Game.SourceId, entry.Game.Key);

            if (!result.Confirmed)
            {
                // Recorded as unconfirmed and stopped. The entry may well exist, so this is not
                // retried; the next scan adopts whatever actually survived.
                Fail(generation,
                    $"{entry.Plan.Name}: {result.Error} The run stopped here; {applied} entry/entries "
                    + "were applied and nothing was retried.");
                return;
            }

            // Everything past the confirmed shortcut is decoration or policy the user can redo by
            // hand, so a failure here is noted and the run carries on. Losing the whole import over
            // a capsule that would not download is not a trade worth making.
            var artwork = await FinishEntryAsync(generation, entry, result.AppId, cancellationToken)
                .ConfigureAwait(false);
            saved.ArtworkApplied = artwork;
            _store.Save(saved);

            // The entry now has the id Steam gave it, so the review can offer its artwork without a
            // rescan - which is the point of choosing art after the write rather than before it.
            // Deselected once done. A run is capped, and a finished title left selected would be
            // taken first again by the next apply, so the titles past the cap would never be reached.
            lock (_gate)
            {
                entry.AppliedAppId = result.AppId;
                entry.ArtworkApplied = artwork;
                entry.Selected = false;
            }

            applied++;
            Progress(generation, applied);
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _phase = "done";
            _notice = $"Applied {applied} of {selected.Count} selected entry/entries."
                      + (selected.Count > MaximumPerRun
                          ? $" A run takes {MaximumPerRun} at a time; apply again for the rest."
                          : string.Empty)
                      + (_artworkMissing
                          ? " Some titles got no Store artwork; open one's details and choose Change"
                            + " artwork to pick some."
                          : string.Empty);

            // A controller-only title with no override has no working controller route, and nothing
            // on a rescan would show it: its record and shortcut say controller-only. So it is an
            // error the user sees, not a note the completion message replaces.
            _error = _controllerFailures.Count == 0
                ? null
                : $"The controller override could not be written for {string.Join(", ", _controllerFailures)}. "
                  + "Set Xbox 360 on its per-game profile in Quick Access, or apply it again.";
            Publish();
        }
    }

    /// <summary>Why a mode cannot be used for an entry, or null when it can.</summary>
    /// <param name="plan">What the plan established about the title.</param>
    /// <param name="wanted">The mode asked for.</param>
    /// <param name="acknowledged">Whether the risk was accepted along with it.</param>
    /// <returns>The refusal in words the page shows, or null.</returns>
    /// <remarks>
    ///     One rule for both the command and a stored choice, so a choice saved under one set of
    ///     facts is judged again, not trusted, when the facts change.
    /// </remarks>
    private static string? Refusal(ImportPlanEntry plan, ImportMode wanted, bool acknowledged)
    {
        if (wanted is not ImportMode.SteamIntegration)
        {
            return null;
        }

        if (!plan.CanUseSteamIntegration)
        {
            return "This title has no validated launch route, so Steam integration is not available for it.";
        }

        return plan.RequiresAcknowledgement && !acknowledged
            ? "This title is marked multiplayer. Steam integration injects into it, so the risk has to be "
              + "accepted first."
            : null;
    }

    /// <summary>Whether the user may leave a title out.</summary>
    /// <param name="plan">The title's plan entry.</param>
    /// <returns>True for a title Steam does not have as ours yet.</returns>
    /// <remarks>
    ///     Only something not yet imported can be left out. An imported title is taken out of Steam
    ///     by removing it, which is a different and deliberate act.
    /// </remarks>
    private static bool Excludable(ImportPlanEntry plan)
    {
        return plan.Action is ImportAction.Add or ImportAction.Adopt;
    }

    private Task<SteamUiCommandResult> SetExcludedAsync(string id, bool excluded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_phase == "applying")
            {
                return Task.FromResult(Frozen);
            }

            if (!_entries.TryGetValue(id, out var entry))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That entry is no longer listed."));
            }

            if (excluded && !Excludable(entry.Plan))
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "Only a title that is not imported yet can be left out. Remove an imported one instead."));
            }

            entry.Excluded = excluded;
            entry.Selected = false;
            Remember(entry, entry.Mode != entry.Plan.Mode ? entry.Mode : null);
            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Stores what the user decided about one entry, so the next scan keeps it.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="picked">The mode the user picked, or null when they have not picked one.</param>
    private void Remember(Entry entry, ImportMode? picked)
    {
        _store.SaveChoice(new ImportChoice
        {
            Source = entry.Plan.Source,
            Key = entry.Plan.Key,
            Mode = picked?.ToString() ?? string.Empty,
            Acknowledged = picked is ImportMode.SteamIntegration && entry.Acknowledged,
            Excluded = entry.Excluded
        });
    }

    /// <summary>Re-checks one selected entry against the library as it is right now.</summary>
    /// <param name="entry">The entry the user selected.</param>
    /// <param name="existing">The shortcuts Steam holds as of a moment ago.</param>
    /// <param name="launcher">The launcher a generated entry points at.</param>
    /// <returns>What to do now, or null when Steam has moved and it should be left alone.</returns>
    /// <remarks>
    ///     The user's chosen action is kept; only its premise is rechecked. An Add whose entry has
    ///     appeared in the meantime becomes an Update rather than a duplicate, and anything that
    ///     names a live entry has to still find it, and still own it.
    /// </remarks>
    private static ImportPlanEntry? Revalidate(
        Entry entry, IReadOnlyList<ExistingShortcut> existing, string launcher)
    {
        var plan = entry.Plan;
        if (entry.Action is ImportAction.Add)
        {
            return existing.FirstOrDefault(shortcut =>
                    PackagedLauncherShortcut.Owns(shortcut, launcher, entry.Game.Key))
                is { } appeared
                ? plan with { Action = ImportAction.Update, AppId = appeared.AppId }
                : plan with { Action = ImportAction.Add, AppId = 0 };
        }

        var live = existing.FirstOrDefault(shortcut => shortcut.AppId == plan.AppId);

        // A removal whose entry Steam no longer has still has a record and an override to clear,
        // and there is nothing left there to change under us.
        if (live is null)
        {
            return entry.Action is ImportAction.Remove ? plan : null;
        }

        return PackagedLauncherShortcut.Owns(live, launcher, entry.Game.Key)
            ? plan with { Action = entry.Action }
            : null;
    }

    /// <summary>Applies the catalog artwork and the controller override for one written entry.</summary>
    /// <param name="generation">The run this belongs to.</param>
    /// <param name="entry">The entry that was written.</param>
    /// <param name="appId">Its confirmed app id.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many Store images were applied.</returns>
    private async Task<int> FinishEntryAsync(
        long generation, Entry entry, uint appId, CancellationToken cancellationToken)
    {
        try
        {
            if (_setControllerTarget is not null)
            {
                await _setControllerTarget(
                        Identity(appId), entry.Plan.Name,
                        entry.Mode is ImportMode.ControllerOnly ? ManagedControllerTarget.Xbox360 : null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_gate)
            {
                _controllerFailures.Add(entry.Plan.Name);
            }

            Note(generation,
                $"{entry.Plan.Name} was added, but its controller override could not be written.");
        }

        if (_applyArtwork is null)
        {
            return 0;
        }

        if (entry.Game.Artwork.Count == 0)
        {
            _artworkMissing = true;
            return 0;
        }

        try
        {
            var applied = await _applyArtwork(appId, entry.Game.Artwork, cancellationToken)
                .ConfigureAwait(false);
            _artworkMissing |= applied == 0;
            return applied;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _artworkMissing = true;
            Note(generation, $"{entry.Plan.Name} was added, but its Store artwork did not apply.");
            return 0;
        }
    }

    /// <summary>Clears a controller override left by an earlier import.</summary>
    /// <param name="appId">The app id whose override to release.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task ReleaseControllerTargetAsync(uint appId, CancellationToken cancellationToken)
    {
        if (_setControllerTarget is null)
        {
            return;
        }

        try
        {
            await _setControllerTarget(Identity(appId), string.Empty, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The shortcut is already gone; a stale override is a profile the user can delete.
        }
    }

    /// <summary>The canonical profile identity of a Steam app id.</summary>
    /// <param name="appId">The app id.</param>
    /// <returns>The identity the running-application target reports for it when running.</returns>
    private static string Identity(uint appId)
    {
        return "steam:" + appId.ToString(CultureInfo.InvariantCulture);
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, long generation)
    {
        try
        {
            var token = _work?.Token ?? CancellationToken.None;
            await work(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _phase = "review";
                    _notice = "Stopped.";
                    Publish();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(generation, ex.Message);
        }
    }

    private long Begin(string phase)
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _phase = phase;
        _notice = null;
        _error = null;
        _progress = 0;
        _progressTotal = 0;
        Publish();
        return ++_generation;
    }

    private void Fail(long generation, string? error)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _phase = "review";
            _error = error ?? "The operation did not complete.";
            Publish();
        }
    }

    private void Note(long generation, string note)
    {
        lock (_gate)
        {
            if (generation == _generation)
            {
                _notice = note;
                Publish();
            }
        }
    }

    private void Progress(long generation, int applied)
    {
        lock (_gate)
        {
            if (generation == _generation)
            {
                _progress = applied;
                Publish();
            }
        }
    }

    private int Count(ImportAction action)
    {
        return _entries.Values.Count(entry => !entry.Excluded && entry.Action == action);
    }

    private void Publish()
    {
        _revision++;
        Changed?.Invoke();
    }

    /// <summary>What to call a source on screen, falling back to its id for one no longer registered.</summary>
    private string SourceName(string id)
    {
        return _sources.FirstOrDefault(source =>
            string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;
    }

    private static DiscoveredGame Placeholder(ImportPlanEntry entry)
    {
        // A title that is no longer installed has no discovery record, so it stands in with its
        // plan entry's own identity: the source it came from, not an assumed one.
        return new DiscoveredGame(entry.Source, entry.Key, entry.Name, string.Empty,
            new GameLaunch("Not installed", false, entry.Reason),
            MultiplayerVerdict.Unknown, entry.Reason, false, [], []);
    }

    private GameLibraryEntry Project(Entry entry)
    {
        return new GameLibraryEntry(
            entry.Id,
            entry.Plan.Name,
            SourceName(entry.Plan.Source),
            entry.Game.Key,
            entry.Game.InstallPath,
            entry.Game.Launch.Label,
            entry.Game.Launch.Validated,
            entry.Game.Launch.Evidence,
            entry.Game.Multiplayer.ToString(),
            entry.Game.MultiplayerEvidence,
            entry.Mode.ToString(),
            entry.Plan.CanUseSteamIntegration,
            entry.Plan.RequiresAcknowledgement,
            entry.Acknowledged,
            entry.Action.ToString(),
            entry.Reason,
            entry.Selected,
            entry.Selectable,
            entry.Excluded,
            entry.Game.Notes,
            entry.AppId,
            entry.Game.Artwork.Count,
            entry.ArtworkApplied);
    }

    private sealed class Entry(string id, ImportPlanEntry plan, DiscoveredGame game)
    {
        internal string Id { get; } = id;
        internal ImportPlanEntry Plan { get; } = plan;
        internal DiscoveredGame Game { get; } = game;
        internal ImportMode Mode { get; set; }
        internal bool Acknowledged { get; set; }
        internal bool Selected { get; set; }

        /// <summary>Whether the user said not to import this title.</summary>
        internal bool Excluded { get; set; }

        /// <summary>The id this run's write was confirmed with, or zero.</summary>
        internal uint AppliedAppId { get; set; }

        /// <summary>How many Store images were applied, or null before an import.</summary>
        internal int? ArtworkApplied { get; set; }

        /// <summary>The entry's Steam app id, once it has one.</summary>
        internal uint AppId => AppliedAppId > 0 ? AppliedAppId : Plan.AppId;

        /// <summary>Whether the user has moved this entry off the mode Steam's entry launches with.</summary>
        private bool Rerouted => Mode != Plan.Mode && Plan.AppId > 0;

        /// <summary>What applying this entry would now do.</summary>
        /// <remarks>
        ///     An entry Steam already has is a Skip, or an Adopt, until the user changes its route;
        ///     then the shortcut really does need rewriting. Without this the page would show the new
        ///     mode and never apply it - and an Adopt would record a mode its shortcut does not
        ///     launch with, because adopting writes nothing.
        /// </remarks>
        internal ImportAction Action =>
            Plan.Action is ImportAction.Skip or ImportAction.Adopt && Rerouted
                ? ImportAction.Update
                : Plan.Action;

        /// <summary>Whether the user may tick this entry.</summary>
        internal bool Selectable =>
            !Excluded && (Plan.Selectable || (Plan.Action is ImportAction.Skip && Rerouted));

        /// <summary>Why it cannot be ticked, when it cannot.</summary>
        internal string Reason => Excluded ? "You chose not to import this." : Plan.Reason;
    }
}

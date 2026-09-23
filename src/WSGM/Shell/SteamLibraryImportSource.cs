using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Drives the library import page.</summary>
/// <remarks>
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
internal sealed class SteamLibraryImportSource : ISteamLibraryImportBackend, IDisposable
{
    /// <summary>How many entries one apply may write, so a mistake has a bounded blast radius.</summary>
    private const int MaximumPerRun = 50;

    private readonly Func<uint, IReadOnlyList<DiscoveredArtwork>, CancellationToken, Task<int>>? _applyArtwork;
    private readonly Func<ImportMode> _defaultMode;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();
    private readonly Func<bool> _includeUnknownRuntime;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> _readLibrary;
    private readonly Func<string?> _resolveLauncher;
    private readonly Func<string, string, ManagedControllerTarget?, CancellationToken, Task>? _setControllerTarget;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ILibrarySource _source;
    private readonly ImportStateStore _store;
    private readonly Func<SteamShortcutWriter?> _writer;
    private bool _artworkMissing;
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
    /// <param name="source">Where games are discovered.</param>
    /// <param name="store">Where this run's records are kept.</param>
    /// <param name="writer">Opens a shortcut writer over the live client, or null when unreachable.</param>
    /// <param name="readLibrary">Reads the shortcuts Steam currently holds.</param>
    /// <param name="defaultMode">The mode an entry starts on.</param>
    /// <param name="includeUnknownRuntime">Whether unclassified titles may be selected.</param>
    /// <param name="applyArtwork">Applies catalog images to a confirmed app id, or null to skip.</param>
    /// <param name="setControllerTarget">Writes the per-game controller override, or null to skip.</param>
    /// <param name="resolveLauncher">Finds the packaged-game launcher, or null for the real one.</param>
    internal SteamLibraryImportSource(
        ILibrarySource source,
        ImportStateStore store,
        Func<SteamShortcutWriter?> writer,
        Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> readLibrary,
        Func<ImportMode> defaultMode,
        Func<bool> includeUnknownRuntime,
        Func<uint, IReadOnlyList<DiscoveredArtwork>, CancellationToken, Task<int>>? applyArtwork = null,
        Func<string, string, ManagedControllerTarget?, CancellationToken, Task>? setControllerTarget = null,
        Func<string?>? resolveLauncher = null)
    {
        _source = source;
        _store = store;
        _writer = writer;
        _readLibrary = readLibrary;
        _defaultMode = defaultMode;
        _includeUnknownRuntime = includeUnknownRuntime;
        _applyArtwork = applyArtwork;
        _setControllerTarget = setControllerTarget;
        _resolveLauncher = resolveLauncher ?? PackagedLauncherShortcut.ResolveLauncher;
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
            if (!_entries.TryGetValue(id, out var entry))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That entry is no longer listed."));
            }

            if (!Enum.TryParse<ImportMode>(mode, true, out var wanted))
            {
                return Task.FromResult(new SteamUiCommandResult(false, $"'{mode}' is not a launch mode."));
            }

            if (wanted is ImportMode.SteamIntegration)
            {
                // Checked here, not only in the page. A page defect must not be able to put a
                // multiplayer title on the route that injects into it.
                if (!entry.Plan.CanUseSteamIntegration)
                {
                    return Task.FromResult(new SteamUiCommandResult(false,
                        "This title has no validated launch route, so Steam integration is not "
                        + "available for it."));
                }

                if (entry.Plan.RequiresAcknowledgement && !acknowledged)
                {
                    return Task.FromResult(new SteamUiCommandResult(false,
                        "This title is marked multiplayer. Steam integration injects into it, so "
                        + "the risk has to be accepted first."));
                }
            }

            entry.Mode = wanted;
            entry.Acknowledged = wanted is ImportMode.SteamIntegration && acknowledged;
            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
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
    internal SteamLibraryImportState ReadState()
    {
        lock (_gate)
        {
            var launcher = _resolveLauncher();
            var entries = _entries.Values.OrderBy(entry => entry.Plan.Name, StringComparer.CurrentCulture)
                .Select(Project).ToList();

            return new SteamLibraryImportState(
                _source.DisplayName,
                _phase,
                entries,
                entries.Count(entry => entry.Selected),
                Count(ImportAction.Add),
                Count(ImportAction.Update),
                Count(ImportAction.Remove),
                Count(ImportAction.Skip),
                Count(ImportAction.Conflict),
                _entries.Values.Count(entry => entry.Game.Runtime is XboxRuntime.Unknown),
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
        var discovered = await _source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var existing = await _readLibrary(cancellationToken).ConfigureAwait(false);
        var launcher = _resolveLauncher() ?? string.Empty;
        var recorded = _store.Entries();
        var plan = ImportPlan.Build(
            discovered, recorded, existing, launcher, _defaultMode(), _includeUnknownRuntime());

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
                    string.Equals(candidate.Key, entry.Key, StringComparison.OrdinalIgnoreCase));

                // Opaque per-publication ids, so a page rendered against an older scan cannot
                // address an entry by guessing a title's identity.
                var id = index++.ToString(CultureInfo.InvariantCulture);

                // An acknowledgement the user already gave is theirs, and losing it here is not
                // cosmetic: an acknowledged multiplayer title whose launch fields changed becomes an
                // Update, and composing that update without the acknowledgement throws.
                var record = recorded.FirstOrDefault(saved =>
                    string.Equals(saved.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
                _entries[id] = new Entry(id, entry, game ?? Placeholder(entry))
                {
                    Mode = entry.Mode,
                    Acknowledged = entry.Mode is ImportMode.SteamIntegration
                                   && (record?.Acknowledged ?? false),

                    // Never pre-tick something the sources could not vouch for, or a deletion.
                    Selected = entry.Selectable
                               && entry.Action is ImportAction.Add or ImportAction.Update
                               && (game?.IsGame ?? false)
                };
            }

            _phase = "review";
            _notice = plan.Count == 0 ? $"No games were found from {_source.DisplayName}." : null;
            Publish();
        }
    }

    private async Task ApplyCoreAsync(
        long generation, IReadOnlyList<Entry> selected, CancellationToken cancellationToken)
    {
        _artworkMissing = false;
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
            var existing = await _readLibrary(cancellationToken).ConfigureAwait(false);
            var current = ImportPlan.Build(
                [entry.Game], _store.Entries(), existing, launcher, _defaultMode(),
                _includeUnknownRuntime()).FirstOrDefault();
            if (current is null || !current.Selectable)
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
                ImportAction.Remove => await writer.RemoveAsync(current.AppId, cancellationToken)
                    .ConfigureAwait(false),
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
                    applied++;
                    Progress(generation, applied);
                    continue;
                }

                Fail(generation, $"{entry.Plan.Name}: {result.Error}");
                return;
            }

            _store.Save(new ImportedEntry
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
            });

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
            await FinishEntryAsync(generation, entry, result.AppId, cancellationToken)
                .ConfigureAwait(false);

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
                      + (_artworkMissing
                          ? " Some titles had no Store artwork; open a game's menu and choose Change"
                            + " Artwork to set its capsule."
                          : string.Empty);
            Publish();
        }
    }

    /// <summary>Applies the catalog artwork and the controller override for one written entry.</summary>
    /// <param name="generation">The run this belongs to.</param>
    /// <param name="entry">The entry that was written.</param>
    /// <param name="appId">Its confirmed app id.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    private async Task FinishEntryAsync(
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
            Note(generation,
                $"{entry.Plan.Name} was added, but its controller override could not be written.");
        }

        if (_applyArtwork is null)
        {
            return;
        }

        if (entry.Game.Artwork.Count == 0)
        {
            _artworkMissing = true;
            return;
        }

        try
        {
            if (await _applyArtwork(appId, entry.Game.Artwork, cancellationToken)
                    .ConfigureAwait(false) == 0)
            {
                _artworkMissing = true;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _artworkMissing = true;
            Note(generation, $"{entry.Plan.Name} was added, but its Store artwork did not apply.");
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
        return _entries.Values.Count(entry => entry.Action == action);
    }

    private void Publish()
    {
        _revision++;
        Changed?.Invoke();
    }

    private static DiscoveredGame Placeholder(ImportPlanEntry entry)
    {
        return new DiscoveredGame("xbox", entry.Key, entry.Name, string.Empty, XboxRuntime.Unknown,
            entry.Reason, MultiplayerVerdict.Unknown, entry.Reason, false, [], []);
    }

    private SteamLibraryImportEntry Project(Entry entry)
    {
        return new SteamLibraryImportEntry(
            entry.Id,
            entry.Plan.Name,
            _source.DisplayName,
            entry.Game.Key,
            entry.Game.InstallPath,
            entry.Game.Runtime.ToString(),
            entry.Game.RuntimeEvidence,
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
            entry.Game.Notes);
    }

    private sealed class Entry(string id, ImportPlanEntry plan, DiscoveredGame game)
    {
        internal string Id { get; } = id;
        internal ImportPlanEntry Plan { get; } = plan;
        internal DiscoveredGame Game { get; } = game;
        internal ImportMode Mode { get; set; }
        internal bool Acknowledged { get; set; }
        internal bool Selected { get; set; }

        /// <summary>Whether the user has moved this entry off the mode the plan recorded.</summary>
        private bool Rerouted => Mode != Plan.Mode && Plan.AppId > 0;

        /// <summary>What applying this entry would now do.</summary>
        /// <remarks>
        ///     An already imported title is a Skip until the user changes its route, at which point
        ///     the shortcut really does need rewriting. Without this the page would show the new
        ///     mode, refuse the tick, and quietly never apply it.
        /// </remarks>
        internal ImportAction Action =>
            Plan.Action is ImportAction.Skip && Rerouted ? ImportAction.Update : Plan.Action;

        /// <summary>Whether the user may tick this entry.</summary>
        internal bool Selectable => Plan.Selectable || (Plan.Action is ImportAction.Skip && Rerouted);

        /// <summary>Why it cannot be ticked, when it cannot.</summary>
        internal string Reason => Plan.Reason;
    }
}

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

    private readonly Lock _gate = new();
    private readonly ILibrarySource _source;
    private readonly ImportStateStore _store;
    private readonly Func<SteamShortcutWriter?> _writer;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> _readLibrary;
    private readonly Func<ImportMode> _defaultMode;
    private readonly Func<bool> _includeUnknownRuntime;
    private readonly CancellationTokenSource _shutdown = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private CancellationTokenSource? _work;
    private long _generation;
    private string _phase = "idle";
    private string? _notice;
    private string? _error;
    private int _progress;
    private int _progressTotal;
    private long _revision;
    private bool _disposed;

    /// <summary>Creates the backend over its sources and the client calls it drives.</summary>
    internal SteamLibraryImportSource(
        ILibrarySource source,
        ImportStateStore store,
        Func<SteamShortcutWriter?> writer,
        Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> readLibrary,
        Func<ImportMode> defaultMode,
        Func<bool> includeUnknownRuntime)
    {
        _source = source;
        _store = store;
        _writer = writer;
        _readLibrary = readLibrary;
        _defaultMode = defaultMode;
        _includeUnknownRuntime = includeUnknownRuntime;
    }

    /// <summary>Raised when the published state changed.</summary>
    internal event Action? Changed;

    /// <summary>The state Steam should currently render.</summary>
    internal SteamLibraryImportState ReadState()
    {
        lock (_gate)
        {
            var launcher = PackagedLauncherShortcut.ResolveLauncher();
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

            if (!entry.Plan.Selectable)
            {
                return Task.FromResult(new SteamUiCommandResult(
                    false, entry.Plan.Reason));
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
            foreach (var entry in _entries.Values.Where(entry => entry.Plan.Selectable))
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

            if (PackagedLauncherShortcut.ResolveLauncher() is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "The packaged-game launcher is missing from this install, so an imported entry "
                    + "would point at nothing."));
            }

            var selected = _entries.Values.Where(entry => entry.Selected && entry.Plan.Selectable).ToList();
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

    private async Task ScanCoreAsync(long generation, CancellationToken cancellationToken)
    {
        var discovered = await _source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var existing = await _readLibrary(cancellationToken).ConfigureAwait(false);
        var launcher = PackagedLauncherShortcut.ResolveLauncher() ?? string.Empty;
        var plan = ImportPlan.Build(
            discovered, _store.Entries(), existing, launcher, _defaultMode(), _includeUnknownRuntime());

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
                _entries[id] = new Entry(id, entry, game ?? Placeholder(entry))
                {
                    Mode = entry.Mode,
                    Selected = entry.Selectable && entry.Action is ImportAction.Add or ImportAction.Update
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
        var writer = _writer();
        if (writer is null)
        {
            Fail(generation, "Steam is not reachable, so nothing was imported.");
            return;
        }

        var launcher = PackagedLauncherShortcut.ResolveLauncher();
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
            _notice = $"Applied {applied} of {selected.Count} selected entry/entries. "
                      + "Open a game's menu and choose Change Artwork to set its capsule.";
            Publish();
        }
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
        return _entries.Values.Count(entry => entry.Plan.Action == action);
    }

    private void Publish()
    {
        _revision++;
        Changed?.Invoke();
    }

    private static DiscoveredGame Placeholder(ImportPlanEntry entry)
    {
        return new DiscoveredGame("xbox", entry.Key, entry.Name, string.Empty, XboxRuntime.Unknown,
            entry.Reason, MultiplayerVerdict.Unknown, entry.Reason, false, []);
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
            entry.Plan.Action.ToString(),
            entry.Plan.Reason,
            entry.Selected,
            entry.Plan.Selectable,
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
    }
}

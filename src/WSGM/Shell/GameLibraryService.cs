using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>The Game Library: the one backend both of its surfaces drive.</summary>
/// <remarks>
///     <para>
///         Sources discover games, the plan decides what a sync would do, the user's stored choices
///         are laid over it, the artwork stage gathers candidates in the background, and an apply
///         writes shortcuts, records, controller overrides and the chosen artwork. The Steam page and
///         the overlay view both render <see cref="ReadState" /> and call the same methods, and every
///         label either shows comes from here, so a change made in one is what the other shows next,
///         in the same words.
///     </para>
///     <para>
///         A scan writes nothing to Steam. It runs on a worker, never on the caller's thread and never
///         under the lock the surfaces read through: it detects and reads the ticked sources together,
///         reads Steam's shortcuts once, classifies, and publishes a plan. The dry run is the default
///         rather than a mode.
///     </para>
///     <para>
///         Applies are serialized, one write at a time. The library is read once per run and each
///         entry's own shortcut is read again immediately before its write, so a library that changed
///         between scan and apply is not acted on from stale state. A failure stops the run and
///         reports how far it got; it does not roll back, because removing a batch of somebody's
///         shortcuts over one failed write is a worse outcome than stopping.
///     </para>
///     <para>
///         An entry's id is derived from its source and key, so the same title keeps it across scans.
///         A surface that acts on an entry after a rescan acts on the title it showed, and the user's
///         selection survives the rescan.
///     </para>
/// </remarks>
internal sealed class GameLibraryService : IGameLibraryOverlaySource, IDisposable, IChangeSource
{
    private const string NotEditable =
        "Only a title that is being imported or is already in Steam can be changed here.";

    /// <summary>How long disposal waits for a write in flight to be recorded.</summary>

    /// <summary>The answer to any change to the list while an apply is working through it.</summary>
    /// <remarks>
    ///     An apply composes a title's shortcut, writes it, and then records the mode and writes the
    ///     controller override. A mode changed in between would be recorded and pinned while the
    ///     shortcut still launched the old way, so the list is read-only until the run ends.
    /// </remarks>
    private static readonly SteamUiCommandResult Frozen =
        new(false, "An import is running. Wait for it to finish, or stop it, before changing the list.");

    private static readonly SteamUiCommandResult Unlisted = new(false, "That entry is no longer listed.");

    private static readonly SteamUiCommandResult ShuttingDown = new(false, "The Game Library is shutting down.");

    private readonly Func<uint, IReadOnlyList<(ArtworkAsset Asset, string Url)>, CancellationToken,
        Task<IReadOnlyList<ArtworkResult>>>? _applyArtwork;

    private readonly GameLibraryArtwork? _artwork;

    /// <summary>What each source's last detection found.</summary>
    private readonly Dictionary<string, SourceAvailability> _availability = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _collectionSync = new(1, 1);

    private readonly Func<bool> _controllerManaged;

    /// <summary>How many titles each source's last scan found; a source not read in full is absent.</summary>
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<ShortcutFolderConfig, ILibrarySource>? _folderSource;
    private readonly Lock _gate = new();
    private readonly IReadOnlyList<ILibrarySource> _launchers;

    /// <summary>What the last scan or apply had to say that is not an error, in the order it came up.</summary>
    private readonly List<string> _notes = [];

    private readonly Func<uint, string, CancellationToken, Task<SteamUiCommandResult>>? _openArtwork;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> _readLibrary;
    private readonly Func<IReadOnlyList<UninstallEntry>> _readPrograms;
    private readonly Func<uint, CancellationToken, Task<ExistingShortcut?>> _readShortcut;
    private readonly Func<string?> _resolveLauncher;

    private readonly Func<string, string, ManagedControllerTarget?, bool, CancellationToken, Task<bool>>?
        _setControllerTarget;

    private GameLibraryConfig _settings;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ImportStateStore _store;

    private readonly Func<string?, string, IReadOnlyCollection<uint>, IReadOnlyCollection<uint>, CancellationToken,
        Task<SteamCollectionSyncResult>>? _syncCollection;

    private readonly Func<Action<GameLibraryConfig>, GameLibraryConfig>? _updateSettings;
    private readonly Func<SteamShortcutWriter?> _writer;
    private bool _disposed;
    private string? _error;
    private long _generation;
    private string? _launcher;
    private Phase _phase = Phase.Idle;
    private int _progress;
    private int _progressTotal;
    private GameLibraryState? _published;
    private long _publishedRevision = -1;
    private bool _rescanAfterRun;
    private long _revision;
    private Task _running = Task.CompletedTask;
    private CancellationTokenSource? _work;

    /// <summary>Creates the backend over its sources and the client calls it drives.</summary>
    /// <param name="sources">The launchers games are discovered in, in the order they are listed.</param>
    /// <param name="readPrograms">
    ///     Reads Windows' installed-programs list. Called once per detection or scan, and the one list is
    ///     handed to every source.
    /// </param>
    /// <param name="store">Where this run's records are kept.</param>
    /// <param name="writer">Opens a shortcut writer over the live client, or null when unreachable.</param>
    /// <param name="readLibrary">Reads every shortcut Steam holds; throws when the library cannot be read whole.</param>
    /// <param name="readShortcut">
    ///     Reads one shortcut again, or answers null when Steam no longer has it; throws when Steam
    ///     cannot be reached.
    /// </param>
    /// <param name="applyArtwork">Applies images to a confirmed app id, or null to skip.</param>
    /// <param name="setControllerTarget">
    ///     Writes the per-game controller override, or null to skip: identity, name, target (null to
    ///     clear), whether a cleared profile left empty is removed, and a token. Answers whether the
    ///     write created the profile, which is the only kind a clear may remove.
    /// </param>
    /// <param name="resolveLauncher">Finds the packaged-game launcher, or null for the real one.</param>
    /// <param name="openArtwork">Opens the artwork page for an app id and title, or null without one.</param>
    /// <param name="controllerManaged">Whether anything switches the controller for a running title.</param>
    /// <param name="settings">Reads the library's settings, or null for the defaults.</param>
    /// <param name="updateSettings">
    ///     Persists a change to them, applied to a fresh load inside the configuration's own lock, or
    ///     null when the sources cannot be changed.
    /// </param>
    /// <param name="folderSource">Creates the source for one shortcuts folder, or null without folders.</param>
    /// <param name="artwork">The artwork stage, or null to offer only the sources' own images.</param>
    /// <param name="syncCollection">
    ///     Brings one Steam collection in step, or null without collections: the id WSGM recorded (null
    ///     for none), the name a new one gets, the apps that belong in it, the apps WSGM takes back, and
    ///     a token. An emptied collection is deleted.
    /// </param>
    internal GameLibraryService(
        IReadOnlyList<ILibrarySource> sources,
        Func<IReadOnlyList<UninstallEntry>> readPrograms,
        ImportStateStore store,
        Func<SteamShortcutWriter?> writer,
        Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> readLibrary,
        Func<uint, CancellationToken, Task<ExistingShortcut?>> readShortcut,
        GameLibraryConfig settings,
        Func<uint, IReadOnlyList<(ArtworkAsset Asset, string Url)>, CancellationToken,
            Task<IReadOnlyList<ArtworkResult>>>? applyArtwork = null,
        Func<string, string, ManagedControllerTarget?, bool, CancellationToken, Task<bool>>? setControllerTarget =
            null,
        Func<string?>? resolveLauncher = null,
        Func<uint, string, CancellationToken, Task<SteamUiCommandResult>>? openArtwork = null,
        Func<bool>? controllerManaged = null,
        Func<Action<GameLibraryConfig>, GameLibraryConfig>? updateSettings = null,
        Func<ShortcutFolderConfig, ILibrarySource>? folderSource = null,
        GameLibraryArtwork? artwork = null,
        Func<string?, string, IReadOnlyCollection<uint>, IReadOnlyCollection<uint>, CancellationToken,
            Task<SteamCollectionSyncResult>>? syncCollection = null)
    {
        _launchers = sources;
        _readPrograms = readPrograms;
        _store = store;
        _writer = writer;
        _readLibrary = readLibrary;
        _readShortcut = readShortcut;
        _applyArtwork = applyArtwork;
        _setControllerTarget = setControllerTarget;
        _resolveLauncher = resolveLauncher ?? PackagedLauncherShortcut.ResolveLauncher;
        _openArtwork = openArtwork;
        _controllerManaged = controllerManaged ?? (() => true);
        _settings = settings.Copy();
        _updateSettings = updateSettings;
        _folderSource = folderSource;
        _artwork = artwork;
        _syncCollection = syncCollection;
        if (_artwork is not null)
        {
            _artwork.Changed += OnArtworkChanged;
        }
    }

    /// <summary>The revision of the state <see cref="ReadState" /> answers, for revision-aware publication.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    private bool Busy => _phase is Phase.Scanning or Phase.Applying;

    /// <inheritdoc />
    public void Dispose() => CloseAdmission();

    /// <summary>Refuses new commands and cancels pending work without blocking the caller.</summary>
    internal void CloseAdmission()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_artwork is not null)
        {
            _artwork.Changed -= OnArtworkChanged;
        }

        _shutdown.Cancel();
    }

    /// <summary>Waits for active writes within the caller's shutdown deadline.</summary>
    internal async Task StopAsync(Deadline deadline)
    {
        CloseAdmission();
        Task running;
        lock (_gate)
        {
            running = _running;
        }

        using var stop = deadline.CreateCancellationSource();
        try
        {
            await running.WaitAsync(stop.Token).ConfigureAwait(false);
            await _collectionSync.WaitAsync(stop.Token).ConfigureAwait(false);
            // Every collection writer uses the cancelled lifetime or run token.
            _collectionSync.Release();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Active work keeps its state until it finishes; a sent write must still be recorded.
        }
    }

    /// <summary>Raised when the published state changed.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(ShuttingDown);
            }

            if (_phase is Phase.Applying)
            {
                return Refuse("An import is already running.");
            }

            StartScan();
        }

        Notify();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CancelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_disposed)
            {
                _work?.Cancel();
            }
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ToggleEntryAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditEntry(id, false, entry =>
        {
            if (!entry.Selectable)
            {
                return entry.Reason;
            }

            entry.Selected = !entry.Selected;
            return null;
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SelectAsync(
        string group, string query, bool selected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (Guard() is { } refusal)
            {
                return Task.FromResult(refusal);
            }

            var search = query.Trim();
            foreach (var entry in _entries.Values)
            {
                if ((group.Length > 0 && !string.Equals(GroupOf(entry), group, StringComparison.Ordinal))
                    || (search.Length > 0 && !entry.Plan.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // Selecting everything never takes a deletion, an add Steam may already have, or a
                // title the user removed from Steam: each of those is asked for one at a time.
                if (!selected || entry.BulkSelectable)
                {
                    entry.Selected = selected && entry.Selectable;
                }
            }

            Publish();
        }

        Notify();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetModeAsync(
        string id, string mode, bool acknowledged, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.TryParse<ImportMode>(mode, true, out var wanted) || !Enum.IsDefined(wanted))
        {
            return Refuse($"'{mode}' is not a launch mode.");
        }

        return EditEntry(id, true, entry => ChangeMode(entry, wanted, acknowledged));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CycleLaunchAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var acknowledge = false;
        var result = EditEntry(id, true, entry =>
        {
            if (!entry.Packaged)
            {
                var routes = entry.Game.CommandRoutes;
                if (routes.Count < 2)
                {
                    return "This title has one way to launch.";
                }

                var index = routes.ToList().FindIndex(route => route.Id == entry.Route);
                entry.Route = routes[(index + 1) % routes.Count].Id;
                return null;
            }

            if (entry.Mode is ImportMode.SteamIntegration)
            {
                return ChangeMode(entry, ImportMode.ControllerOnly, false);
            }

            if (entry.Plan.RequiresAcknowledgement && !entry.Acknowledged)
            {
                // Nothing changes yet: the surface asks, and comes back with the acknowledgement.
                acknowledge = true;
                return null;
            }

            return ChangeMode(entry, ImportMode.SteamIntegration, entry.Acknowledged);
        });

        return acknowledge
            ? Task.FromResult(new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
                new GameLibraryAcknowledgeAnswer(true), GameLibraryJsonContext.Default.GameLibraryAcknowledgeAnswer)))
            : result;
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetRouteAsync(string id, string route, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditEntry(id, true, entry =>
        {
            if (entry.Game.CommandRoutes.All(candidate => candidate.Id != route))
            {
                return "This title has no such route.";
            }

            entry.Route = route;
            return null;
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ExcludeAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditEntry(id, false, entry =>
        {
            if (!Excludable(entry.Plan))
            {
                return "Only a title that is not imported yet can be left out. Remove an imported one instead.";
            }

            entry.Excluded = true;
            entry.Selected = false;
            return null;
        }, true);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> IncludeAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditEntry(id, false, entry =>
        {
            entry.Excluded = false;
            return null;
        }, true);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DetailsAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ReadDetails(id) is { } details
            ? Task.FromResult(new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
                details, GameLibraryJsonContext.Default.GameLibraryDetails)))
            : Task.FromResult(Unlisted);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     For a title already in Steam. Before an import, artwork is chosen in the review itself; this
    ///     opens the artwork page for the shortcut, with the entry's own title as the search so a
    ///     shortcut Steam has not listed yet is not searched for as "App N".
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
                return Unlisted;
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

        SteamUiCommandResult opened;
        try
        {
            opened = await _openArtwork(appId, title, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
                                   && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            Log.Error("Opening imported-game artwork failed", ex);
            return new SteamUiCommandResult(false, "The artwork page could not be opened.");
        }

        if (!opened.Succeeded)
        {
            return opened;
        }

        // The route travels in the answer, the same contract the game menu's Change Artwork uses,
        // so the page follows it the way every other page-opening action is followed.
        return new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
            new GameLibraryRouteAnswer(SteamArtworkBrowserSurface.RouteFor(appId)),
            GameLibraryJsonContext.Default.GameLibraryRouteAnswer));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ApplyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var launcher = _resolveLauncher();
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(ShuttingDown);
            }

            if (Busy)
            {
                return Refuse("Something is already running.");
            }

            var selected = _entries.Values
                .Where(entry => entry is { Selected: true, Selectable: true })
                .OrderBy(entry => entry.Plan.Name, StringComparer.CurrentCulture)
                .ToList();
            if (selected.Count == 0)
            {
                return Refuse("Nothing is selected.");
            }

            _launcher = launcher;
            if (launcher is null && selected.Any(entry => entry.NeedsLauncher))
            {
                return Refuse("WSGM.PackagedLaunch is missing from this install, and the Xbox titles and the "
                              + "titles that start through their launcher run through it. Deselect them or "
                              + "repair the install.");
            }

            var (generation, token) = Begin(Phase.Applying);
            _progressTotal = selected.Count;
            Run(work => ApplyCoreAsync(generation, selected, launcher ?? string.Empty, work), generation, token);
        }

        Notify();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetSourceEnabledAsync(
        string id, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateSettings is null)
        {
            return Refuse("Sources cannot be changed in this session.");
        }

        lock (_gate)
        {
            if (Guard() is { } refusal)
            {
                return Task.FromResult(refusal);
            }

            if (Busy)
            {
                return Refuse("Wait for the current run to finish.");
            }
        }

        if (Sources(ReadSettings()).All(source => !string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return Refuse("That source is not known.");
        }

        if (!TryUpdateSettings(settings =>
            {
                settings.DisabledSources.RemoveAll(disabled =>
                    string.Equals(disabled, id, StringComparison.OrdinalIgnoreCase));
                if (!enabled)
                {
                    settings.DisabledSources.Add(id);
                }
            }, out var saveRefusal))
        {
            return Task.FromResult(saveRefusal);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(ShuttingDown);
            }

            if (!enabled)
            {
                // Taken out of the review at once, and out of the artwork stage, so its lookups stop
                // holding up the titles still listed. Its records are neither read nor offered for
                // removal until it is ticked again.
                Drop(id);
            }

            // Its titles have never been read, so only a scan can show them. An apply that started
            // in the moment the setting was being saved gets the scan once it has finished.
            if (enabled)
            {
                ScanOrQueue();
            }

            Publish();
        }

        if (!enabled)
        {
            ResetArtwork(Sources(ReadSettings()));
        }

        Notify();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetCollectionsAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateSettings is null || _syncCollection is null)
        {
            return Refuse("Collections cannot be made in this session.");
        }

        lock (_gate)
        {
            if (Guard() is { } refusal)
            {
                return Task.FromResult(refusal);
            }
        }

        if (!TryUpdateSettings(settings => settings.CreateCollections = enabled, out var saveRefusal))
        {
            return Task.FromResult(saveRefusal);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(ShuttingDown);
            }

            Publish();
        }

        Notify();

        // Titles imported before the switch was on join their collections now. A run in progress
        // reads the switch when it finishes and brings them in itself.
        if (enabled)
        {
            _ = Task.Run(async () =>
            {
                List<string> problems = [];
                await SyncCollectionsAsync(problems, _shutdown.Token).ConfigureAwait(false);
                if (problems.Count == 0)
                {
                    return;
                }

                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _error = string.Join(" ", problems);
                    Publish();
                }

                Notify();
            });
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> AddFolderAsync(
        string path, bool includeSubfolders, IReadOnlyList<string> extensions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateSettings is null || _folderSource is null)
        {
            return Refuse("Folders cannot be added in this session.");
        }

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return Refuse("Choose an absolute folder path on this machine.");
            }

            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refuse("That is not a folder path.");
        }

        // A folder on this machine, as the folder picker lists them. A network share would make every
        // scan open a connection on the page's say-so.
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Refuse("Choose a folder on this machine.");
        }

        if (!Directory.Exists(full))
        {
            return Refuse("That folder does not exist.");
        }

        List<string> types = [];
        foreach (var extension in extensions)
        {
            var normalized = extension.StartsWith('.')
                ? extension.ToLowerInvariant()
                : "." + extension.ToLowerInvariant();
            if (!ShortcutFolderConfig.AllowedExtensions.Contains(normalized, StringComparer.Ordinal))
            {
                return Refuse($"'{extension}' is not a file type a shortcuts folder can offer.");
            }

            if (!types.Contains(normalized, StringComparer.Ordinal))
            {
                types.Add(normalized);
            }
        }

        if (types.Count == 0)
        {
            return Refuse("Choose at least one file type.");
        }

        lock (_gate)
        {
            if (Guard() is { } refusal)
            {
                return Task.FromResult(refusal);
            }
        }

        // Checked inside the configuration's own lock, against the configuration as it is, so two
        // presses cannot both add the same folder.
        string? refused = null;
        if (!TryUpdateSettings(current =>
            {
                if (current.ShortcutFolders.Any(folder =>
                        string.Equals(Path.TrimEndingDirectorySeparator(folder.Path), full,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    refused = "That folder is already a source.";
                    return;
                }

                current.ShortcutFolders.Add(new ShortcutFolderConfig
                {
                    Id = FolderId(full),
                    Path = full,
                    IncludeSubfolders = includeSubfolders,
                    Extensions = types
                });
            }, out var saveRefusal))
        {
            return Task.FromResult(saveRefusal);
        }

        if (refused is not null)
        {
            return Refuse(refused);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(ShuttingDown);
            }

            // A scan already running started without the folder, so it starts again with it.
            ScanOrQueue();
            Publish();
        }

        Notify();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RemoveFolderAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateSettings is null)
        {
            return Refuse("Folders cannot be removed in this session.");
        }

        lock (_gate)
        {
            if (Guard() is { } refusal)
            {
                return Task.FromResult(refusal);
            }
        }

        var removed = false;
        if (!TryUpdateSettings(settings =>
            {
                removed = settings.ShortcutFolders.RemoveAll(folder =>
                    string.Equals(folder.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
                settings.DisabledSources.RemoveAll(disabled =>
                    string.Equals(disabled, id, StringComparison.OrdinalIgnoreCase));
            }, out var saveRefusal))
        {
            return Task.FromResult(saveRefusal);
        }

        if (!removed)
        {
            return Refuse("That folder is not a source.");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(ShuttingDown);
            }

            Drop(id);
            _availability.Remove(id);

            // A scan in progress still holds the folder's source and would bring its titles back.
            if (_phase is Phase.Scanning)
            {
                StartScan();
            }

            Publish();
        }

        ResetArtwork(Sources(ReadSettings()));
        Notify();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CycleArtworkAsync(
        string id, string asset, int delta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditArtwork(id, asset, (entry, type) =>
        {
            var options = Options(entry, type);
            if (options.Count == 0)
            {
                return "No images have been found for this yet.";
            }

            var current = Slot(entry, type, options, ReadSettings().ArtworkPreference);
            var index = current.Index - 1;
            var next = index < 0
                ? delta >= 0 ? 0 : options.Count - 1
                : ((index + delta) % options.Count + options.Count) % options.Count;
            entry.Picks[type] = Pick(type, options[next]);
            return null;
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> PickArtworkAsync(
        string id, string asset, string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditArtwork(id, asset, (entry, type) =>
        {
            var option = Options(entry, type).FirstOrDefault(candidate =>
                string.Equals(candidate.Url, url, StringComparison.Ordinal));
            if (option is null)
            {
                return "That image is not one of this title's candidates.";
            }

            entry.Picks[type] = Pick(type, option);
            return null;
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ClearArtworkAsync(string id, string asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditArtwork(id, asset, (entry, type) =>
        {
            entry.Picks[type] = new ArtworkPick { Asset = type };
            return null;
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> FillArtworkAsync(
        string preference, bool onlyEmpty, string asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.TryParse<ArtworkPreference>(preference, true, out var wanted) || !Enum.IsDefined(wanted))
        {
            return Refuse($"'{preference}' is not an artwork source.");
        }

        ArtworkAsset[] types;
        if (asset.Length == 0)
        {
            types = GameLibraryArtwork.Assets;
        }
        else if (ArtworkAssetNames.TryParse(asset, out var one))
        {
            types = [one];
        }
        else
        {
            return Refuse($"'{asset}' is not an artwork type.");
        }

        return EditMany(entry =>
        {
            var changed = false;
            var fallback = ReadSettings().ArtworkPreference;
            foreach (var type in types)
            {
                var options = Options(entry, type);

                // "Empty" is a slot that would get nothing: an image already chosen, the default a new
                // title starts on, and the artwork Steam already has all count as filled.
                if (onlyEmpty && Slot(entry, type, options, fallback).Kind is "pick" or "default" or "keep")
                {
                    continue;
                }

                if (Preferred(options, wanted, false) is { } option)
                {
                    entry.Picks[type] = Pick(type, option);
                    changed = true;
                }
            }

            return changed;
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ResetArtworkAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditMany(entry =>
        {
            if (entry.Picks.Count == 0)
            {
                return false;
            }

            entry.Picks.Clear();
            if (!entry.Selectable)
            {
                entry.Selected = false;
            }

            return true;
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ArtworkOptionsAsync(string id, string asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ArtworkAssetNames.TryParse(asset, out var type))
        {
            return Refuse($"'{asset}' is not an artwork type.");
        }

        GameLibraryOptionsAnswer answer;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return Task.FromResult(Unlisted);
            }

            if (!entry.Editable)
            {
                return Refuse(NotEditable);
            }

            answer = ArtworkOptionsOf(entry, type);
        }

        return Task.FromResult(new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
            answer, GameLibraryJsonContext.Default.GameLibraryOptionsAnswer)));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Every provider is searched at once and the results are listed together, as the artwork page
    ///     does, so a title only Screenscraper knows can be matched even when SteamGridDB returns
    ///     guesses. Only the automatic match asks the providers one after another.
    /// </remarks>
    public async Task<SteamUiCommandResult> SearchMatchAsync(
        string id, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_artwork is null)
        {
            return new SteamUiCommandResult(false, "No artwork provider is available in this session.");
        }

        string term;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return Unlisted;
            }

            term = query.Trim().Length > 0 ? query.Trim() : entry.Plan.Name;
        }

        var matches = await _artwork.SearchAsync(term, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
            new GameLibraryMatchesAnswer(
            [
                .. matches.Select(match => new GameLibraryMatchAnswer(
                    match.ProviderId,
                    ArtworkSearch.Find(match.ProviderId)?.DisplayName ?? match.ProviderId,
                    match.Id,
                    match.Name,
                    match.Exact))
            ]),
            GameLibraryJsonContext.Default.GameLibraryMatchesAnswer));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetMatchAsync(
        string id, string provider, string gameId, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (gameId.Length > 0 && ArtworkSearch.Find(provider) is null)
        {
            return Refuse("That artwork provider is not known.");
        }

        return EditEntry(id, true, entry =>
        {
            entry.Match = gameId.Length == 0 ? null : new ArtworkGameMatch(provider, gameId, name, true);

            // The provider images picked for the old match belong to a different game; the source's
            // own images do not.
            foreach (var (type, pick) in entry.Picks.ToList())
            {
                if (pick.Url.Length > 0 && !entry.Game.Artwork.Any(image =>
                        string.Equals(image.Url, pick.Url, StringComparison.Ordinal)))
                {
                    entry.Picks.Remove(type);
                }
            }

            _artwork?.Rematch(entry.Id, entry.Match);
            return null;
        });
    }

    public GameLibraryOptionsAnswer? ReadArtworkOptions(string id, string asset)
    {
        if (!ArtworkAssetNames.TryParse(asset, out var type))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || !entry.Editable)
            {
                return null;
            }

            return ArtworkOptionsOf(entry, type);
        }
    }

    private GameLibraryOptionsAnswer ArtworkOptionsOf(Entry entry, ArtworkAsset type)
    {
        _artwork?.Prioritize(entry.Id);
        var options = Options(entry, type);
        var progress = Progress(entry);
        return new GameLibraryOptionsAnswer(ArtworkAssetNames.ToId(type), StatusName(progress.Status),
            progress.Detail, Slot(entry, type, options, ReadSettings().ArtworkPreference).Index, options);
    }

    /// <summary>The state both surfaces render.</summary>
    /// <remarks>
    ///     Built once per revision and kept: the Steam bridge and the overlay both ask on every round,
    ///     and rebuilding every entry each time was the whole library projected over and over while
    ///     nothing had changed.
    /// </remarks>
    public GameLibraryState ReadState()
    {
        lock (_gate)
        {
            if (_published is { } cached && _publishedRevision == _revision)
            {
                return cached;
            }

            _published = BuildState();
            _publishedRevision = _revision;
            return _published;
        }
    }

    /// <summary>The evidence behind one title, for its details sheet.</summary>
    /// <param name="id">The entry.</param>
    /// <returns>The details, or null when the entry is no longer listed.</returns>
    public GameLibraryDetails? ReadDetails(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return null;
            }

            var route = entry.Game.CommandRoutes.FirstOrDefault(candidate => candidate.Id == entry.Route);
            return new GameLibraryDetails(
                entry.Game.InstallPath,
                $"{SourceNames(Sources(ReadSettings())).GetValueOrDefault(entry.Plan.Source, entry.Plan.Source)}: "
                + entry.Plan.Key,
                route?.Evidence ?? entry.Game.Launch.Evidence,
                entry.Game.Multiplayer.ToString(),
                entry.Game.MultiplayerEvidence,
                entry.Game.Notes);
        }
    }

    private GameLibraryConfig ReadSettings()
    {
        lock (_gate)
        {
            return _settings;
        }
    }

    private bool TryUpdateSettings(Action<GameLibraryConfig> change, out SteamUiCommandResult refusal)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    refusal = ShuttingDown;
                    return false;
                }

                _settings = _updateSettings!(change).Copy();
            }
            refusal = SteamUiCommandResult.Applied;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Saving Game Library settings failed", ex);
            refusal = new SteamUiCommandResult(false, "Game Library settings could not be saved.");
            return false;
        }
    }

    /// <summary>Detects every source in the background, so the sidebar knows what is installed.</summary>
    internal void Start()
    {
        _ = Task.Run(() =>
        {
            var launcher = _resolveLauncher();
            var detected = DetectSources(Sources(ReadSettings()), _readPrograms());
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _launcher = launcher;
                Record(detected);
                Publish();
            }

            Notify();
        });
    }

    /// <summary>Takes a configuration reload: the artwork settings and the library's own may have changed.</summary>
    internal void ConfigurationChanged(GameLibraryConfig settings)
    {
        _artwork?.ConfigurationChanged();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _settings = settings.Copy();
            Publish();
        }

        Notify();
    }

    private static Task<SteamUiCommandResult> Refuse(string reason)
    {
        return Task.FromResult(new SteamUiCommandResult(false, reason));
    }

    /// <summary>The refusal every change to the list gets while the service cannot take one, or null.</summary>
    private SteamUiCommandResult? Guard()
    {
        return _disposed ? ShuttingDown : _phase is Phase.Applying ? Frozen : null;
    }

    /// <summary>Changes one entry under the lock, then stores the user's choice and publishes it.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="editable">Whether the change needs a title whose launch and artwork can be changed.</param>
    /// <param name="edit">The change; answers a refusal, or null when it was made.</param>
    /// <param name="remember">Whether the change is a choice to keep across scans.</param>
    private Task<SteamUiCommandResult> EditEntry(
        string id, bool editable, Func<Entry, string?> edit, bool remember = false)
    {
        var result = SteamUiCommandResult.Applied;
        lock (_gate)
        {
            if (Guard() is { } refusal)
            {
                return Task.FromResult(refusal);
            }

            if (!_entries.TryGetValue(id, out var entry))
            {
                return Task.FromResult(Unlisted);
            }

            if (editable && !entry.Editable)
            {
                return Refuse(NotEditable);
            }

            if (edit(entry) is { } reason)
            {
                return Refuse(reason);
            }

            Publish();
            if (editable || remember)
            {
                try
                {
                    _store.SaveChoice(ChoiceOf(entry));
                }
                catch (ImportStateException ex)
                {
                    result = new SteamUiCommandResult(false, $"Changed here, but not saved for the next scan. {ex.Message}");
                }
            }
        }

        Notify();
        return Task.FromResult(result);
    }

    /// <summary>Changes one entry's artwork type, then stores and publishes it.</summary>
    private Task<SteamUiCommandResult> EditArtwork(string id, string asset, Func<Entry, ArtworkAsset, string?> edit)
    {
        if (!ArtworkAssetNames.TryParse(asset, out var type))
        {
            return Refuse($"'{asset}' is not an artwork type.");
        }

        return EditEntry(id, true, entry =>
        {
            if (edit(entry, type) is { } refusal)
            {
                return refusal;
            }

            // An imported title whose artwork was just changed is the thing to save next.
            if (entry.ArtworkOnly)
            {
                entry.Selected = true;
            }

            return null;
        });
    }

    /// <summary>Changes every selected, editable entry, storing all their choices in one write.</summary>
    /// <param name="edit">The change; answers whether it changed the entry.</param>
    private Task<SteamUiCommandResult> EditMany(Func<Entry, bool> edit)
    {
        var result = SteamUiCommandResult.Applied;
        lock (_gate)
        {
            if (Guard() is { } refusal)
            {
                return Task.FromResult(refusal);
            }

            var targets = _entries.Values.Where(entry => entry is { Selected: true, Editable: true }).ToList();
            if (targets.Count == 0)
            {
                return Refuse("Select the titles to change first.");
            }

            List<ImportChoice> changed = [.. targets.Where(edit).Select(ChoiceOf)];
            Publish();
            try
            {
                _store.SaveChoices(changed);
            }
            catch (ImportStateException ex)
            {
                result = new SteamUiCommandResult(false, $"Changed here, but not saved for the next scan. {ex.Message}");
            }
        }

        Notify();
        return Task.FromResult(result);
    }

    /// <summary>Moves an entry to a mode, if the plan allows it.</summary>
    /// <returns>A refusal, or null when the mode changed.</returns>
    private static string? ChangeMode(Entry entry, ImportMode wanted, bool acknowledged)
    {
        if (!entry.Packaged)
        {
            return "This title launches by its own command, so it has no input mode. Pick a route instead.";
        }

        // Checked here, not only in the page. A page defect must not be able to put a multiplayer
        // title on the route that injects into it.
        if (Refusal(entry.Plan, wanted, acknowledged) is { } refusal)
        {
            return refusal;
        }

        entry.Mode = wanted;
        entry.Acknowledged = wanted is ImportMode.SteamIntegration && acknowledged;
        return null;
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

    /// <summary>What the user decided about one entry, as it is kept across scans.</summary>
    private static ImportChoice ChoiceOf(Entry entry)
    {
        var picked = entry.Packaged && entry.Mode != entry.Plan.Mode ? entry.Mode : (ImportMode?)null;
        return new ImportChoice
        {
            Source = entry.Plan.Source,
            Key = entry.Plan.Key,
            Mode = picked?.ToString() ?? string.Empty,
            Acknowledged = picked is ImportMode.SteamIntegration && entry.Acknowledged,
            Excluded = entry.Excluded,
            Route = entry.Route != entry.Plan.Route ? entry.Route : string.Empty,
            Artwork = [.. entry.Picks.Values],
            MatchProvider = entry.Match?.ProviderId ?? string.Empty,
            MatchId = entry.Match?.Id ?? string.Empty,
            MatchName = entry.Match?.Name ?? string.Empty
        };
    }

    /// <summary>Takes one source's titles out of the review and the artwork stage.</summary>
    private void Drop(string sourceId)
    {
        foreach (var (id, entry) in _entries.ToList())
        {
            if (string.Equals(entry.Plan.Source, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                _entries.Remove(id);
            }
        }

        _counts.Remove(sourceId);
    }

    /// <summary>Starts a scan now, or after the apply that is running.</summary>
    private void ScanOrQueue()
    {
        if (_phase is Phase.Applying)
        {
            _rescanAfterRun = true;
            return;
        }

        StartScan();
    }

    /// <summary>The sources a scan can read now: the launchers, then the configured folders.</summary>
    private IReadOnlyList<ILibrarySource> Sources(GameLibraryConfig settings)
    {
        return _folderSource is null
            ? _launchers
            : [.. _launchers, .. settings.ShortcutFolders.Select(_folderSource)];
    }

    private static Dictionary<string, string> SourceNames(IReadOnlyList<ILibrarySource> sources)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            names.TryAdd(source.Id, source.DisplayName);
        }

        return names;
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

    private void Record(List<(string Id, SourceAvailability Found)> detected)
    {
        foreach (var (id, found) in detected)
        {
            _availability[id] = found;
        }
    }

    private void StartScan()
    {
        var (generation, token) = Begin(Phase.Scanning);
        Run(work => ScanCoreAsync(generation, work), generation, token);
    }

    private async Task ScanCoreAsync(long generation, CancellationToken cancellationToken)
    {
        var launcher = _resolveLauncher();
        var settings = ReadSettings();
        var sources = Sources(settings);
        var programs = _readPrograms();
        var detected = DetectSources(sources, programs);
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _launcher = launcher;
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

        var existing = await _readLibrary(cancellationToken).ConfigureAwait(false);
        var recorded = _store.Entries();
        var recordedSources = recorded.Select(record => record.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> notes = [];
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        List<DiscoveredGame> discovered = [];
        foreach (var (source, found, failure) in answers)
        {
            if (failure is not null)
            {
                notes.Add(failure);
                continue;
            }

            discovered.AddRange(found);
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

        var plan = ImportPlan.Build(
            discovered, recorded, existing, launcher ?? string.Empty, settings.DefaultMode, settings.ImportUnroutable,
            StateOf);

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

        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _store.PruneChoices(listed, source => StateOf(source) is ImportSourceState.Read);
            }
            catch (ImportStateException ex)
            {
                notes.Add(ex.Message);
            }
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            IReadOnlyList<ImportChoice> choices;
            try
            {
                // Read here, under the lock, so a choice made while the scan ran is laid over it too.
                choices = _store.Choices();
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

            var previous = new Dictionary<string, Entry>(_entries, StringComparer.Ordinal);
            _entries.Clear();
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
                _entries[created.Id] = created;
            }

            _counts.Clear();
            foreach (var (id, count) in counts)
            {
                _counts[id] = count;
            }

            _phase = Phase.Review;
            _notes.AddRange(notes);
            if (plan.Count == 0 && notes.Count == 0)
            {
                _notes.Add(reading.Count == 0 ? "No source is ticked and installed." : "No games were found.");
            }

            Publish();
        }

        ResetArtwork(sources);
        Notify();
    }

    /// <summary>Lays what the user decided and what was recorded over one planned title.</summary>
    private static Entry Create(
        ImportPlanEntry planned, DiscoveredGame? game, ImportedEntry? record, ImportChoice? choice)
    {
        var created = new Entry(EntryId(planned.Source, planned.Key), planned, game ?? Placeholder(planned))
        {
            Mode = planned.Mode,
            Route = planned.Route,

            // An acknowledgement the user already gave is theirs, and losing it here is not cosmetic:
            // an acknowledged multiplayer title whose launch fields changed becomes an Update, and
            // composing that update without the acknowledgement throws.
            Acknowledged = planned.Mode is ImportMode.SteamIntegration && (record?.Acknowledged ?? false),
            ArtworkApplied = record is { AppId: > 0 } ? record.ArtworkApplied : null
        };

        if (choice is null)
        {
            return created;
        }

        // The user's own decisions, laid over what the plan derived. A picked mode the plan would now
        // refuse - the title lost its validated route, or became multiplayer without the risk having
        // been accepted - is not honoured, and the plan's stands. A picked route the source no longer
        // offers is dropped the same way.
        if (choice.PickedMode() is { } picked && created.Packaged
                                              && Refusal(planned, picked, choice.Acknowledged) is null)
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
                created.Picks[pick.Asset] = pick;
            }

            if (choice.MatchId.Length > 0)
            {
                created.Match = new ArtworkGameMatch(choice.MatchProvider, choice.MatchId, choice.MatchName, true);
            }
        }

        created.Excluded = choice.Excluded && Excludable(planned);
        return created;
    }

    /// <summary>Gives the artwork stage the titles now listed, those that can take artwork only.</summary>
    private void ResetArtwork(IReadOnlyList<ILibrarySource> sources)
    {
        if (_artwork is null)
        {
            return;
        }

        Dictionary<string, string> catalogs = new(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            catalogs.TryAdd(source.Id, source.CatalogName);
        }

        GameLibraryArtworkRequest[] requests;
        lock (_gate)
        {
            requests =
            [
                .. _entries.Values
                    .Where(entry => entry.Action is not (ImportAction.Remove or ImportAction.Conflict))
                    .OrderByDescending(entry => entry.Selected)
                    .Select(entry => new GameLibraryArtworkRequest(
                        entry.Id,
                        entry.Plan.Name,
                        entry.Game.Artwork,
                        catalogs.GetValueOrDefault(entry.Plan.Source, entry.Plan.Source),
                        entry.Match))
            ];
        }

        _artwork.Reset(requests);
    }

    private async Task ApplyCoreAsync(
        long generation, IReadOnlyList<Entry> selected, string launcher, CancellationToken cancellationToken)
    {
        var writer = _writer();
        if (writer is null)
        {
            Fail(generation, "Steam is not reachable, so nothing was imported.", []);
            return;
        }

        // Read once for the run. Each entry's own shortcut is read again right before its write, and
        // this run's own writes are folded in as they happen.
        var existing = (await _readLibrary(cancellationToken).ConfigureAwait(false)).ToList();
        Dictionary<(string Source, string Key), ImportedEntry> records = new(ImportPlan.Identity);
        foreach (var record in _store.Entries())
        {
            records[(record.Source, record.Key)] = record;
        }

        List<string> problems = [];
        var applied = 0;
        foreach (var entry in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = (entry.Plan.Source, entry.Plan.Key);
            var record = records.GetValueOrDefault(identity);

            // Re-read immediately before the write: the user may have changed the shortcut in Steam
            // since the scan, and acting on stale state is how the wrong entry is changed.
            ExistingShortcut? live = null;
            if (entry.Plan.AppId > 0)
            {
                live = await _readShortcut(entry.Plan.AppId, cancellationToken).ConfigureAwait(false);
                existing.RemoveAll(shortcut => shortcut.AppId == entry.Plan.AppId);
                if (live is not null)
                {
                    existing.Add(live);
                }
            }

            var claimedByOthers = records
                .Where(pair => !ImportPlan.Identity.Equals(pair.Key, identity) && pair.Value.AppId > 0)
                .Select(pair => pair.Value.AppId).ToHashSet();
            if (Revalidate(entry, existing, launcher, record, live, claimedByOthers) is not { } current)
            {
                Note(generation, $"{entry.Plan.Name} changed since the scan and was left alone.");
                continue;
            }

            if (current.Action is ImportAction.Remove)
            {
                // A record whose shortcut Steam no longer has: there is nothing to ask the client to
                // delete, so nothing is asked, and only the record and its override go.
                var removed = live is null
                    ? new ShortcutWriteResult(current.AppId, true, null)
                    : await writer.RemoveAsync(current.AppId, cancellationToken).ConfigureAwait(false);
                if (!removed.Confirmed)
                {
                    Fail(generation, $"{entry.Plan.Name}: {removed.Error}", problems);
                    return;
                }

                // Not cancellable: the shortcut is gone, and stopping before the record is dropped
                // would leave a removal the next scan offers again.
                await ReleaseControllerTargetAsync(current.AppId, record?.OwnsProfile == true)
                    .ConfigureAwait(false);
                _store.Forget(entry.Plan.Source, entry.Plan.Key);
                records.Remove(identity);
                existing.RemoveAll(shortcut => shortcut.AppId == current.AppId);
                lock (_gate)
                {
                    _entries.Remove(entry.Id);
                }

                applied++;
                Progress(generation, applied);
                continue;
            }

            var artworkOnly = entry.ArtworkOnly;
            ShortcutFields fields;
            ShortcutWriteResult result;
            if (artworkOnly && record is not null)
            {
                fields = new ShortcutFields(record.Target, string.Empty, record.LaunchOptions);
                result = new ShortcutWriteResult(current.AppId, true, null);
            }
            else if (current.Action is ImportAction.Adopt && live is not null)
            {
                // Adoption writes nothing, so it records what the shortcut actually says. Recording
                // freshly composed fields instead would make the very next scan report that somebody
                // had changed the command.
                fields = new ShortcutFields(live.Target, string.Empty, live.LaunchOptions);
                result = new ShortcutWriteResult(current.AppId, true, null);
            }
            else
            {
                if (!TryCompose(entry, launcher, out fields, out var refusal))
                {
                    // Nothing was written, so the run goes on to the next title.
                    problems.Add($"{entry.Plan.Name}: {refusal}");
                    continue;
                }

                result = current.Action is ImportAction.Add
                    ? await writer.AddAsync(entry.Plan.Name, fields, cancellationToken).ConfigureAwait(false)
                    : await writer.UpdateAsync(current.AppId, fields, cancellationToken).ConfigureAwait(false);
            }

            var saved = artworkOnly && record is not null
                ? record
                : new ImportedEntry
                {
                    Source = entry.Plan.Source,
                    Key = entry.Plan.Key,
                    AppId = result.AppId,
                    Name = entry.Plan.Name,
                    Target = fields.Target,
                    LaunchOptions = fields.LaunchOptions,
                    Mode = entry.Mode.ToString(),
                    Route = entry.Packaged ? string.Empty : entry.Route,
                    Acknowledged = entry.Acknowledged,
                    ImportedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ConfirmedUtc = result.Confirmed
                        ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                        : string.Empty,
                    ArtworkApplied = record?.ArtworkApplied ?? 0,

                    // Whether an earlier run created this title's profile. Only such a profile may be
                    // removed when its override is cleared; one the user made, or had before an
                    // adoption, is theirs.
                    OwnsProfile = record?.OwnsProfile == true
                };

            if (!result.Confirmed)
            {
                if (current.Action is ImportAction.Update)
                {
                    try
                    {
                        var observed = await _readShortcut(current.AppId, cancellationToken).ConfigureAwait(false);
                        if (observed is not null && observed.AppId == current.AppId
                                                 && CommandShortcut.Same(observed, fields.Target, fields.LaunchOptions))
                        {
                            saved.Target = observed.Target;
                            saved.LaunchOptions = observed.LaunchOptions;
                            saved.ConfirmedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                            _store.Save(saved);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException
                                               && !(ex is OperationCanceledException &&
                                                    cancellationToken.IsCancellationRequested))
                    {
                        problems.Add($"The shortcut update could not be reconciled: {ex.Message}");
                    }
                }

                // An add Steam did not confirm may still exist, so it is recorded as unconfirmed and
                // never retried; the next scan offers it for the user to check. An update is recorded
                // only when the single read above observed the composed command. Otherwise the old
                // record remains, including after a partial update. Neither path retries the write.
                if (current.Action is ImportAction.Add && result.AppId > 0)
                {
                    _store.Save(saved);
                }

                Fail(generation,
                    $"{entry.Plan.Name}: {result.Error} The run stopped here; {applied} entry/entries were "
                    + "applied and nothing was retried.", problems);
                return;
            }

            // Recorded straight after the write, before anything slower: a shortcut Steam has and
            // nothing records is the one outcome an apply must never leave behind.
            _store.Save(saved);
            records[identity] = saved;
            existing.RemoveAll(shortcut => shortcut.AppId == result.AppId);
            existing.Add(new ExistingShortcut(result.AppId, fields.Target, fields.LaunchOptions));
            if (result.Mismatch is { } mismatch)
            {
                problems.Add($"{entry.Plan.Name}: {mismatch}");
            }

            // A shortcut the user deleted and this run replaced: its override and profile go with it.
            if (current.Action is ImportAction.Add && entry.Plan.ReplacedAppId > 0)
            {
                await ReleaseControllerTargetAsync(entry.Plan.ReplacedAppId, record?.OwnsProfile == true)
                    .ConfigureAwait(false);
                saved.OwnsProfile = false;
            }

            // Everything past the recorded shortcut is decoration or policy the user can redo by hand,
            // so a failure here is noted and the run carries on.
            var ownsProfile = await WriteControllerTargetAsync(entry, current.Action, result.AppId,
                saved.OwnsProfile, problems).ConfigureAwait(false);
            int artwork;
            try
            {
                artwork = await ApplyImagesAsync(entry, current.Action is ImportAction.Add, result.AppId, problems,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Saved even when Stop lands mid-artwork: the override and the images already applied
                // are facts about Steam now.
                saved.OwnsProfile = ownsProfile;
                _store.SaveApplied(saved);
            }

            saved.ArtworkApplied = Math.Max(saved.ArtworkApplied, artwork);
            if (artwork > 0)
            {
                _store.Save(saved);
            }

            lock (_gate)
            {
                entry.Settle(result.AppId, saved.ArtworkApplied);
            }

            applied++;
            Progress(generation, applied);
        }

        // Decoration like the artwork: a collection that cannot be brought in step is a problem the
        // user sees, and the shortcuts stay as written.
        await SyncCollectionsAsync(problems, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _phase = Phase.Done;
            _notes.Add($"Applied {applied} of {selected.Count} selected entry/entries.");

            // A problem is an error the user sees, not a note the completion message replaces: a
            // controller-only title with no override has no working controller route, and nothing on a
            // rescan would show it.
            _error = problems.Count == 0 ? null : string.Join(" ", problems);
            Publish();
        }

        ResetArtwork(Sources(ReadSettings()));
        Notify();
    }

    /// <summary>
    ///     Keeps one Steam collection per group of imported titles in step with WSGM's records, when the
    ///     switch is on: created with the group's first confirmed titles, given the ones imported since,
    ///     and cleared of the ones WSGM no longer has. A group whose source is unticked is left alone.
    /// </summary>
    /// <param name="problems">Where a collection that could not be brought in step is reported.</param>
    /// <param name="cancellationToken">Cancels waiting on Steam.</param>
    private async Task SyncCollectionsAsync(List<string> problems, CancellationToken cancellationToken)
    {
        if (_syncCollection is null || !ReadSettings().CreateCollections)
        {
            return;
        }

        await _collectionSync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = ReadSettings();
            var names = SourceNames(Sources(settings));
            HashSet<string> unticked = new(settings.DisabledSources, StringComparer.OrdinalIgnoreCase);
            var recorded = _store.Collections()
                .ToDictionary(collection => collection.Group, StringComparer.OrdinalIgnoreCase);
            var imported = _store.Entries()
                .Where(entry => entry.AppId != 0)
                .GroupBy(CollectionGroup, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(entry => entry.AppId).Distinct().ToList(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (var group in imported.Keys.Union(recorded.Keys, StringComparer.OrdinalIgnoreCase)
                         .Order(StringComparer.Ordinal).ToList())
            {
                if (unticked.Contains(group))
                {
                    continue;
                }

                recorded.TryGetValue(group, out var record);
                var want = imported.GetValueOrDefault(group) ?? [];
                var name = record?.Name is { Length: > 0 } kept ? kept : names.GetValueOrDefault(group, group);
                List<uint> takeBack = record is null ? [] : [.. record.AppIds.Except(want)];
                if (want.Count == 0 && record is null)
                {
                    continue;
                }

                var result = await _syncCollection(record?.Id, name, want, takeBack, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Outcome == SteamClientWriteOutcome.NotSent)
                {
                    problems.Add("Steam could not be reached, so the collections were not updated.");
                    return;
                }

                if (!result.Succeeded)
                {
                    // A collection Steam created before a later step failed is still WSGM's: its id is
                    // kept, so the next sync changes it instead of creating a second one.
                    if (result.Id is { Length: > 0 } createdId)
                    {
                        _store.SaveCollection(new ImportedCollection
                        {
                            Group = group, Id = createdId, Name = name, AppIds = [.. want]
                        });
                    }

                    problems.Add(result.Outcome == SteamClientWriteOutcome.Unknown
                        ? $"Steam did not answer while the {name} collection was being updated."
                        : $"The {name} collection could not be updated: {result.Error}");
                    continue;
                }

                _store.SaveCollection(new ImportedCollection
                {
                    Group = group, Id = result.Id ?? string.Empty, Name = name, AppIds = [.. want]
                });
            }
        }
        catch (ImportStateException ex)
        {
            problems.Add(ex.Message);
        }
        finally
        {
            _collectionSync.Release();
        }
    }

    /// <summary>The collection an imported title belongs in: its source's.</summary>
    /// <remarks>
    ///     One per launcher and one per shortcuts folder. An emulator source gives each system its
    ///     own group here once ROMs are imported.
    /// </remarks>
    private static string CollectionGroup(ImportedEntry entry)
    {
        return entry.Source;
    }

    /// <summary>Re-checks one selected entry against the library as it is right now.</summary>
    /// <param name="entry">The entry the user selected.</param>
    /// <param name="existing">The shortcuts Steam holds, with this entry's own read a moment ago.</param>
    /// <param name="launcher">The launcher a generated packaged entry points at.</param>
    /// <param name="record">What WSGM wrote for the title before, if anything.</param>
    /// <param name="live">The entry's own shortcut as Steam has it now, when it has one.</param>
    /// <param name="claimedByOthers">The app ids other records already name, which this entry may not adopt.</param>
    /// <returns>What to do now, or null when Steam has moved and it should be left alone.</returns>
    /// <remarks>
    ///     The user's chosen action is kept; only its premise is rechecked. An Add whose entry has
    ///     appeared in the meantime becomes an Update rather than a duplicate, and anything that
    ///     names a live entry has to still find it, still own it, and for anything WSGM recorded,
    ///     still hold exactly what was recorded: a launch option the user edited between the scan and
    ///     the apply is not overwritten.
    /// </remarks>
    private static ImportPlanEntry? Revalidate(
        Entry entry, IReadOnlyList<ExistingShortcut> existing, string launcher, ImportedEntry? record,
        ExistingShortcut? live, IReadOnlySet<uint> claimedByOthers)
    {
        var plan = entry.Plan;
        if (entry.Action is ImportAction.Add)
        {
            return existing.FirstOrDefault(shortcut => !claimedByOthers.Contains(shortcut.AppId)
                && ImportPlan.Owns(shortcut, entry.Game, launcher))
                is { } appeared
                ? plan with { Action = ImportAction.Update, AppId = appeared.AppId }
                : plan with { Action = ImportAction.Add, AppId = 0 };
        }

        // A removal whose entry Steam no longer has still has a record and an override to clear,
        // and there is nothing left there to change under us.
        if (live is null)
        {
            return entry.Action is ImportAction.Remove ? plan : null;
        }

        if (record is not null)
        {
            return ImportPlan.OwnsRecorded(live, record) ? plan with { Action = entry.Action } : null;
        }

        // Nothing recorded: an adoption, or an adoption the user moved to another route, which has
        // only ownership to go on.
        return entry.Action is ImportAction.Adopt or ImportAction.Update && ImportPlan.Owns(live, entry.Game, launcher)
            ? plan with { Action = entry.Action }
            : null;
    }

    /// <summary>Composes what an entry's shortcut should run, without throwing.</summary>
    private static bool TryCompose(Entry entry, string launcher, out ShortcutFields fields, out string refusal)
    {
        if (!entry.Packaged)
        {
            var route = entry.Game.CommandRoutes.FirstOrDefault(candidate => candidate.Id == entry.Route)
                        ?? entry.Game.CommandRoutes[0];
            return CommandShortcut.TryCompose(route, launcher, out fields, out refusal);
        }

        fields = new ShortcutFields(string.Empty, string.Empty, string.Empty);
        if (launcher.Length == 0)
        {
            refusal = "WSGM.PackagedLaunch is missing from this install.";
            return false;
        }

        try
        {
            fields = PackagedLauncherShortcut.Compose(launcher, entry.Plan.Key, entry.Mode,
                entry.Game.Multiplayer is MultiplayerVerdict.Multiplayer, entry.Acknowledged);
            refusal = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            refusal = ex.Message;
            return false;
        }
    }

    /// <summary>Writes the controller override a packaged entry launches with.</summary>
    /// <returns>Whether an import now owns the title's profile.</returns>
    /// <remarks>
    ///     Only a packaged title that was written has an input mode to pin. Adopting writes nothing,
    ///     so it never touches a profile that may be the user's own, and a command title is launched
    ///     by Steam like any other non-Steam game. The write is not cancellable: it pairs with the
    ///     record just saved.
    /// </remarks>
    private async Task<bool> WriteControllerTargetAsync(
        Entry entry, ImportAction action, uint appId, bool ownsProfile, List<string> problems)
    {
        if (!entry.Packaged || entry.ArtworkOnly || action is not (ImportAction.Add or ImportAction.Update)
            || _setControllerTarget is null)
        {
            return ownsProfile;
        }

        if (entry.Mode is ImportMode.ControllerOnly && !_controllerManaged())
        {
            // The override is still written, so it takes effect as soon as management is on, but
            // until then nothing switches the controller and the title has no controller route.
            problems.Add($"{entry.Plan.Name} launches controller only, but controller management is off, so "
                         + "nothing will switch the controller. Turn on Device Integration and controller "
                         + "management in Settings.");
        }

        try
        {
            return ownsProfile | await _setControllerTarget(
                    Identity(appId), entry.Plan.Name,
                    entry.Mode is ImportMode.ControllerOnly ? ManagedControllerTarget.Xbox360 : null,
                    ownsProfile, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warn($"Game Library: the controller override for {entry.Plan.Name} failed: {exception.Message}");
            problems.Add($"The controller override could not be written for {entry.Plan.Name}. Set Xbox 360 on "
                         + "its per-game profile in Quick Access, or save it again.");
            return ownsProfile;
        }
    }

    /// <summary>Applies an entry's artwork to its confirmed shortcut.</summary>
    /// <returns>How many images the shortcut now has from WSGM.</returns>
    private async Task<int> ApplyImagesAsync(
        Entry entry, bool created, uint appId, List<string> problems, CancellationToken cancellationToken)
    {
        IReadOnlyList<(ArtworkAsset Asset, string Url)> images;
        lock (_gate)
        {
            images = Images(entry, created);
        }

        var before = entry.ArtworkApplied ?? 0;
        if (images.Count == 0 || _applyArtwork is null)
        {
            return before;
        }

        var results = await _applyArtwork(appId, images, cancellationToken).ConfigureAwait(false);
        var failed = results.Where(result => !result.Succeeded).Select(result => result.Detail).Distinct().ToList();
        if (failed.Count > 0)
        {
            problems.Add($"{entry.Plan.Name}: some images could not be applied ({string.Join(" ", failed)}). "
                         + "Pick others and save again.");
        }

        return Math.Max(before, results.Count(result => result.Succeeded));
    }

    /// <summary>The images an apply writes for one entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="created">Whether this run created its shortcut, so the defaults apply too.</param>
    /// <returns>One image per artwork type at most.</returns>
    /// <remarks>
    ///     A shortcut this run created gets every slot that shows an image, the defaults included. An
    ///     entry Steam already had keeps the artwork it has, apart from what the user picked here.
    /// </remarks>
    private List<(ArtworkAsset Asset, string Url)> Images(Entry entry, bool created)
    {
        var preference = ReadSettings().ArtworkPreference;
        List<(ArtworkAsset Asset, string Url)> images = [];
        foreach (var type in GameLibraryArtwork.Assets)
        {
            if (entry.Picks.TryGetValue(type, out var pick))
            {
                images.Add((type, pick.Url));

                continue;
            }

            if (created && Preferred(Options(entry, type), preference, true) is { } option)
            {
                images.Add((type, option.Url));
            }
        }

        return images;
    }

    /// <summary>Clears a controller override left by an earlier import. Never cancelled: it pairs with a record.</summary>
    /// <param name="appId">The app id whose override to release.</param>
    /// <param name="ownsProfile">Whether an import created the profile, so it may go once empty.</param>
    private async Task ReleaseControllerTargetAsync(uint appId, bool ownsProfile)
    {
        if (_setControllerTarget is null)
        {
            return;
        }

        try
        {
            await _setControllerTarget(Identity(appId), string.Empty, null, ownsProfile, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The shortcut is already gone; a stale override is a profile the user can delete.
            Log.Warn($"Game Library: the controller override for {appId} could not be released: {exception.Message}");
        }
    }

    /// <summary>The canonical profile identity of a Steam app id.</summary>
    /// <param name="appId">The app id.</param>
    /// <returns>The identity the running-application target reports for it when running.</returns>
    private static string Identity(uint appId)
    {
        return RunningApplicationTargetProjection.SteamIdentity(appId);
    }

    /// <summary>A shortcuts folder's source id: stable for the path, never reused for another.</summary>
    private static string FolderId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return "folder:" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>An entry's id: derived from its source and key, so a title keeps it across scans.</summary>
    /// <remarks>Hashed, so a page cannot address a title by guessing its identity.</remarks>
    private static string EntryId(string source, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            source.ToUpperInvariant() + "\u001f" + key.ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    /// <summary>Every candidate for one entry's artwork type.</summary>
    private IReadOnlyList<GameLibraryArtworkOption> Options(Entry entry, ArtworkAsset type)
    {
        return _artwork is null
            ?
            [
                .. entry.Game.Artwork.Where(image => image.Asset == type)
                    .Select(image => new GameLibraryArtworkOption(image.Url, image.Url, "Catalog", true, 0, 0))
            ]
            : _artwork.Options(entry.Id, type);
    }

    private GameLibraryArtworkProgress Progress(Entry entry)
    {
        return _artwork?.StatusOf(entry.Id)
               ?? new GameLibraryArtworkProgress(GameLibraryArtworkStatus.Ready, string.Empty, string.Empty);
    }

    /// <summary>The first candidate of the preferred kind, or of any kind when there is none of it.</summary>
    /// <param name="options">The candidates.</param>
    /// <param name="preference">Which kind leads.</param>
    /// <param name="fallBack">Whether a candidate of the other kind may stand in.</param>
    private static GameLibraryArtworkOption? Preferred(
        IReadOnlyList<GameLibraryArtworkOption> options, ArtworkPreference preference, bool fallBack)
    {
        var catalog = preference is ArtworkPreference.Catalog;
        return options.FirstOrDefault(option => option.Catalog == catalog)
               ?? (fallBack ? options.FirstOrDefault() : null);
    }

    /// <summary>What one artwork type of an entry would get.</summary>
    private GameLibraryArtworkSlot Slot(
        Entry entry, ArtworkAsset type, IReadOnlyList<GameLibraryArtworkOption> options, ArtworkPreference preference)
    {
        var name = ArtworkAssetNames.ToId(type);
        if (entry.Picks.TryGetValue(type, out var pick))
        {
            return pick.Url.Length == 0
                ? new GameLibraryArtworkSlot(name, "none", string.Empty, string.Empty, 0, options.Count)
                : new GameLibraryArtworkSlot(name, "pick", pick.Thumb, pick.Provider, IndexOf(options, pick.Url) + 1,
                    options.Count);
        }

        if (entry.AppId > 0)
        {
            return new GameLibraryArtworkSlot(name, "keep", string.Empty, string.Empty, 0, options.Count);
        }

        if (Preferred(options, preference, true) is { } option)
        {
            return new GameLibraryArtworkSlot(name, "default", option.Thumb, option.Provider,
                IndexOf(options, option.Url) + 1, options.Count);
        }

        return new GameLibraryArtworkSlot(name,
            Progress(entry).Status is GameLibraryArtworkStatus.Pending or GameLibraryArtworkStatus.Loading
                ? "loading"
                : "none",
            string.Empty, string.Empty, 0, 0);
    }

    private static int IndexOf(IReadOnlyList<GameLibraryArtworkOption> options, string url)
    {
        for (var index = 0; index < options.Count; index++)
        {
            if (string.Equals(options[index].Url, url, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static ArtworkPick Pick(ArtworkAsset type, GameLibraryArtworkOption option)
    {
        return new ArtworkPick { Asset = type, Url = option.Url, Thumb = option.Thumb, Provider = option.Provider };
    }

    private static string StatusName(GameLibraryArtworkStatus status)
    {
        return status switch
        {
            GameLibraryArtworkStatus.Loading => "loading",
            GameLibraryArtworkStatus.Ready => "ready",
            GameLibraryArtworkStatus.NotFound => "notFound",
            GameLibraryArtworkStatus.Failed => "failed",
            GameLibraryArtworkStatus.Unavailable => "unavailable",
            _ => "pending"
        };
    }

    /// <summary>Which tab lists an entry besides All; one group each, attention first.</summary>
    private string GroupOf(Entry entry)
    {
        return GroupOf(entry, Progress(entry).Status);
    }

    private static string GroupOf(Entry entry, GameLibraryArtworkStatus artwork)
    {
        if (entry.Excluded)
        {
            return "excluded";
        }

        if (entry.Action is ImportAction.Conflict or ImportAction.Remove
            || (entry.Editable && artwork is GameLibraryArtworkStatus.Failed))
        {
            return "attention";
        }

        return entry.Action is ImportAction.Add or ImportAction.Adopt ? "new" : "imported";
    }

    /// <summary>What an action is called on either surface.</summary>
    private static string ActionLabel(string action)
    {
        return action switch
        {
            "Add" => "New",
            "Update" => "Update",
            "Artwork" => "Artwork",
            "Adopt" => "Adopt",
            "Remove" => "Remove",
            "Skip" => "Imported",
            "Conflict" => "Edited by hand",
            _ => action
        };
    }

    /// <summary>What an input mode is called on either surface.</summary>
    internal static string ModeLabel(ImportMode mode)
    {
        return mode is ImportMode.SteamIntegration ? "Steam overlay" : "Controller only";
    }

    private void OnArtworkChanged()
    {
        // Rematch runs inside an edit; that edit publishes and notifies after releasing the gate.
        if (_gate.IsHeldByCurrentThread)
        {
            return;
        }

        if (!Volatile.Read(ref _disposed))
        {
            Publish();
            Notify();
        }
    }

    /// <summary>Runs scan or apply work on a worker, reporting how it ended.</summary>
    /// <param name="work">The work.</param>
    /// <param name="generation">The run it belongs to.</param>
    /// <param name="token">The run's own token, taken when it began.</param>
    private void Run(Func<CancellationToken, Task> work, long generation, CancellationToken token)
    {
        _running = Task.Run(async () =>
        {
            try
            {
                await work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The user's Stop, a rescan that replaced this one, or shutdown: the run's own token.
                lock (_gate)
                {
                    if (generation != _generation || _disposed)
                    {
                        return;
                    }

                    _phase = Phase.Review;
                    _notes.Add("Stopped.");
                    Publish();
                }

                Notify();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Anything else, a timeout's cancellation included, is a failure with its reason.
                Log.Warn($"Game Library run failed: {ex.Message}");
                Fail(generation, ex.Message, []);
            }
            finally
            {
                var restarted = false;
                lock (_gate)
                {
                    if (generation == _generation && _rescanAfterRun && !_disposed && !Busy)
                    {
                        _rescanAfterRun = false;
                        StartScan();
                        restarted = true;
                    }
                }

                if (restarted)
                {
                    Notify();
                }
            }
        });
    }

    private (long Generation, CancellationToken Token) Begin(Phase phase)
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _phase = phase;
        _notes.Clear();
        _error = null;
        _progress = 0;
        _progressTotal = 0;
        Publish();
        return (++_generation, _work.Token);
    }

    private void Fail(long generation, string error, IReadOnlyList<string> problems)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _phase = Phase.Review;
            _error = string.Join(" ", [error, .. problems]);
            Publish();
        }

        ResetArtwork(Sources(ReadSettings()));
        Notify();
    }

    private void Note(long generation, string note)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _notes.Add(note);
            Publish();
        }

        Notify();
    }

    private void Progress(long generation, int applied)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _progress = applied;
            Publish();
        }

        Notify();
    }

    private void Publish()
    {
        Interlocked.Increment(ref _revision);
    }

    private void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Game Library: a change subscriber failed: {ex.Message}");
        }
    }

    private GameLibraryState BuildState()
    {
        var settings = ReadSettings();
        var sources = Sources(settings);
        var names = SourceNames(sources);
        var disabled = new HashSet<string>(settings.DisabledSources, StringComparer.OrdinalIgnoreCase);
        var entries = _entries.Values
            .OrderBy(entry => entry.Plan.Name, StringComparer.CurrentCulture)
            .Select(entry => Project(entry, names, settings.ArtworkPreference))
            .ToList();
        List<GameLibrarySource> published =
        [
            .. sources.Select(source =>
            {
                var found = _availability.TryGetValue(source.Id, out var availability)
                    ? availability
                    : new SourceAvailability(false, "Checking…");
                return new GameLibrarySource(
                    source.Id,
                    source.DisplayName,
                    source.Id.StartsWith("folder:", StringComparison.Ordinal) ? "folder" : "launcher",
                    found.Installed,
                    !disabled.Contains(source.Id),
                    found.Detail,
                    _counts.TryGetValue(source.Id, out var count) ? count : -1);
            })
        ];

        return new GameLibraryState(
            published,
            [.. published.Where(source => source is { Installed: true, Enabled: true }).Select(source => source.Name)],
            _phase.ToString().ToLowerInvariant(),
            entries,
            entries.Count(entry => entry.Selected),
            _progress,
            _progressTotal,
            _launcher is not null,
            _launcher is null && _entries.Values.Any(entry => entry.Packaged && entry.Editable)
                ? "The packaged-game launcher is missing from this install, so Xbox titles cannot be imported."
                : null,
            Busy,
            _notes.Count == 0 ? null : string.Join(" ", _notes),
            _error,
            settings.ArtworkPreference.ToString(),
            settings.CreateCollections,
            _revision);
    }

    private static DiscoveredGame Placeholder(ImportPlanEntry entry)
    {
        // A title that is no longer installed has no discovery record, so it stands in with its plan
        // entry's own identity: the source it came from, not an assumed one. Whether it launched
        // through the packaged launcher is the plan's (its record's) to say, not this stand-in's.
        return new DiscoveredGame(entry.Source, entry.Key, entry.Name, string.Empty,
            new GameLaunch("Not installed", false, entry.Reason),
            MultiplayerVerdict.Unknown, string.Empty, false, [], []);
    }

    private GameLibraryEntry Project(Entry entry, Dictionary<string, string> names, ArtworkPreference preference)
    {
        var progress = Progress(entry);
        var route = entry.Game.CommandRoutes.FirstOrDefault(candidate => candidate.Id == entry.Route);
        return new GameLibraryEntry(
            entry.Id,
            entry.Plan.Name,
            names.GetValueOrDefault(entry.Plan.Source, entry.Plan.Source),
            entry.Plan.Source,
            entry.ActionName,
            entry.Excluded ? "Left out" : ActionLabel(entry.ActionName),
            GroupOf(entry, progress.Status),
            entry.Reason,
            entry.Selected,
            entry.Selectable,
            entry.Excluded,
            entry.Editable,
            entry.Packaged,
            entry.Mode.ToString(),
            entry.Plan.CanUseSteamIntegration,
            entry.Plan.RequiresAcknowledgement,
            entry.Acknowledged,
            entry.Packaged ? ModeLabel(entry.Mode) : route?.Label ?? entry.Game.Launch.Label,
            route?.Follows ?? false,
            [
                .. entry.Game.CommandRoutes.Select(candidate => new GameLibraryRoute(candidate.Id, candidate.Label,
                    candidate.Follows))
            ],
            entry.Route,
            entry.AppId,
            entry.ArtworkApplied,
            entry.Editable
                ? [.. GameLibraryArtwork.Assets.Select(type => Slot(entry, type, Options(entry, type), preference))]
                : [],
            StatusName(progress.Status),
            progress.Detail,
            entry.Match?.Name ?? progress.MatchName,
            entry.Match is not null);
    }

    private enum Phase
    {
        Idle,
        Scanning,
        Review,
        Applying,
        Done
    }

    private sealed class Entry(string id, ImportPlanEntry plan, DiscoveredGame game)
    {
        internal string Id { get; } = id;
        internal ImportPlanEntry Plan { get; private set; } = plan;
        internal DiscoveredGame Game { get; } = game;
        internal ImportMode Mode { get; set; }

        /// <summary>The command route it would launch with, or empty for a packaged title.</summary>
        internal string Route { get; set; } = string.Empty;

        internal bool Acknowledged { get; set; }
        internal bool Selected { get; set; }

        /// <summary>Whether the user said not to import this title.</summary>
        internal bool Excluded { get; set; }

        /// <summary>How many images were applied, or null before an import.</summary>
        internal int? ArtworkApplied { get; set; }

        /// <summary>The artwork the user picked, by type. A pick with no URL clears that type.</summary>
        internal Dictionary<ArtworkAsset, ArtworkPick> Picks { get; } = [];

        /// <summary>The game the user matched the title to at the artwork providers, or null.</summary>
        internal ArtworkGameMatch? Match { get; set; }

        /// <summary>Whether it launches through the packaged launcher.</summary>
        /// <remarks>
        ///     The plan's route says so, not the discovery: a removal's stand-in has no routes whether
        ///     it was an Xbox title or a GOG one, and its record's route is what it was imported with.
        /// </remarks>
        internal bool Packaged => Plan.Route.Length == 0;

        /// <summary>Whether saving this entry writes a shortcut that runs WSGM.PackagedLaunch.</summary>
        internal bool NeedsLauncher =>
            Action is ImportAction.Add or ImportAction.Update && !ArtworkOnly
                                                              && (Packaged ||
                                                                  Game.CommandRoutes.FirstOrDefault(candidate =>
                                                                      candidate.Id == Route) is { Follows: true });

        /// <summary>The entry's Steam app id, once it has one.</summary>
        internal uint AppId => Plan.AppId;

        /// <summary>Whether its launch and artwork can be changed here.</summary>
        internal bool Editable => Action is not (ImportAction.Remove or ImportAction.Conflict) && !Excluded;

        /// <summary>Whether the user has moved this entry off the mode or route Steam's entry launches with.</summary>
        private bool Rerouted => (Mode != Plan.Mode || Route != Plan.Route) && Plan.AppId > 0;

        /// <summary>Whether an entry Steam already has has artwork picked that is not applied yet.</summary>
        private bool ArtworkPending => Plan.AppId > 0 && Picks.Count > 0;

        /// <summary>Whether a choice the user made is waiting to be saved to an imported title.</summary>
        internal bool PendingChange => Plan.Action is ImportAction.Skip && (Rerouted || ArtworkPending);

        /// <summary>Whether saving this entry means applying artwork and nothing else.</summary>
        internal bool ArtworkOnly => Plan.Action is ImportAction.Skip && !Rerouted && ArtworkPending;

        /// <summary>What applying this entry would now do.</summary>
        /// <remarks>
        ///     An entry Steam already has is a Skip, or an Adopt, until the user changes its route or its
        ///     artwork; then it really does need saving. Without this the page would show the new mode
        ///     and never apply it, and an Adopt would record a mode its shortcut does not launch with,
        ///     because adopting writes nothing.
        /// </remarks>
        internal ImportAction Action =>
            Plan.Action switch
            {
                ImportAction.Skip when Rerouted || ArtworkPending => ImportAction.Update,
                ImportAction.Adopt when Rerouted => ImportAction.Update,
                _ => Plan.Action
            };

        /// <summary>The action's published name: Artwork for an artwork-only save.</summary>
        internal string ActionName => ArtworkOnly ? "Artwork" : Action.ToString();

        /// <summary>Whether the user may tick this entry.</summary>
        internal bool Selectable => !Excluded && (Plan.Selectable || PendingChange);

        /// <summary>Whether "select all" may tick it: never a removal, an unconfirmed add, or a title the user deleted.</summary>
        internal bool BulkSelectable =>
            Selectable && Action is not ImportAction.Remove && !Plan.Unconfirmed && Plan.ReplacedAppId == 0;

        /// <summary>Why it cannot be ticked, when it cannot.</summary>
        internal string Reason => Excluded ? "You chose not to import this." : Plan.Reason;

        /// <summary>Takes the outcome of a save: Steam now has exactly what the entry says.</summary>
        /// <param name="appId">The confirmed shortcut id.</param>
        /// <param name="artworkApplied">How many images it has from WSGM.</param>
        /// <remarks>
        ///     Without this the entry kept its scan-time action: a title just imported showed as new,
        ///     and a later artwork pick went through an update that rewrote the shortcut.
        /// </remarks>
        internal void Settle(uint appId, int artworkApplied)
        {
            Plan = Plan with
            {
                Action = ImportAction.Skip,
                AppId = appId,
                Reason = "Imported.",
                Selectable = false,
                Preselect = false,
                Unconfirmed = false,
                ReplacedAppId = 0,
                Mode = Mode,
                Route = Packaged ? string.Empty : Route
            };
            ArtworkApplied = artworkApplied;
            Picks.Clear();
            Selected = false;
        }
    }
}

using System;
using System.Collections.Generic;
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

/// <summary>Shared Game Library backend for Steam and the overlay.</summary>
/// <remarks>
///     Owns the command surface both surfaces call, the review they render and the scan/apply lifecycle.
///     The <see cref="GameLibraryScanner" /> reads the sources and publishes review plans without
///     writing Steam; the <see cref="GameLibraryApplier" /> serializes writes and re-reads each shortcut
///     before changing it, and a failure stops the run without rolling back completed writes. Stable
///     source-derived entry identities preserve selections across scans. Both surfaces consume the same
///     state, labels and commands, projected by <see cref="GameLibraryProjection" />.
/// </remarks>
internal sealed partial class GameLibraryService : IGameLibraryOverlaySource, IDisposable, IChangeSource
{
    private const string NotEditable =
        "Only a title that is being imported or is already in Steam can be changed here.";

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

    private readonly GameLibraryApplier _applier;
    private readonly GameLibraryArtwork? _artwork;
    private readonly GameLibraryEmulators _emulators;
    private readonly Dictionary<string, GameLibraryEntryState> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>What the last scan or apply had to say that is not an error, in the order it came up.</summary>
    private readonly List<string> _notes = [];

    private readonly Func<uint, string, CancellationToken, Task<SteamUiCommandResult>>? _openArtwork;
    private readonly Func<string?> _resolveLauncher;
    private readonly GameLibraryScanner _scanner;

    // Serializes settings writes so the kept copy is always the newest saved one, without holding
    // _gate across the disk write. Taken before _gate, never inside it.
    private readonly Lock _settingsWrite = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly GameLibraryCardStorage _storage;
    private readonly ImportStateStore _store;
    private readonly Func<Action<GameLibraryConfig>, GameLibraryConfig>? _updateSettings;
    private bool _disposed;
    private string? _error;
    private long _generation;
    private string? _launcher;
    // One of the GameLibraryPhases values, published as it is.
    private string _phase = GameLibraryPhases.Idle;
    private int _progress;
    private int _progressTotal;
    private GameLibraryState? _published;
    private long _publishedRevision = -1;
    private bool _rescanAfterRun;
    private long _revision;
    private Task _running = Task.CompletedTask;
    private GameLibraryConfig _settings;
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
    /// <param name="emulators">The session-owned emulator manager, or null in an isolated importer.</param>
    /// <param name="availability">The session's shortcut availability tracker, or null in an isolated importer.</param>
    /// <param name="readCollections">Reads collections while verifying the captured Steam account.</param>
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
            Task<SteamCollectionSyncResult>>? syncCollection = null,
        EmulatorService? emulators = null,
        ShortcutAvailabilityTracker? availability = null,
        Func<uint, CancellationToken, Task<IReadOnlyList<ImportedCollection>>>? readCollections = null)
    {
        _store = store;
        _resolveLauncher = resolveLauncher ?? PackagedLauncherShortcut.ResolveLauncher;
        _openArtwork = openArtwork;
        _settings = settings.Copy();
        _updateSettings = updateSettings;
        _artwork = artwork;
        _emulators = new GameLibraryEmulators(emulators);
        _storage = new GameLibraryCardStorage(this, store, availability);
        // The scanner is created next; the applier reads its sources only once a run is under way.
        _applier = new GameLibraryApplier(this, store, writer, readLibrary, readShortcut, applyArtwork,
            setControllerTarget, controllerManaged ?? (() => true), syncCollection, readCollections,
            () => _scanner!.Sources(), artwork, _storage, _emulators);
        _scanner = new GameLibraryScanner(this, sources, readPrograms, _resolveLauncher, readLibrary, folderSource,
            store, _storage, _emulators, _applier);
        if (availability is not null)
        {
            availability.Changed += OnAvailabilityChanged;
        }

        if (emulators is not null)
        {
            emulators.ChoicesChanged += OnEmulatorsChanged;
            emulators.ShortcutsChanged += NotifyManagedEntriesChanged;
        }

        if (_artwork is not null)
        {
            _artwork.Changed += OnArtworkChanged;
        }
    }

    /// <summary>The revision of the state <see cref="ReadState" /> answers, for revision-aware publication.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    /// <summary>The gate that guards the review, its run state and the collaborators' state beside it.</summary>
    internal Lock Gate => _gate;

    /// <summary>The review's entries by id. The caller holds <see cref="Gate" />.</summary>
    internal Dictionary<string, GameLibraryEntryState> Entries => _entries;

    /// <summary>The run's phase, one of the <see cref="GameLibraryPhases" /> values. The caller holds <see cref="Gate" />.</summary>
    internal string Phase => _phase;

    /// <summary>Whether admission closed. The caller holds <see cref="Gate" />.</summary>
    internal bool Closed => _disposed;

    /// <summary>Cancelled when admission closes; every run and immediate storage command is linked to it.</summary>
    internal CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>Scan, apply and the immediate storage commands share this lane; card writes also take the tracker's gate.</summary>
    internal SemaphoreSlim StorageLane { get; } = new(1, 1);

    private bool Busy => _phase is GameLibraryPhases.Scanning or GameLibraryPhases.Applying;

    /// <inheritdoc />
    public void Dispose()
    {
        CloseAdmission();
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

            if (_phase is GameLibraryPhases.Applying)
            {
                return Refuse("An import is already running.");
            }

            _scanner.StartScan();
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
                if ((group.Length > 0
                     && !string.Equals(GameLibraryProjection.GroupOf(entry, _artwork), group, StringComparison.Ordinal))
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
    public Task<SteamUiCommandResult> SetSelectedAsync(
        IReadOnlyList<string> ids, bool selected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (Guard() is { } refusal)
            {
                return Task.FromResult(refusal);
            }

            foreach (var id in ids)
            {
                if (_entries.TryGetValue(id, out var entry))
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
    public async Task<GameLibraryLaunchCycle> CycleLaunchAsync(string id, CancellationToken cancellationToken)
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

        return new GameLibraryLaunchCycle(await result.ConfigureAwait(false), acknowledge);
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
            if (!GameLibraryEntryState.Excludable(entry.Plan))
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

            var (generation, token) = Begin(GameLibraryPhases.Applying);
            _progressTotal = selected.Count;
            Run(async work =>
            {
                try
                {
                    await _applier.ApplyAsync(generation, selected, launcher ?? string.Empty, work)
                        .ConfigureAwait(false);
                }
                finally
                {
                    NotifyManagedEntriesChanged();
                }
            }, generation, token);
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

        if (_scanner.Sources().All(source => !string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return Refuse("That source is not known.");
        }

        return Task.FromResult(ChangeSettings(settings =>
            {
                settings.DisabledSources.RemoveAll(disabled =>
                    string.Equals(disabled, id, StringComparison.OrdinalIgnoreCase));
                if (!enabled)
                {
                    settings.DisabledSources.Add(id);
                }
            },
            () =>
            {
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
                    _scanner.ScanOrQueue();
                }
            },
            settled: enabled ? null : () => ResetArtwork(_scanner.Sources())));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetCollectionsAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateSettings is null || !_applier.CollectionsSupported)
        {
            return Refuse("Collections cannot be made in this session.");
        }

        var result = ChangeSettings(settings => settings.CreateCollections = enabled, static () => { });
        if (!result.Succeeded)
        {
            return Task.FromResult(result);
        }

        // Titles imported before the switch was on join their collections now. A run in progress
        // reads the switch when it finishes and brings them in itself.
        if (enabled)
        {
            _ = Task.Run(async () =>
            {
                List<string> problems = [];
                await _applier.SyncCollectionsAsync(problems, _shutdown.Token).ConfigureAwait(false);
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
        if (_updateSettings is null || !_scanner.FoldersSupported)
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
        var binding = ManagedContentStorage.CapturePath(full, true);
        string sourceId;
        try
        {
            sourceId = _store.Entries().FirstOrDefault(entry =>
                    entry.Content is { SourceKind: LibrarySourceKind.Folder } content
                    && ManagedContentStorage.SameLocation(content.SourceRoot, binding))
                ?.Source ?? FolderId(full);
        }
        catch (ImportStateException ex)
        {
            return Refuse(ex.Message);
        }

        string? refused = null;
        return Task.FromResult(ChangeSettings(current =>
            {
                if (current.ShortcutFolders.Any(folder => ManagedContentStorage.SameLocation(folder.Root, binding)))
                {
                    refused = "That folder is already a source.";
                    return;
                }

                current.ShortcutFolders.Add(new ShortcutFolderConfig
                {
                    Id = sourceId,
                    Root = binding,
                    IncludeSubfolders = includeSubfolders,
                    Extensions = types
                });
            },
            // A scan already running started without the folder, so it starts again with it.
            _scanner.ScanOrQueue,
            () => refused));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RemoveFolderAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateSettings is null)
        {
            return Refuse("Folders cannot be removed in this session.");
        }

        var removed = false;
        return Task.FromResult(ChangeSettings(settings =>
            {
                removed = settings.ShortcutFolders.RemoveAll(folder =>
                    string.Equals(folder.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
                settings.DisabledSources.RemoveAll(disabled =>
                    string.Equals(disabled, id, StringComparison.OrdinalIgnoreCase));
            },
            () =>
            {
                Drop(id);
                _scanner.ForgetDetection(id);

                // A scan in progress still holds the folder's source and would bring its titles back.
                if (_phase is GameLibraryPhases.Scanning)
                {
                    _scanner.StartScan();
                }
            },
            () => removed ? null : "That folder is not a source.",
            () => ResetArtwork(_scanner.Sources())));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CycleArtworkAsync(
        string id, string asset, int delta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditArtwork(id, asset, (entry, type) =>
        {
            var options = GameLibraryProjection.Options(entry, type, _artwork);
            if (options.Count == 0)
            {
                return "No images have been found for this yet.";
            }

            var current = GameLibraryProjection.Slot(entry, type, options, ReadSettings().ArtworkPreference,
                _artwork);
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
            var option = GameLibraryProjection.Options(entry, type, _artwork).FirstOrDefault(candidate =>
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
            entry.Picks[type] = new ArtworkPick { Asset = type, Revision = Guid.NewGuid().ToString("N") };
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
                var options = GameLibraryProjection.Options(entry, type, _artwork);

                // "Empty" is a slot that would get nothing: an image already chosen, the default a new
                // title starts on, and the artwork Steam already has all count as filled.
                if (onlyEmpty && GameLibraryProjection.Slot(entry, type, options, fallback, _artwork).Kind
                        is "pick" or "default" or "keep")
                {
                    continue;
                }

                if (GameLibraryProjection.Preferred(options, wanted, false) is { } option)
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
    public async Task<GameLibraryMatchSearch> SearchMatchAsync(
        string id, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_artwork is null)
        {
            return new GameLibraryMatchSearch(
                new SteamUiCommandResult(false, "No artwork provider is available in this session."));
        }

        string term;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return new GameLibraryMatchSearch(Unlisted);
            }

            term = query.Trim().Length > 0 ? query.Trim() : entry.Plan.Name;
        }

        var matches = await _artwork.SearchAsync(term, cancellationToken).ConfigureAwait(false);
        return new GameLibraryMatchSearch(SteamUiCommandResult.Applied, new GameLibraryMatchesAnswer(
        [
            .. matches.Select(match => new GameLibraryMatchAnswer(
                match.ProviderId,
                ArtworkSearch.Find(match.ProviderId)?.DisplayName ?? match.ProviderId,
                match.Id,
                match.Name,
                match.Exact))
        ]));
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

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RecheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        return _storage.RecheckAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RepairLibraryAsync(string id, CancellationToken cancellationToken)
    {
        return _storage.RepairLibraryAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> UseLibraryAsync(string id, CancellationToken cancellationToken)
    {
        return _storage.UseLibraryAsync(id, cancellationToken);
    }

    /// <summary>The Steam details page's Recheck: one pass, then whether the shortcut is tracked at all.</summary>
    /// <param name="appId">The shortcut's app id.</param>
    /// <param name="cancellationToken">Cancels waiting for the pass.</param>
    /// <returns>The pass's outcome, or why the shortcut has nothing to check.</returns>
    internal Task<SteamUiCommandResult> RecheckShortcutAsync(uint appId, CancellationToken cancellationToken)
    {
        return _storage.RecheckShortcutAsync(appId, cancellationToken);
    }

    /// <summary>The artwork candidates of one editable entry's type, for the overlay's picker.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="asset">The artwork type's id.</param>
    /// <returns>The candidates, or null for an unknown type or an entry that cannot be edited.</returns>
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

    /// <summary>The state both surfaces render.</summary>
    /// <remarks>
    ///     Built once per revision and kept: the Steam bridge and the overlay both ask on every round,
    ///     and rebuilding every entry each time was the whole library projected over and over while
    ///     nothing had changed.
    /// </remarks>
    /// <returns>The latest published review snapshot; this read does not start a scan.</returns>
    public GameLibraryState ReadState()
    {
        lock (_gate)
        {
            if (_published is { } cached && _publishedRevision == _revision)
            {
                return cached;
            }

            _ = _emulators.Read();
            _published = GameLibraryProjection.BuildState(
                ReadSettings(),
                _scanner.Sources(),
                _scanner.Availability,
                _scanner.Counts,
                _entries.Values,
                _artwork,
                _storage.Tracked,
                _phase,
                _progress,
                _progressTotal,
                _launcher,
                Busy,
                _notes,
                _error,
                _revision);
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
                $"{GameLibraryScanner.SourceNames(_scanner.Sources()).GetValueOrDefault(entry.Plan.Source, entry.Plan.Source)}: "
                + entry.Plan.Key,
                route?.Evidence ?? entry.Game.Launch.Evidence,
                entry.Game.Multiplayer.ToString(),
                entry.Game.MultiplayerEvidence,
                [.. entry.Game.Notes, .. GameLibraryProjection.ContentNotes(entry.Game.Content)]);
        }
    }

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

        if (_emulators.Service is { } emulators)
        {
            emulators.ChoicesChanged -= OnEmulatorsChanged;
            emulators.ShortcutsChanged -= NotifyManagedEntriesChanged;
        }

        if (_storage.Tracker is { } tracker)
        {
            tracker.Changed -= OnAvailabilityChanged;
        }

        _shutdown.Cancel();
    }

    /// <summary>Waits for active writes within the caller's shutdown deadline.</summary>
    /// <param name="deadline">Shared monotonic shutdown budget for joining admitted work.</param>
    /// <returns>Completion after work settles or the deadline expires; timeout does not undo completed imports.</returns>
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
            await StorageLane.WaitAsync(stop.Token).ConfigureAwait(false);
            StorageLane.Release();
            await _applier.JoinCollectionSyncAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Active work keeps its state until it finishes; a sent write must still be recorded.
        }
    }

    /// <summary>Detects every source in the background, so the sidebar knows what is installed.</summary>
    internal void Start()
    {
        _scanner.StartDetection();
    }

    /// <summary>Takes a configuration reload: the artwork settings and the library's own may have changed.</summary>
    /// <param name="settings">Fresh library settings used to drop disabled sources and queue any required rescan.</param>
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
            _scanner.InvalidateSources();
            if (_phase == GameLibraryPhases.Scanning)
            {
                _scanner.StartScan();
            }

            Publish();
        }

        UpdateEmulatorDependencies();
        Notify();
    }

    /// <summary>Counts emulator dependencies again and asks the tracker to check the shortcuts.</summary>
    internal void NotifyManagedEntriesChanged()
    {
        UpdateEmulatorDependencies();
        _storage.RequestCheck();
    }

    /// <summary>The library's settings as last loaded or saved.</summary>
    /// <returns>The kept copy.</returns>
    internal GameLibraryConfig ReadSettings()
    {
        lock (_gate)
        {
            return _settings;
        }
    }

    /// <summary>Whether a run is still the newest one. The caller holds <see cref="Gate" />.</summary>
    /// <param name="generation">The run.</param>
    /// <returns>True while no later run began.</returns>
    internal bool IsCurrent(long generation)
    {
        return generation == _generation;
    }

    /// <summary>Records the packaged-game launcher a detection or scan found. The caller holds <see cref="Gate" />.</summary>
    /// <param name="launcher">The launcher, or null when it is missing.</param>
    internal void RecordLauncher(string? launcher)
    {
        _launcher = launcher;
    }

    /// <summary>Starts a scan once the apply that runs has finished. The caller holds <see cref="Gate" />.</summary>
    internal void QueueRescan()
    {
        _rescanAfterRun = true;
    }

    /// <summary>The refusal every change to the list gets while the service cannot take one, or null.</summary>
    /// <returns>Shutting down, frozen during an apply, or null. The caller holds <see cref="Gate" />.</returns>
    internal SteamUiCommandResult? Guard()
    {
        return _disposed ? ShuttingDown : _phase is GameLibraryPhases.Applying ? Frozen : null;
    }

    /// <summary>Ends a scan in review with what it had to say. The caller holds <see cref="Gate" />.</summary>
    /// <param name="notes">The scan's notes, in the order they came up.</param>
    internal void ShowReview(IReadOnlyList<string> notes)
    {
        _phase = GameLibraryPhases.Review;
        _notes.AddRange(notes);
        Publish();
    }

    /// <summary>Ends an apply that went through its whole selection.</summary>
    /// <param name="generation">The run.</param>
    /// <param name="note">How much it applied.</param>
    /// <param name="problems">What went wrong without stopping it.</param>
    internal void Complete(long generation, string note, IReadOnlyList<string> problems)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _phase = GameLibraryPhases.Done;
            _notes.Add(note);

            // A problem is an error the user sees, not a note the completion message replaces: a
            // controller-only title with no override has no working controller route, and nothing on a
            // rescan would show it.
            _error = problems.Count == 0 ? null : string.Join(" ", problems);
            Publish();
        }

        ResetArtwork(_scanner.Sources());
        Notify();
    }

    /// <summary>Gives the artwork stage the titles now listed, those that can take artwork only.</summary>
    /// <param name="sources">The sources whose catalog names the lookups use.</param>
    internal void ResetArtwork(IReadOnlyList<ILibrarySource> sources)
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
                        entry.Match,
                        entry.Game.ArtworkQuery?.ProviderId ?? "steamgriddb",
                        entry.Game.ArtworkQuery?.PlatformId ?? 0,
                        entry.Game.ArtworkQuery?.ContentName ?? "",
                        entry.Game.ArtworkQuery?.ContentSize ?? 0))
            ];
        }

        _artwork.Reset(requests);
    }

    /// <summary>Counts the emulator dependencies again from the settings in force.</summary>
    internal void UpdateEmulatorDependencies()
    {
        _emulators.UpdateDependencies(ReadSettings(), _store);
    }

    /// <summary>Runs scan or apply work on a worker, reporting how it ended.</summary>
    /// <param name="work">The work.</param>
    /// <param name="generation">The run it belongs to.</param>
    /// <param name="token">The run's own token, taken when it began.</param>
    /// <remarks>The caller holds <see cref="Gate" />.</remarks>
    internal void Run(Func<CancellationToken, Task> work, long generation, CancellationToken token)
    {
        _running = Task.Run(async () =>
        {
            try
            {
                await StorageLane.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    await work(token).ConfigureAwait(false);
                }
                finally
                {
                    StorageLane.Release();
                }
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

                    _phase = GameLibraryPhases.Review;
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
                        _scanner.StartScan();
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

    /// <summary>Begins a run in a phase, cancelling the one before it.</summary>
    /// <param name="phase">The phase the run publishes.</param>
    /// <returns>The run's generation and its own token.</returns>
    /// <remarks>The caller holds <see cref="Gate" />.</remarks>
    internal (long Generation, CancellationToken Token) Begin(string phase)
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

    /// <summary>Ends a run in review with its error.</summary>
    /// <param name="generation">The run.</param>
    /// <param name="error">Why it failed.</param>
    /// <param name="problems">What went wrong before it failed.</param>
    internal void Fail(long generation, string error, IReadOnlyList<string> problems)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _phase = GameLibraryPhases.Review;
            _error = string.Join(" ", [error, .. problems]);
            Publish();
        }

        ResetArtwork(_scanner.Sources());
        Notify();
    }

    /// <summary>Adds a note to a run that is still the newest.</summary>
    /// <param name="generation">The run.</param>
    /// <param name="note">What it had to say.</param>
    internal void Note(long generation, string note)
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

    /// <summary>Reports how many selected changes a run that is still the newest has applied.</summary>
    /// <param name="generation">The run.</param>
    /// <param name="applied">How many it applied.</param>
    internal void Progress(long generation, int applied)
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

    /// <summary>Moves the published revision on, so the next read builds the state again.</summary>
    internal void Publish()
    {
        Interlocked.Increment(ref _revision);
    }

    /// <summary>Tells the subscribers the published state changed. Never under <see cref="Gate" />.</summary>
    internal void Notify()
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

    private static Task<SteamUiCommandResult> Refuse(string reason)
    {
        return Task.FromResult(new SteamUiCommandResult(false, reason));
    }

    /// <summary>Moves an entry to a mode, if the plan allows it.</summary>
    /// <returns>A refusal, or null when the mode changed.</returns>
    private static string? ChangeMode(GameLibraryEntryState entry, ImportMode wanted, bool acknowledged)
    {
        if (!entry.Packaged)
        {
            return "This title launches by its own command, so it has no input mode. Pick a route instead.";
        }

        // Checked here, not only in the page. A page defect must not be able to put a multiplayer
        // title on the route that injects into it.
        if (GameLibraryEntryState.ModeRefusal(entry.Plan, wanted, acknowledged) is { } refusal)
        {
            return refusal;
        }

        entry.Mode = wanted;
        entry.Acknowledged = wanted is ImportMode.SteamIntegration && acknowledged;
        return null;
    }

    /// <summary>A shortcuts folder's source id: stable for the path, never reused for another.</summary>
    private static string FolderId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return "folder:" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private static ArtworkPick Pick(ArtworkAsset type, GameLibraryArtworkOption option)
    {
        return new ArtworkPick
        {
            Asset = type, Url = option.Url, Thumb = option.Thumb, Provider = option.Provider,
            Revision = Guid.NewGuid().ToString("N")
        };
    }

    private GameLibraryOptionsAnswer ArtworkOptionsOf(GameLibraryEntryState entry, ArtworkAsset type)
    {
        _artwork?.Prioritize(entry.Id);
        var options = GameLibraryProjection.Options(entry, type, _artwork);
        var progress = GameLibraryProjection.Progress(entry, _artwork);
        return new GameLibraryOptionsAnswer(ArtworkAssetNames.ToId(type),
            GameLibraryProjection.StatusName(progress.Status), progress.Detail,
            GameLibraryProjection.Slot(entry, type, options, ReadSettings().ArtworkPreference, _artwork).Index,
            options);
    }

    /// <summary>
    ///     Saves a command's settings change and applies it: refused while the list cannot change, then
    ///     the write, then <paramref name="saved" /> under the gate and a publication.
    /// </summary>
    /// <param name="change">The change, applied inside the configuration's own lock.</param>
    /// <param name="saved">Applies the stored change to the review, under the gate.</param>
    /// <param name="refusal">A refusal the change itself found, read after the write; null proceeds.</param>
    /// <param name="settled">Runs after the gate is released and before subscribers are told.</param>
    /// <returns>Applied, or why the change was refused or could not be saved.</returns>
    private SteamUiCommandResult ChangeSettings(Action<GameLibraryConfig> change, Action saved,
        Func<string?>? refusal = null, Action? settled = null)
    {
        lock (_gate)
        {
            if (Guard() is { } blocked)
            {
                return blocked;
            }
        }

        if (!TryUpdateSettings(change, out var failure))
        {
            return failure;
        }

        if (refusal?.Invoke() is { } reason)
        {
            return new SteamUiCommandResult(false, reason);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return ShuttingDown;
            }

            saved();
            Publish();
        }

        settled?.Invoke();
        Notify();
        return SteamUiCommandResult.Applied;
    }

    private bool TryUpdateSettings(Action<GameLibraryConfig> change, out SteamUiCommandResult refusal)
    {
        try
        {
            lock (_settingsWrite)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        refusal = ShuttingDown;
                        return false;
                    }
                }

                // The disk write runs outside the gate, so reading the state and other commands do
                // not wait on it.
                var stored = _updateSettings!(change).Copy();
                lock (_gate)
                {
                    _settings = stored;
                    _scanner.InvalidateSources();
                }
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

    /// <summary>Changes one entry under the lock, then stores the user's choice and publishes it.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="editable">Whether the change needs a title whose launch and artwork can be changed.</param>
    /// <param name="edit">The change; answers a refusal, or null when it was made.</param>
    /// <param name="remember">Whether the change is a choice to keep across scans.</param>
    private Task<SteamUiCommandResult> EditEntry(
        string id, bool editable, Func<GameLibraryEntryState, string?> edit, bool remember = false)
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
                    _store.SaveChoice(entry.ToChoice());
                }
                catch (ImportStateException ex)
                {
                    result = new SteamUiCommandResult(false,
                        $"Changed here, but not saved for the next scan. {ex.Message}");
                }
            }

            if ((editable || remember) && _phase == GameLibraryPhases.Scanning)
            {
                _scanner.StartScan();
            }
        }

        Notify();
        return Task.FromResult(result);
    }

    /// <summary>Changes one entry's artwork type, then stores and publishes it.</summary>
    private Task<SteamUiCommandResult> EditArtwork(string id, string asset,
        Func<GameLibraryEntryState, ArtworkAsset, string?> edit)
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
    private Task<SteamUiCommandResult> EditMany(Func<GameLibraryEntryState, bool> edit)
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

            List<ImportChoice> changed = [.. targets.Where(edit).Select(entry => entry.ToChoice())];
            Publish();
            try
            {
                _store.SaveChoices(changed);
            }
            catch (ImportStateException ex)
            {
                result = new SteamUiCommandResult(false,
                    $"Changed here, but not saved for the next scan. {ex.Message}");
            }
        }

        Notify();
        return Task.FromResult(result);
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

        _scanner.ForgetCount(sourceId);
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

    private void OnAvailabilityChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // The connected cards are sources too; the next read lists the ones in the drives now.
            _scanner.InvalidateSources();
            Publish();
        }

        Notify();
    }

    private void OnEmulatorsChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _emulators.Invalidate();
            _scanner.InvalidateSources();
            Publish();
        }

        UpdateEmulatorDependencies();
        Notify();
    }
}

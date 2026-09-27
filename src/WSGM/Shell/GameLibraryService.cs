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
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The Game Library: the one backend both of its surfaces drive.</summary>
/// <remarks>
///     <para>
///         Sources discover games, the plan decides what a sync would do, the user's stored choices
///         are laid over it, the artwork stage gathers candidates in the background, and an apply
///         writes shortcuts, records, controller overrides and the chosen artwork. The Steam page and
///         the overlay view both render <see cref="ReadState" /> and call the same methods, so a
///         change made in one is what the other shows next.
///     </para>
///     <para>
///         A scan writes nothing to Steam. It detects and reads the ticked sources, classifies,
///         matches against Steam and the state file, and publishes a plan; the dry run is the
///         default rather than a mode.
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

    /// <summary>The source whose titles need the packaged launcher and carry Store images.</summary>
    private const string XboxSourceId = "xbox";

    /// <summary>The answer to any change to the list while an apply is working through it.</summary>
    /// <remarks>
    ///     An apply composes a title's shortcut, writes it, and then records the mode and writes the
    ///     controller override. A mode changed in between would be recorded and pinned while the
    ///     shortcut still launched the old way, so the list is read-only until the run ends.
    /// </remarks>
    private static readonly SteamUiCommandResult Frozen =
        new(false, "An import is running. Wait for it to finish, or stop it, before changing the list.");

    private static readonly SteamUiCommandResult Unlisted = new(false, "That entry is no longer listed.");

    private readonly Func<uint, IReadOnlyList<DiscoveredArtwork>, CancellationToken, Task<int>>? _applyArtwork;
    private readonly GameLibraryArtwork? _artwork;

    /// <summary>What each source's last detection found.</summary>
    private readonly Dictionary<string, SourceAvailability> _availability = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Titles whose controller override could not be written in this run.</summary>
    private readonly List<string> _controllerFailures = [];

    private readonly Func<bool> _controllerManaged;

    /// <summary>Controller-only titles applied while nothing manages the controller.</summary>
    private readonly List<string> _controllerUnmanaged = [];

    /// <summary>How many titles each source's last scan found; a source not scanned is absent.</summary>
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<ImportMode> _defaultMode;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<ShortcutFolderConfig, ILibrarySource>? _folderSource;

    private readonly Lock _gate = new();
    private readonly Func<bool> _includeUnroutable;
    private readonly IReadOnlyList<ILibrarySource> _launchers;
    private readonly Func<uint, string, CancellationToken, Task<SteamUiCommandResult>>? _openArtwork;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> _readLibrary;
    private readonly Func<string?> _resolveLauncher;

    private readonly Func<string, string, ManagedControllerTarget?, bool, CancellationToken, Task<bool>>?
        _setControllerTarget;

    private readonly Func<GameLibraryConfig> _settings;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ImportStateStore _store;
    private readonly Action<Action<GameLibraryConfig>>? _updateSettings;
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
    /// <param name="sources">The launchers games are discovered in, in the order they are listed.</param>
    /// <param name="store">Where this run's records are kept.</param>
    /// <param name="writer">Opens a shortcut writer over the live client, or null when unreachable.</param>
    /// <param name="readLibrary">Reads the shortcuts Steam currently holds.</param>
    /// <param name="defaultMode">The mode an Xbox entry starts on.</param>
    /// <param name="includeUnroutable">Whether titles with no validated launch route are offered.</param>
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
    /// <param name="updateSettings">Persists a change to them, or null when the sources cannot be changed.</param>
    /// <param name="folderSource">Creates the source for one shortcuts folder, or null without folders.</param>
    /// <param name="artwork">The artwork stage, or null to offer only the sources' own images.</param>
    internal GameLibraryService(
        IReadOnlyList<ILibrarySource> sources,
        ImportStateStore store,
        Func<SteamShortcutWriter?> writer,
        Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> readLibrary,
        Func<ImportMode> defaultMode,
        Func<bool> includeUnroutable,
        Func<uint, IReadOnlyList<DiscoveredArtwork>, CancellationToken, Task<int>>? applyArtwork = null,
        Func<string, string, ManagedControllerTarget?, bool, CancellationToken, Task<bool>>? setControllerTarget =
            null,
        Func<string?>? resolveLauncher = null,
        Func<uint, string, CancellationToken, Task<SteamUiCommandResult>>? openArtwork = null,
        Func<bool>? controllerManaged = null,
        Func<GameLibraryConfig>? settings = null,
        Action<Action<GameLibraryConfig>>? updateSettings = null,
        Func<ShortcutFolderConfig, ILibrarySource>? folderSource = null,
        GameLibraryArtwork? artwork = null)
    {
        _launchers = sources;
        _store = store;
        _writer = writer;
        _readLibrary = readLibrary;
        _defaultMode = defaultMode;
        _includeUnroutable = includeUnroutable;
        _applyArtwork = applyArtwork;
        _setControllerTarget = setControllerTarget;
        _resolveLauncher = resolveLauncher ?? PackagedLauncherShortcut.ResolveLauncher;
        _openArtwork = openArtwork;
        _controllerManaged = controllerManaged ?? (() => true);
        GameLibraryConfig fallback = new();
        _settings = settings ?? (() => fallback);
        _updateSettings = updateSettings;
        _folderSource = folderSource;
        _artwork = artwork;
        if (_artwork is not null)
        {
            _artwork.Changed += OnArtworkChanged;
        }
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

        if (_artwork is not null)
        {
            _artwork.Changed -= OnArtworkChanged;
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

            StartScan();
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
                return Task.FromResult(Unlisted);
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
                return Task.FromResult(Unlisted);
            }

            if (!entry.Game.Packaged)
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "This title launches by its own command, so it has no input mode. Pick a route instead."));
            }

            if (!Enum.TryParse<ImportMode>(mode, true, out var wanted) || !Enum.IsDefined(wanted))
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
            Remember(entry);
            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetRouteAsync(string id, string route, CancellationToken cancellationToken)
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
                return Task.FromResult(Unlisted);
            }

            if (entry.Game.CommandRoutes.All(candidate => candidate.Id != route))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "This title has no such route."));
            }

            entry.Route = route;
            Remember(entry);
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

        var opened = await _openArtwork(appId, title, cancellationToken).ConfigureAwait(false);
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
        lock (_gate)
        {
            if (_phase is "scanning" or "applying")
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Something is already running."));
            }

            var selected = _entries.Values.Where(entry => entry.Selected && entry.Selectable).ToList();
            if (selected.Count == 0)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Nothing is selected."));
            }

            if (_resolveLauncher() is null
                && selected.Any(entry => entry.NeedsLauncher && entry.Action is not ImportAction.Remove
                                                             && !entry.ArtworkOnly))
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "WSGM.PackagedLaunch is missing from this install, and the Xbox titles and the titles "
                    + "that start through their launcher run through it. Deselect them or repair the install."));
            }

            var generation = Begin("applying");
            _progressTotal = Math.Min(selected.Count, MaximumPerRun);
            _ = RunAsync(token => ApplyCoreAsync(generation, selected, token), generation);
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetSourceEnabledAsync(
        string id, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateSettings is null)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Sources cannot be changed in this session."));
        }

        lock (_gate)
        {
            if (_phase is "scanning" or "applying")
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Wait for the current run to finish."));
            }

            if (Sources().All(source => !string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That source is not known."));
            }
        }

        _updateSettings(settings =>
        {
            settings.DisabledSources.RemoveAll(disabled =>
                string.Equals(disabled, id, StringComparison.OrdinalIgnoreCase));
            if (!enabled)
            {
                settings.DisabledSources.Add(id);
            }
        });

        lock (_gate)
        {
            if (enabled)
            {
                // Its titles have never been read, so only a scan can show them.
                StartScan();
            }
            else
            {
                // Taken out of the review at once. Its records are neither read nor offered for removal
                // until it is ticked again.
                Drop(id);
                Publish();
            }
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> AddFolderAsync(
        string path, bool includeSubfolders, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateSettings is null || _folderSource is null)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Folders cannot be added in this session."));
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That is not a folder path."));
        }

        if (!Path.IsPathFullyQualified(full) || !Directory.Exists(full))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That folder does not exist."));
        }

        var settings = _settings();
        if (settings.ShortcutFolders.Any(folder =>
                string.Equals(Path.TrimEndingDirectorySeparator(folder.Path), full,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That folder is already a source."));
        }

        if (settings.ShortcutFolders.Count >= GameLibraryConfig.MaximumFolders)
        {
            return Task.FromResult(new SteamUiCommandResult(false,
                $"At most {GameLibraryConfig.MaximumFolders} folders can be sources."));
        }

        ShortcutFolderConfig added = new()
        {
            Id = FolderId(full),
            Path = full,
            IncludeSubfolders = includeSubfolders
        };
        _updateSettings(current => current.ShortcutFolders.Add(added));
        lock (_gate)
        {
            if (_phase is not ("scanning" or "applying"))
            {
                StartScan();
            }
            else
            {
                Publish();
            }
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RemoveFolderAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateSettings is null)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Folders cannot be removed in this session."));
        }

        if (_settings().ShortcutFolders.All(folder => !string.Equals(folder.Id, id, StringComparison.Ordinal)))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That folder is not a source."));
        }

        lock (_gate)
        {
            if (_phase == "applying")
            {
                return Task.FromResult(Frozen);
            }
        }

        _updateSettings(settings =>
        {
            settings.ShortcutFolders.RemoveAll(folder => string.Equals(folder.Id, id, StringComparison.Ordinal));
            settings.DisabledSources.RemoveAll(disabled => string.Equals(disabled, id, StringComparison.Ordinal));
        });
        lock (_gate)
        {
            Drop(id);
            _availability.Remove(id);
            _counts.Remove(id);
            Publish();
        }

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

            var current = Slot(entry, type, options);
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
            return Task.FromResult(new SteamUiCommandResult(false, $"'{preference}' is not an artwork source."));
        }

        ArtworkAsset[] types;
        if (asset.Length == 0)
        {
            types = GameLibraryArtwork.Assets;
        }
        else if (TryAsset(asset, out var one))
        {
            types = [one];
        }
        else
        {
            return Task.FromResult(new SteamUiCommandResult(false, $"'{asset}' is not an artwork type."));
        }

        lock (_gate)
        {
            if (_phase == "applying")
            {
                return Task.FromResult(Frozen);
            }

            var targets = _entries.Values.Where(entry => entry.Selected).ToList();
            if (targets.Count == 0)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Select the titles to fill first."));
            }

            foreach (var entry in targets)
            {
                foreach (var type in types)
                {
                    var options = Options(entry, type);
                    if (onlyEmpty && Slot(entry, type, options).Kind is "pick" or "default")
                    {
                        continue;
                    }

                    if (Preferred(options, wanted, false) is { } option)
                    {
                        entry.Picks[type] = Pick(type, option);
                    }
                }

                Remember(entry);
            }

            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ResetArtworkAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_phase == "applying")
            {
                return Task.FromResult(Frozen);
            }

            foreach (var entry in _entries.Values.Where(entry => entry.Selected && entry.Picks.Count > 0))
            {
                entry.Picks.Clear();
                Remember(entry);
                if (!entry.Selectable)
                {
                    entry.Selected = false;
                }
            }

            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ArtworkOptionsAsync(string id, string asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryAsset(asset, out var type))
        {
            return Task.FromResult(new SteamUiCommandResult(false, $"'{asset}' is not an artwork type."));
        }

        GameLibraryOptionsAnswer answer;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return Task.FromResult(Unlisted);
            }

            _artwork?.Prioritize(entry.ArtworkId);
            var options = Options(entry, type);
            var slot = Slot(entry, type, options);
            var status = _artwork?.StatusOf(entry.ArtworkId);
            answer = new GameLibraryOptionsAnswer(
                AssetName(type),
                Status(status?.Status),
                slot.Index,
                [
                    .. options.Select(option => new GameLibraryOptionAnswer(
                        option.Url, option.Thumb, option.Provider, option.Catalog, option.Width, option.Height))
                ]);
        }

        return Task.FromResult(new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
            answer, GameLibraryJsonContext.Default.GameLibraryOptionsAnswer)));
    }

    /// <inheritdoc />
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
                .. matches.Take(20).Select(match =>
                    new GameLibraryMatchAnswer(match.ProviderId, match.Id, match.Name, match.Exact))
            ]),
            GameLibraryJsonContext.Default.GameLibraryMatchesAnswer));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetMatchAsync(
        string id, string provider, string gameId, string name, CancellationToken cancellationToken)
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
                return Task.FromResult(Unlisted);
            }

            if (gameId.Length > 0 && ArtworkSearch.Find(provider) is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That artwork provider is not known."));
            }

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

            Remember(entry);
            _artwork?.Rematch(entry.ArtworkId, entry.Match);
            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Raised when the published state changed.</summary>
    internal event Action? Changed;

    /// <summary>Detects every source in the background, so the sidebar knows what is installed.</summary>
    internal void Start()
    {
        _ = Task.Run(() =>
        {
            DetectSources();
            lock (_gate)
            {
                Publish();
            }
        });
    }

    /// <summary>The state both surfaces render.</summary>
    internal GameLibraryState ReadState()
    {
        lock (_gate)
        {
            var launcher = _resolveLauncher();
            var entries = _entries.Values
                .OrderBy(entry => entry.Plan.Name, StringComparer.CurrentCulture)
                .Select(Project)
                .ToList();
            var disabled = _settings().DisabledSources;

            return new GameLibraryState(
                [
                    .. Sources().Select(source =>
                    {
                        var found = _availability.TryGetValue(source.Id, out var availability)
                            ? availability
                            : new SourceAvailability(false, "Checking…");
                        return new GameLibrarySource(
                            source.Id,
                            source.DisplayName,
                            source.Id.StartsWith("folder:", StringComparison.Ordinal) ? "folder" : "launcher",
                            found.Installed,
                            !disabled.Contains(source.Id, StringComparer.OrdinalIgnoreCase),
                            found.Detail,
                            _counts.TryGetValue(source.Id, out var count) ? count : -1);
                    })
                ],
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
                launcher is null && _entries.Values.Any(entry => entry.Game.Packaged)
                    ? "The packaged-game launcher is missing from this install, so Xbox titles cannot be imported."
                    : null,
                _phase is "scanning" or "applying",
                _notice,
                _error,
                _settings().ArtworkPreference.ToString(),
                _revision);
        }
    }

    /// <summary>The sources a scan can read now: the launchers, then the configured folders.</summary>
    private IReadOnlyList<ILibrarySource> Sources()
    {
        if (_folderSource is null)
        {
            return _launchers;
        }

        return [.. _launchers, .. _settings().ShortcutFolders.Select(_folderSource)];
    }

    /// <summary>Runs every source's detection, outside the lock: each one reads the registry or disk.</summary>
    private void DetectSources()
    {
        List<(string Id, SourceAvailability Found)> results = [];
        foreach (var source in Sources())
        {
            SourceAvailability found;
            try
            {
                found = source.Detect();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Game Library source {source.Id} could not be detected: {ex.Message}");
                found = new SourceAvailability(false, "Could not be checked");
            }

            results.Add((source.Id, found));
        }

        lock (_gate)
        {
            foreach (var (id, found) in results)
            {
                _availability[id] = found;
            }
        }
    }

    private void StartScan()
    {
        var generation = Begin("scanning");
        _ = RunAsync(token => ScanCoreAsync(generation, token), generation);
    }

    private async Task ScanCoreAsync(long generation, CancellationToken cancellationToken)
    {
        DetectSources();
        var disabled = _settings().DisabledSources;
        List<DiscoveredGame> discovered = [];
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Sources())
        {
            bool installed;
            lock (_gate)
            {
                installed = _availability.TryGetValue(source.Id, out var found) && found.Installed;
            }

            if (!installed || disabled.Contains(source.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var games = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                discovered.AddRange(games);
                counts[source.Id] = games.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // One launcher's broken data does not hide every other launcher's games. Its records
                // are treated as unscanned, so nothing of it is offered for removal either.
                Log.Warn($"Game Library source {source.Id} could not be read: {ex.Message}");
                Note(generation, $"{source.DisplayName} could not be read: {ex.Message}");
            }
        }

        var existing = await _readLibrary(cancellationToken).ConfigureAwait(false);
        var launcher = _resolveLauncher() ?? string.Empty;
        var recorded = _store.Entries();
        var choices = _store.Choices();
        var plan = ImportPlan.Build(
            discovered, recorded, existing, launcher, _defaultMode(), _includeUnroutable(), counts.ContainsKey);

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _counts.Clear();
            foreach (var (id, count) in counts)
            {
                _counts[id] = count;
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
                    Route = entry.Route,
                    Acknowledged = entry.Mode is ImportMode.SteamIntegration
                                   && (record?.Acknowledged ?? false),
                    ArtworkApplied = record is { AppId: > 0 } ? record.ArtworkApplied : null
                };

                // The user's own decisions, laid over what the plan derived. A picked mode the plan
                // would now refuse - the title lost its validated route, or became multiplayer
                // without the risk having been accepted - is not honoured, and the plan's stands. A
                // picked route the source no longer offers is dropped the same way.
                var choice = choices.FirstOrDefault(saved =>
                    string.Equals(saved.Source, entry.Source, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(saved.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
                if (choice?.PickedMode() is { } picked && created.Game.Packaged
                                                       && Refusal(entry, picked, choice.Acknowledged) is null)
                {
                    created.Mode = picked;
                    created.Acknowledged = picked is ImportMode.SteamIntegration && choice.Acknowledged;
                }

                if (choice is { Route.Length: > 0 }
                    && created.Game.CommandRoutes.Any(route => route.Id == choice.Route))
                {
                    created.Route = choice.Route;
                }

                if (choice is not null && entry.Action is not ImportAction.Remove)
                {
                    foreach (var pick in choice.Artwork)
                    {
                        created.Picks[pick.Asset] = pick;
                    }

                    if (choice.MatchId.Length > 0)
                    {
                        created.Match =
                            new ArtworkGameMatch(choice.MatchProvider, choice.MatchId, choice.MatchName, true);
                    }
                }

                created.Excluded = choice?.Excluded == true && Excludable(entry);

                // Never pre-tick something the sources could not vouch for, a deletion, a title the
                // user said to leave out, or an add that may already have happened. An imported title
                // with artwork picked and not yet applied is ticked, because applying it is the point.
                created.Selected = created.Selectable
                                   && ((created.Action is ImportAction.Add && !entry.Unconfirmed
                                                                           && (game?.IsGame ?? false))
                                       || created.Action is ImportAction.Update);
                _entries[id] = created;
            }

            _phase = "review";
            _notice = plan.Count == 0
                ? counts.Count == 0 ? "No source is ticked and installed." : "No games were found."
                : _notice;
            _artwork?.Reset(
            [
                .. _entries.Values
                    .Where(entry => entry.Action is not ImportAction.Remove)
                    .OrderByDescending(entry => entry.Selected)
                    .Select(entry => new GameLibraryArtworkRequest(
                        entry.ArtworkId,
                        entry.Plan.Name,
                        entry.Game.Artwork,
                        string.Equals(entry.Game.SourceId, XboxSourceId, StringComparison.OrdinalIgnoreCase)
                            ? "Microsoft Store"
                            : entry.Game.SourceId,
                        entry.Match))
            ]);
            Publish();
        }
    }

    private async Task ApplyCoreAsync(
        long generation, IReadOnlyList<Entry> selected, CancellationToken cancellationToken)
    {
        _artworkMissing = false;
        _controllerFailures.Clear();
        _controllerUnmanaged.Clear();
        var writer = _writer();
        if (writer is null)
        {
            Fail(generation, "Steam is not reachable, so nothing was imported.");
            return;
        }

        var launcher = _resolveLauncher();
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
            var record = _store.Entries()
                .FirstOrDefault(saved => ImportPlan.Matches(saved, entry.Game.SourceId, entry.Game.Key));
            if (Revalidate(entry, existing, launcher ?? string.Empty, record) is not { } current)
            {
                Note(generation, $"{entry.Plan.Name} changed since the scan and was left alone.");
                continue;
            }

            // Whether an earlier run created this title's profile. Only such a profile may be removed
            // when its override is cleared; one the user made, or had before an adoption, is theirs.
            var ownsProfile = record?.OwnsProfile == true;
            var artworkOnly = entry.ArtworkOnly;

            ShortcutFields fields;
            if (artworkOnly && record is not null)
            {
                fields = new ShortcutFields(record.Target, string.Empty, record.LaunchOptions);
            }
            else if (entry.Game.Packaged)
            {
                if (launcher is null && current.Action is not ImportAction.Remove)
                {
                    Fail(generation, "The packaged-game launcher is missing from this install.");
                    return;
                }

                fields = PackagedLauncherShortcut.Compose(
                    launcher ?? string.Empty, entry.Game.Key, entry.Mode,
                    entry.Game.Multiplayer is MultiplayerVerdict.Multiplayer, entry.Acknowledged);
            }
            else
            {
                var route = entry.Game.CommandRoutes.FirstOrDefault(candidate => candidate.Id == entry.Route)
                            ?? entry.Game.CommandRoutes.FirstOrDefault();
                if (route is { Follows: true } && launcher is null && current.Action is not ImportAction.Remove)
                {
                    Fail(generation, "WSGM.PackagedLaunch is missing from this install.");
                    return;
                }

                fields = route is null
                    ? new ShortcutFields(string.Empty, string.Empty, string.Empty)
                    : CommandShortcut.Compose(route, launcher ?? string.Empty);
            }

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
                _ when artworkOnly => new ShortcutWriteResult(current.AppId, true, null),
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
                    // Not cancellable: the shortcut is gone, and stopping before the record is
                    // dropped would leave a removal the next scan offers again.
                    await ReleaseControllerTargetAsync(current.AppId, ownsProfile, CancellationToken.None)
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

            var saved = artworkOnly && record is not null
                ? record
                : new ImportedEntry
                {
                    Source = entry.Game.SourceId,
                    Key = entry.Game.Key,
                    AppId = result.AppId,
                    Name = entry.Plan.Name,
                    Target = fields.Target,
                    LaunchOptions = fields.LaunchOptions,
                    Mode = entry.Mode.ToString(),
                    Route = entry.Game.Packaged ? string.Empty : entry.Route,
                    Acknowledged = entry.Acknowledged,
                    ImportedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ConfirmedUtc = result.Confirmed
                        ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                        : string.Empty,
                    ArtworkApplied = record?.ArtworkApplied ?? 0,
                    OwnsProfile = ownsProfile
                };
            _store.Save(saved);

            if (!result.Confirmed)
            {
                // Recorded as unconfirmed and stopped. The entry may well exist, so this is not
                // retried; the next scan adopts whatever actually survived.
                _store.ForgetChoice(entry.Game.SourceId, entry.Game.Key);
                Fail(generation,
                    $"{entry.Plan.Name}: {result.Error} The run stopped here; {applied} entry/entries "
                    + "were applied and nothing was retried.");
                return;
            }

            // Everything past the confirmed shortcut is decoration or policy the user can redo by
            // hand, so a failure here is noted and the run carries on. Losing the whole import over
            // a capsule that would not download is not a trade worth making.
            IReadOnlyList<DiscoveredArtwork> images;
            lock (_gate)
            {
                images = Images(entry, current.Action is ImportAction.Add);
            }

            var (artwork, ownsAfter) = await FinishEntryAsync(generation, entry, result.AppId, images,
                    ownsProfile, artworkOnly, cancellationToken)
                .ConfigureAwait(false);
            saved.ArtworkApplied = artwork;
            saved.OwnsProfile = ownsAfter;
            _store.Save(saved);

            // The record now says what Steam has, so a choice for this title would only be a second,
            // stale answer to the same question.
            _store.ForgetChoice(entry.Game.SourceId, entry.Game.Key);

            // Deselected once done. A run is capped, and a finished title left selected would be
            // taken first again by the next apply, so the titles past the cap would never be reached.
            lock (_gate)
            {
                entry.AppliedAppId = result.AppId;
                entry.ArtworkApplied = artwork;
                entry.Picks.Clear();
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
                          ? " Some images could not be applied; pick others for those titles and save again."
                          : string.Empty);

            // A controller-only title with no override has no working controller route, and nothing
            // on a rescan would show it: its record and shortcut say controller-only. So it is an
            // error the user sees, not a note the completion message replaces.
            List<string> problems = [];
            if (_controllerFailures.Count > 0)
            {
                problems.Add($"The controller override could not be written for "
                             + $"{string.Join(", ", _controllerFailures)}. Set Xbox 360 on its per-game "
                             + "profile in Quick Access, or apply it again.");
            }

            if (_controllerUnmanaged.Count > 0)
            {
                problems.Add($"{string.Join(", ", _controllerUnmanaged)} launch controller only, but "
                             + "controller management is off, so nothing will switch the controller. Turn on "
                             + "Device Integration and controller management in Settings.");
            }

            _error = problems.Count == 0 ? null : string.Join(" ", problems);
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
                return Task.FromResult(Unlisted);
            }

            if (excluded && !Excludable(entry.Plan))
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "Only a title that is not imported yet can be left out. Remove an imported one instead."));
            }

            entry.Excluded = excluded;
            entry.Selected = false;
            Remember(entry);
            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Changes one entry's artwork under the lock, then stores and publishes it.</summary>
    private Task<SteamUiCommandResult> EditArtwork(
        string id, string asset, Func<Entry, ArtworkAsset, string?> edit)
    {
        if (!TryAsset(asset, out var type))
        {
            return Task.FromResult(new SteamUiCommandResult(false, $"'{asset}' is not an artwork type."));
        }

        lock (_gate)
        {
            if (_phase == "applying")
            {
                return Task.FromResult(Frozen);
            }

            if (!_entries.TryGetValue(id, out var entry))
            {
                return Task.FromResult(Unlisted);
            }

            if (entry.Action is ImportAction.Remove or ImportAction.Conflict || entry.Excluded)
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "Artwork can only be chosen for a title that is being imported or is already in Steam."));
            }

            if (edit(entry, type) is { } refusal)
            {
                return Task.FromResult(new SteamUiCommandResult(false, refusal));
            }

            // An imported title whose artwork was just changed is the thing to save next.
            if (entry.ArtworkOnly)
            {
                entry.Selected = true;
            }

            Remember(entry);
            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Stores what the user decided about one entry, so the next scan keeps it.</summary>
    /// <param name="entry">The entry.</param>
    private void Remember(Entry entry)
    {
        var picked = entry.Game.Packaged && entry.Mode != entry.Plan.Mode ? entry.Mode : (ImportMode?)null;
        _store.SaveChoice(new ImportChoice
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
        });
    }

    /// <summary>Takes one source's titles out of the review.</summary>
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

    /// <summary>Re-checks one selected entry against the library as it is right now.</summary>
    /// <param name="entry">The entry the user selected.</param>
    /// <param name="existing">The shortcuts Steam holds as of a moment ago.</param>
    /// <param name="launcher">The launcher a generated packaged entry points at.</param>
    /// <param name="record">What WSGM wrote for the title before, if anything.</param>
    /// <returns>What to do now, or null when Steam has moved and it should be left alone.</returns>
    /// <remarks>
    ///     The user's chosen action is kept; only its premise is rechecked. An Add whose entry has
    ///     appeared in the meantime becomes an Update rather than a duplicate, and anything that
    ///     names a live entry has to still find it, and still own it.
    /// </remarks>
    private static ImportPlanEntry? Revalidate(
        Entry entry, IReadOnlyList<ExistingShortcut> existing, string launcher, ImportedEntry? record)
    {
        var plan = entry.Plan;
        if (entry.Action is ImportAction.Add)
        {
            return existing.FirstOrDefault(shortcut => ImportPlan.Owns(shortcut, entry.Game, launcher))
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

        // A removal's title is gone, so only the record can say whether the entry is still ours.
        if (entry.Action is ImportAction.Remove || entry.ArtworkOnly)
        {
            return record is not null && ImportPlan.OwnsRecorded(live, record, launcher)
                ? plan with { Action = entry.Action }
                : null;
        }

        return ImportPlan.Owns(live, entry.Game, launcher)
            ? plan with { Action = entry.Action }
            : null;
    }

    /// <summary>The images an apply writes for one entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="created">Whether this run created its shortcut, so the defaults apply too.</param>
    /// <returns>One image per artwork type at most.</returns>
    /// <remarks>
    ///     A shortcut this run created gets every slot that shows an image, the defaults included. An
    ///     entry Steam already had keeps the artwork it has, apart from what the user picked here.
    /// </remarks>
    private IReadOnlyList<DiscoveredArtwork> Images(Entry entry, bool created)
    {
        List<DiscoveredArtwork> images = [];
        foreach (var type in GameLibraryArtwork.Assets)
        {
            var options = Options(entry, type);
            if (entry.Picks.TryGetValue(type, out var pick))
            {
                if (pick.Url.Length > 0)
                {
                    images.Add(new DiscoveredArtwork(type, pick.Url));
                }

                continue;
            }

            if (created && Preferred(options, _settings().ArtworkPreference, true) is { } option)
            {
                images.Add(new DiscoveredArtwork(type, option.Url));
            }
        }

        return images;
    }

    /// <summary>Applies the artwork and the controller override for one written entry.</summary>
    /// <param name="generation">The run this belongs to.</param>
    /// <param name="entry">The entry that was written.</param>
    /// <param name="appId">Its confirmed app id.</param>
    /// <param name="images">The images to apply.</param>
    /// <param name="ownsProfile">Whether an earlier run created this title's profile.</param>
    /// <param name="artworkOnly">Whether only the artwork changed, so the override is left alone.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    ///     How many images the entry now has applied, and whether an import now owns the title's
    ///     profile.
    /// </returns>
    private async Task<(int Artwork, bool OwnsProfile)> FinishEntryAsync(
        long generation, Entry entry, uint appId, IReadOnlyList<DiscoveredArtwork> images, bool ownsProfile,
        bool artworkOnly, CancellationToken cancellationToken)
    {
        // Only the packaged route has an input mode. A command title is launched by Steam like any
        // other non-Steam game and gets no override.
        if (entry.Game.Packaged && !artworkOnly)
        {
            if (entry.Mode is ImportMode.ControllerOnly && !_controllerManaged())
            {
                // The override is still written, so it takes effect as soon as management is on, but
                // until then nothing switches the controller and the title has no controller route.
                lock (_gate)
                {
                    _controllerUnmanaged.Add(entry.Plan.Name);
                }
            }

            try
            {
                if (_setControllerTarget is not null)
                {
                    ownsProfile |= await _setControllerTarget(
                            Identity(appId), entry.Plan.Name,
                            entry.Mode is ImportMode.ControllerOnly ? ManagedControllerTarget.Xbox360 : null,
                            ownsProfile, cancellationToken)
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
        }

        var before = entry.ArtworkApplied ?? 0;
        if (images.Count == 0 || _applyArtwork is null)
        {
            return (before, ownsProfile);
        }

        try
        {
            var applied = await _applyArtwork(appId, images, cancellationToken).ConfigureAwait(false);
            _artworkMissing |= applied < images.Count;
            return (Math.Max(before, applied), ownsProfile);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _artworkMissing = true;
            Note(generation, $"{entry.Plan.Name} was saved, but its artwork did not apply.");
            return (before, ownsProfile);
        }
    }

    /// <summary>Clears a controller override left by an earlier import.</summary>
    /// <param name="appId">The app id whose override to release.</param>
    /// <param name="ownsProfile">Whether an import created the profile, so it may go once empty.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task ReleaseControllerTargetAsync(uint appId, bool ownsProfile, CancellationToken cancellationToken)
    {
        if (_setControllerTarget is null)
        {
            return;
        }

        try
        {
            await _setControllerTarget(Identity(appId), string.Empty, null, ownsProfile, cancellationToken)
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

    /// <summary>A shortcuts folder's source id: stable for the path, never reused for another.</summary>
    private static string FolderId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return "folder:" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
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
            : _artwork.Options(entry.ArtworkId, type);
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
        Entry entry, ArtworkAsset type, IReadOnlyList<GameLibraryArtworkOption> options)
    {
        var name = AssetName(type);
        if (entry.Picks.TryGetValue(type, out var pick))
        {
            if (pick.Url.Length == 0)
            {
                return new GameLibraryArtworkSlot(name, "none", string.Empty, string.Empty, 0, options.Count);
            }

            var index = IndexOf(options, pick.Url);
            return new GameLibraryArtworkSlot(name, "pick", pick.Thumb, pick.Provider, index + 1, options.Count);
        }

        if (entry.AppId > 0)
        {
            return new GameLibraryArtworkSlot(name, "keep", string.Empty, string.Empty, 0, options.Count);
        }

        if (Preferred(options, _settings().ArtworkPreference, true) is { } option)
        {
            return new GameLibraryArtworkSlot(name, "default", option.Thumb, option.Provider,
                IndexOf(options, option.Url) + 1, options.Count);
        }

        var status = _artwork?.StatusOf(entry.ArtworkId).Status;
        return new GameLibraryArtworkSlot(name,
            status is GameLibraryArtworkStatus.Pending or GameLibraryArtworkStatus.Loading ? "loading" : "none",
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

    /// <summary>The name the surfaces use for an artwork type.</summary>
    private static string AssetName(ArtworkAsset type)
    {
        return type switch
        {
            ArtworkAsset.Grid => "grid",
            ArtworkAsset.Wide => "wide",
            ArtworkAsset.Hero => "hero",
            ArtworkAsset.Logo => "logo",
            _ => "icon"
        };
    }

    private static bool TryAsset(string name, out ArtworkAsset type)
    {
        foreach (var candidate in GameLibraryArtwork.Assets)
        {
            if (string.Equals(AssetName(candidate), name, StringComparison.Ordinal))
            {
                type = candidate;
                return true;
            }
        }

        type = ArtworkAsset.Grid;
        return false;
    }

    private static string Status(GameLibraryArtworkStatus? status)
    {
        return status switch
        {
            GameLibraryArtworkStatus.Loading => "loading",
            GameLibraryArtworkStatus.Ready => "ready",
            GameLibraryArtworkStatus.Failed => "failed",
            _ => "pending"
        };
    }

    private void OnArtworkChanged()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                Publish();
            }
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
        return Sources().FirstOrDefault(source =>
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
        var status = _artwork?.StatusOf(entry.ArtworkId);
        var route = entry.Game.CommandRoutes.FirstOrDefault(candidate => candidate.Id == entry.Route);
        return new GameLibraryEntry(
            entry.Id,
            entry.Plan.Name,
            SourceName(entry.Plan.Source),
            entry.Plan.Source,
            entry.Game.Key,
            entry.Game.InstallPath,
            route?.Label ?? entry.Game.Launch.Label,
            entry.Game.Launch.Validated,
            route?.Evidence ?? entry.Game.Launch.Evidence,
            entry.Game.Multiplayer.ToString(),
            entry.Game.MultiplayerEvidence,
            entry.Mode.ToString(),
            entry.Plan.CanUseSteamIntegration,
            entry.Plan.RequiresAcknowledgement,
            entry.Acknowledged,
            entry.ArtworkOnly ? "Artwork" : entry.Action.ToString(),
            entry.Reason,
            entry.Selected,
            entry.Selectable,
            entry.Excluded,
            entry.Game.Notes,
            entry.AppId,
            entry.Game.Artwork.Count,
            entry.ArtworkApplied,
            [
                .. entry.Game.CommandRoutes.Select(candidate =>
                    new GameLibraryRoute(candidate.Id, candidate.Label, candidate.Evidence))
            ],
            entry.Route,
            entry.Action is ImportAction.Remove
                ? []
                : [.. GameLibraryArtwork.Assets.Select(type => Slot(entry, type, Options(entry, type)))],
            Status(status?.Status),
            entry.Match?.Name ?? status?.MatchName ?? string.Empty,
            entry.Match is not null);
    }

    private sealed class Entry(string id, ImportPlanEntry plan, DiscoveredGame game)
    {
        internal string Id { get; } = id;
        internal ImportPlanEntry Plan { get; } = plan;
        internal DiscoveredGame Game { get; } = game;
        internal ImportMode Mode { get; set; }

        /// <summary>The command route it would launch with, or empty for a packaged title.</summary>
        internal string Route { get; set; } = string.Empty;

        internal bool Acknowledged { get; set; }
        internal bool Selected { get; set; }

        /// <summary>Whether the user said not to import this title.</summary>
        internal bool Excluded { get; set; }

        /// <summary>The id this run's write was confirmed with, or zero.</summary>
        internal uint AppliedAppId { get; set; }

        /// <summary>How many images were applied, or null before an import.</summary>
        internal int? ArtworkApplied { get; set; }

        /// <summary>The artwork the user picked, by type. A pick with no URL clears that type.</summary>
        internal Dictionary<ArtworkAsset, ArtworkPick> Picks { get; } = [];

        /// <summary>The game the user matched the title to at the artwork providers, or null.</summary>
        internal ArtworkGameMatch? Match { get; set; }

        /// <summary>Whether saving this entry writes a shortcut that runs WSGM.PackagedLaunch.</summary>
        internal bool NeedsLauncher =>
            Game.Packaged
            || Game.CommandRoutes.FirstOrDefault(candidate => candidate.Id == Route) is { Follows: true };

        /// <summary>The title's identity in the artwork stage: its source and key.</summary>
        internal string ArtworkId => Game.SourceId + "\u001f" + Game.Key;

        /// <summary>The entry's Steam app id, once it has one.</summary>
        internal uint AppId => AppliedAppId > 0 ? AppliedAppId : Plan.AppId;

        /// <summary>Whether the user has moved this entry off the mode or route Steam's entry launches with.</summary>
        private bool Rerouted => (Mode != Plan.Mode || Route != Plan.Route) && Plan.AppId > 0;

        /// <summary>Whether an entry Steam already has has artwork picked that is not applied yet.</summary>
        private bool ArtworkPending => Plan.AppId > 0 && Picks.Values.Any(pick => pick.Url.Length > 0);

        /// <summary>Whether saving this entry means applying artwork and nothing else.</summary>
        internal bool ArtworkOnly => Plan.Action is ImportAction.Skip && !Rerouted && ArtworkPending;

        /// <summary>What applying this entry would now do.</summary>
        /// <remarks>
        ///     An entry Steam already has is a Skip, or an Adopt, until the user changes its route or its
        ///     artwork; then it really does need saving. Without this the page would show the new mode
        ///     and never apply it - and an Adopt would record a mode its shortcut does not launch with,
        ///     because adopting writes nothing.
        /// </remarks>
        internal ImportAction Action =>
            Plan.Action switch
            {
                ImportAction.Skip when Rerouted || ArtworkPending => ImportAction.Update,
                ImportAction.Adopt when Rerouted => ImportAction.Update,
                _ => Plan.Action
            };

        /// <summary>Whether the user may tick this entry.</summary>
        internal bool Selectable =>
            !Excluded && (Plan.Selectable || (Plan.Action is ImportAction.Skip && (Rerouted || ArtworkPending)));

        /// <summary>Why it cannot be ticked, when it cannot.</summary>
        internal string Reason => Excluded ? "You chose not to import this." : Plan.Reason;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The boot movies' one owner: the repository's list, WSGM's library, which movie Big Picture
///     starts with, and the override file that makes it so. The Animations page in Steam, the Quick
///     Access section and the overlay's Animations view all read its state and call its methods.
/// </summary>
/// <remarks>
///     <para>
///         Content and choice stay apart, as Animation Changer keeps them: the library holds the
///         movies, the configuration names one, and applying copies it to the file the client asks
///         for (<see cref="AnimationOverrides" />). A shuffle picks anew from the library and an empty
///         choice is Steam's own movie.
///     </para>
///     <para>
///         Steam lets its own Startup Movie choice (Settings &gt; Customization) replace the override,
///         so while one of WSGM's movies is chosen that choice is set aside once Big Picture is ready,
///         at WSGM's start, at each Steam start and with each choice, and kept in the configuration. A
///         return to Steam's own gives it back unless the user chose anew in Steam since.
///     </para>
///     <para>
///         The client caches its override lookup for the life of the document, so an override written
///         while Steam runs shows at the next Steam start; the state says so until then. Only the boot
///         movie is offered: nothing on Windows drives Steam's suspend flow, so its suspend movies
///         never play (#116, #21).
///     </para>
/// </remarks>
internal sealed class AnimationService : ISteamAnimationsBackend, IDisposable, IChangeSource, IExtensionsTabSection
{
    /// <summary>The section's item id.</summary>
    internal const string ExtensionsId = "wsgm.animations";

    /// <summary>The action that opens the Browse tab.</summary>
    internal const string ExtensionsBrowseId = "wsgm.animations.browse";

    /// <summary>The action that opens the Library tab.</summary>
    internal const string ExtensionsManageId = "wsgm.animations.manage";

    /// <summary>The action that picks the boot movie anew.</summary>
    internal const string ExtensionsShuffleId = "wsgm.animations.shuffle";

    /// <summary>What the choice that puts Big Picture back on Steam's own movie is called.</summary>
    internal const string StockLabel = "Steam's own";

    private const string RestartNote = "Restart Steam to see it.";

    /// <summary>How many cards the Browse tab adds at a time.</summary>
    internal const int BrowsePage = 48;

    private readonly Lock _applyGate = new();
    private readonly AnimationRepoClient _client;
    private readonly AnimationLibrary _library;
    private readonly Random _random;
    private readonly Func<AnimationsConfig> _readConfig;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SteamStartupMovieAccess? _steamChoice;
    private readonly Func<string?> _steamDirectory;
    private readonly Func<bool> _steamRunning;
    private readonly Lock _sync = new();
    private readonly Action<Action<AnimationsConfig>> _writeConfig;
    private string _activeTab = "browse";
    private IReadOnlyList<SteamAnimationsItem>? _browseItems;
    private IReadOnlyList<AnimationListing>? _browseOrder;
    private string _browseSearch = string.Empty;
    private int _browseShown = BrowsePage;
    private string _browseSort = SteamAnimationsSurface.Sorts[0].Id;
    private bool _busy;
    private AnimationsConfig _config;
    private string? _detailId;
    private bool _disposed;
    private string? _error;
    private string? _notice;
    private IReadOnlyList<AnimationListing>? _repo;
    private string? _repoError;
    private bool _repoLoading;
    private int _repoSequence;
    private bool _restartNeeded;
    private long _revision;
    private CancellationTokenSource? _steamChoiceWork;

    /// <summary>Creates the service.</summary>
    /// <param name="library">The movies.</param>
    /// <param name="client">The repository.</param>
    /// <param name="readConfig">The current animations configuration.</param>
    /// <param name="writeConfig">Saves a change to the animations configuration.</param>
    /// <param name="steamDirectory">Steam's install directory, or null when Steam is not installed.</param>
    /// <param name="random">The shuffle's source of choice, or null for a fresh one.</param>
    /// <param name="steamRunning">
    ///     Whether Steam runs now, so a change it has already read is announced as needing a restart;
    ///     null counts Steam as running.
    /// </param>
    /// <param name="steamChoice">
    ///     Reaches Steam's own startup movie choice, which replaces the override while it is set; null
    ///     leaves it alone.
    /// </param>
    internal AnimationService(
        AnimationLibrary library,
        AnimationRepoClient client,
        Func<AnimationsConfig> readConfig,
        Action<Action<AnimationsConfig>> writeConfig,
        Func<string?> steamDirectory,
        Random? random = null,
        Func<bool>? steamRunning = null,
        SteamStartupMovieAccess? steamChoice = null)
    {
        _library = library;
        _client = client;
        _readConfig = readConfig;
        _writeConfig = writeConfig;
        _steamDirectory = steamDirectory;
        _random = random ?? new Random();
        _steamRunning = steamRunning ?? (() => true);
        _steamChoice = steamChoice;
        _config = readConfig();
    }

    /// <summary>The revision of the state <see cref="ReadState" /> answers.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    internal IReadOnlyList<AnimationListing> Catalog
    {
        get
        {
            lock (_sync)
            {
                return _repo ?? [];
            }
        }
    }

    /// <summary>Raised on every change the page, the section or the overlay should draw.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // A Steam choice still waiting is linked to the shutdown and disposes its own source.
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    /// <inheritdoc />
    public string SectionId => ExtensionsId;

    /// <summary>The Quick Access section: the boot choice, the shuffle, and the way to the page.</summary>
    /// <returns>The item.</returns>
    public SteamExtensionsTabItem ReadExtensionsItem()
    {
        lock (_sync)
        {
            var entries = _library.Entries;
            var detail = entries.Count == 0 ? "No movies yet" : $"{entries.Count} in the library";
            if (_restartNeeded)
            {
                detail += " · Restart Steam to see the change";
            }

            return new SteamExtensionsTabItem(
                ExtensionsId,
                "Boot animation",
                string.Empty,
                _busy ? "Working…" : "Ready",
                detail,
                [
                    new SteamExtensionsTabAction(ExtensionsBrowseId, "Browse movies…"),
                    new SteamExtensionsTabAction(ExtensionsManageId, "Library…"),
                    new SteamExtensionsTabAction(ExtensionsShuffleId, "Shuffle")
                ],
                [
                    new SteamExtensionsTabSetting("boot", "Boot movie", "text", TextValue: SelectedLocked(),
                        Choices: [string.Empty, .. entries.Select(entry => entry.Id)],
                        ChoiceLabels: [StockLabel, .. entries.Select(entry => entry.Name)]),
                    new SteamExtensionsTabSetting("shuffleOnStart", "Shuffle on start", "boolean",
                        _config.ShuffleOnStart,
                        Description: "Picks the boot movie anew from the library each time WSGM starts.")
                ],
                Revision);
        }
    }

    /// <summary>Answers one of the section's actions.</summary>
    /// <param name="id">The action id.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result, carrying the page's route for the two that open it.</returns>
    public async Task<SteamUiCommandResult> ActivateExtensionAsync(string id, CancellationToken cancellationToken)
    {
        switch (id)
        {
            case ExtensionsBrowseId:
                await SetTabAsync("browse", cancellationToken).ConfigureAwait(false);
                return SteamUiCommandResult.Route(SteamAnimationsSurface.Route);
            case ExtensionsManageId:
                await SetTabAsync("library", cancellationToken).ConfigureAwait(false);
                return SteamUiCommandResult.Route(SteamAnimationsSurface.Route);
            case ExtensionsShuffleId:
                return await ShuffleAsync(cancellationToken).ConfigureAwait(false);
            default:
                return new SteamUiCommandResult(false, "That entry is no longer available.");
        }
    }

    /// <summary>Applies one of the section's settings: the boot movie by library id, or the shuffle.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="value">Its new value.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result.</returns>
    public Task<SteamUiCommandResult> ConfigureExtensionAsync(
        string key, JsonElement value, CancellationToken cancellationToken)
    {
        return (key, value.ValueKind) switch
        {
            ("boot", JsonValueKind.String) => SelectAsync(value.GetString() ?? string.Empty, cancellationToken),
            ("shuffleOnStart", JsonValueKind.True or JsonValueKind.False) =>
                SetShuffleOnStartAsync(value.GetBoolean(), cancellationToken),
            _ => Task.FromResult(new SteamUiCommandResult(false, "That setting is no longer available."))
        };
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _activeTab = tab;
            _detailId = null;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> BrowseAsync(string sort, string search, CancellationToken cancellationToken)
    {
        int? fetch = null;
        lock (_sync)
        {
            sort = SteamAnimationsSurface.Sorts.Any(candidate => candidate.Id == sort)
                ? sort
                : SteamAnimationsSurface.Sorts[0].Id;
            search = search.Trim();
            var changed = sort != _browseSort || search != _browseSearch;
            _browseSort = sort;
            _browseSearch = search;
            if (_repo is null && !_repoLoading)
            {
                _repoLoading = true;
                _repoError = null;
                fetch = ++_repoSequence;
            }
            else if (!changed)
            {
                // Asking again for what is shown changes nothing, so a view that asks whenever the
                // list is empty cannot redraw itself in a loop.
                return Task.FromResult(SteamUiCommandResult.Applied);
            }

            ResetBrowseLocked();
        }

        Publish();
        if (fetch is { } sequence)
        {
            _ = Task.Run(() => FetchRepoAsync(sequence, _shutdown.Token));
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> BrowseMoreAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_browseOrder is null || _browseShown >= _browseOrder.Count)
            {
                return Task.FromResult(SteamUiCommandResult.Applied);
            }

            _browseShown += BrowsePage;
            _browseItems = null;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        int sequence;
        lock (_sync)
        {
            if (_repoLoading)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "The repository is still answering."));
            }

            _repoLoading = true;
            _repoError = null;
            sequence = ++_repoSequence;
        }

        Publish();
        _ = Task.Run(() => FetchRepoAsync(sequence, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (FindListingLocked(id) is null && _library.Find(id) is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That movie is not listed."));
            }

            _detailId = id;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _detailId = null;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        AnimationListing? listing;
        lock (_sync)
        {
            listing = FindListingLocked(id) ?? _library.Find(id)?.Listing;
        }

        if (listing is null)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That movie is not listed."));
        }

        return StartWorkAsync(async token =>
        {
            // The movie goes to a staging file beside its place outside the lock; only the move into
            // the library holds it.
            var staged = _library.StagingPath(listing.Id);
            await _client.DownloadAsync(listing, staged, token).ConfigureAwait(false);
            string? error;
            lock (_sync)
            {
                error = _library.Adopt(listing, staged);
                _browseItems = null;
            }

            if (error is not null)
            {
                throw new AnimationRepoException($"The movie could not be kept: {error}");
            }

            return $"Downloaded {listing.Name}. Choose it under Library to start Big Picture with it.";
        });
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        bool saved;
        string removed;
        lock (_sync)
        {
            var entry = _library.Find(id);
            if (entry is null)
            {
                return new SteamUiCommandResult(false, "That movie is not in the library.");
            }

            var error = _library.Remove(id);
            if (error is not null)
            {
                return RefuseLocked(error);
            }

            _browseItems = null;
            removed = $"Removed {entry.Name}.";
            if (_config.Boot != id)
            {
                SetNoticeLocked(removed);
                saved = false;
            }
            else
            {
                saved = ChangeConfigLocked(config => config.Boot = string.Empty);
            }
        }

        if (saved)
        {
            await ApplyChoiceAsync(removed).ConfigureAwait(false);
            KickSteamChoice();
        }

        Publish();
        return SteamUiCommandResult.Applied;
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SelectAsync(string id, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (id.Length > 0 && _library.Find(id) is null)
            {
                return RefuseLocked("That movie is not in the library.");
            }

            if (_config.Boot == id)
            {
                return SteamUiCommandResult.Applied;
            }
        }

        return await SetBootAsync(id).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ShuffleAsync(CancellationToken cancellationToken)
    {
        string picked;
        lock (_sync)
        {
            if (_library.Entries.Count == 0)
            {
                return RefuseLocked("The library is empty; download a movie first.");
            }

            picked = AnimationShuffle.Pick(_library.Entries, _random);
        }

        return await SetBootAsync(picked).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetShuffleOnStartAsync(bool shuffle, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ChangeConfigLocked(config => config.ShuffleOnStart = shuffle);
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetBootVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ChangeConfigLocked(config => config.BootVolume = volume);
        }

        await ApplyChoiceAsync(null).ConfigureAwait(false);
        Publish();
        return SteamUiCommandResult.Applied;
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> AddFileAsync(string path, CancellationToken cancellationToken)
    {
        return StartWorkAsync(_ =>
        {
            // The copy runs outside the lock, so the pages and the overlay keep reading the state
            // while a large movie is copied; only the library's reread holds it.
            var (id, error) = _library.Import(path);
            if (error is not null)
            {
                throw new AnimationRepoException(error);
            }

            string name;
            lock (_sync)
            {
                _library.Load();
                _browseItems = null;
                name = _library.Find(id!)?.Name ?? id!;
            }

            return Task.FromResult($"Added {name}. Choose it under Library to start Big Picture with it.");
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _notice = null;
            _error = null;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Reads the library, shuffles when asked to, and writes the override before Steam starts.</summary>
    internal void Start()
    {
        lock (_sync)
        {
            _library.Load();
            _browseItems = null;
            if (_config.ShuffleOnStart)
            {
                var picked = AnimationShuffle.Pick(_library.Entries, _random);
                ChangeConfigLocked(config => config.Boot = picked);
            }
        }

        var report = ApplyChoice();
        lock (_sync)
        {
            _error = report.Error ?? _error;
        }

        Log.Info($"Animations: {_library.Entries.Count} in the library, boot '{_config.Boot}'"
                 + (report.Changed ? ", override written" : "")
                 + (report.Error is { } error ? $", {error}" : "") + ".");
        Publish();
        KickSteamChoice();
    }

    /// <summary>
    ///     Steam started again, so it has read the override as it stands; its own startup movie choice
    ///     is brought in step once Big Picture is ready.
    /// </summary>
    internal void SteamStarted()
    {
        bool changed;
        lock (_sync)
        {
            changed = _restartNeeded;
            _restartNeeded = false;
        }

        if (changed)
        {
            Publish();
        }

        KickSteamChoice();
    }

    /// <summary>Takes the reloaded configuration.</summary>
    internal void ConfigurationChanged()
    {
        bool apply;
        lock (_sync)
        {
            var previous = _config;
            _config = _readConfig();
            apply = previous.Boot != _config.Boot || previous.BootVolume != _config.BootVolume;
            if (!apply && previous.ShuffleOnStart == _config.ShuffleOnStart)
            {
                return;
            }
        }

        if (!apply)
        {
            Publish();
            return;
        }

        _ = Task.Run(async () =>
        {
            await ApplyChoiceAsync(null).ConfigureAwait(false);
            Publish();
            KickSteamChoice();
        });
    }

    /// <summary>Everything the page and the overlay draw.</summary>
    /// <returns>The state.</returns>
    internal SteamAnimationsState ReadState()
    {
        lock (_sync)
        {
            var downloaded = _library.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            // The order is kept until the list, the sort or the search changes; only the page shown is
            // projected, since the repository lists thousands.
            _browseOrder ??= _repo is null ? [] : [.. Filtered(_repo)];
            _browseItems ??=
            [
                .. _browseOrder.Take(_browseShown)
                    .Select(listing => ProjectListing(listing, downloaded.Contains(listing.Id)))
            ];
            var steam = _steamDirectory();
            return new SteamAnimationsState(
                _activeTab,
                SelectedLocked(),
                StockLabel,
                [.. _library.Entries.Select(ProjectEntry)],
                new SteamAnimationsBrowse(
                    _browseSort,
                    SteamAnimationsSurface.Sorts,
                    _browseSearch,
                    _browseItems,
                    _browseOrder.Count,
                    _repo?.Count ?? 0,
                    _repoLoading,
                    _repoError),
                DetailLocked(downloaded),
                new SteamAnimationsSettings(
                    _config.ShuffleOnStart,
                    _config.BootVolume,
                    _library.Root,
                    steam is null ? null : AnimationOverrides.Directory(steam),
                    _restartNeeded),
                _busy,
                _notice,
                _error ?? _library.LoadError,
                Revision);
        }
    }

    private async Task FetchRepoAsync(int sequence, CancellationToken cancellationToken)
    {
        IReadOnlyList<AnimationListing>? list = null;
        string? error = null;
        try
        {
            list = await _client.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AnimationRepoException ex)
        {
            error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_sync)
        {
            if (sequence != _repoSequence)
            {
                return;
            }

            _repoLoading = false;
            _repoError = error;
            if (list is not null)
            {
                _repo = list;
                ResetBrowseLocked();
            }
        }

        Log.Change("animations.repository", error ?? $"{list!.Count} listed");
        Publish();
    }

    /// <summary>Starts the Browse tab over at its first page, for a new list, sort or search. Called under the lock.</summary>
    private void ResetBrowseLocked()
    {
        _browseOrder = null;
        _browseItems = null;
        _browseShown = BrowsePage;
    }

    private AnimationListing? FindListingLocked(string id)
    {
        return _repo?.FirstOrDefault(candidate => candidate.Id == id);
    }

    /// <summary>The choice when the library still holds it, else Steam's own. Called under the lock.</summary>
    private string SelectedLocked()
    {
        return _library.Find(_config.Boot) is null ? string.Empty : _config.Boot;
    }

    /// <summary>The opened movie, projected at each read so its library badge is current. Called under the lock.</summary>
    private SteamAnimationsItem? DetailLocked(HashSet<string> downloaded)
    {
        if (_detailId is not { } id)
        {
            return null;
        }

        if (FindListingLocked(id) is { } listing)
        {
            return ProjectListing(listing, downloaded.Contains(id));
        }

        return _library.Find(id) is { } entry ? ProjectEntry(entry) : null;
    }

    private IEnumerable<AnimationListing> Filtered(IReadOnlyList<AnimationListing> repo)
    {
        IEnumerable<AnimationListing> items = repo;
        if (_browseSearch.Length > 0)
        {
            items = items.Where(listing => listing.Name.Contains(_browseSearch, StringComparison.OrdinalIgnoreCase));
        }

        return _browseSort switch
        {
            "oldest" => items.OrderBy(listing => When(listing.Updated)),
            "name" => items.OrderBy(listing => listing.Name, StringComparer.OrdinalIgnoreCase),
            "popular" => items.OrderByDescending(listing => listing.Downloads),
            "liked" => items.OrderByDescending(listing => listing.Likes),
            _ => items.OrderByDescending(listing => When(listing.Updated))
        };
    }

    private static DateTimeOffset? When(string updated)
    {
        return DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var when)
            ? when
            : null;
    }

    internal AnimationBrowseSession CreateBrowserSession()
    {
        return new AnimationBrowseSession(this);
    }

    internal string? PreviewPath(string id)
    {
        lock (_sync)
        {
            return _library.Find(id)?.Path;
        }
    }

    internal static SteamAnimationsItem ProjectListing(AnimationListing listing, bool downloaded)
    {
        return new SteamAnimationsItem(
            listing.Id,
            listing.Name,
            listing.Author,
            listing.ThumbnailUrl.Length > 0 ? listing.ThumbnailUrl : null,
            listing.PreviewUrl.Length > 0 ? listing.PreviewUrl : null,
            listing.Description,
            listing.Likes,
            listing.Downloads,
            When(listing.Updated)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? listing.Updated,
            downloaded,
            false);
    }

    private static SteamAnimationsItem ProjectEntry(AnimationEntry entry)
    {
        return entry.Listing is { } listing
            ? ProjectListing(listing, true)
            : new SteamAnimationsItem(entry.Id, entry.Name, string.Empty, null, null, string.Empty, 0, 0,
                string.Empty, true, true);
    }

    /// <summary>Makes a library id, or empty for Steam's own, the boot movie and says so.</summary>
    private async Task<SteamUiCommandResult> SetBootAsync(string id)
    {
        bool saved;
        lock (_sync)
        {
            saved = ChangeConfigLocked(config => config.Boot = id);
        }

        await ApplyChoiceAsync(saved ? string.Empty : null).ConfigureAwait(false);
        Publish();
        KickSteamChoice();
        return SteamUiCommandResult.Applied;
    }

    /// <summary>
    ///     Brings Steam's own startup movie choice in step with WSGM's once Big Picture is ready: set
    ///     aside while one of WSGM's movies plays, given back once Steam's own plays again. A newer
    ///     request supersedes one still waiting.
    /// </summary>
    private void KickSteamChoice()
    {
        if (_steamChoice is not { } access)
        {
            return;
        }

        CancellationTokenSource work;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _steamChoiceWork?.Cancel();
            work = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _steamChoiceWork = work;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await access.WhenReady("Boot movie: Steam's startup movie choice",
                    token => ReconcileSteamChoiceAsync(access, token), work.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_steamChoiceWork, work))
                    {
                        _steamChoiceWork = null;
                    }
                }

                work.Dispose();
            }
        });
    }

    /// <summary>One attempt at <see cref="KickSteamChoice" />.</summary>
    /// <returns>False only when Steam could not be reached, so the attempt is made again.</returns>
    private async Task<bool> ReconcileSteamChoiceAsync(SteamStartupMovieAccess access, CancellationToken token)
    {
        bool plays;
        SteamStartupMovieSetAside? kept;
        lock (_sync)
        {
            plays = SelectedLocked().Length > 0;
            kept = _config.SteamSetAside;
        }

        if (!plays && kept is null)
        {
            return true;
        }

        var result = plays
            ? await access.SetAside(token).ConfigureAwait(false)
            : await access.Restore(new SteamStartupMovieChoice(kept!.MovieId, kept.LocalPath, kept.Shuffle), token)
                .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return false;
        }

        string outcome;
        lock (_sync)
        {
            if (!result.Accepted)
            {
                outcome = $"Steam's own Startup Movie choice could not be {(plays ? "set aside" : "given back")}: "
                          + result.Error;
                _error = outcome;
            }
            else if (result.Choice is not { } choice)
            {
                // Nothing to set aside, or the user chose anew in Steam since: that choice stays theirs.
                if (!plays)
                {
                    ChangeConfigLocked(config => config.SteamSetAside = null);
                }

                return true;
            }
            else
            {
                ChangeConfigLocked(config => config.SteamSetAside = plays
                    ? new SteamStartupMovieSetAside
                    {
                        MovieId = choice.MovieId, LocalPath = choice.LocalPath, Shuffle = choice.Shuffle
                    }
                    : null);
                _restartNeeded |= _steamRunning();
                outcome = plays
                    ? "Steam's own Startup Movie choice is set aside so WSGM's movie plays."
                    : "Steam's own Startup Movie choice is back.";
                SetNoticeLocked(outcome + (_restartNeeded ? $" {RestartNote}" : string.Empty));
            }
        }

        Log.Info($"Animations: {outcome}");
        Publish();
        return true;
    }

    /// <summary>
    ///     Brings the override in step with the choice off the calling thread, then records whether
    ///     Steam must restart and, with <paramref name="announce" />, says what Big Picture starts with
    ///     after it.
    /// </summary>
    /// <param name="announce">A line to lead the notice with, or null for no notice.</param>
    private async Task ApplyChoiceAsync(string? announce)
    {
        var report = await Task.Run(ApplyChoice).ConfigureAwait(false);
        lock (_sync)
        {
            _restartNeeded |= report.Changed && _steamRunning();
            if (report.Error is not null)
            {
                _error = report.Error;
            }
            else if (announce is not null || report.Note is not null)
            {
                var name = _library.Find(_config.Boot)?.Name ?? "Steam's own movie";
                var said = announce is null ? string.Empty : $"{announce} Big Picture starts with {name}.";
                SetNoticeLocked($"{said} {report.Note}".Trim()
                                + (_restartNeeded ? $" {RestartNote}" : string.Empty));
            }
        }
    }

    /// <summary>
    ///     Copies the current choice to the override. The copy runs outside the state lock, so a
    ///     reader never waits on it; applies are serialized by their own gate and each takes the
    ///     choice as it stands when it runs, so the last one wins.
    /// </summary>
    private AnimationApplyReport ApplyChoice()
    {
        lock (_applyGate)
        {
            var steam = _steamDirectory();
            if (steam is null)
            {
                return new AnimationApplyReport(false, "Steam is not installed; there is nowhere to write the movie.");
            }

            string? source;
            int volume;
            lock (_sync)
            {
                source = _config.Boot.Length == 0 ? null : _library.Find(_config.Boot)?.Path;
                volume = _config.BootVolume;
            }

            return AnimationOverrides.Apply(AnimationOverrides.Directory(steam), source, volume);
        }
    }

    /// <summary>Writes one change and shows it at once. Called under the lock.</summary>
    /// <returns>Whether the change was saved.</returns>
    private bool ChangeConfigLocked(Action<AnimationsConfig> change)
    {
        var saved = true;
        try
        {
            _writeConfig(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or TimeoutException)
        {
            _error = $"The choice could not be saved: {ex.Message}";
            saved = false;
        }

        var next = _config.Clone();
        change(next);
        _config = next;
        return saved;
    }

    private void SetNoticeLocked(string notice)
    {
        _notice = notice;
        _error = null;
    }

    private SteamUiCommandResult RefuseLocked(string error)
    {
        _error = error;
        return new SteamUiCommandResult(false, error);
    }

    /// <summary>Runs download or copy work in the background, answering the command at once.</summary>
    private Task<SteamUiCommandResult> StartWorkAsync(Func<CancellationToken, Task<string>> work)
    {
        lock (_sync)
        {
            if (_busy)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another movie is still being fetched."));
            }

            _busy = true;
            _error = null;
            _notice = null;
        }

        Publish();
        _ = Task.Run(async () =>
        {
            string? notice = null;
            string? error = null;
            try
            {
                notice = await work(_shutdown.Token).ConfigureAwait(false);
            }
            catch (AnimationRepoException ex)
            {
                error = ex.Message;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_sync)
            {
                _busy = false;
                _notice = notice;
                _error = error;
            }

            Log.Info($"Animations: {notice ?? error}");
            Publish();
        });
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private void Publish()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }
}

/// <summary>How the boot movies reach Steam's own startup movie choice.</summary>
/// <param name="SetAside">Puts Steam on its default movie and answers what it held.</param>
/// <param name="Restore">Gives Steam back a choice set aside, unless the user has chosen anew since.</param>
/// <param name="WhenReady">
///     Runs one attempt once the Steam UI transport opens and again at each later ready edge while it
///     answers false, as <see cref="SteamUiReadiness.RunWhenReadyAsync" /> does.
/// </param>
internal sealed record SteamStartupMovieAccess(
    Func<CancellationToken, Task<SteamStartupMovieResult>> SetAside,
    Func<SteamStartupMovieChoice, CancellationToken, Task<SteamStartupMovieResult>> Restore,
    Func<string, Func<CancellationToken, Task<bool>>, CancellationToken, Task<bool>> WhenReady);

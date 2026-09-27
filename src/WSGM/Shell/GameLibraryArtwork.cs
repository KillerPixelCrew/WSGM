using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One image a title could use for one artwork type.</summary>
/// <param name="Url">The full-size image.</param>
/// <param name="Thumb">A smaller copy for the page, or the same URL.</param>
/// <param name="Provider">Who supplied it, as shown on screen.</param>
/// <param name="Catalog">Whether it came from the title's own source rather than an artwork provider.</param>
/// <param name="Width">Its width, or zero when unknown.</param>
/// <param name="Height">Its height, or zero when unknown.</param>
internal sealed record GameLibraryArtworkOption(
    string Url,
    string Thumb,
    string Provider,
    bool Catalog,
    int Width,
    int Height);

/// <summary>What the artwork stage knows about one title.</summary>
internal enum GameLibraryArtworkStatus
{
    /// <summary>Waiting its turn.</summary>
    Pending,

    /// <summary>Being fetched now.</summary>
    Loading,

    /// <summary>Images were found.</summary>
    Ready,

    /// <summary>Every provider that could be asked was asked, and none had images for it.</summary>
    NotFound,

    /// <summary>A provider could not be asked: a rejected key, a spent quota, no connection.</summary>
    Failed,

    /// <summary>No provider is set up, so none was asked.</summary>
    Unavailable
}

/// <summary>Where one title's gathering stands.</summary>
/// <param name="Status">The status.</param>
/// <param name="Detail">Why, for a failed or unavailable title; otherwise empty.</param>
/// <param name="MatchName">The game it was matched to, or empty.</param>
internal readonly record struct GameLibraryArtworkProgress(
    GameLibraryArtworkStatus Status,
    string Detail,
    string MatchName);

/// <summary>A title the artwork stage should gather candidates for.</summary>
/// <param name="Id">The title's identity in the library.</param>
/// <param name="Name">The name the providers are searched with.</param>
/// <param name="Catalog">The images the title's own source offered.</param>
/// <param name="CatalogName">What to call that source's images, such as "Microsoft Store".</param>
/// <param name="Match">The game the user matched the title to, or null for the automatic match.</param>
internal sealed record GameLibraryArtworkRequest(
    string Id,
    string Name,
    IReadOnlyList<DiscoveredArtwork> Catalog,
    string CatalogName,
    ArtworkGameMatch? Match);

/// <summary>The artwork providers as the Game Library asks them.</summary>
internal interface IGameLibraryArtworkProviders
{
    /// <summary>Why nothing can be asked, or null when at least one provider is ready.</summary>
    string? Unavailable();

    /// <summary>A value that changes whenever what the providers can answer might have: keys, accounts, switches.</summary>
    string Signature();

    /// <summary>The automatic match: the first provider, in preference order, that knows the title.</summary>
    /// <exception cref="ArtworkProviderException">A provider that should have been asked could not be.</exception>
    Task<ArtworkGameMatch?> FindMatchAsync(
        string name, IReadOnlyCollection<string> skip, CancellationToken cancellationToken);

    /// <summary>Every ready provider's matches together, for the user fixing a match.</summary>
    Task<IReadOnlyList<ArtworkGameMatch>> SearchAsync(string term, CancellationToken cancellationToken);

    /// <summary>One artwork type for one provider's game, static images only.</summary>
    Task<ArtworkSearchResult> FetchAsync(ArtworkAsset asset, ArtworkGameMatch match,
        CancellationToken cancellationToken);
}

/// <summary><see cref="ArtworkSearch" /> over the live configuration.</summary>
/// <param name="config">Reads the current artwork configuration.</param>
internal sealed class ArtworkSearchProviders(Func<ArtworkConfig> config) : IGameLibraryArtworkProviders
{
    /// <summary>Static images, no adult ones: what a library tile can show.</summary>
    /// <remarks>Asked of the provider rather than filtered afterwards, so a page of animations is not emptied.</remarks>
    private static readonly ArtworkQuery LibraryQuery = new(Animated: false, Adult: false);

    /// <inheritdoc />
    public string? Unavailable()
    {
        var current = config();
        return ArtworkSearch.Providers.Any(provider => provider.GetStatus(current).IsReady)
            ? null
            : ArtworkSearch.Providers.Select(provider => provider.GetStatus(current).Detail)
                .FirstOrDefault(detail => detail.Length > 0) ?? "No artwork provider is set up.";
    }

    /// <inheritdoc />
    public string Signature()
    {
        var current = config();
        return string.Join('\u001f', current.SteamGridDbApiKey.Trim(), current.ScreenscraperEnabled,
            current.ScreenscraperUser.Trim(), current.ScreenscraperUserPassword.Trim());
    }

    /// <inheritdoc />
    public Task<ArtworkGameMatch?> FindMatchAsync(
        string name, IReadOnlyCollection<string> skip, CancellationToken cancellationToken)
    {
        return ArtworkSearch.FindMatchAsync(name, config(), skip, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ArtworkGameMatch>> SearchAsync(string term, CancellationToken cancellationToken)
    {
        return ArtworkSearch.SearchGamesAsync(term, config(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ArtworkSearchResult> FetchAsync(
        ArtworkAsset asset, ArtworkGameMatch match, CancellationToken cancellationToken)
    {
        return ArtworkSearch.GetAssetsForMatchAsync(asset, match, config(), LibraryQuery, cancellationToken);
    }
}

/// <summary>The Game Library's artwork candidates, gathered in the background after a scan.</summary>
/// <remarks>
///     <para>
///         A scan can list dozens of titles, and every one needs a search and five asset lookups at the
///         providers. Waiting for all of them before showing the review would take minutes, so the
///         review is shown at once and each title's artwork arrives as it is found. A few titles are
///         gathered at a time, a title's five lookups go out together, and each provider paces its own
///         requests behind its gate. All of it runs as background work there, so the artwork page or a
///         match being fixed is answered first. A title a surface asks for moves to the front.
///     </para>
///     <para>
///         The automatic match asks the providers in preference order (<see cref="ArtworkSearch.FindMatchAsync" />):
///         SteamGridDB, then Screenscraper only when SteamGridDB does not know the title or its game there
///         has no images. A provider that could not be asked stops the match there and shows as a
///         failure; it is never read as "no artwork". Fixing a match searches every provider at once and
///         lets the user pick.
///     </para>
///     <para>
///         What was found is kept across scans for a title whose name and match did not change, so a
///         rescan does not ask the providers everything again. A title that failed, found nothing or had
///         no provider to ask is tried again when the artwork settings change.
///     </para>
///     <para>
///         This only gathers candidates. What the user picks, and applying it, belong to the service.
///     </para>
/// </remarks>
internal sealed class GameLibraryArtwork : IDisposable
{
    /// <summary>How many titles are gathered at once.</summary>
    /// <remarks>
    ///     Three keeps the providers' own gates busy without queuing a whole library behind them:
    ///     SteamGridDB takes a few requests at once, Screenscraper one. Doing one title at a time,
    ///     six round trips each, took minutes for twenty titles.
    /// </remarks>
    private const int Concurrency = 3;

    /// <summary>The artwork types, in the order the surfaces show them.</summary>
    internal static readonly ArtworkAsset[] Assets =
    [
        ArtworkAsset.Grid, ArtworkAsset.Wide, ArtworkAsset.Hero, ArtworkAsset.Logo, ArtworkAsset.Icon
    ];

    private readonly Lock _gate = new();
    private readonly IGameLibraryArtworkProviders _providers;
    private readonly List<string> _queue = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, Title> _titles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly List<Task> _workers = [];
    private bool _disposed;
    private string _signature;

    /// <summary>Creates the stage over the providers.</summary>
    /// <param name="providers">How the providers are asked.</param>
    internal GameLibraryArtwork(IGameLibraryArtworkProviders providers)
    {
        _providers = providers;
        _signature = providers.Signature();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Cancels the workers and stops raising <see cref="Changed" /> at once; the cancellation token
    ///     and the wake are released once the last worker has returned, so none is left holding them.
    /// </remarks>
    public void Dispose()
    {
        Task[] workers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            workers = [.. _workers];
        }

        _shutdown.Cancel();
        _ = Task.WhenAll(workers).ContinueWith(
            _ =>
            {
                _shutdown.Dispose();
                _wake.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Raised when a title's candidates or status changed.</summary>
    internal event Action? Changed;

    /// <summary>Replaces the set of titles, keeping what is already known about unchanged ones.</summary>
    /// <param name="requests">Every title now listed, in the order they should be fetched.</param>
    internal void Reset(IReadOnlyList<GameLibraryArtworkRequest> requests)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Dictionary<string, Title> kept = new(StringComparer.Ordinal);
            _queue.Clear();
            foreach (var request in requests)
            {
                if (_titles.TryGetValue(request.Id, out var known) && known.Matches(request))
                {
                    known.Replace(request);
                    kept[request.Id] = known;

                    // A title a worker is gathering right now finishes there; queuing it again would
                    // ask the providers twice for it.
                    if (known.Status is GameLibraryArtworkStatus.Pending)
                    {
                        _queue.Add(request.Id);
                    }

                    continue;
                }

                kept[request.Id] = new Title(request);
                _queue.Add(request.Id);
            }

            _titles.Clear();
            foreach (var (id, title) in kept)
            {
                _titles[id] = title;
            }

            StartWorkers();
        }

        Wake();
        Raise();
    }

    /// <summary>Tries again every title that failed, found nothing or had no provider, once the provider settings change.</summary>
    /// <remarks>
    ///     A key entered or corrected, or Screenscraper turned on, can answer what was not answered
    ///     before. Any other settings change asks nothing again: a miss costs Screenscraper's daily
    ///     allowance, and repeating every miss on every save would spend it.
    /// </remarks>
    internal void ConfigurationChanged()
    {
        var signature = _providers.Signature();
        lock (_gate)
        {
            if (_disposed || string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                return;
            }

            _signature = signature;

            foreach (var (id, title) in _titles)
            {
                if (title.Status is GameLibraryArtworkStatus.Failed or GameLibraryArtworkStatus.NotFound
                    or GameLibraryArtworkStatus.Unavailable)
                {
                    title.Restart();
                    _queue.Add(id);
                }
            }

            StartWorkers();
        }

        Wake();
        Raise();
    }

    /// <summary>Moves a title to the front of the queue.</summary>
    /// <param name="id">The title.</param>
    internal void Prioritize(string id)
    {
        lock (_gate)
        {
            if (!_queue.Remove(id))
            {
                return;
            }

            _queue.Insert(0, id);
        }

        Wake();
    }

    /// <summary>Fetches a title again for a game the user picked.</summary>
    /// <param name="id">The title.</param>
    /// <param name="match">The game, or null to go back to the automatic match.</param>
    internal void Rematch(string id, ArtworkGameMatch? match)
    {
        lock (_gate)
        {
            if (_disposed || !_titles.TryGetValue(id, out var title))
            {
                return;
            }

            // A new title, not the old one edited: a worker still gathering the old match finishes
            // into an object nothing refers to any more.
            _titles[id] = new Title(title.Request with { Match = match });
            _queue.Remove(id);
            _queue.Insert(0, id);
            StartWorkers();
        }

        Wake();
        Raise();
    }

    /// <summary>Searches every ready provider for games, for the user fixing a wrong match.</summary>
    /// <param name="query">What to search for.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Every provider's matches together, exact ones first.</returns>
    internal Task<IReadOnlyList<ArtworkGameMatch>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        return _providers.SearchAsync(query, cancellationToken);
    }

    /// <summary>Every candidate for one title and artwork type: the catalog's, then the providers'.</summary>
    /// <param name="id">The title.</param>
    /// <param name="asset">The artwork type.</param>
    /// <returns>The candidates, empty when none are known yet or the title is not listed.</returns>
    internal IReadOnlyList<GameLibraryArtworkOption> Options(string id, ArtworkAsset asset)
    {
        lock (_gate)
        {
            return _titles.TryGetValue(id, out var title) ? title.Options(asset) : [];
        }
    }

    /// <summary>Where a title's gathering stands.</summary>
    /// <param name="id">The title.</param>
    /// <returns>Its progress; a title the stage does not know is pending.</returns>
    internal GameLibraryArtworkProgress StatusOf(string id)
    {
        lock (_gate)
        {
            return _titles.TryGetValue(id, out var title)
                ? new GameLibraryArtworkProgress(title.Status, title.Detail, title.MatchName)
                : new GameLibraryArtworkProgress(GameLibraryArtworkStatus.Pending, string.Empty, string.Empty);
        }
    }

    private void StartWorkers()
    {
        _workers.RemoveAll(worker => worker.IsCompleted);
        while (_workers.Count < Concurrency)
        {
            _workers.Add(Task.Run(RunAsync));
        }
    }

    private void Wake()
    {
        // Released under the lock: disposal sets the flag under it, so a wake never lands on a
        // semaphore the last worker's exit has already disposed.
        lock (_gate)
        {
            if (!_disposed)
            {
                _wake.Release();
            }
        }
    }

    /// <summary>Raises <see cref="Changed" />, never letting a subscriber's failure reach a worker.</summary>
    private void Raise()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Game Library artwork: a change subscriber failed: {ex.Message}");
        }
    }

    private async Task RunAsync()
    {
        // Every request this worker makes waits behind the ones a person is waiting on.
        using var background = ArtworkRequestGate.Background();
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            Title? next = null;
            var more = false;
            lock (_gate)
            {
                while (_queue.Count > 0 && next is null)
                {
                    var id = _queue[0];
                    _queue.RemoveAt(0);
                    if (_titles.TryGetValue(id, out var title) && title.Status is GameLibraryArtworkStatus.Pending)
                    {
                        title.Status = GameLibraryArtworkStatus.Loading;
                        next = title;
                    }
                }

                more = _queue.Count > 0;
            }

            // One release wakes one worker. Passing the wake on while the queue still has work is
            // what lets the others start rather than sleep through a whole scan.
            if (more)
            {
                Wake();
            }

            if (next is null)
            {
                try
                {
                    await _wake.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            GatherResult result;
            try
            {
                result = await GatherAsync(next.Request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Artwork for {next.Request.Name} could not be gathered: {ex.Message}");
                result = GatherResult.Nothing($"The artwork could not be gathered: {ex.Message}");
            }

            lock (_gate)
            {
                next.Finish(result, _providers.Unavailable());
            }

            // Once per title, after all five types are in: a change per type made seven full
            // publications of the whole library for every title.
            Raise();
        }
    }

    /// <summary>Finds the title's game and its images, falling back to the next provider when a game has none.</summary>
    private async Task<GatherResult> GatherAsync(GameLibraryArtworkRequest request, CancellationToken cancellationToken)
    {
        var fixedMatch = request.Match;
        List<string> skip = [];
        string? failure = null;
        while (true)
        {
            ArtworkGameMatch? match;
            if (fixedMatch is not null)
            {
                match = fixedMatch;
            }
            else
            {
                try
                {
                    match = await _providers.FindMatchAsync(request.Name, skip, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ArtworkProviderException ex)
                {
                    return GatherResult.Nothing(ex.Message);
                }
            }

            if (match is null)
            {
                return GatherResult.Nothing(failure);
            }

            // The five types go out together; the provider paces its own requests behind its gate.
            var fetched = await Task.WhenAll(Assets.Select(async asset =>
                    (Asset: asset, Result: await _providers.FetchAsync(asset, match, cancellationToken)
                        .ConfigureAwait(false))))
                .ConfigureAwait(false);
            failure ??= fetched.SelectMany(entry => entry.Result.Outcomes)
                .Select(outcome => outcome.Failure)
                .FirstOrDefault(message => message is not null);

            Dictionary<ArtworkAsset, IReadOnlyList<GameLibraryArtworkOption>> found = [];
            foreach (var (asset, result) in fetched)
            {
                found[asset] =
                [
                    .. result.Candidates
                        .Where(candidate => !candidate.Animated && !candidate.Nsfw && IsHttps(candidate.Url))
                        .Select(candidate => new GameLibraryArtworkOption(
                            candidate.Url,
                            IsHttps(candidate.Thumb) ? candidate.Thumb : candidate.Url,
                            candidate.ProviderName.Length > 0 ? candidate.ProviderName : match.ProviderId,
                            false,
                            candidate.Width,
                            candidate.Height))
                ];
            }

            // A match the user fixed is theirs, images or not. An automatic match whose game has no
            // images gives way to the next provider, which may know the same title with art.
            if (fixedMatch is not null || found.Values.Any(options => options.Count > 0) || failure is not null)
            {
                return new GatherResult(found, match.Name, failure);
            }

            skip.Add(match.ProviderId);
        }
    }

    private static bool IsHttps(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>What one gathering found.</summary>
    /// <param name="Found">The providers' candidates by type.</param>
    /// <param name="MatchName">The game they belong to, or empty.</param>
    /// <param name="Failure">Why a provider could not be asked, or null.</param>
    private sealed record GatherResult(
        IReadOnlyDictionary<ArtworkAsset, IReadOnlyList<GameLibraryArtworkOption>> Found,
        string MatchName,
        string? Failure)
    {
        /// <summary>Nothing found: no game, or a failure before any image was asked for.</summary>
        internal static GatherResult Nothing(string? failure)
        {
            return new GatherResult(
                new Dictionary<ArtworkAsset, IReadOnlyList<GameLibraryArtworkOption>>(), string.Empty, failure);
        }
    }

    private sealed class Title(GameLibraryArtworkRequest request)
    {
        private readonly Dictionary<ArtworkAsset, IReadOnlyList<GameLibraryArtworkOption>> _merged = [];

        private IReadOnlyDictionary<ArtworkAsset, IReadOnlyList<GameLibraryArtworkOption>> _found =
            new Dictionary<ArtworkAsset, IReadOnlyList<GameLibraryArtworkOption>>();

        internal GameLibraryArtworkRequest Request { get; private set; } = request;
        internal GameLibraryArtworkStatus Status { get; set; } = GameLibraryArtworkStatus.Pending;
        internal string Detail { get; private set; } = string.Empty;
        internal string MatchName { get; private set; } = string.Empty;

        internal bool Matches(GameLibraryArtworkRequest other)
        {
            return string.Equals(Request.Name, other.Name, StringComparison.Ordinal)
                   && Equals(Request.Match, other.Match);
        }

        /// <summary>Takes a newer request for the same title: its catalog may have changed.</summary>
        internal void Replace(GameLibraryArtworkRequest request)
        {
            Request = request;
            _merged.Clear();
        }

        internal void Restart()
        {
            Status = GameLibraryArtworkStatus.Pending;
            Detail = string.Empty;
        }

        internal void Finish(GatherResult result, string? unavailable)
        {
            _found = result.Found;
            _merged.Clear();
            MatchName = result.MatchName;
            var any = Assets.Any(asset => Options(asset).Count > 0);
            // Images found with one type failing is still a title with images; the failure is kept as
            // the detail so the page can say why a type is missing.
            (Status, Detail) = result switch
            {
                _ when any => (GameLibraryArtworkStatus.Ready, result.Failure ?? string.Empty),
                { Failure: { } failure } => (GameLibraryArtworkStatus.Failed, failure),
                _ when unavailable is not null => (GameLibraryArtworkStatus.Unavailable, unavailable),
                _ => (GameLibraryArtworkStatus.NotFound, string.Empty)
            };
        }

        /// <summary>The catalog's images, then the providers', each URL once; merged once and kept.</summary>
        internal IReadOnlyList<GameLibraryArtworkOption> Options(ArtworkAsset asset)
        {
            if (_merged.TryGetValue(asset, out var cached))
            {
                return cached;
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            List<GameLibraryArtworkOption> options = [];
            foreach (var image in Request.Catalog)
            {
                if (image.Asset == asset && IsHttps(image.Url) && seen.Add(image.Url))
                {
                    options.Add(new GameLibraryArtworkOption(image.Url, image.Url, Request.CatalogName, true, 0, 0));
                }
            }

            if (_found.TryGetValue(asset, out var found))
            {
                options.AddRange(found.Where(option => seen.Add(option.Url)));
            }

            _merged[asset] = options;
            return options;
        }
    }
}

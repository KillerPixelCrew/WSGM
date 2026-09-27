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

    /// <summary>Everything that answered is in.</summary>
    Ready,

    /// <summary>No provider could be asked, or every one failed.</summary>
    Failed
}

/// <summary>A title the artwork stage should gather candidates for.</summary>
/// <param name="Id">The title's identity in the library: its source and key.</param>
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

/// <summary>The Game Library's artwork candidates, gathered in the background after a scan.</summary>
/// <remarks>
///     <para>
///         A scan can list dozens of titles, and every one needs a search and five asset lookups at the
///         providers. Waiting for all of them before showing the review would take minutes, so the
///         review is shown at once and each title's artwork arrives as it is found. A few titles are
///         gathered at a time and a title's five lookups go out together; each provider paces its own
///         requests. SteamGridDB is asked first and Screenscraper only when it had nothing. A title a
///         surface asks for moves to the front of the queue.
///     </para>
///     <para>
///         Results are kept across scans for a title whose name and match did not change, so a rescan
///         does not ask the providers everything again.
///     </para>
///     <para>
///         This only gathers candidates. What the user picks, and applying it, belong to the service.
///     </para>
/// </remarks>
internal sealed class GameLibraryArtwork : IDisposable
{
    /// <summary>The artwork types, in the order the surfaces show them.</summary>
    internal static readonly ArtworkAsset[] Assets =
    [
        ArtworkAsset.Grid, ArtworkAsset.Wide, ArtworkAsset.Hero, ArtworkAsset.Logo, ArtworkAsset.Icon
    ];

    /// <summary>How many titles are gathered at once.</summary>
    /// <remarks>
    ///     Three keeps the providers' own gates busy without queuing a whole library behind them:
    ///     SteamGridDB takes a few requests at once, anonymous Screenscraper one. Doing one title at a
    ///     time, six round trips each, took minutes for twenty titles.
    /// </remarks>
    private const int Concurrency = 3;

    private readonly Func<ArtworkAsset, ArtworkGameMatch, CancellationToken,
        Task<IReadOnlyList<ArtworkCandidate>>> _fetch;

    private readonly Lock _gate = new();
    private readonly Func<IReadOnlyList<string>> _providers;
    private readonly List<string> _queue = [];
    private readonly Func<string, string, CancellationToken, Task<IReadOnlyList<ArtworkGameMatch>>> _search;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, Title> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly List<Task> _workers = [];
    private bool _disposed;

    /// <summary>Creates the stage over the providers.</summary>
    /// <param name="providers">The providers that can be asked, in the order their results are preferred.</param>
    /// <param name="search">Searches one provider for games by name, in its own ranking.</param>
    /// <param name="fetch">Fetches one artwork type for one provider's game.</param>
    internal GameLibraryArtwork(
        Func<IReadOnlyList<string>> providers,
        Func<string, string, CancellationToken, Task<IReadOnlyList<ArtworkGameMatch>>> search,
        Func<ArtworkAsset, ArtworkGameMatch, CancellationToken, Task<IReadOnlyList<ArtworkCandidate>>> fetch)
    {
        _providers = providers;
        _search = search;
        _fetch = fetch;
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
        _wake.Release();
    }

    /// <summary>Raised when a title's candidates changed.</summary>
    internal event Action? Changed;

    /// <summary>Replaces the set of titles, keeping what is already known about unchanged ones.</summary>
    /// <param name="requests">Every title now listed, in the order they should be fetched.</param>
    internal void Reset(IReadOnlyList<GameLibraryArtworkRequest> requests)
    {
        lock (_gate)
        {
            Dictionary<string, Title> kept = new(StringComparer.OrdinalIgnoreCase);
            _queue.Clear();
            foreach (var request in requests)
            {
                if (_titles.TryGetValue(request.Id, out var known) && known.Matches(request))
                {
                    known.Request = request;
                    kept[request.Id] = known;
                    if (known.Status is GameLibraryArtworkStatus.Pending or GameLibraryArtworkStatus.Loading)
                    {
                        known.Status = GameLibraryArtworkStatus.Pending;
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

            StartWorker();
        }

        _wake.Release();
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

        _wake.Release();
    }

    /// <summary>Fetches a title again for a game the user picked.</summary>
    /// <param name="id">The title.</param>
    /// <param name="match">The game, or null to go back to the automatic match.</param>
    internal void Rematch(string id, ArtworkGameMatch? match)
    {
        lock (_gate)
        {
            if (!_titles.TryGetValue(id, out var title))
            {
                return;
            }

            _titles[id] = new Title(title.Request with { Match = match });
            _queue.Remove(id);
            _queue.Insert(0, id);
            StartWorker();
        }

        _wake.Release();
        Changed?.Invoke();
    }

    /// <summary>Searches the providers for games, for the user fixing a wrong match.</summary>
    /// <param name="query">What to search for.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The first provider's matches that are not empty, exact ones first.</returns>
    /// <remarks>
    ///     The same fallback the gathering uses: SteamGridDB knows Steam libraries, and Screenscraper,
    ///     a ROM database, is asked only when SteamGridDB has nothing for the name.
    /// </remarks>
    internal async Task<IReadOnlyList<ArtworkGameMatch>> SearchAsync(
        string query, CancellationToken cancellationToken)
    {
        foreach (var provider in _providers())
        {
            var matches = await _search(provider, query, cancellationToken).ConfigureAwait(false);
            if (matches.Count > 0)
            {
                return [.. matches.OrderByDescending(match => match.Exact)];
            }
        }

        return [];
    }

    /// <summary>Every candidate for one title and artwork type: the catalog's, then the providers'.</summary>
    /// <param name="id">The title.</param>
    /// <param name="asset">The artwork type.</param>
    /// <returns>The candidates, empty when none are known yet.</returns>
    internal IReadOnlyList<GameLibraryArtworkOption> Options(string id, ArtworkAsset asset)
    {
        lock (_gate)
        {
            if (!_titles.TryGetValue(id, out var title))
            {
                return [];
            }

            List<GameLibraryArtworkOption> options =
            [
                .. title.Request.Catalog
                    .Where(image => image.Asset == asset)
                    .Select(image => new GameLibraryArtworkOption(
                        image.Url, image.Url, title.Request.CatalogName, true, 0, 0))
            ];
            if (title.Found.TryGetValue(asset, out var found))
            {
                options.AddRange(found.Where(option =>
                    options.All(existing => !string.Equals(existing.Url, option.Url, StringComparison.Ordinal))));
            }

            return options;
        }
    }

    /// <summary>Where a title's gathering stands.</summary>
    /// <param name="id">The title.</param>
    /// <returns>Its status, and the name of the game it was matched to, when there is one.</returns>
    internal (GameLibraryArtworkStatus Status, string MatchName) StatusOf(string id)
    {
        lock (_gate)
        {
            return _titles.TryGetValue(id, out var title)
                ? (title.Status, title.MatchName)
                : (GameLibraryArtworkStatus.Pending, string.Empty);
        }
    }

    private void StartWorker()
    {
        _workers.RemoveAll(worker => worker.IsCompleted);
        while (_workers.Count < Concurrency)
        {
            _workers.Add(Task.Run(RunAsync));
        }
    }

    private async Task RunAsync()
    {
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
                _wake.Release();
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

            Changed?.Invoke();
            try
            {
                await GatherAsync(next, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Artwork for {next.Request.Name} could not be gathered: {ex.Message}");
                lock (_gate)
                {
                    next.Status = GameLibraryArtworkStatus.Failed;
                }
            }

            Changed?.Invoke();
        }
    }

    private async Task GatherAsync(Title title, CancellationToken cancellationToken)
    {
        // The providers in preference order, each asked only when the ones before had nothing:
        // SteamGridDB knows Steam libraries and answers in a moment; Screenscraper is a ROM database
        // whose anonymous access takes one request at a time, and it is a fallback, not a second
        // opinion. Asking both for every title held every card behind Screenscraper's queue.
        var match = title.Request.Match;
        if (match is null)
        {
            foreach (var provider in _providers())
            {
                IReadOnlyList<ArtworkGameMatch> matches;
                try
                {
                    matches = await _search(provider, title.Request.Name, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    Log.Warn($"Artwork provider {provider} could not search for {title.Request.Name}: {ex.Message}");
                    continue;
                }

                // Its first exact match, else its own first result.
                match = matches.FirstOrDefault(candidate => candidate.Exact) ?? matches.FirstOrDefault();
                if (match is not null)
                {
                    break;
                }
            }
        }

        if (match is not null)
        {
            // The five types go out together; the provider paces its own requests behind its gate.
            await Task.WhenAll(Assets.Select(asset => GatherAssetAsync(title, asset, match, cancellationToken)))
                .ConfigureAwait(false);
        }

        lock (_gate)
        {
            title.MatchName = match?.Name ?? string.Empty;
            title.Status = match is null && title.Request.Catalog.Count == 0
                ? GameLibraryArtworkStatus.Failed
                : GameLibraryArtworkStatus.Ready;
        }
    }

    /// <summary>Gathers one artwork type for a title from its match, and publishes it.</summary>
    private async Task GatherAssetAsync(
        Title title, ArtworkAsset asset, ArtworkGameMatch match, CancellationToken cancellationToken)
    {
        IReadOnlyList<ArtworkCandidate> candidates;
        try
        {
            candidates = await _fetch(asset, match, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Warn($"Artwork provider {match.ProviderId} failed for {title.Request.Name}: {ex.Message}");
            candidates = [];
        }

        List<GameLibraryArtworkOption> options =
        [
            .. candidates
                .Where(candidate => !candidate.Animated && !candidate.Nsfw
                                                        && candidate.Url.StartsWith("https://",
                                                            StringComparison.OrdinalIgnoreCase))
                .Select(candidate => new GameLibraryArtworkOption(
                    candidate.Url,
                    candidate.Thumb.Length > 0 ? candidate.Thumb : candidate.Url,
                    candidate.ProviderName.Length > 0 ? candidate.ProviderName : match.ProviderId,
                    false,
                    candidate.Width,
                    candidate.Height))
        ];

        lock (_gate)
        {
            title.Found[asset] = options;
        }

        Changed?.Invoke();
    }

    private sealed class Title(GameLibraryArtworkRequest request)
    {
        internal GameLibraryArtworkRequest Request { get; set; } = request;
        internal GameLibraryArtworkStatus Status { get; set; } = GameLibraryArtworkStatus.Pending;
        internal string MatchName { get; set; } = string.Empty;
        internal Dictionary<ArtworkAsset, List<GameLibraryArtworkOption>> Found { get; } = [];

        internal bool Matches(GameLibraryArtworkRequest other)
        {
            return string.Equals(Request.Name, other.Name, StringComparison.Ordinal)
                   && Equals(Request.Match, other.Match);
        }
    }
}

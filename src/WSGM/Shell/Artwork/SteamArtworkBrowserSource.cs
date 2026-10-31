using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One surface's browsing of WSGM's artwork providers in the toolkit's Steam-native browser.</summary>
/// <remarks>
///     Created by the shared <see cref="ArtworkBrowser" />, which owns the providers, the saved state, Steam and
///     the writes to it. This session owns what one page shows: the open game, its tab, filter and match, and the
///     results it loaded. <see cref="ArtworkBrowserTabs" /> supplies the tab layout and default filters, and
///     <see cref="ArtworkManagedSlots" /> the game's current artwork.
/// </remarks>
internal sealed class SteamArtworkBrowserSource : IArtworkBrowseSession
{
    private readonly Dictionary<string, SteamArtworkBrowserFilter> _filters = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private readonly ArtworkBrowser _owner;
    private readonly IReadOnlyList<IArtworkProvider> _providers;
    private readonly Func<ArtworkConfig> _readConfiguration;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SteamClient _steam;
    private readonly ArtworkStateStore _store;

    /// <summary>What a caller said a game is called, for games Steam's list does not name yet.</summary>
    /// <remarks>
    ///     The Game Library opens this page for a shortcut it created seconds earlier, which Steam's
    ///     library listing does not carry yet. Without a hint the page names it "App N" and searches
    ///     SteamGridDB for exactly that.
    /// </remarks>
    private readonly Dictionary<uint, string> _titleHints = [];

    private readonly SteamArtworkWriter _writer;

    private Dictionary<string, ArtworkCandidate> _candidates = new(StringComparer.Ordinal);
    private int _disposeStarted;
    private long _generation;
    private CancellationTokenSource? _load;
    private Dictionary<string, ArtworkGameMatch> _matches = new(StringComparer.Ordinal);
    private Dictionary<string, SgdbOfficialAsset> _officialCandidates = new(StringComparer.Ordinal);
    private long _revision;
    private ArtworkGameMatch? _selectedMatch;
    private SteamArtworkBrowserState? _state;

    /// <summary>Creates a session over the shared owner; use <see cref="ArtworkBrowser.CreateViewSession" />.</summary>
    /// <param name="owner">The owner of the providers, the saved state and Steam.</param>
    internal SteamArtworkBrowserSource(ArtworkBrowser owner)
    {
        _owner = owner;
        _readConfiguration = owner.ReadConfiguration;
        _steam = owner.Steam;
        _providers = owner.Providers;
        _store = owner.Store;
        _writer = owner.Writer;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void CancelBrowsing()
    {
        var changed = false;
        lock (_gate)
        {
            _load?.Cancel();
            _generation++;
            if (_state is not null)
            {
                _state = _state with { Loading = false, Revision = ++_revision };
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _owner.Remove(this);
        _shutdown.Cancel();
        lock (_gate)
        {
            _load?.Cancel();
            _state = null;
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SelectTabAsync(string tab, CancellationToken cancellationToken)
    {
        if (!ArtworkBrowserTabs.Configured(_readConfiguration()).Any(candidate => candidate.Id == tab))
        {
            return Refuse("That artwork tab is not available.");
        }

        if (OpenAppId() is not { } appId)
        {
            return Refuse("No artwork page is open.");
        }

        CancellationToken loadToken;
        long generation;
        var managed = ArtworkManagedSlots.Read(appId);
        lock (_gate)
        {
            if (_state?.AppId != appId)
            {
                return Refuse("The artwork page changed.");
            }

            (loadToken, generation) = RestartLoad();
            _candidates = new Dictionary<string, ArtworkCandidate>(StringComparer.Ordinal);
            _officialCandidates = new Dictionary<string, SgdbOfficialAsset>(StringComparer.Ordinal);
            _state = _state with
            {
                ActiveTab = tab,
                Assets = [],
                OfficialAssets = [],
                ManagedSlots = managed,
                Filter = FilterFor(tab),
                Page = 0,
                HasMore = false,
                Loading = tab != ArtworkBrowserTabs.ManageTab,
                Error = null,
                Notice = null,
                Revision = ++_revision
            };
        }

        Changed?.Invoke();
        if (tab != ArtworkBrowserTabs.ManageTab)
        {
            _ = LoadAsync(appId, tab, 0, false, generation, loadToken);
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ApplyAsync(string id, CancellationToken cancellationToken)
    {
        ArtworkCandidate candidate;
        uint appId;
        ArtworkAsset asset;
        lock (_gate)
        {
            if (_state is null || !_candidates.TryGetValue(id, out candidate!))
            {
                return Refuse("That artwork result is no longer current.");
            }

            appId = _state.AppId;
            if (!ArtworkAssetNames.TryParse(_state.ActiveTab, out asset))
            {
                return Refuse("That artwork slot cannot be changed.");
            }
        }

        PublishOutcome(appId, "Applying artwork…");
        _ = PublishWriteAsync(appId, _writer.ApplyFromUrlAsync(appId, asset, candidate.Url, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ApplyOfficialAsync(string id, CancellationToken cancellationToken)
    {
        SgdbOfficialAsset candidate;
        uint appId;
        ArtworkAsset asset;
        lock (_gate)
        {
            if (_state is null || !_officialCandidates.TryGetValue(id, out candidate!))
            {
                return Refuse("That official artwork selection is no longer current.");
            }

            appId = _state.AppId;
            if (!ArtworkAssetNames.TryParse(_state.ActiveTab, out asset))
            {
                return Refuse("That artwork slot cannot be changed.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        PublishOutcome(appId, "Applying official artwork…");
        _ = PublishWriteAsync(appId, _writer.ApplyFromUrlAsync(appId, asset, candidate.Url, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ClearAsync(string tab, CancellationToken cancellationToken)
    {
        if (!ArtworkAssetNames.TryParse(tab, out var asset))
        {
            return Refuse("That artwork slot cannot be reset.");
        }

        if (OpenAppId() is not { } appId)
        {
            return Refuse("No artwork page is open.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        PublishOutcome(appId, "Resetting artwork…");
        _ = PublishWriteAsync(appId, _writer.ClearAsync(appId, asset, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken)
    {
        uint appId;
        string tab;
        int page;
        long generation;
        CancellationToken loadToken;
        lock (_gate)
        {
            if (_state is not { HasMore: true, Loading: false } state
                || !ArtworkAssetNames.TryParse(state.ActiveTab, out _))
            {
                return Refuse("The current artwork providers returned all available results.");
            }

            appId = state.AppId;
            tab = state.ActiveTab;
            page = state.Page + 1;
            (loadToken, generation) = RestartLoad();
            _state = state with { Loading = true, Page = page, Revision = ++_revision };
        }

        Changed?.Invoke();
        _ = LoadAsync(appId, tab, page, true, generation, loadToken);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ApplyLocalAsync(string tab, string path, CancellationToken cancellationToken)
    {
        if (!ArtworkAssetNames.TryParse(tab, out var asset))
        {
            return Refuse("That artwork slot cannot be changed.");
        }

        // A local file on this machine, as Steam's file picker lists them. A network path would make
        // WSGM open a connection on the page's say-so.
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
                                             || Path.GetExtension(path).ToLowerInvariant() is not
                                                 (".png" or ".jpg" or ".jpeg" or ".webp" or ".ico"))
        {
            return Refuse("Choose a PNG, JPEG, WebP, or ICO image.");
        }

        if (OpenAppId() is not { } appId)
        {
            return Refuse("No artwork page is open.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        PublishOutcome(appId, "Applying local artwork…");
        _ = PublishWriteAsync(appId, _writer.ApplyLocalAsync(appId, asset, path, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ApplyInvisibleAsync(string tab, CancellationToken cancellationToken)
    {
        if (!ArtworkAssetNames.TryParse(tab, out var asset) || asset is ArtworkAsset.Icon)
        {
            return Refuse("That artwork slot cannot be made invisible.");
        }

        if (OpenAppId() is not { } appId)
        {
            return Refuse("No artwork page is open.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        PublishOutcome(appId, "Applying invisible artwork…");
        _ = PublishWriteAsync(appId, _writer.ApplyInvisibleAsync(appId, asset, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetFilterAsync(
        SteamArtworkBrowserFilter filter, CancellationToken cancellationToken)
    {
        uint appId;
        string tab;
        long generation;
        CancellationToken loadToken;
        lock (_gate)
        {
            if (_state is null || !ArtworkAssetNames.TryParse(_state.ActiveTab, out _))
            {
                return Refuse("No artwork result tab is open.");
            }

            appId = _state.AppId;
            tab = _state.ActiveTab;
            (loadToken, generation) = RestartLoad();
            _candidates.Clear();
            _officialCandidates.Clear();
            _filters[tab] = filter;
            _state = _state with
            {
                Filter = filter,
                Assets = [],
                OfficialAssets = [],
                Page = 0,
                HasMore = false,
                Loading = true,
                Error = null,
                Revision = ++_revision
            };
        }

        Changed?.Invoke();
        _store.SaveFilter(tab, filter);
        _ = LoadAsync(appId, tab, 0, false, generation, loadToken);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SearchGamesAsync(
        string term, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = SearchGamesObservedAsync(term, _shutdown.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SelectGameAsync(string? id, CancellationToken cancellationToken)
    {
        uint appId;
        string tab;
        long generation;
        CancellationToken loadToken;
        lock (_gate)
        {
            if (_state is null || !ArtworkAssetNames.TryParse(_state.ActiveTab, out _))
            {
                return Refuse("No artwork result tab is open.");
            }

            if (id is not null && !_matches.TryGetValue(id, out _selectedMatch))
            {
                return Refuse("That game match is no longer current.");
            }

            if (id is null)
            {
                _selectedMatch = null;
            }

            appId = _state.AppId;
            tab = _state.ActiveTab;
            (loadToken, generation) = RestartLoad();
            _candidates.Clear();
            _officialCandidates.Clear();
            _matches.Clear();
            _state = _state with
            {
                SelectedGame = _selectedMatch?.Name,
                GameMatches = [],
                Assets = [],
                OfficialAssets = [],
                Page = 0,
                HasMore = false,
                Loading = true,
                Error = null,
                Revision = ++_revision
            };
        }

        Changed?.Invoke();
        PersistSelectedGame(appId, _selectedMatch);
        _ = LoadAsync(appId, tab, 0, false, generation, loadToken);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SaveLogoPositionAsync(
        string anchor, int width, int height, CancellationToken cancellationToken)
    {
        if (OpenAppId() is not { } appId)
        {
            return Refuse("No artwork page is open.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = PublishWriteAsync(appId,
            _writer.SaveLogoPositionAsync(appId, new SteamLogoPosition(anchor, width, height), _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ResetLogoPositionAsync(CancellationToken cancellationToken)
    {
        if (OpenAppId() is not { } appId)
        {
            return Refuse("No artwork page is open.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = PublishWriteAsync(appId, _writer.ResetLogoPositionAsync(appId, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public SteamArtworkBrowserState? ReadState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    /// <inheritdoc />
    public Task<SteamLibraryReadResult> ReadGamesAsync()
    {
        return OverlayLibraryLookup.ReadAsync(_steam, _shutdown.Token);
    }

    /// <inheritdoc />
    public async Task<SteamLogoPosition?> ReadLogoPositionAsync()
    {
        var appId = ReadState()?.AppId ?? 0;
        // No position and a read that failed both leave the editor on its default placement.
        var read = await _steam.Apps.ReadLogoPositionAsync(appId, _shutdown.Token).ConfigureAwait(false);
        return read.Succeeded ? read.Value : null;
    }

    /// <summary>Opens the page for one game, naming it when Steam cannot yet.</summary>
    /// <param name="appId">The game's Steam app id.</param>
    /// <param name="titleHint">What the caller calls it, or null to rely on Steam's list.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the page could be opened.</returns>
    public Task<SteamUiCommandResult> OpenAsync(uint appId, string? titleHint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (appId == 0)
        {
            return Refuse("Steam did not identify the selected game.");
        }

        CancellationToken loadToken;
        long generation;
        var savedLink = _store.FindGame(appId);
        var configuration = _readConfiguration();
        var tabs = ArtworkBrowserTabs.Configured(configuration);
        var initialTab = tabs.Any(tab => tab.Id == configuration.DefaultTab)
            ? configuration.DefaultTab
            : tabs[0].Id;
        var managed = ArtworkManagedSlots.Read(appId);
        var savedFilters = _store.ReadFilters();
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(titleHint))
            {
                _titleHints[appId] = titleHint.Trim();
            }

            (loadToken, generation) = RestartLoad();
            _candidates = new Dictionary<string, ArtworkCandidate>(StringComparer.Ordinal);
            _officialCandidates = new Dictionary<string, SgdbOfficialAsset>(StringComparer.Ordinal);
            _matches = new Dictionary<string, ArtworkGameMatch>(StringComparer.Ordinal);
            _filters.Clear();
            foreach (var savedFilter in savedFilters)
            {
                _filters[savedFilter.Tab] = ArtworkBrowserTabs.FromSaved(savedFilter);
            }

            _selectedMatch = savedLink is not null
                ? new ArtworkGameMatch(
                    savedLink.ProviderId,
                    savedLink.GameId,
                    savedLink.Name,
                    true)
                : null;
            _state = new SteamArtworkBrowserState(
                appId,
                FallbackName(appId),
                tabs,
                initialTab,
                [],
                [],
                managed,
                FilterFor(initialTab),
                [],
                _selectedMatch?.Name,
                Loading: true,
                Revision: generation);
        }

        // The one line that says a request from Steam's menu reached WSGM: without it, "the page
        // never opened" and "it opened and found nothing" read the same in the log.
        Log.Info($"Steam artwork page: opened for app {appId} on the {initialTab} tab"
                 + (savedLink is null ? "." : $", matched to {savedLink.ProviderId} game {savedLink.GameId}."));
        Changed?.Invoke();
        if (initialTab != ArtworkBrowserTabs.ManageTab)
        {
            _ = LoadAsync(appId, initialTab, 0, false, generation, loadToken);
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Shows what another session applied, when this session has the same game open.</summary>
    /// <param name="appId">The game.</param>
    /// <param name="message">The outcome.</param>
    /// <param name="managed">The game's managed slots now.</param>
    internal void ShowArtwork(uint appId, string message, SteamArtworkManagedSlot[] managed)
    {
        lock (_gate)
        {
            if (_state?.AppId != appId)
            {
                return;
            }

            _state = _state with { ManagedSlots = managed, Notice = message, Error = null, Revision = ++_revision };
        }

        Changed?.Invoke();
    }

    /// <summary>Opens this session's game again, for a changed tab layout or credentials.</summary>
    internal void Reopen()
    {
        if (OpenAppId() is { } current)
        {
            _ = Task.Run(() => OpenAsync(current, _shutdown.Token));
        }
    }

    /// <summary>Opens the page for one game under the name Steam's list gives it.</summary>
    /// <param name="appId">The game's Steam app id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the page could be opened.</returns>
    internal Task<SteamUiCommandResult> OpenAsync(uint appId, CancellationToken cancellationToken)
    {
        return OpenAsync(appId, null, cancellationToken);
    }

    private static Task<SteamUiCommandResult> Refuse(string reason)
    {
        return Task.FromResult(new SteamUiCommandResult(false, reason));
    }

    /// <summary>The open game, or null when no page is open.</summary>
    private uint? OpenAppId()
    {
        lock (_gate)
        {
            return _state?.AppId;
        }
    }

    /// <summary>Cancels the current load and starts the next one's generation. Call under the gate.</summary>
    /// <returns>The new load's token and generation.</returns>
    private (CancellationToken Token, long Generation) RestartLoad()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        return (_load.Token, ++_generation);
    }

    private async Task SearchGamesCoreAsync(string term, CancellationToken cancellationToken)
    {
        var config = _readConfiguration();
        var matches = await ArtworkSearch.SearchGamesAsync(term, config, cancellationToken, _providers)
            .ConfigureAwait(false);
        var mapped = new List<SteamArtworkBrowserGame>(matches.Count);
        var lookup = new Dictionary<string, ArtworkGameMatch>(StringComparer.Ordinal);
        foreach (var match in matches)
        {
            var id = Guid.NewGuid().ToString("N");
            lookup[id] = match;
            mapped.Add(new SteamArtworkBrowserGame(
                id, match.Name,
                _providers.FirstOrDefault(provider => provider.Id == match.ProviderId)?.DisplayName ??
                match.ProviderId));
        }

        lock (_gate)
        {
            if (_state is null)
            {
                return;
            }

            _matches = lookup;
            _state = _state with
            {
                GameMatches = mapped,
                Notice = mapped.Count == 0 ? "No matching games were found." : null,
                Revision = ++_revision
            };
        }

        Changed?.Invoke();
    }

    private async Task SearchGamesObservedAsync(string term, CancellationToken cancellationToken)
    {
        try
        {
            await SearchGamesCoreAsync(term, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: game search failed: {ex.Message}");
            lock (_gate)
            {
                if (_state is not null)
                {
                    _state = _state with { Error = ex.Message, Revision = ++_revision };
                }
            }

            Changed?.Invoke();
        }
    }

    /// <summary>What to call a game Steam's list does not name. Call under the gate.</summary>
    private string FallbackName(uint appId)
    {
        return _titleHints.TryGetValue(appId, out var hint)
            ? hint
            : $"App {appId.ToString(CultureInfo.InvariantCulture)}";
    }

    private async Task LoadAsync(
        uint appId, string tab, int page, bool append, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var gamesTask = _steam.Library.ReadGamesAsync(cancellationToken);
            var config = _readConfiguration();
            if (!ArtworkAssetNames.TryParse(tab, out var asset))
            {
                return;
            }

            ArtworkGameMatch? selected;
            SteamArtworkBrowserFilter filter;
            lock (_gate)
            {
                selected = _selectedMatch;
                filter = _state?.Filter ?? ArtworkBrowserTabs.DefaultFilter(tab);
            }

            // A match kept from a provider that is now off, or has lost its credentials, would ask
            // nobody and leave the page empty without a single request. The ready providers are
            // asked instead, the way a game with no match is.
            if (selected is not null && _providers.FirstOrDefault(provider => provider.Id == selected.ProviderId)
                    ?.GetStatus(config).IsReady != true)
            {
                Log.Info($"Steam artwork page: app {appId} was matched through {selected.ProviderId}, "
                         + "which is not ready; asking the ready providers instead.");
                selected = null;
                lock (_gate)
                {
                    if (_state is null || generation != _generation)
                    {
                        return;
                    }

                    _selectedMatch = null;
                    _state = _state with { SelectedGame = null };
                }
            }

            // A failed read names the game from the hint or its id, as an empty library did.
            var games = (await gamesTask.ConfigureAwait(false)).Games;
            string fallback;
            lock (_gate)
            {
                fallback = FallbackName(appId);
            }

            var name = games.FirstOrDefault(game => game.AppId == appId)?.Name ?? fallback;
            if (selected is null && SteamApps.IsShortcutAppId(appId) && page == 0)
            {
                selected = (await ArtworkSearch.SearchGamesAsync(name, config, cancellationToken, _providers)
                        .ConfigureAwait(false))
                    .FirstOrDefault();
                if (selected is not null)
                {
                    lock (_gate)
                    {
                        if (_state is null || generation != _generation)
                        {
                            return;
                        }

                        _selectedMatch = selected;
                        _state = _state with { SelectedGame = selected.Name };
                    }

                    PersistSelectedGame(appId, selected);
                }
                else
                {
                    Log.Info($"Steam artwork page: no ready provider matched \"{name}\" (app {appId}).");
                }
            }

            // A shortcut's id is Steam's own number for it, not a store id: no provider can look it up,
            // and SteamGridDB answers a request by it with 404. The person searches by name instead.
            if (selected is null && SteamApps.IsShortcutAppId(appId))
            {
                lock (_gate)
                {
                    if (_state is null || generation != _generation || _state.AppId != appId
                        || _state.ActiveTab != tab)
                    {
                        return;
                    }

                    _state = _state with
                    {
                        AppName = name,
                        Assets = [],
                        Loading = false,
                        HasMore = false,
                        Page = page,
                        Notice = null,
                        Error = $"No artwork provider found a game called \"{name}\". "
                                + "Find it by name in the Filter panel's Game search.",
                        Revision = ++_revision
                    };
                }

                Changed?.Invoke();
                return;
            }

            var query = new ArtworkQuery(
                page,
                filter.Styles,
                filter.Dimensions,
                filter.Mimes,
                filter.Static,
                filter.Animated,
                filter.Adult,
                filter.Humor,
                filter.Epilepsy,
                filter.Untagged);
            // The provider search and the official lookup are independent, so both run at once.
            var fetchedTask = selected is null
                ? ArtworkSearch.GetAssetsForSteamAppAsync(asset, appId, config, query, cancellationToken, _providers)
                : ArtworkSearch.GetAssetsForMatchAsync(asset, selected, config, query, cancellationToken, _providers);
            var officialTask = _owner.LoadOfficialAssetsAsync(asset, appId, selected, config, cancellationToken);
            await Task.WhenAll(fetchedTask, officialTask).ConfigureAwait(false);
            var fetched = await fetchedTask.ConfigureAwait(false);
            var official = await officialTask.ConfigureAwait(false);
            var candidates = fetched.Candidates
                .Where(candidate => candidate.Width == 0
                                    || ImageHeader.IsWithinLimits(candidate.Width, candidate.Height))
                .ToArray();
            var mapped = new List<SteamArtworkBrowserAsset>(candidates.Length);
            Dictionary<string, ArtworkCandidate> lookup;
            Dictionary<string, SgdbOfficialAsset> officialLookup = new(StringComparer.Ordinal);
            List<SteamArtworkOfficialAsset> officialMapped = [];
            HashSet<string> seen;
            lock (_gate)
            {
                lookup = append
                    ? new Dictionary<string, ArtworkCandidate>(_candidates, StringComparer.Ordinal)
                    : new Dictionary<string, ArtworkCandidate>(StringComparer.Ordinal);
                seen = [.. lookup.Values.Select(candidate => candidate.Url)];
            }

            foreach (var candidate in candidates)
            {
                if (!seen.Add(candidate.Url))
                {
                    continue;
                }

                var id = Guid.NewGuid().ToString("N");
                lookup[id] = candidate;
                mapped.Add(new SteamArtworkBrowserAsset(
                    id,
                    candidate.Url,
                    candidate.Thumb.Length == 0 ? candidate.Url : candidate.Thumb,
                    candidate.Width,
                    candidate.Height,
                    candidate.Extension,
                    candidate.ProviderName.Length == 0 ? "Artwork provider" : candidate.ProviderName,
                    candidate.Author,
                    candidate.Style,
                    candidate.Notes,
                    candidate.Animated,
                    candidate.Nsfw,
                    candidate.Humor,
                    candidate.Epilepsy));
            }

            foreach (var candidate in official)
            {
                var id = Guid.NewGuid().ToString("N");
                officialLookup[id] = candidate;
                officialMapped.Add(new SteamArtworkOfficialAsset(
                    id, candidate.Label, candidate.Url, candidate.Width, candidate.Height, candidate.Extension));
            }

            var messages = fetched.Failures.Concat(fetched.Skipped).ToArray();
            var reasons = string.Join("  ", messages);
            // The page shows an error in place of the notice, so when nobody answered the reasons
            // travel with the error: "no provider answered" alone does not say which one or why.
            var error = !fetched.NoProviderAnswered
                ? null
                : messages.Length == 0
                    ? "No configured artwork provider answered."
                    : "No configured artwork provider answered.  " + reasons;
            if (!append && mapped.Count == 0)
            {
                var source = selected is null
                    ? "its Steam app id"
                    : $"{selected.ProviderId} game {selected.Id}";
                Log.Info($"Steam artwork page: no {tab} artwork for app {appId} from {source}"
                         + (messages.Length == 0 ? "." : $": {reasons}"));
            }

            var managed = ArtworkManagedSlots.Read(appId);
            lock (_gate)
            {
                if (_state is null || generation != _generation || _state.AppId != appId || _state.ActiveTab != tab)
                {
                    return;
                }

                _candidates = lookup;
                _officialCandidates = officialLookup;
                _state = _state with
                {
                    AppName = name,
                    Assets = append ? [.. _state.Assets, .. mapped] : mapped,
                    OfficialAssets = officialMapped,
                    ManagedSlots = managed,
                    Loading = false,
                    HasMore = fetched.HasMore,
                    Page = page,
                    Notice = messages.Length == 0 || error is not null ? null : reasons,
                    Error = error,
                    Revision = ++_revision
                };
            }

            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: load failed: {ex.Message}");
            lock (_gate)
            {
                if (_state is null || generation != _generation)
                {
                    return;
                }

                _state = _state with { Loading = false, Error = ex.Message, Revision = ++_revision };
            }

            Changed?.Invoke();
        }
    }

    /// <summary>Publishes a write's outcome once it finishes; a write its cancellation ended publishes nothing.</summary>
    /// <param name="appId">The game the write was for.</param>
    /// <param name="write">The write in flight.</param>
    private async Task PublishWriteAsync(uint appId, Task<(string Message, bool Failed)?> write)
    {
        if (await write.ConfigureAwait(false) is { } outcome)
        {
            PublishOutcome(appId, outcome.Message, outcome.Failed);
        }
    }

    private void PublishOutcome(uint appId, string message, bool error = false)
    {
        var managed = ArtworkManagedSlots.Read(appId);
        if (!error)
        {
            _owner.BroadcastArtwork(this, appId, message, managed);
        }

        lock (_gate)
        {
            if (_state is null || _state.AppId != appId)
            {
                return;
            }

            _state = _state with
            {
                ManagedSlots = managed,
                Notice = error ? null : message,
                Error = error ? message : null,
                Revision = ++_revision
            };
        }

        Changed?.Invoke();
    }

    private SteamArtworkBrowserFilter FilterFor(string tab)
    {
        return _filters.TryGetValue(tab, out var filter) ? filter : ArtworkBrowserTabs.DefaultFilter(tab);
    }

    private void PersistSelectedGame(uint appId, ArtworkGameMatch? match)
    {
        if (!SteamApps.IsShortcutAppId(appId))
        {
            return;
        }

        _store.SaveGame(appId, match);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Projects WSGM's artwork providers into the toolkit's Steam-native browser.</summary>
internal sealed class SteamArtworkBrowserSource : ISteamArtworkBrowserBackend, IDisposable
{
    private static readonly SteamArtworkBrowserTab[] AllTabs =
    [
        new("grid", "Capsule"),
        new("wide", "Wide Capsule"),
        new("hero", "Hero"),
        new("logo", "Logo"),
        new("icon", "Icon"),
        new("manage", "Manage", true)
    ];

    private readonly Dictionary<string, SteamArtworkBrowserFilter> _filters = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private readonly Func<ArtworkConfig> _readConfiguration;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ArtworkStateStore _store;

    /// <summary>What a caller said a game is called, for games Steam's list does not name yet.</summary>
    /// <remarks>
    ///     The Game Library opens this page for a shortcut it created seconds earlier, which Steam's
    ///     library listing does not carry yet. Without a hint the page names it "App N" and searches
    ///     SteamGridDB for exactly that.
    /// </remarks>
    private readonly Dictionary<uint, string> _titleHints = [];

    private Dictionary<string, ArtworkCandidate> _candidates = new(StringComparer.Ordinal);
    private long _generation;
    private CancellationTokenSource? _load;
    private Dictionary<string, ArtworkGameMatch> _matches = new(StringComparer.Ordinal);
    private Dictionary<string, SgdbOfficialAsset> _officialCandidates = new(StringComparer.Ordinal);
    private ArtworkGameMatch? _selectedMatch;
    private SteamArtworkBrowserState? _state;

    internal SteamArtworkBrowserSource(
        Func<ArtworkConfig> readConfiguration,
        ArtworkStateStore store)
    {
        _readConfiguration = readConfiguration;
        _store = store;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _load?.Cancel();
        _load?.Dispose();
        _shutdown.Dispose();
    }

    public Task<SteamUiCommandResult> SelectTabAsync(string tab, CancellationToken cancellationToken)
    {
        if (!ConfiguredTabs(_readConfiguration()).Any(candidate => candidate.Id == tab))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That artwork tab is not available."));
        }

        uint appId;
        CancellationTokenSource load;
        long generation;
        lock (_gate)
        {
            if (_state is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "No artwork page is open."));
            }

            appId = _state.AppId;
            _load?.Cancel();
            _load?.Dispose();
            _load = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
            load = _load;
            generation = ++_generation;
            _candidates = new Dictionary<string, ArtworkCandidate>(StringComparer.Ordinal);
            _officialCandidates = new Dictionary<string, SgdbOfficialAsset>(StringComparer.Ordinal);
            _state = _state with
            {
                ActiveTab = tab,
                Assets = [],
                OfficialAssets = [],
                ManagedSlots = Managed(appId),
                Filter = FilterFor(tab),
                Page = 0,
                HasMore = false,
                Loading = tab != "manage",
                Error = null,
                Notice = null,
                Revision = generation
            };
        }

        Changed?.Invoke();
        if (tab != "manage")
        {
            _ = LoadAsync(appId, tab, 0, false, generation, load.Token);
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> ApplyAsync(string id, CancellationToken cancellationToken)
    {
        ArtworkCandidate candidate;
        uint appId;
        ArtworkAsset asset;
        lock (_gate)
        {
            if (_state is null || !_candidates.TryGetValue(id, out candidate!))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That artwork result is no longer current."));
            }

            appId = _state.AppId;
            if (!TryAsset(_state.ActiveTab, out asset))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That artwork slot cannot be changed."));
            }
        }

        PublishOutcome(appId, "Applying artwork…");
        _ = ApplyCandidateCoreAsync(appId, asset, candidate, _shutdown.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> ApplyOfficialAsync(string id, CancellationToken cancellationToken)
    {
        SgdbOfficialAsset candidate;
        uint appId;
        ArtworkAsset asset;
        lock (_gate)
        {
            if (_state is null || !_officialCandidates.TryGetValue(id, out candidate!))
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "That official artwork selection is no longer current."));
            }

            appId = _state.AppId;
            if (!TryAsset(_state.ActiveTab, out asset))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That artwork slot cannot be changed."));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        PublishOutcome(appId, "Applying official artwork…");
        _ = ApplyCandidateCoreAsync(appId, asset, new ArtworkCandidate(
            candidate.Url,
            candidate.Url,
            candidate.Width,
            candidate.Height,
            candidate.Extension,
            "steam",
            "Steam",
            Style: "official"), _shutdown.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> ClearAsync(string tab, CancellationToken cancellationToken)
    {
        if (!TryAsset(tab, out var asset))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That artwork slot cannot be reset."));
        }

        uint appId;
        lock (_gate)
        {
            if (_state is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "No artwork page is open."));
            }

            appId = _state.AppId;
        }

        cancellationToken.ThrowIfCancellationRequested();
        PublishOutcome(appId, "Resetting artwork…");
        _ = ClearCoreAsync(appId, asset, _shutdown.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken)
    {
        uint appId;
        string tab;
        int page;
        long generation;
        CancellationTokenSource load;
        lock (_gate)
        {
            if (_state is not { HasMore: true, Loading: false } state || !TryAsset(state.ActiveTab, out _))
            {
                return Task.FromResult(new SteamUiCommandResult(false,
                    "The current artwork providers returned all available results."));
            }

            appId = state.AppId;
            tab = state.ActiveTab;
            page = state.Page + 1;
            _load?.Cancel();
            _load?.Dispose();
            _load = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            load = _load;
            generation = ++_generation;
            _state = state with { Loading = true, Page = page, Revision = generation };
        }

        Changed?.Invoke();
        _ = LoadAsync(appId, tab, page, true, generation, load.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> ApplyLocalAsync(
        string tab, string name, string base64, CancellationToken cancellationToken)
    {
        if (!TryAsset(tab, out var asset))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That artwork slot cannot be changed."));
        }

        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (extension is not ("png" or "jpg" or "jpeg" or "webp" or "ico"))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Choose a PNG, JPEG, WebP, or ICO image."));
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "The selected image could not be read."));
        }

        if (bytes.Length is 0 or > 16 * 1024 * 1024)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "The selected image must be smaller than 16 MB."));
        }

        uint appId;
        lock (_gate)
        {
            if (_state is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "No artwork page is open."));
            }

            appId = _state.AppId;
        }

        cancellationToken.ThrowIfCancellationRequested();
        PublishOutcome(appId, "Applying local artwork…");
        _ = ApplyBytesCoreAsync(appId, asset, bytes, extension, _shutdown.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> SetFilterAsync(
        SteamArtworkBrowserFilter filter, CancellationToken cancellationToken)
    {
        uint appId;
        string tab;
        long generation;
        CancellationTokenSource load;
        lock (_gate)
        {
            if (_state is null || !TryAsset(_state.ActiveTab, out _))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "No artwork result tab is open."));
            }

            appId = _state.AppId;
            tab = _state.ActiveTab;
            _load?.Cancel();
            _load?.Dispose();
            _load = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            load = _load;
            generation = ++_generation;
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
                Revision = generation
            };
        }

        Changed?.Invoke();
        PersistFilter(tab, filter);
        _ = LoadAsync(appId, tab, 0, false, generation, load.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> SearchGamesAsync(
        string term, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = SearchGamesObservedAsync(term, _shutdown.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> SelectGameAsync(string? id, CancellationToken cancellationToken)
    {
        uint appId;
        string tab;
        long generation;
        CancellationTokenSource load;
        lock (_gate)
        {
            if (_state is null || !TryAsset(_state.ActiveTab, out _))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "No artwork result tab is open."));
            }

            if (id is not null && !_matches.TryGetValue(id, out _selectedMatch))
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That game match is no longer current."));
            }

            if (id is null)
            {
                _selectedMatch = null;
            }

            appId = _state.AppId;
            tab = _state.ActiveTab;
            _load?.Cancel();
            _load?.Dispose();
            _load = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            load = _load;
            generation = ++_generation;
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
                Revision = generation
            };
        }

        Changed?.Invoke();
        PersistSelectedGame(appId, _selectedMatch);
        _ = LoadAsync(appId, tab, 0, false, generation, load.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> SaveLogoPositionAsync(
        string anchor, int width, int height, CancellationToken cancellationToken)
    {
        uint appId;
        lock (_gate)
        {
            if (_state is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "No artwork page is open."));
            }

            appId = _state.AppId;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = SaveLogoPositionCoreAsync(appId, new SteamLogoPosition(anchor, width, height), _shutdown.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> ResetLogoPositionAsync(CancellationToken cancellationToken)
    {
        uint appId;
        lock (_gate)
        {
            if (_state is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "No artwork page is open."));
            }

            appId = _state.AppId;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = ResetLogoPositionCoreAsync(appId, _shutdown.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    internal event Action? Changed;

    internal SteamArtworkBrowserState? ReadState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    internal void ConfigurationChanged()
    {
        // The key may be the thing that changed, and a response fetched with the old one — or the
        // refusal it earned — must not be reused to answer for the new one.
        SteamGridDb.ResetCache();

        uint? appId;
        lock (_gate)
        {
            appId = _state?.AppId;
        }

        if (appId is { } current)
        {
            _ = OpenAsync(current, _shutdown.Token);
        }
    }

    internal Task<SteamUiCommandResult> OpenAsync(uint appId, CancellationToken cancellationToken)
    {
        return OpenAsync(appId, null, cancellationToken);
    }

    /// <summary>Opens the page for one game, naming it when Steam cannot yet.</summary>
    /// <param name="appId">The game's Steam app id.</param>
    /// <param name="titleHint">What the caller calls it, or null to rely on Steam's list.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the page could be opened.</returns>
    internal Task<SteamUiCommandResult> OpenAsync(uint appId, string? titleHint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (appId == 0)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Steam did not identify the selected game."));
        }

        CancellationTokenSource load;
        long generation;
        var savedLink = _store.FindGame(appId);
        var configuration = _readConfiguration();
        var tabs = ConfiguredTabs(configuration);
        var initialTab = tabs.Any(tab => tab.Id == configuration.DefaultTab)
            ? configuration.DefaultTab
            : tabs[0].Id;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(titleHint))
            {
                _titleHints[appId] = titleHint.Trim();
            }

            _load?.Cancel();
            _load?.Dispose();
            _load = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            load = _load;
            generation = ++_generation;
            _candidates = new Dictionary<string, ArtworkCandidate>(StringComparer.Ordinal);
            _officialCandidates = new Dictionary<string, SgdbOfficialAsset>(StringComparer.Ordinal);
            _matches = new Dictionary<string, ArtworkGameMatch>(StringComparer.Ordinal);
            _filters.Clear();
            foreach (var savedFilter in _store.ReadFilters())
            {
                _filters[savedFilter.Tab] = FromConfig(savedFilter);
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
                Managed(appId),
                FilterFor(initialTab),
                [],
                _selectedMatch?.Name,
                Loading: true,
                Revision: generation);
        }

        Changed?.Invoke();
        if (initialTab != "manage")
        {
            _ = LoadAsync(appId, initialTab, 0, false, generation, load.Token);
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private async Task SearchGamesCoreAsync(string term, CancellationToken cancellationToken)
    {
        var config = _readConfiguration();
        var matches = await ArtworkSearch.SearchGamesAsync(term, config, cancellationToken).ConfigureAwait(false);
        var mapped = new List<SteamArtworkBrowserGame>(matches.Count);
        var lookup = new Dictionary<string, ArtworkGameMatch>(StringComparer.Ordinal);
        foreach (var match in matches.Take(40))
        {
            var id = Guid.NewGuid().ToString("N");
            lookup[id] = match;
            mapped.Add(new SteamArtworkBrowserGame(
                id, match.Name, ArtworkSearch.Find(match.ProviderId)?.DisplayName ?? match.ProviderId));
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
                Revision = ++_generation
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
                    _state = _state with { Error = ex.Message, Revision = ++_generation };
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
            var gamesTask = SteamLibraryData.ListGamesAsync(cancellationToken);
            var config = _readConfiguration();
            if (!TryAsset(tab, out var asset))
            {
                return;
            }

            ArtworkGameMatch? selected;
            SteamArtworkBrowserFilter filter;
            lock (_gate)
            {
                selected = _selectedMatch;
                filter = _state?.Filter ?? DefaultFilter(tab);
            }

            var games = await gamesTask.ConfigureAwait(false);
            string fallback;
            lock (_gate)
            {
                fallback = FallbackName(appId);
            }

            var name = games.FirstOrDefault(game => unchecked((uint)game.AppId) == appId)?.Name ?? fallback;
            if (selected is null && SteamApps.IsShortcutAppId(appId) && page == 0)
            {
                selected = (await ArtworkSearch.SearchGamesAsync(name, config, cancellationToken)
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
            var result = selected is null
                ? ArtworkSearch.GetAssetsForSteamAppAsync(asset, appId, config, query, cancellationToken)
                : ArtworkSearch.GetAssetsForMatchAsync(asset, selected, config, query, cancellationToken);
            var fetched = await result.ConfigureAwait(false);
            var official = await LoadOfficialAssetsAsync(asset, appId, selected, config, cancellationToken)
                .ConfigureAwait(false);
            var candidates = fetched.Candidates
                .Where(candidate => candidate.Width == 0
                                    || ArtworkImageHeader.IsWithinLimits(candidate.Width, candidate.Height))
                .Take(50)
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
                    ManagedSlots = Managed(appId),
                    Loading = false,
                    HasMore = candidates.Length == 50 && mapped.Count > 0,
                    Page = page,
                    Notice = messages.Length == 0 ? null : string.Join("  ", messages),
                    Error = fetched.NoProviderAnswered ? "No configured artwork provider answered." : null,
                    Revision = generation
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

                _state = _state with { Loading = false, Error = ex.Message, Revision = generation };
            }

            Changed?.Invoke();
        }
    }

    private async Task ApplyCandidateCoreAsync(
        uint appId, ArtworkAsset asset, ArtworkCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await SteamGridDb.DownloadImageAsync(candidate.Url, cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                PublishOutcome(appId, "The artwork download was empty.", true);
                return;
            }

            await ApplyBytesCoreAsync(appId, asset, bytes, candidate.Extension, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: apply failed: {ex.Message}");
            PublishOutcome(appId, ex.Message, true);
        }
    }

    private static async Task<IReadOnlyList<SgdbOfficialAsset>> LoadOfficialAssetsAsync(
        ArtworkAsset asset,
        uint appId,
        ArtworkGameMatch? selected,
        ArtworkConfig config,
        CancellationToken cancellationToken)
    {
        var key = SteamGridDb.ResolveKey(config);
        if (key.Length == 0)
        {
            return [];
        }

        try
        {
            if (selected is { ProviderId: "steamgriddb" }
                && int.TryParse(selected.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var gameId))
            {
                return await SteamGridDb.GetOfficialAssetsForGameAsync(asset, gameId, key, cancellationToken)
                    .ConfigureAwait(false);
            }

            return SteamApps.IsShortcutAppId(appId)
                ? []
                : await SteamGridDb.GetOfficialAssetsForSteamAppAsync(asset, appId, key, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: official artwork lookup failed: {ex.Message}");
            return [];
        }
    }

    private async Task ApplyBytesCoreAsync(
        uint appId, ArtworkAsset asset, byte[] bytes, string extension, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SteamArtwork.ApplyAsync(appId, asset, bytes, extension, cancellationToken)
                .ConfigureAwait(false);
            PublishOutcome(appId, result.Detail, !result.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: apply failed: {ex.Message}");
            PublishOutcome(appId, ex.Message, true);
        }
    }

    private async Task ClearCoreAsync(
        uint appId, ArtworkAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SteamArtwork.ClearAsync(appId, asset, cancellationToken).ConfigureAwait(false);
            PublishOutcome(appId, result.Detail, !result.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: reset failed: {ex.Message}");
            PublishOutcome(appId, ex.Message, true);
        }
    }

    private async Task SaveLogoPositionCoreAsync(
        uint appId, SteamLogoPosition position, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SteamApps.SaveLogoPositionAsync(appId, position, cancellationToken)
                .ConfigureAwait(false);
            PublishOutcome(appId,
                result.Accepted ? "Logo position saved." : result.Error ?? "Steam rejected the logo position.",
                !result.Accepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishOutcome(appId, ex.Message, true);
        }
    }

    private async Task ResetLogoPositionCoreAsync(uint appId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SteamApps.ClearLogoPositionAsync(appId, cancellationToken).ConfigureAwait(false);
            PublishOutcome(appId,
                result.Accepted ? "Logo position reset." : result.Error ?? "Steam rejected the logo reset.",
                !result.Accepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishOutcome(appId, ex.Message, true);
        }
    }

    private void PublishOutcome(uint appId, string message, bool error = false)
    {
        lock (_gate)
        {
            if (_state is null || _state.AppId != appId)
            {
                return;
            }

            _state = _state with
            {
                ManagedSlots = Managed(appId),
                Notice = error ? null : message,
                Error = error ? message : null,
                Revision = ++_generation
            };
        }

        Changed?.Invoke();
    }

    private static SteamArtworkManagedSlot[] Managed(uint appId)
    {
        return
        [
            ManagedSlot(appId, ArtworkAsset.Grid, "grid", "Capsule"),
            ManagedSlot(appId, ArtworkAsset.Wide, "wide", "Wide Capsule"),
            ManagedSlot(appId, ArtworkAsset.Hero, "hero", "Hero"),
            ManagedSlot(appId, ArtworkAsset.Logo, "logo", "Logo"),
            ManagedSlot(appId, ArtworkAsset.Icon, "icon", "Icon")
        ];
    }

    private static SteamArtworkManagedSlot ManagedSlot(uint appId, ArtworkAsset asset, string id, string label)
    {
        var path = SteamArtwork.FindCustomArtFile(appId, asset);
        return new SteamArtworkManagedSlot(id, label, path is not null, DataUrl(path));
    }

    private static string? DataUrl(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 16 * 1024 * 1024)
            {
                return null;
            }

            var extension = info.Extension.ToLowerInvariant();
            var mime = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".ico" => "image/vnd.microsoft.icon",
                ".webp" => "image/webp",
                _ => "image/png"
            };
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: current-art preview failed: {ex.Message}");
            return null;
        }
    }

    private static SteamArtworkBrowserFilter DefaultFilter(string tab)
    {
        var styles = tab switch
        {
            "hero" => new[] { "alternate", "blurred", "material" },
            "logo" => ["official", "white", "black", "custom"],
            "icon" => ["official", "custom"],
            _ => ["alternate", "white_logo", "no_logo", "blurred", "material"]
        };
        var dimensions = tab switch
        {
            "grid" => new[] { "600x900", "342x482", "660x930" },
            "wide" => ["460x215", "920x430"],
            "hero" => ["1920x620", "3840x1240", "1600x650"],
            "icon" => new[]
            {
                "1024", "768", "512", "310", "256", "194", "192", "180", "160", "152", "150",
                "144", "128", "120", "114", "100", "96", "90", "80", "76", "72", "64", "60", "57",
                "56", "54", "48", "40", "35", "32", "28", "24", "20", "16", "14", "10", "8"
            },
            _ => []
        };
        var mimes = tab switch
        {
            "logo" => new[] { "image/png", "image/webp" },
            "icon" => ["image/png", "image/vnd.microsoft.icon"],
            _ => ["image/png", "image/jpeg", "image/webp"]
        };
        return new SteamArtworkBrowserFilter(styles, dimensions, mimes);
    }

    private static SteamArtworkBrowserFilter FromConfig(ArtworkSavedFilter filter)
    {
        return new SteamArtworkBrowserFilter(
            filter.Styles,
            filter.Dimensions,
            filter.Mimes,
            filter.Static,
            filter.Animated,
            filter.Adult,
            filter.Humor,
            filter.Epilepsy,
            filter.Untagged);
    }

    private void PersistFilter(string tab, SteamArtworkBrowserFilter filter)
    {
        _store.SaveFilter(tab, filter);
    }

    private SteamArtworkBrowserFilter FilterFor(string tab)
    {
        return _filters.TryGetValue(tab, out var filter) ? filter : DefaultFilter(tab);
    }

    private void PersistSelectedGame(uint appId, ArtworkGameMatch? match)
    {
        if (!SteamApps.IsShortcutAppId(appId))
        {
            return;
        }

        _store.SaveGame(appId, match);
    }

    private static bool TryAsset(string tab, out ArtworkAsset asset)
    {
        asset = tab switch
        {
            "grid" => ArtworkAsset.Grid,
            "wide" => ArtworkAsset.Wide,
            "hero" => ArtworkAsset.Hero,
            "logo" => ArtworkAsset.Logo,
            "icon" => ArtworkAsset.Icon,
            _ => default
        };
        return tab is "grid" or "wide" or "hero" or "logo" or "icon";
    }

    private static SteamArtworkBrowserTab[] ConfiguredTabs(ArtworkConfig configuration)
    {
        var visibility = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["grid"] = configuration.ShowGrid,
            ["wide"] = configuration.ShowWide,
            ["hero"] = configuration.ShowHero,
            ["logo"] = configuration.ShowLogo,
            ["icon"] = configuration.ShowIcon,
            ["manage"] = configuration.ShowManage
        };
        var lookup = AllTabs.ToDictionary(tab => tab.Id, StringComparer.Ordinal);
        SteamArtworkBrowserTab[] tabs =
        [
            .. configuration.TabOrder.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(id => visibility.GetValueOrDefault(id) && lookup.ContainsKey(id))
                .Distinct(StringComparer.Ordinal)
                .Select(id => lookup[id])
        ];
        return tabs.Length == 0 ? AllTabs : tabs;
    }
}

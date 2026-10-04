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

/// <summary>Projects WSGM's artwork providers into the toolkit's Steam-native browser.</summary>
internal sealed class SteamArtworkBrowserSource : IArtworkBrowseSession
{
    private static readonly SteamArtworkBrowserTab[] AllTabs =
    [
        .. ArtworkAssetNames.Ordered.Select(slot => new SteamArtworkBrowserTab(slot.Id, slot.Label)),
        new("manage", "Manage", true)
    ];

    /// <summary>A 1×1 fully transparent PNG: what "Invisible" applies to a slot.</summary>
    private static readonly byte[] TransparentPixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVQYV2NgYAAAAAMAAWgmWQ0AAAAASUVORK5CYII=");

    private readonly List<SteamArtworkBrowserSource> _contexts = [];

    private readonly Dictionary<string, SteamArtworkBrowserFilter> _filters = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private readonly Func<ArtworkConfig> _readConfiguration;
    private readonly SteamGridDbProvider? _steamGridDb;
    private readonly IReadOnlyList<IArtworkProvider> _providers;
    private string _tabSignature;
    private string _providerSignature;
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

    private Dictionary<string, ArtworkCandidate> _candidates = new(StringComparer.Ordinal);
    private int _disposeStarted;
    private long _generation;
    private CancellationTokenSource? _load;
    private Dictionary<string, ArtworkGameMatch> _matches = new(StringComparer.Ordinal);
    private Dictionary<string, SgdbOfficialAsset> _officialCandidates = new(StringComparer.Ordinal);
    private SteamArtworkBrowserSource? _parent;
    private long _revision;
    private ArtworkGameMatch? _selectedMatch;
    private SteamArtworkBrowserState? _state;

    internal SteamArtworkBrowserSource(
        Func<ArtworkConfig> readConfiguration,
        ArtworkStateStore store, SteamClient steam, IReadOnlyList<IArtworkProvider>? providers = null)
    {
        _readConfiguration = readConfiguration;
        _steam = steam ?? throw new ArgumentNullException(nameof(steam));
        _providers = providers ?? ArtworkSearch.Providers;
        _steamGridDb = _providers.OfType<SteamGridDbProvider>().FirstOrDefault();
        var configuration = readConfiguration();
        _tabSignature = configuration.TabSignature();
        _providerSignature = configuration.ProviderSignature();
        _store = store;
    }

    public event Action? Changed;

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        SteamArtworkBrowserSource[] contexts;
        lock (_gate)
        {
            contexts = [.. _contexts];
            _contexts.Clear();
        }

        foreach (var context in contexts)
        {
            context.Dispose();
        }

        if (_parent is not null)
        {
            lock (_parent._gate)
            {
                _parent._contexts.Remove(this);
            }
        }

        _shutdown.Cancel();
        lock (_gate)
        {
            _load?.Cancel();
            _state = null;
        }
    }

    public Task<SteamUiCommandResult> SelectTabAsync(string tab, CancellationToken cancellationToken)
    {
        if (!ConfiguredTabs(_readConfiguration()).Any(candidate => candidate.Id == tab))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That artwork tab is not available."));
        }

        uint appId;
        CancellationToken loadToken;
        long generation;
        lock (_gate)
        {
            if (_state is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "No artwork page is open."));
            }

            appId = _state.AppId;
        }

        var managed = Managed(appId);
        lock (_gate)
        {
            if (_state?.AppId != appId)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "The artwork page changed."));
            }

            _load?.Cancel();
            _load?.Dispose();
            _load = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            loadToken = _load.Token;
            generation = ++_generation;
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
                Loading = tab != "manage",
                Error = null,
                Notice = null,
                Revision = ++_revision
            };
        }

        Changed?.Invoke();
        if (tab != "manage")
        {
            _ = LoadAsync(appId, tab, 0, false, generation, loadToken);
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
        CancellationToken loadToken;
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
            loadToken = _load.Token;
            generation = ++_generation;
            _state = state with { Loading = true, Page = page, Revision = ++_revision };
        }

        Changed?.Invoke();
        _ = LoadAsync(appId, tab, page, true, generation, loadToken);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> ApplyLocalAsync(string tab, string path, CancellationToken cancellationToken)
    {
        if (!TryAsset(tab, out var asset))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That artwork slot cannot be changed."));
        }

        // A local file on this machine, as Steam's file picker lists them. A network path would make
        // WSGM open a connection on the page's say-so.
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
                                             || Path.GetExtension(path).ToLowerInvariant() is not
                                                 (".png" or ".jpg" or ".jpeg" or ".webp" or ".ico"))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Choose a PNG, JPEG, WebP, or ICO image."));
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
        _ = ApplyLocalCoreAsync(appId, asset, path, _shutdown.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> ApplyInvisibleAsync(string tab, CancellationToken cancellationToken)
    {
        if (!TryAsset(tab, out var asset) || asset is ArtworkAsset.Icon)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That artwork slot cannot be made invisible."));
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
        PublishOutcome(appId, "Applying invisible artwork…");
        _ = ApplyBytesCoreAsync(appId, asset, [.. TransparentPixel], _shutdown.Token);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> SetFilterAsync(
        SteamArtworkBrowserFilter filter, CancellationToken cancellationToken)
    {
        uint appId;
        string tab;
        long generation;
        CancellationToken loadToken;
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
            loadToken = _load.Token;
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
                Revision = ++_revision
            };
        }

        Changed?.Invoke();
        PersistFilter(tab, filter);
        _ = LoadAsync(appId, tab, 0, false, generation, loadToken);
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
        CancellationToken loadToken;
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
            loadToken = _load.Token;
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
                Revision = ++_revision
            };
        }

        Changed?.Invoke();
        PersistSelectedGame(appId, _selectedMatch);
        _ = LoadAsync(appId, tab, 0, false, generation, loadToken);
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

    public SteamArtworkBrowserState? ReadState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public Task<OverlayLibraryResult> ReadGamesAsync()
    {
        return OverlayLibraryLookup.ReadAsync(_steam, _shutdown.Token);
    }

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
            return Task.FromResult(new SteamUiCommandResult(false, "Steam did not identify the selected game."));
        }

        CancellationToken loadToken;
        long generation;
        var savedLink = _store.FindGame(appId);
        var configuration = _readConfiguration();
        var tabs = ConfiguredTabs(configuration);
        var initialTab = tabs.Any(tab => tab.Id == configuration.DefaultTab)
            ? configuration.DefaultTab
            : tabs[0].Id;
        var managed = Managed(appId);
        var savedFilters = _store.ReadFilters();
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(titleHint))
            {
                _titleHints[appId] = titleHint.Trim();
            }

            _load?.Cancel();
            _load?.Dispose();
            _load = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            loadToken = _load.Token;
            generation = ++_generation;
            _candidates = new Dictionary<string, ArtworkCandidate>(StringComparer.Ordinal);
            _officialCandidates = new Dictionary<string, SgdbOfficialAsset>(StringComparer.Ordinal);
            _matches = new Dictionary<string, ArtworkGameMatch>(StringComparer.Ordinal);
            _filters.Clear();
            foreach (var savedFilter in savedFilters)
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
        if (initialTab != "manage")
        {
            _ = LoadAsync(appId, initialTab, 0, false, generation, loadToken);
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    internal SteamArtworkBrowserSource CreateViewSession()
    {
        var context = new SteamArtworkBrowserSource(_readConfiguration, _store, _steam, _providers) { _parent = this };
        lock (_gate)
        {
            _contexts.Add(context);
        }

        return context;
    }

    private void BroadcastArtwork(uint appId, string message, SteamArtworkManagedSlot[] managed)
    {
        var owner = _parent ?? this;
        SteamArtworkBrowserSource[] contexts;
        lock (owner._gate)
        {
            contexts = [owner, .. owner._contexts];
        }

        foreach (var context in contexts)
        {
            if (ReferenceEquals(context, this))
            {
                continue;
            }

            lock (context._gate)
            {
                if (context._state?.AppId != appId)
                {
                    continue;
                }

                context._state = context._state with
                {
                    ManagedSlots = managed, Notice = message, Error = null, Revision = ++context._revision
                };
            }

            context.Changed?.Invoke();
        }
    }

    private async Task ApplyLocalCoreAsync(
        uint appId, ArtworkAsset asset, string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                PublishOutcome(appId, "The selected image no longer exists.", true);
                return;
            }

            if (info.Length is 0 or > ArtworkDownload.MaximumBytes)
            {
                PublishOutcome(appId, "The selected image must be smaller than 16 MB.", true);
                return;
            }

            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Steam artwork page: local image could not be read: {ex.Message}");
            PublishOutcome(appId, "The selected image could not be read.", true);
            return;
        }

        await ApplyBytesCoreAsync(appId, asset, bytes, cancellationToken).ConfigureAwait(false);
    }

    internal void ConfigurationChanged()
    {
        var configuration = _readConfiguration();
        var tabSignature = configuration.TabSignature();
        var providerSignature = configuration.ProviderSignature();
        SteamArtworkBrowserSource[] contexts;
        bool credentialsChanged;
        lock (_gate)
        {
            if (_tabSignature == tabSignature && _providerSignature == providerSignature)
            {
                return;
            }

            _tabSignature = tabSignature;
            credentialsChanged = _providerSignature != providerSignature;
            _providerSignature = providerSignature;
            contexts = [.. _contexts];
        }

        if (_parent is null && credentialsChanged)
        {
            foreach (var provider in _providers)
            {
                provider.ResetCache();
            }
        }

        foreach (var context in contexts)
        {
            context.ConfigurationChanged();
        }

        uint? appId;
        lock (_gate)
        {
            appId = _state?.AppId;
        }

        if (appId is { } current)
        {
            _ = Task.Run(() => OpenAsync(current, _shutdown.Token));
        }
    }

    internal Task<SteamUiCommandResult> OpenAsync(uint appId, CancellationToken cancellationToken)
    {
        return OpenAsync(appId, null, cancellationToken);
    }

    private async Task SearchGamesCoreAsync(string term, CancellationToken cancellationToken)
    {
        var config = _readConfiguration();
        var matches = await ArtworkSearch.SearchGamesAsync(term, config, cancellationToken, _providers).ConfigureAwait(false);
        var mapped = new List<SteamArtworkBrowserGame>(matches.Count);
        var lookup = new Dictionary<string, ArtworkGameMatch>(StringComparer.Ordinal);
        foreach (var match in matches)
        {
            var id = Guid.NewGuid().ToString("N");
            lookup[id] = match;
            mapped.Add(new SteamArtworkBrowserGame(
                id, match.Name, _providers.FirstOrDefault(provider => provider.Id == match.ProviderId)?.DisplayName ?? match.ProviderId));
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

            // A match kept from a provider that is now off, or has lost its credentials, would ask
            // nobody and leave the page empty without a single request. The ready providers are
            // asked instead, the way a game with no match is.
            if (selected is not null && _providers.FirstOrDefault(provider => provider.Id == selected.ProviderId)?.GetStatus(config).IsReady != true)
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
            var result = selected is null
                ? ArtworkSearch.GetAssetsForSteamAppAsync(asset, appId, config, query, cancellationToken, _providers)
                : ArtworkSearch.GetAssetsForMatchAsync(asset, selected, config, query, cancellationToken, _providers);
            var fetched = await result.ConfigureAwait(false);
            var official = await LoadOfficialAssetsAsync(asset, appId, selected, config, cancellationToken)
                .ConfigureAwait(false);
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

            var managed = Managed(appId);
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

    private async Task ApplyCandidateCoreAsync(
        uint appId, ArtworkAsset asset, ArtworkCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SteamArtwork
                .ApplyFromUrlAsync(_steam, appId, asset, candidate.Url, _readConfiguration(), cancellationToken)
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

    private async Task<IReadOnlyList<SgdbOfficialAsset>> LoadOfficialAssetsAsync(
        ArtworkAsset asset,
        uint appId,
        ArtworkGameMatch? selected,
        ArtworkConfig config,
        CancellationToken cancellationToken)
    {
        var key = SteamGridDbProvider.ResolveKey(config);
        if (key.Length == 0 || _steamGridDb is null)
        {
            return [];
        }

        try
        {
            if (selected is { ProviderId: "steamgriddb" }
                && int.TryParse(selected.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var gameId))
            {
                return await _steamGridDb.GetOfficialAssetsForGameAsync(asset, gameId, key, cancellationToken)
                    .ConfigureAwait(false);
            }

            return SteamApps.IsShortcutAppId(appId)
                ? []
                : await _steamGridDb.GetOfficialAssetsForSteamAppAsync(asset, appId, key, cancellationToken)
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
        uint appId, ArtworkAsset asset, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SteamArtwork.ApplyAsync(_steam, appId, asset, bytes, cancellationToken)
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
            var result = await SteamArtwork.ClearAsync(_steam, appId, asset, cancellationToken).ConfigureAwait(false);
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
            var result = await _steam.Apps.SaveLogoPositionAsync(appId, position, cancellationToken)
                .ConfigureAwait(false);
            // A position Steam received without answering is published as written.
            var written = result.Outcome is SteamClientWriteOutcome.Applied or SteamClientWriteOutcome.Unknown;
            PublishOutcome(appId,
                written ? "Logo position saved." : result.Error ?? "Steam rejected the logo position.",
                !written);
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
            var result = await _steam.Apps.ClearLogoPositionAsync(appId, cancellationToken).ConfigureAwait(false);
            var written = result.Outcome is SteamClientWriteOutcome.Applied or SteamClientWriteOutcome.Unknown;
            PublishOutcome(appId,
                written ? "Logo position reset." : result.Error ?? "Steam rejected the logo reset.",
                !written);
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
        var managed = Managed(appId);
        if (!error)
        {
            BroadcastArtwork(appId, message, managed);
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

    private static SteamArtworkManagedSlot[] Managed(uint appId)
    {
        return ArtworkAssetNames.Ordered.Select(slot => ManagedSlot(appId, slot.Asset, slot.Id, slot.Label)).ToArray();
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

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > ArtworkDownload.MaximumBytes)
            {
                Log.Warn($"Steam artwork page: current-art preview exceeds {ArtworkDownload.MaximumBytes} bytes: {path}");
                return null;
            }

            if (stream.Length <= 0)
            {
                return null;
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var mime = SteamArtwork.ImageFormat(bytes) switch
            {
                "jpg" => "image/jpeg",
                "ico" => "image/vnd.microsoft.icon",
                "webp" => "image/webp",
                "png" => "image/png",
                _ => null
            };
            return mime is null ? null : $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
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
        return ArtworkAssetNames.TryParse(tab, out asset);
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One surface's store browsing, using its session's shared client and durable theme owner.</summary>
/// <remarks>The Steam page and the overlay each hold one, so neither surface's listing or detail moves the other's.</remarks>
internal sealed class ThemeBrowseSession : IThemeBrowseSession
{
    private readonly ThemeStoreClient _client;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly ThemeService _owner;
    private ThemeStoreDetails? _detail;
    private string? _detailError;
    private string? _detailId;
    private bool _detailLoading;
    private CancellationTokenSource? _detailWork;
    private string? _error;
    private ThemeStoreFilters? _filters;
    private List<ThemeStoreSummary> _items = [];
    private bool _loading;
    private int _page;
    private ThemeStoreQuery _query = ThemeStoreQuery.Default;
    private CancellationTokenSource? _queryWork;
    private long _revision;
    private int _total;

    /// <summary>Creates a session over the shared owner.</summary>
    /// <param name="owner">The durable theme owner, whose changes this session republishes.</param>
    /// <param name="client">The owner's store client.</param>
    /// <param name="tab">The tab this surface opens on.</param>
    /// <param name="lifetime">The owner's lifetime; its end cancels this session's reads.</param>
    internal ThemeBrowseSession(ThemeService owner, ThemeStoreClient client, string tab, CancellationToken lifetime)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _owner = owner;
        _client = client;
        Tab = tab;
        owner.Changed += Publish;
    }

    /// <summary>Advances with every change of the owner's state or this session's own.</summary>
    internal long Revision => _owner.Revision + Interlocked.Read(ref _revision);

    /// <inheritdoc />
    public string Tab { get; set; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        _owner.Changed -= Publish;
        _lifetime.Cancel();
        CancelQueries();
        _queryWork?.Dispose();
        _detailWork?.Dispose();
    }

    /// <inheritdoc />
    public SteamThemesState ReadState()
    {
        var revision = Revision;
        var shared = _owner.ReadState();
        lock (_gate)
        {
            var items = _items.Select(_owner.BrowserItem).ToArray();
            var browse = new SteamThemesBrowse(_query.Filter, _query.Order, _query.Search,
                _filters?.Filters ?? new Dictionary<string, int>(StringComparer.Ordinal),
                _filters?.Orders ?? [ThemeStoreQuery.DefaultOrder],
                items, _total, _page, _loading, _error);
            SteamThemesDetail? detail = null;
            if (_detailId is { } id)
            {
                var listing = _detail?.Summary ?? _items.FirstOrDefault(item => item.Id == id);
                var item = listing is not null
                    ? _owner.BrowserItem(listing)
                    : new SteamThemesStoreItem(id, string.Empty, "Loading…", string.Empty, string.Empty, [],
                        string.Empty, null, 0, 0, null, ThemeStates.None);
                var installed = shared.Themes.Concat(shared.Presets).Select(theme => theme.Name)
                    .ToHashSet(StringComparer.Ordinal);
                detail = new SteamThemesDetail(item, _detail?.Description ?? "",
                    _detail is null ? [] : _detail.Summary.ImageIds.Select(_client.BlobUrl).ToArray(),
                    _detail is null
                        ? []
                        : _detail.Dependencies.Select(dependency => new SteamThemesDependency(dependency.Id,
                            dependency.Name, dependency.DisplayName, installed.Contains(dependency.Name))).ToArray(),
                    _detailLoading, _detailError);
            }

            return shared with { ActiveTab = Tab, Browse = browse, Detail = detail, Revision = revision };
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> BrowseAsync(string filter, string order, string search, CancellationToken token)
    {
        lock (_gate)
        {
            _query = new ThemeStoreQuery(
                1,
                ThemeStoreQuery.Default.PerPage,
                filter.Length == 0 ? ThemeStoreQuery.AllFilter : filter,
                order.Length == 0 ? ThemeStoreQuery.DefaultOrder : order,
                search);
            _items = [];
            _page = 0;
            _total = 0;
        }

        return Fetch(false);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (_loading)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "The store is still answering."));
            }

            if (_items.Count >= _total)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Every matching theme is listed."));
            }

            _query = _query with { Page = _page + 1 };
        }

        return Fetch(true);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken token)
    {
        CancellationToken cancellation;
        lock (_gate)
        {
            _detailWork?.Cancel();
            _detailWork?.Dispose();
            // The session's read, not the command's: see Fetch.
            _detailWork = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cancellation = _detailWork.Token;
            _detailId = id;
            _detail = null;
            _detailError = null;
            _detailLoading = true;
        }

        Publish();
        _owner.Track(ReadAsync());
        return Task.FromResult(SteamUiCommandResult.Applied);

        async Task ReadAsync()
        {
            try
            {
                var detail = await _client.GetAsync(id, cancellation).ConfigureAwait(false);
                lock (_gate)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    _detail = detail;
                    _detailLoading = false;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    _detailError = ex.Message;
                    _detailLoading = false;
                }
            }

            Publish();
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken token)
    {
        CloseDetail();
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public void CancelQueries()
    {
        lock (_gate)
        {
            _queryWork?.Cancel();
            _detailWork?.Cancel();
            _loading = false;
            _detailId = null;
            _detail = null;
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken)
    {
        Tab = tab;
        CloseDetail();
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private void CloseDetail()
    {
        lock (_gate)
        {
            _detailWork?.Cancel();
            _detailId = null;
            _detail = null;
        }
    }

    private void Publish()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            Interlocked.Increment(ref _revision);
            Changed?.Invoke();
        }
    }

    // The read belongs to the session, not to the command that asked for it: the command answers at
    // once, and its token ending must not cancel the read and leave the list loading.
    private Task<SteamUiCommandResult> Fetch(bool append)
    {
        ThemeStoreQuery query;
        CancellationToken cancellation;
        lock (_gate)
        {
            _queryWork?.Cancel();
            _queryWork?.Dispose();
            _queryWork = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cancellation = _queryWork.Token;
            query = _query;
            _loading = true;
            _error = null;
        }

        Publish();
        _owner.Track(FetchAsync());
        return Task.FromResult(SteamUiCommandResult.Applied);

        async Task FetchAsync()
        {
            try
            {
                var filters = _filters;
                if (filters is null)
                {
                    try
                    {
                        filters = await _client.FiltersAsync(cancellation).ConfigureAwait(false);
                    }
                    catch (ThemeStoreException ex)
                    {
                        Log.Warn($"Themes: the store's filters could not be read: {ex.Message}");
                    }
                }

                var page = await _client.QueryAsync(query, cancellation).ConfigureAwait(false);
                lock (_gate)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    _filters = filters;
                    _items = append ? [.. _items, .. page.Items] : [.. page.Items];
                    _total = page.Total;
                    _page = query.Page;
                    _loading = false;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    _error = ex.Message;
                    _loading = false;
                }
            }

            Publish();
        }
    }
}

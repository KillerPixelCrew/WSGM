using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One surface's store browsing, using its session's shared client and durable theme owner.</summary>
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
    private int _total;

    internal ThemeBrowseSession(ThemeService owner, ThemeStoreClient client, CancellationToken lifetime)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _owner = owner;
        _client = client;
        owner.Changed += Publish;
    }

    public string Tab { get; set; } = "installed";
    public event Action? Changed;

    public void Dispose()
    {
        _owner.Changed -= Publish;
        _lifetime.Cancel();
        CancelQueries();
        _queryWork?.Dispose();
        _detailWork?.Dispose();
    }

    public SteamThemesState ReadState()
    {
        var shared = _owner.ReadState();
        lock (_gate)
        {
            var items = _items.Select(_owner.BrowserItem).ToArray();
            var browse = new SteamThemesBrowse(_query.Filter, _query.Order, _query.Search,
                _filters?.Filters ?? new Dictionary<string, int>(), _filters?.Orders ?? [ThemeStoreQuery.DefaultOrder],
                items, _total, _page, _loading, _error);
            SteamThemesDetail? detail = null;
            if (_detailId is { } id)
            {
                var listing = _detail?.Summary ?? _items.FirstOrDefault(item => item.Id == id);
                var item = listing is not null
                    ? _owner.BrowserItem(listing)
                    : new SteamThemesStoreItem(id, "", "Loading…", "", "", [], "", null, 0, 0, null, "none");
                var installed = shared.Themes.Concat(shared.Presets).Select(theme => theme.Name).ToHashSet();
                detail = new SteamThemesDetail(item, _detail?.Description ?? "",
                    _detail is null ? [] : _detail.Summary.ImageIds.Select(_client.BlobUrl).ToArray(),
                    _detail is null
                        ? []
                        : _detail.Dependencies.Select(dependency => new SteamThemesDependency(dependency.Id,
                            dependency.Name, dependency.DisplayName, installed.Contains(dependency.Name))).ToArray(),
                    _detailLoading, _detailError);
            }

            return shared with { ActiveTab = Tab, Browse = browse, Detail = detail };
        }
    }

    public Task<SteamUiCommandResult> BrowseAsync(string filter, string order, string search, CancellationToken token)
    {
        lock (_gate)
        {
            _query = new ThemeStoreQuery(1, ThemeStoreQuery.Default.PerPage, filter, order, search);
            _items = [];
            _page = 0;
            _total = 0;
        }

        return Fetch(false);
    }

    public Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (_loading)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "The store is still answering."));
            }

            _query = _query with { Page = _page + 1 };
        }

        return Fetch(true);
    }

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
        _ = ReadAsync();
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

    public Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken token)
    {
        lock (_gate)
        {
            _detailWork?.Cancel();
            _detailId = null;
            _detail = null;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

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

    public Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken)
    {
        Tab = tab;
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> InstallAsync(string id, CancellationToken cancellationToken)
    {
        return _owner.InstallAsync(id, cancellationToken);
    }

    public Task<SteamUiCommandResult> UpdateAsync(string name, CancellationToken cancellationToken)
    {
        return _owner.UpdateAsync(name, cancellationToken);
    }

    public Task<SteamUiCommandResult> UpdateAllAsync(CancellationToken cancellationToken)
    {
        return _owner.UpdateAllAsync(cancellationToken);
    }

    public Task<SteamUiCommandResult> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        return _owner.DeleteAsync(name, cancellationToken);
    }

    public Task<SteamUiCommandResult> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        return _owner.SetEnabledAsync(name, enabled, cancellationToken);
    }

    public Task<SteamUiCommandResult> SetPatchAsync(string theme, string patch, string value,
        CancellationToken cancellationToken)
    {
        return _owner.SetPatchAsync(theme, patch, value, cancellationToken);
    }

    public Task<SteamUiCommandResult> SetComponentAsync(string theme, string patch, string component, string value,
        CancellationToken cancellationToken)
    {
        return _owner.SetComponentAsync(theme, patch, component, value, cancellationToken);
    }

    public Task<SteamUiCommandResult> SetProfileAsync(string name, CancellationToken cancellationToken)
    {
        return _owner.SetProfileAsync(name, cancellationToken);
    }

    public Task<SteamUiCommandResult> CreateProfileAsync(string name, CancellationToken cancellationToken)
    {
        return _owner.CreateProfileAsync(name, cancellationToken);
    }

    public Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        return _owner.RefreshAsync(cancellationToken);
    }

    public Task<SteamUiCommandResult> SetHiddenAsync(string name, bool hidden, CancellationToken cancellationToken)
    {
        return _owner.SetHiddenAsync(name, hidden, cancellationToken);
    }

    public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken)
    {
        return _owner.DismissAsync(cancellationToken);
    }

    public Task<SteamUiCommandResult> SetSettingAsync(string key, JsonElement value,
        CancellationToken cancellationToken)
    {
        return _owner.SetSettingAsync(key, value, cancellationToken);
    }

    private void Publish()
    {
        if (!_lifetime.IsCancellationRequested)
        {
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
        _ = FetchAsync();
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
                        Log.Warn("Theme store filters: " + ex.Message);
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

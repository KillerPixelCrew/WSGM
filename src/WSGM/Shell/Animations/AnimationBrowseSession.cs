using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One surface's tab, sort, search and detail over the shared boot-movie owner.</summary>
/// <remarks>
///     The Steam page and the overlay each hold one. The sorted order is kept until the catalog, the sort or the
///     search changes; only the page shown is projected, since the repository lists thousands.
/// </remarks>
internal sealed class AnimationBrowseSession : IAnimationBrowseSession
{
    /// <summary>How many cards the Browse tab adds at a time.</summary>
    internal const int BrowsePage = 48;

    private readonly Lock _gate = new();
    private readonly AnimationService _owner;
    private readonly AnimationRepoCatalog _repository;
    private string? _detailId;
    private IReadOnlyList<AnimationListing>? _order;
    private IReadOnlyList<AnimationListing>? _orderedCatalog;
    private long _revision;
    private string _search = string.Empty;
    private int _shown = BrowsePage;
    private string _sort = SteamAnimationsSurface.Sorts[0].Id;

    /// <summary>Creates a session over the shared owner.</summary>
    /// <param name="owner">The boot-movie owner, whose changes this session republishes.</param>
    /// <param name="repository">The owner's repository listings, which this session sorts and pages.</param>
    /// <param name="tab">The tab this surface opens on.</param>
    internal AnimationBrowseSession(AnimationService owner, AnimationRepoCatalog repository, string tab)
    {
        _owner = owner;
        _repository = repository;
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
    }

    /// <inheritdoc />
    public SteamAnimationsState ReadState()
    {
        var revision = Revision;
        var shared = _owner.ReadState();
        var catalog = _repository.Listings;
        lock (_gate)
        {
            if (_order is null || !ReferenceEquals(catalog, _orderedCatalog))
            {
                if (!ReferenceEquals(catalog, _orderedCatalog))
                {
                    _shown = BrowsePage;
                }

                _order = [.. Ordered(catalog)];
                _orderedCatalog = catalog;
            }

            var downloaded = shared.Library.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var browse = new SteamAnimationsBrowse(
                _sort,
                SteamAnimationsSurface.Sorts,
                _search,
                [
                    .. _order.Take(_shown)
                        .Select(listing => AnimationRepoCatalog.Project(listing, downloaded.Contains(listing.Id)))
                ],
                _order.Count,
                catalog.Count,
                shared.Browse.Loading,
                shared.Browse.Error);
            return shared with
            {
                ActiveTab = Tab,
                Browse = browse,
                Detail = _detailId is { } id ? _owner.ReadItem(id) : null,
                Revision = revision
            };
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> BrowseAsync(string sort, string search, CancellationToken token)
    {
        sort = SteamAnimationsSurface.Sorts.Any(candidate => candidate.Id == sort)
            ? sort
            : SteamAnimationsSurface.Sorts[0].Id;
        search = search.Trim();
        bool changed;
        lock (_gate)
        {
            changed = sort != _sort || search != _search;
            _sort = sort;
            _search = search;
            if (changed)
            {
                _order = null;
                _shown = BrowsePage;
            }
        }

        // Asking again for what is shown changes nothing, so a view that asks whenever the list is
        // empty cannot redraw itself in a loop. A first fetch publishes through the owner.
        _repository.Ensure();
        if (changed)
        {
            Publish();
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> BrowseMoreAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (_order is null || _shown >= _order.Count)
            {
                return Task.FromResult(SteamUiCommandResult.Applied);
            }

            _shown += BrowsePage;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken)
    {
        Tab = tab;
        lock (_gate)
        {
            _detailId = null;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken)
    {
        if (_owner.ReadItem(id) is null)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That movie is not listed."));
        }

        lock (_gate)
        {
            _detailId = id;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _detailId = null;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>The catalog in this session's search and sort. Called under the gate.</summary>
    private IEnumerable<AnimationListing> Ordered(IReadOnlyList<AnimationListing> catalog)
    {
        IEnumerable<AnimationListing> items = catalog;
        if (_search.Length > 0)
        {
            items = items.Where(listing => listing.Name.Contains(_search, StringComparison.OrdinalIgnoreCase));
        }

        return _sort switch
        {
            "oldest" => items.OrderBy(listing => AnimationRepoCatalog.When(listing.Updated)),
            "name" => items.OrderBy(listing => listing.Name, StringComparer.OrdinalIgnoreCase),
            "popular" => items.OrderByDescending(listing => listing.Downloads),
            "liked" => items.OrderByDescending(listing => listing.Likes),
            _ => items.OrderByDescending(listing => AnimationRepoCatalog.When(listing.Updated))
        };
    }

    private void Publish()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }
}

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

internal sealed class AnimationBrowseSession : IAnimationBrowseSession
{
    private readonly AnimationService _owner;
    private string? _detailId;
    private string _search = "";
    private int _shown = AnimationService.BrowsePage;
    private string _sort = SteamAnimationsSurface.Sorts[0].Id;

    internal AnimationBrowseSession(AnimationService owner)
    {
        _owner = owner;
        owner.Changed += Publish;
    }

    public string Tab { get; set; } = "library";
    public event Action? Changed;

    public void Dispose()
    {
        _owner.Changed -= Publish;
    }

    public SteamAnimationsState ReadState()
    {
        var shared = _owner.ReadState();
        var catalog = _owner.Catalog;
        var entries = catalog.Where(item => item.Name.Contains(_search, StringComparison.OrdinalIgnoreCase));
        entries = _sort switch
        {
            "name" => entries.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "popular" => entries.OrderByDescending(item => item.Downloads),
            "liked" => entries.OrderByDescending(item => item.Likes),
            "oldest" => entries.OrderBy(item =>
                DateTimeOffset.TryParse(item.Updated, CultureInfo.InvariantCulture, out var date)
                    ? date
                    : DateTimeOffset.MinValue),
            _ => entries.OrderByDescending(item =>
                DateTimeOffset.TryParse(item.Updated, CultureInfo.InvariantCulture, out var date)
                    ? date
                    : DateTimeOffset.MinValue)
        };
        var matched = entries.ToArray();
        var local = shared.Library.Select(item => item.Id).ToHashSet();
        var browse = new SteamAnimationsBrowse(_sort, SteamAnimationsSurface.Sorts, _search,
            matched.Take(_shown).Select(item => AnimationService.ProjectListing(item, local.Contains(item.Id)))
                .ToArray(),
            matched.Length, catalog.Count, shared.Browse.Loading, shared.Browse.Error);
        return shared with
        {
            ActiveTab = Tab, Browse = browse,
            Detail = shared.Library.Concat(browse.Items).FirstOrDefault(item => item.Id == _detailId)
        };
    }

    public Task<SteamUiCommandResult> BrowseAsync(string sort, string search, CancellationToken token)
    {
        _sort = sort;
        _search = search;
        _shown = AnimationService.BrowsePage;
        Publish();
        return _owner.Catalog.Count == 0 ? _owner.RefreshAsync(token) : Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> BrowseMoreAsync(CancellationToken token)
    {
        _shown += AnimationService.BrowsePage;
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken)
    {
        Tab = tab;
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        return _owner.RefreshAsync(cancellationToken);
    }

    public Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken)
    {
        _detailId = id;
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken)
    {
        _detailId = null;
        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    public Task<SteamUiCommandResult> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        return _owner.DownloadAsync(id, cancellationToken);
    }

    public Task<SteamUiCommandResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        return _owner.DeleteAsync(id, cancellationToken);
    }

    public Task<SteamUiCommandResult> SelectAsync(string id, CancellationToken cancellationToken)
    {
        return _owner.SelectAsync(id, cancellationToken);
    }

    public Task<SteamUiCommandResult> ShuffleAsync(CancellationToken cancellationToken)
    {
        return _owner.ShuffleAsync(cancellationToken);
    }

    public Task<SteamUiCommandResult> SetShuffleOnStartAsync(bool shuffle, CancellationToken cancellationToken)
    {
        return _owner.SetShuffleOnStartAsync(shuffle, cancellationToken);
    }

    public Task<SteamUiCommandResult> SetBootVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        return _owner.SetBootVolumeAsync(volume, cancellationToken);
    }

    public Task<SteamUiCommandResult> AddFileAsync(string path, CancellationToken cancellationToken)
    {
        return _owner.AddFileAsync(path, cancellationToken);
    }

    public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken)
    {
        return _owner.DismissAsync(cancellationToken);
    }

    public string? PreviewPath(string id)
    {
        return _owner.PreviewPath(id);
    }

    private void Publish()
    {
        Changed?.Invoke();
    }
}

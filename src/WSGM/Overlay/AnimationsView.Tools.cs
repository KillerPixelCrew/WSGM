using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class AnimationsView
{
    private void SelectTab(string tab)
    {
        _browser!.Tab = tab;
        var browse = _browser.ReadState().Browse;
        if (tab == "browse" && browse.Total == 0 && !browse.Loading && browse.Error is null)
        {
            Run(token => _browser.BrowseAsync(browse.Sort, browse.Search, token));
        }

        Replace(RenderHome);
    }

    private async Task AddFileAsync()
    {
        var generation = _navigationGeneration;
        var path = await PickPathAsync(false, ".webm");
        if (path is not null && generation == _navigationGeneration && _service is not null)
        {
            Run(token => _service.AddFileAsync(path, token), "add file");
        }
    }

    private void AddMoviePreview(StackPanel body, SteamAnimationsItem item)
    {
        var source = _browser!.PreviewPath(item.Id) ?? item.PreviewUrl;
        if (source is not null)
        {
            body.Children.Add(new OverlayMediaPreview(Context, source) { Tag = "movie.preview:" + item.Id });
        }
        else
        {
            body.Children.Add(Caption("No video preview is available."));
        }
    }

    private void RenderMovieBrowse(StackPanel body, SteamAnimationsState state)
    {
        var browse = state.Browse;
        AddStatus(body, state);
        body.Children.Add(Tagged(Row("Search", browse.Search, Icons.ListLines, () => EditText("Search movies",
            browse.Search, 64,
            search => Run(token => _browser!.BrowseAsync(browse.Sort, search, token)))), "search"));
        body.Children.Add(ChoiceRow("Sort", browse.Sorts.Select(sort => (sort.Id, sort.Label)).ToArray(), browse.Sort,
            sort => Run(token => _browser!.BrowseAsync(sort, browse.Search, token))));
        body.Children.Add(Tagged(
            Row("Refresh", "Reload the repository", Icons.Restart,
                browse.Loading ? null : () => Run(_service!.RefreshAsync)), "refresh"));
        if (browse.Error is not null)
        {
            body.Children.Add(Caption(browse.Error));
        }

        var cards = new WrapPanel();
        foreach (var item in browse.Items)
        {
            var id = item.Id;
            cards.Children.Add(PreviewCard(id, item.ThumbnailUrl, item.Name, AnimationsRows.DescribeListing(item),
                () => Navigate(() => RenderDetail(id))));
        }

        body.Children.Add(cards);
        if (!browse.Loading && browse.Items.Count == 0 && browse.Error is null)
        {
            body.Children.Add(Caption("No matching movies."));
        }

        if (browse.Items.Count < browse.Matched)
        {
            body.Children.Add(Tagged(
                Row("Load more", $"{browse.Items.Count} of {browse.Matched}", Icons.ArrowDown,
                    browse.Loading ? null : () => Run(_browser!.BrowseMoreAsync)), "load-more"));
        }
    }

    private void RenderMovieSettings(StackPanel body, SteamAnimationsState state)
    {
        AddStatus(body, state);
        body.Children.Add(ToggleRow("Shuffle on start", state.Settings.ShuffleOnStart,
            enabled => Run(token => _service!.SetShuffleOnStartAsync(enabled, token))));
        body.Children.Add(new DeviceSliderRow("boot-volume", "Boot volume", "Opus movies only", 0, 100, 5,
            CapabilityUnit.Percent, state.Settings.BootVolume, true,
            volume => Run(token => _service!.SetBootVolumeAsync(volume, token))));
        body.Children.Add(Caption("Library: " + state.Settings.LibraryPath));
        body.Children.Add(Caption("Steam override: " + (state.Settings.OverridesPath ?? "Steam is not installed")));
        body.Children.Add(Caption(state.Settings.RestartNeeded
            ? "Restart Steam to see this change."
            : "The movie will be used when Steam starts."));
        body.Children.Add(Caption(
            "Steam's Startup Movie choice is set aside while a WSGM movie is selected and restored when you choose Steam's own."));
        body.Children.Add(Tagged(Row("Dismiss notice", "", Icons.Close, () => Run(_service!.DismissAsync)), "dismiss"));
    }
}

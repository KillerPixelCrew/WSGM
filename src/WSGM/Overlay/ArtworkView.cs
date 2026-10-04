using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The complete artwork workflow over a surface-scoped session of the shared owner.</summary>
public sealed class ArtworkView : ServiceSubView
{
    private UserDataContext? _context;
    private UserDataContext Context => _context ?? throw new InvalidOperationException("The media data context was not supplied.");

    internal void ConfigureContext(UserDataContext context)
    {
        _context = context;
    }

    private int _cardSize = 160;
    private IReadOnlyList<SteamLibraryApp> _games = [];
    private string? _gamesError;
    private bool _gamesRead;
    private SteamLogoPosition _logo = new("BottomLeft", 50, 50);
    private uint _logoApp;
    private string _query = "";
    private bool _readingGames;
    private int _shown = 48;
    private IArtworkBrowseSession? _source;

    /// <inheritdoc />
    protected override string LogScope => "Artwork";

    internal event Action? ReturnRequested;

    internal void Attach(SteamArtworkBrowserSource? source)
    {
        AttachSession(source?.CreateViewSession());
    }

    internal void AttachSession(IArtworkBrowseSession? source)
    {
        _source?.Dispose();
        _source = source;
        AttachSource(source);
    }

    internal async Task OpenGameAsync(uint id, string name, bool replaceHome = false)
    {
        if (_source is null)
        {
            return;
        }

        var source = _source;
        await Task.Run(() => source.OpenAsync(id, name, CancellationToken.None));
        if (replaceHome)
        {
            Replace(RenderArtwork);
        }
        else
        {
            Navigate(RenderArtwork);
        }
    }

    internal override void Leave()
    {
        _source?.CancelBrowsing();
        base.Leave();
    }

    private protected override void RenderHome()
    {
        var body = NewStack("Steam Artwork Changer");
        if (_source is null)
        {
            body.Children.Add(Caption("Artwork is unavailable in this session."));
            SetContent(body);
            return;
        }

        body.Children.Add(Tagged(Row("Search games", _query, Icons.ListLines,
            () => EditText("Search your Steam library", _query, 128, query =>
            {
                _query = query;
                _shown = 48;
                RenderHome();
            })), "games.search"));
        body.Children.Add(Tagged(
            Row("Refresh games", "", Icons.Restart,
                _readingGames ? null : () => _ = RunSafelyAsync(ReadGamesAsync(), "games")), "games.refresh"));
        if (_gamesError is not null)
        {
            body.Children.Add(Caption(_gamesError));
        }

        if (_readingGames)
        {
            body.Children.Add(Caption("Asking Steam…"));
        }

        var games = _games.Where(game => game.Name.Contains(_query, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var game in games.Take(_shown))
        {
            var id = unchecked((uint)game.AppId);
            body.Children.Add(Tagged(Row(game.Name, game.Shortcut ? "Non-Steam game" : "Steam game", Icons.Grid4,
                () => _ = RunSafelyAsync(OpenGameAsync(id, game.Name), "open game")), "game:" + id));
        }

        if (games.Length > _shown)
        {
            body.Children.Add(Tagged(Row("Load more", $"{_shown} of {games.Length}", Icons.ArrowDown, () =>
            {
                _shown += 48;
                RenderHome();
            }), "games.more"));
        }

        if (_gamesRead && !_readingGames && _gamesError is null && games.Length == 0)
        {
            body.Children.Add(Caption("No matching games in Steam."));
        }

        if (ReturnRequested is not null)
        {
            body.Children.Add(Tagged(Row("Return to importer", "", Icons.ArrowLeft, () => ReturnRequested?.Invoke()),
                "return"));
        }

        SetContent(body);
        if (!_gamesRead && !_readingGames)
        {
            _ = RunSafelyAsync(ReadGamesAsync(), "games");
        }
    }

    private async Task ReadGamesAsync()
    {
        var generation = _navigationGeneration;
        _readingGames = true;
        var result = await _source!.ReadGamesAsync();
        _readingGames = false;
        _gamesRead = true;
        if (generation != _navigationGeneration)
        {
            return;
        }

        _games = result.Games;
        _gamesError = result.Error;
        _current?.Invoke();
    }

    private void RenderArtwork()
    {
        if (_source?.ReadState() is not { } state)
        {
            Back();
            return;
        }

        var body = NewStack(state.AppName);
        body.Children.Add(ToolTabs(state.ActiveTab, state.Tabs.Select(tab =>
            (tab.Id, tab.Label, (Action)(() => Run(token => _source.SelectTabAsync(tab.Id, token))))).ToArray()));
        AddStatus(body, state.Loading, state.Error, state.Notice);
        if (state.ActiveTab == "manage")
        {
            RenderManage(body, state);
        }
        else
        {
            body.Children.Add(Tagged(
                Row("Filters", "Styles, size, format and game match", Icons.ListLines, () => Navigate(RenderFilters)),
                "filters"));
            body.Children.Add(Tagged(
                Row("Browse local", "Choose an image file", Icons.Grid4,
                    () => _ = RunSafelyAsync(ApplyLocalAsync(state.ActiveTab), "local artwork")), "local"));
            if (state.ActiveTab == "logo")
            {
                body.Children.Add(Tagged(Row("Adjust logo position", "", Icons.Reorder, OpenLogo), "logo"));
            }

            if (state.OfficialAssets.Count > 0)
            {
                body.Children.Add(Tagged(
                    Row("Official artwork", "Steam's own assets", Icons.SteamLike, () => Navigate(RenderOfficial)),
                    "official"));
            }

            body.Children.Add(ChoiceRow("Thumbnail size",
                new[] { (100, "Small"), (160, "Medium"), (220, "Large"), (260, "Largest") }, _cardSize,
                value =>
                {
                    _cardSize = value;
                    RenderArtwork();
                }));
            var cards = new WrapPanel();
            foreach (var asset in state.Assets)
            {
                var id = asset.Id;
                var card = PreviewCard(id, asset.ThumbnailUrl, asset.Author ?? asset.Provider,
                    $"{asset.Width}×{asset.Height} · {asset.Format}" + Badges(asset),
                    () => Navigate(() => RenderAsset(id)));
                card.Width = _cardSize;
                cards.Children.Add(card);
            }

            body.Children.Add(cards);
            if (!state.Loading && state.Assets.Count == 0 && state.Error is null)
            {
                body.Children.Add(Caption("No artwork found."));
            }

            if (state.HasMore)
            {
                body.Children.Add(Tagged(
                    Row("Load more", "", Icons.ArrowDown, state.Loading ? null : () => Run(_source.LoadMoreAsync)),
                    "more"));
            }
        }

        SetContent(body);
    }

    private static string Badges(SteamArtworkBrowserAsset asset)
    {
        return string.Join("", new[]
        {
            asset.Animated ? " · Animated" : "", asset.Nsfw ? " · Adult" : "", asset.Humor ? " · Humor" : "",
            asset.Epilepsy ? " · Epilepsy" : ""
        });
    }

    private void RenderAsset(string id)
    {
        if (_source?.ReadState() is not { } state)
        {
            return;
        }

        var asset = state.Assets.FirstOrDefault(asset => asset.Id == id);
        if (asset is null)
        {
            Back();
            return;
        }

        var body = NewStack(state.AppName + " · " + state.ActiveTab);
        body.Children.Add(asset.Animated
            ? new OverlayMediaPreview(Context, asset.ImageUrl, true) { Tag = "animated:" + id }
            : new OverlayPreviewImage(asset.ImageUrl, 320) { Tag = "full:" + id });
        body.Children.Add(Caption($"{asset.Width}×{asset.Height} · {asset.Format} · {asset.Provider}" + Badges(asset)));
        body.Children.Add(Caption(string.Join(" · ",
            new[] { asset.Author, asset.Style, asset.Notes }.Where(value => !string.IsNullOrEmpty(value)))));
        AddStatus(body, state.Loading, state.Error, state.Notice);
        body.Children.Add(Tagged(PrimaryRow("Apply artwork", "Changes this game's selected slot immediately",
            Icons.Grid4,
            () => Run(token => _source.ApplyAsync(id, token))), "apply"));
        SetContent(body);
    }

    private void RenderOfficial()
    {
        if (_source?.ReadState() is not { } state)
        {
            return;
        }

        var body = NewStack("Official artwork");
        var cards = new WrapPanel();
        foreach (var asset in state.OfficialAssets)
        {
            var id = asset.Id;
            cards.Children.Add(PreviewCard(id, asset.ImageUrl, asset.Label, $"{asset.Width}×{asset.Height}",
                () => Run(token => _source.ApplyOfficialAsync(id, token))));
        }

        body.Children.Add(cards);
        AddStatus(body, state.Loading, state.Error, state.Notice);
        SetContent(body);
    }

    private async Task ApplyLocalAsync(string slot)
    {
        var generation = _navigationGeneration;
        var source = _source;
        var path = await PickPathAsync(false, ".png", ".jpg", ".jpeg", ".webp", ".ico", ".gif");
        if (path is not null && generation == _navigationGeneration && source is not null)
        {
            Run(token => source.ApplyLocalAsync(slot, path, token));
        }
    }

    private void RenderManage(StackPanel body, SteamArtworkBrowserState state)
    {
        foreach (var slot in state.ManagedSlots)
        {
            var id = slot.Id;
            body.Children.Add(Caption("Current " + slot.Label +
                                      (slot.HasCustomArtwork ? " · Custom" : " · Steam default")));
            body.Children.Add(new OverlayPreviewImage(slot.ImageUrl, 180)
                { Tag = "managed:" + id + ":" + slot.ImageUrl?.GetHashCode() });
            body.Children.Add(Tagged(
                Row("Clear " + slot.Label, "Restore default artwork", Icons.Close,
                    () => Run(token => _source!.ClearAsync(id, token))), "clear:" + id));
            body.Children.Add(Tagged(
                Row("Browse " + slot.Label, "Choose a local file", Icons.Grid4,
                    () => _ = RunSafelyAsync(ApplyLocalAsync(id), "local")), "local:" + id));
            if (id != "icon")
            {
                body.Children.Add(Tagged(
                    Row("Invisible " + slot.Label, "Apply a transparent image", Icons.BlockedCircle,
                        () => Run(token => _source!.ApplyInvisibleAsync(id, token))), "invisible:" + id));
            }
        }

        body.Children.Add(Tagged(Row("Adjust logo position", "", Icons.Reorder, OpenLogo), "logo"));
        body.Children.Add(Tagged(
            Row("Reset logo position", "", Icons.Restart, () => Run(_source!.ResetLogoPositionAsync)), "logo.reset"));
    }

    private void RenderFilters()
    {
        if (_source?.ReadState() is not { } state)
        {
            return;
        }

        var filter = state.Filter;
        var body = NewStack("Artwork filters");
        body.Children.Add(Tagged(Row("Search game match", state.SelectedGame ?? "Use Steam game", Icons.ListLines,
            () => EditText("Find a game", state.AppName, 128,
                term => Run(token => _source.SearchGamesAsync(term, token)))), "match.search"));
        body.Children.Add(Tagged(
            Row("Use Steam game", "Clear the manual provider match", Icons.Restart,
                () => Run(token => _source.SelectGameAsync(null, token))), "match.reset"));
        foreach (var match in state.GameMatches)
        {
            var id = match.Id;
            body.Children.Add(Tagged(
                Row(match.Name, match.Provider, Icons.Grid4, () => Run(token => _source.SelectGameAsync(id, token))),
                "match:" + id));
        }

        AddSet("Styles", OfferedStyles(state.ActiveTab), filter.Styles, values => filter with { Styles = values });
        AddSet("Dimensions", Dimensions(state.ActiveTab), filter.Dimensions,
            values => filter with { Dimensions = values });
        AddSet("Formats",
            state.ActiveTab == "icon" ? ["image/png", "image/vnd.microsoft.icon"] :
            state.ActiveTab == "logo" ? ["image/png", "image/webp"] : ["image/png", "image/jpeg", "image/webp"],
            filter.Mimes, values => filter with { Mimes = values });
        Flag("Static", filter.Static, value => filter with { Static = value, Animated = !value || filter.Animated });
        Flag("Animated", filter.Animated, value => filter with { Animated = value, Static = !value || filter.Static });
        Flag("Adult", filter.Adult, value => filter with { Adult = value });
        Flag("Humor", filter.Humor, value => filter with { Humor = value });
        Flag("Epilepsy", filter.Epilepsy, value => filter with { Epilepsy = value });
        Flag("Untagged", filter.Untagged, value => filter with { Untagged = value });
        AddStatus(body, state.Loading, state.Error, state.Notice);
        SetContent(body);
        return;

        void Flag(string label, bool value, Func<bool, SteamArtworkBrowserFilter> change)
        {
            body.Children.Add(ToggleRow(label, value,
                chosen => Run(token => _source.SetFilterAsync(change(chosen), token))));
        }

        void AddSet(string label, string[] offered, IReadOnlyList<string> selected,
            Func<string[], SteamArtworkBrowserFilter> change)
        {
            body.Children.Add(Caption(label));
            foreach (var option in offered)
            {
                body.Children.Add(ToggleRow(label + ": " + option, selected.Contains(option), value =>
                {
                    var next = selected.ToHashSet(StringComparer.Ordinal);
                    if (value)
                    {
                        next.Add(option);
                    }
                    else
                    {
                        next.Remove(option);
                    }

                    Run(token => _source.SetFilterAsync(change(next.ToArray()), token));
                }));
            }
        }
    }

    private static string[] OfferedStyles(string tab)
    {
        return tab switch
        {
            "grid" or "wide" => ["alternate", "blurred", "white_logo", "material", "no_logo"],
            "icon" => ["official", "custom"],
            "logo" => ["official", "white", "black", "custom"],
            "hero" => ["alternate", "blurred", "material"],
            _ => ["alternate", "white_logo", "no_logo", "blurred", "material"]
        };
    }

    private static string[] Dimensions(string tab)
    {
        return tab switch
        {
            "grid" => ["600x900", "342x482", "660x930", "512x512", "1024x1024"],
            "wide" => ["460x215", "920x430", "512x512", "1024x1024"],
            "hero" => ["1920x620", "3840x1240", "1600x650"],
            "icon" => ["1024", "512", "310", "256", "192", "128", "96", "64", "48", "32", "16"],
            _ => []
        };
    }

    private void OpenLogo()
    {
        _ = RunSafelyAsync(OpenLogoAsync(), "logo position");
    }

    private async Task OpenLogoAsync()
    {
        if (_source?.ReadState() is not { } state)
        {
            return;
        }

        var generation = _navigationGeneration;
        _logoApp = state.AppId;
        try
        {
            _logo = await _source!.ReadLogoPositionAsync() ?? new SteamLogoPosition("BottomLeft", 50, 50);
        }
        catch (Exception ex)
        {
            Toast("Logo position could not be read: " + ex.Message);
            return;
        }

        if (generation == _navigationGeneration)
        {
            Navigate(RenderLogo);
        }
    }

    private void RenderLogo()
    {
        if (_source?.ReadState()?.AppId != _logoApp)
        {
            Back();
            return;
        }

        var body = NewStack("Logo position");
        body.Children.Add(Caption($"{_logo.Anchor} · {_logo.WidthPercent}% × {_logo.HeightPercent}%"));
        var sample = new Grid { Width = 480, Height = 180, MaxWidth = 480 };
        sample.Bind(Panel.BackgroundProperty, this.GetResourceObservable("DeckGroupBrush"));
        var logo = new Border
        {
            Child = new TextBlock
            {
                Text = "GAME LOGO", FontSize = 18, FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            },
            BorderThickness = new Thickness(1)
        };
        logo.Bind(Border.BorderBrushProperty, this.GetResourceObservable("HcAccentBrush"));
        sample.Children.Add(logo);

        void UpdateSample()
        {
            logo.Width = 480 * _logo.WidthPercent / 100d;
            logo.Height = 180 * _logo.HeightPercent / 100d;
            logo.HorizontalAlignment = _logo.Anchor.EndsWith("Left", StringComparison.Ordinal)
                ? HorizontalAlignment.Left
                : _logo.Anchor.EndsWith("Right", StringComparison.Ordinal)
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Center;
            logo.VerticalAlignment = _logo.Anchor.StartsWith("Top", StringComparison.Ordinal) ? VerticalAlignment.Top
                : _logo.Anchor.StartsWith("Bottom", StringComparison.Ordinal) ? VerticalAlignment.Bottom
                : VerticalAlignment.Center;
        }

        UpdateSample();
        body.Children.Add(sample);

        body.Children.Add(ChoiceRow("Anchor",
            new[]
            {
                "TopLeft", "TopCenter", "TopRight", "CenterLeft", "CenterCenter", "CenterRight", "BottomLeft",
                "BottomCenter", "BottomRight"
            }.Select(anchor => (anchor, anchor)).ToArray(),
            _logo.Anchor, anchor =>
            {
                _logo = _logo with { Anchor = anchor };
                RenderLogo();
            }));
        body.Children.Add(new DeviceSliderRow("logo.width", "Width", "Percent", 5, 100, 1, CapabilityUnit.Percent,
            _logo.WidthPercent, true, width =>
            {
                _logo = _logo with { WidthPercent = width };
                UpdateSample();
            }));
        body.Children.Add(new DeviceSliderRow("logo.height", "Height", "Percent", 5, 100, 1, CapabilityUnit.Percent,
            _logo.HeightPercent, true, height =>
            {
                _logo = _logo with { HeightPercent = height };
                UpdateSample();
            }));
        body.Children.Add(Tagged(
            PrimaryRow("Save", "Apply the position to this game", Icons.Grid4,
                () => Run(token =>
                    _source!.SaveLogoPositionAsync(_logo.Anchor, _logo.WidthPercent, _logo.HeightPercent, token))),
            "logo.save"));
        body.Children.Add(Tagged(
            Row("Reset", "Restore default logo position", Icons.Restart, () => Run(_source!.ResetLogoPositionAsync)),
            "logo.reset"));
        SetContent(body);
    }
}

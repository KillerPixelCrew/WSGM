using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Overlay's Game Tools page: the Steam library with the games that already have tools first, and per game
///     the same program, detection, channel and tool actions the routed Steam page offers, all through
///     <see cref="IGameToolsSource" />.
/// </summary>
/// <remarks>
///     Entering the page opens the game whose Steam page is on screen, when Steam reports one; Back from that
///     game returns to the list. Install, update, repair, configure and remove run in the service and outlive
///     the page; their outcome arrives as the game's notice or error.
/// </remarks>
public sealed partial class GameToolsView : ServiceSubView
{
    // The controls pane is narrower than this at the 980 x 640 floor; cards then stack their supporting parts.
    private const double WideWidth = 820;
    private readonly GameList _games = new();
    private string _filter = "all";
    private bool _resolveCurrent = true;
    private IGameToolsSource? _service;
    private bool _wide = true;

    /// <summary>Keeps the cards laid out for the controls pane's width.</summary>
    public GameToolsView()
    {
        SizeChanged += (_, args) =>
        {
            var wide = args.NewSize.Width >= WideWidth;
            if (wide != _wide)
            {
                _wide = wide;
                CurrentLevel?.Invoke();
            }
        };
    }

    /// <inheritdoc />
    protected override string LogScope => "Game Tools";

    /// <summary>Reads the Steam library for the game list; null reads it from the session's Steam client.</summary>
    /// <remarks>Headless captures supply a fixed library here instead of a Steam client.</remarks>
    internal Func<Task<SteamLibraryReadResult>>? LibraryReader { get; set; }

    /// <summary>Reads a game's capsule image; null reads Steam's local artwork through <see cref="SteamArtwork" />.</summary>
    /// <remarks>Headless captures supply fixed images here.</remarks>
    internal Func<uint, CancellationToken, Task<byte[]>>? CapsuleReader { get; set; }

    /// <summary>Supplies the session's Game Tools owner, or none.</summary>
    /// <param name="service">Borrowed service; null detaches it when the overlay closes or in overlay-test.</param>
    internal void Attach(IGameToolsSource? service)
    {
        _service = service;
        AttachSource(IsEffectivelyVisible ? service : null);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            AttachSource(IsVisible ? _service : null);
        }
    }

    /// <inheritdoc />
    /// <remarks>Forgets the open game and drafts; running service work continues.</remarks>
    internal override void Leave()
    {
        _resolveCurrent = true;
        ClearGame();
        base.Leave();
    }

    private protected override void RenderHome()
    {
        if (_resolveCurrent && _service is not null)
        {
            // Entering the page from a game's Steam page opens that game directly, the way the Steam cog
            // menu does. The list stays underneath as the Back target.
            _resolveCurrent = false;
            RenderMessage("Game Tools", "Asking Steam which game is open…");
            _ = RunSafelyAsync(OpenCurrentGameAsync(), "current game");
            return;
        }

        RenderList();
    }

    private async Task OpenCurrentGameAsync()
    {
        var generation = NavigationGeneration;
        uint appId = 0;
        try
        {
            appId = await SteamCurrentGame.AppIdAsync(Steam);
        }
        catch (Exception ex)
        {
            // An unanswered Steam is the same as no game page: the list still works.
            Log.Warn($"Game Tools: could not ask Steam for the current game: {ex.Message}");
        }

        if (generation != NavigationGeneration)
        {
            return;
        }

        if (appId == 0)
        {
            RenderList();
            return;
        }

        var name = _games.Games.FirstOrDefault(game => game.AppId == appId)?.Name ?? "";
        OpenGame(appId, name);
    }

    private void RenderList()
    {
        var body = NewStack("Game Tools");
        if (_service is not { } service)
        {
            body.Children.Add(Caption("Game Tools is not available in this session."));
            SetContent(body);
            return;
        }

        // Read before drawing, so a list published during the draw still counts as a change.
        var library = service.PeekLibrary();
        body.Children.Add(Tagged(Caption("Add upscaler mods such as OptiScaler to a game's own folder. WSGM backs up "
                                         + "every file it replaces and removes everything again on request."),
            "games.intro"));
        var steamNames = _games.Games.GroupBy(game => game.AppId).ToDictionary(group => group.Key,
            group => group.First());
        var withTools = library.Games
            .Where(game => Matches(steamNames.GetValueOrDefault(game.AppId)?.Name ?? game.Name))
            .Where(game => _filter != "updates"
                           || game.Tools.Any(tool => tool.Status == "updateAvailable")).ToArray();
        body.Children.Add(Toolbar(library));
        if (library.Error is { } libraryError)
        {
            body.Children.Add(Banner("games.library-error", FAInfoBarSeverity.Error, null, libraryError));
        }

        if (_games.Error is { } steamError)
        {
            body.Children.Add(Banner("games.error", FAInfoBarSeverity.Error, null, steamError));
            body.Children.Add(Tagged(CommandStrip(Command("games.retry", "Try again", _games.Reading
                ? null
                : () =>
                {
                    _games.Loaded = false;
                    EnsureGameList(_games, ReadLibraryAsync);
                })), "games.retry-strip"));
        }

        AddWithTools(body, library, withTools, steamNames);
        if (_filter == "all")
        {
            AddAllGames(body, library);
        }

        SetLiveContent(body, library, () => _service?.PeekLibrary(),
            static (shown, next) => ReferenceEquals(shown, next), null);
        EnsureGameList(_games, ReadLibraryAsync);
    }

    private Control Toolbar(GameToolsLibrary library)
    {
        var search = Tagged(Row("Search games", _games.Query.Length > 0 ? _games.Query : "Search installed games",
            Icons.ListLines, () => EditText("Search your games", _games.Query, 0, query =>
            {
                _games.Query = query;
                _games.Shown = GameList.Chunk;
            })), "games.search");
        var all = _games.Loaded ? "All " + _games.Games.Count : "All";
        var filters = CommandStrip(
            Command("games.filter:all", all, () => Filter("all"), _filter == "all"),
            Command("games.filter:tools", "With tools " + library.WithTools, () => Filter("tools"),
                _filter == "tools"),
            Command("games.filter:updates", "Updates " + library.Updates, () => Filter("updates"),
                _filter == "updates"));
        filters.VerticalAlignment = VerticalAlignment.Center;
        var bar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(_wide ? "*,Auto" : "*"),
            RowDefinitions = new RowDefinitions(_wide ? "Auto" : "Auto,Auto"),
            ColumnSpacing = 12,
            Tag = "games.toolbar:" + _wide
        };
        bar.Children.Add(search);
        Grid.SetColumn(filters, _wide ? 1 : 0);
        Grid.SetRow(filters, _wide ? 0 : 1);
        filters.Margin = new Thickness(0, _wide ? 0 : 8, 0, 0);
        bar.Children.Add(filters);
        return bar;
    }

    private void Filter(string filter)
    {
        _filter = filter;
        _games.Shown = GameList.Chunk;
        CurrentLevel?.Invoke();
    }

    private void AddWithTools(StackPanel body, GameToolsLibrary library, IReadOnlyList<GameToolsGameSummary> games,
        IReadOnlyDictionary<uint, SteamLibraryApp> steamNames)
    {
        if (library.Games.Count == 0 && _filter == "all" && !library.Loading)
        {
            return;
        }

        body.Children.Add(Tagged(SectionLabel("With tools"), "tools.heading"));
        if (library.Loading)
        {
            body.Children.Add(Tagged(Caption("Reading the games that have tools…"), "tools.loading"));
            return;
        }

        if (games.Count == 0)
        {
            body.Children.Add(Tagged(Caption(library.Games.Count == 0
                ? "No game has a tool yet. Choose a game below to add one."
                : _filter == "updates" && _games.Query.Length == 0
                    ? "No update is known yet. Open a game and use Check for updates to look again."
                    : "No game with tools matches the search."), "tools.empty"));
            return;
        }

        var rows = new StackPanel { Spacing = 8, Tag = "tools.rows" };
        foreach (var game in games)
        {
            var appId = game.AppId;
            var name = steamNames.GetValueOrDefault(appId)?.Name ?? game.Name;
            List<Control> chips = [];
            foreach (var tool in game.Tools)
            {
                if (!tool.DefaultChannel)
                {
                    chips.Add(Chip(tool.ChannelLabel, "info"));
                }

                chips.Add(StatusChip(tool));
            }

            rows.Children.Add(Tagged(new OverlayMediaRow(name,
                string.Join(" · ", game.Tools.Select(tool => tool.Detail)),
                Capsule(appId, _wide ? 120 : 96, _wide ? 56 : 45), null, chips, () => OpenGame(appId, name)),
                "tools:" + appId));
        }

        body.Children.Add(rows);
    }

    private void AddAllGames(StackPanel body, GameToolsLibrary library)
    {
        body.Children.Add(Tagged(SectionLabel("All games"), "games.heading"));
        if (!_games.Loaded || (_games.Reading && _games.Games.Count == 0))
        {
            body.Children.Add(Tagged(Caption("Asking Steam for your games…"), "games.reading"));
            return;
        }

        var games = _games.Games.Where(game => Matches(game.Name)).ToArray();
        if (games.Length == 0)
        {
            body.Children.Add(Tagged(Caption(_games.Games.Count == 0
                ? "Your Steam library has no games."
                : "No games match the search."), "games.empty"));
            return;
        }

        var summaries = library.Games.ToDictionary(game => game.AppId);
        // Two columns of cards whose rows each take their own height, so one long name doesn't stretch every row.
        var shown = games.Take(_games.Shown).ToArray();
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", (shown.Length + 1) / 2))),
            // Cards keep their grid cell when reconciled, so a different set of games is a different grid.
            Tag = "games.grid:" + string.Join(",", shown.Select(game => game.AppId))
        };
        var index = 0;
        foreach (var game in shown)
        {
            var chosen = game;
            var tools = summaries.GetValueOrDefault(game.AppId)?.Tools;
            var detail = tools is { Count: > 0 }
                ? string.Join(", ", tools.Select(tool => tool.ToolName + " " + tool.Version))
                : game.Shortcut ? "Non-Steam game · no tools" : "No tools";
            var card = Tagged(new OverlayMediaRow(game.Name, detail, Capsule(game.AppId, 96, 45), null, [],
                () => OpenGame(chosen.AppId, chosen.Name)), "game:" + game.AppId);
            card.Margin = new Thickness(0, 0, index % 2 == 0 ? 8 : 0, 8);
            Grid.SetColumn(card, index % 2);
            Grid.SetRow(card, index / 2);
            grid.Children.Add(card);
            index++;
        }

        body.Children.Add(grid);
        if (games.Length > _games.Shown)
        {
            var added = "game:" + games[_games.Shown].AppId;
            body.Children.Add(PagedRows.More(this, "games.more", $"{_games.Shown} of {games.Length}", added, () =>
            {
                _games.Shown += GameList.Chunk;
                CurrentLevel?.Invoke();
            }));
        }
    }

    private bool Matches(string name)
    {
        return name.Contains(_games.Query, StringComparison.OrdinalIgnoreCase);
    }

    private Task<SteamLibraryReadResult> ReadLibraryAsync()
    {
        return LibraryReader?.Invoke() ?? OverlayLibraryLookup.ReadAsync(Steam);
    }

    /// <summary>A game's capsule art, read only once it is on screen.</summary>
    private OverlayPreviewImage Capsule(uint appId, double width, double height)
    {
        var reader = CapsuleReader ?? ReadSteamCapsuleAsync;
        return new OverlayPreviewImage("capsule:" + appId, token => reader(appId, token), height,
            Stretch.UniformToFill)
        {
            Width = width, ClipToBounds = true, CornerRadius = new CornerRadius(4), Tag = "capsule:" + appId
        };
    }

    private static async Task<byte[]> ReadSteamCapsuleAsync(uint appId, CancellationToken token)
    {
        return await Task.Run(() => SteamArtwork.FindCapsuleFile(appId) is { } path
            ? File.ReadAllBytes(path)
            : [], token).ConfigureAwait(false);
    }

    private static Border StatusChip(GameToolsToolSummary tool)
    {
        return Chip(tool.Status == "upToDate" ? "✓ " + tool.StatusLabel : tool.StatusLabel, tool.Status switch
        {
            "upToDate" => "good",
            "updateAvailable" => "action",
            "modified" => "warn",
            "interrupted" => "bad",
            _ => ""
        });
    }

    /// <summary>A status chip in one of the shared badge tones.</summary>
    /// <param name="text">The chip's text.</param>
    /// <param name="tone"><c>good</c>, <c>warn</c>, <c>bad</c>, <c>info</c>, <c>action</c> or empty for neutral.</param>
    /// <returns>The chip.</returns>
    private static Border Chip(string text, string tone)
    {
        var chip = new Border
        {
            Classes = { "badge" },
            Child = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (tone.Length > 0)
        {
            chip.Classes.Add(tone);
        }

        return chip;
    }

    /// <summary>An attention message in the shared InfoBar.</summary>
    private static FAInfoBar Banner(string tag, FAInfoBarSeverity severity, string? title, string message)
    {
        return new FAInfoBar
        {
            Tag = tag, IsOpen = true, IsClosable = false, IsIconVisible = true, Severity = severity,
            Title = title ?? "", Message = message, HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    /// <summary>Runs one service command off the UI thread; the refusal, if any, is shown as a toast.</summary>
    /// <param name="operation">The command.</param>
    /// <param name="what">What it was, for the log.</param>
    /// <param name="onApplied">Runs on the UI thread after an accepted command while this level is current.</param>
    private void RunTool(Func<IGameToolsSource, CancellationToken, Task<SteamUiCommandResult>> operation,
        string what, Action? onApplied = null)
    {
        if (_service is not { } service)
        {
            return;
        }

        _ = RunCommandAsync(token => operation(service, token), what, onApplied);
    }
}

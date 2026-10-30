using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Overlay's Game Tools page: a searchable list of the Steam library and, per game, the same runtime,
///     detection and tool actions the routed Steam page offers, all through <see cref="GameToolsService" />.
/// </summary>
/// <remarks>
///     Entering the page opens the game whose Steam page is on screen, when Steam reports one; Back from that
///     game returns to the list. Install, update, repair, configure and remove run in the service and outlive
///     the page; their outcome arrives as the game's notice or error.
/// </remarks>
public sealed partial class GameToolsView : ServiceSubView
{
    private const int ListChunk = 48;
    private IReadOnlyList<SteamLibraryApp> _games = [];
    private string? _gamesError;
    private bool _gamesRead;
    private string _query = "";
    private bool _readingGames;
    private bool _resolveCurrent = true;
    private GameToolsService? _service;
    private int _shown = ListChunk;

    /// <inheritdoc />
    protected override string LogScope => "Game Tools";

    /// <summary>Supplies the session's Game Tools owner, or none.</summary>
    /// <param name="service">Borrowed service; null detaches it when the overlay closes or in overlay-test.</param>
    internal void Attach(GameToolsService? service)
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
            if (Steam is { } steam)
            {
                var page = await steam.CurrentPage.GetAsync();
                appId = page.Succeeded ? page.Value.AppId : 0;
            }
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

        var name = _games.FirstOrDefault(game => game.AppId == appId)?.Name ?? "";
        OpenGame(appId, name);
    }

    private void RenderList()
    {
        var body = NewStack("Game Tools");
        if (_service is null)
        {
            body.Children.Add(Caption("Game Tools is not available in this session."));
            SetContent(body);
            return;
        }

        body.Children.Add(Tagged(Caption(
            "Add tools such as OptiScaler to a game's folder. Choose a game, or open its page in Steam first."),
            "games.intro"));
        body.Children.Add(Tagged(Row("Search games", _query, Icons.ListLines,
            () => EditText("Search your Steam library", _query, 0, query =>
            {
                _query = query;
                _shown = ListChunk;
                RenderList();
            })), "games.search"));
        body.Children.Add(Tagged(Row("Refresh games", "Read the library from Steam again", Icons.Restart,
            _readingGames ? null : () => _ = RunSafelyAsync(ReadGamesAsync(), "games")), "games.refresh"));
        if (_gamesError is not null)
        {
            body.Children.Add(Tagged(Caption(_gamesError), "games.error"));
        }

        if (_readingGames)
        {
            body.Children.Add(Tagged(Caption("Asking Steam…"), "games.reading"));
        }

        var games = _games.Where(game => game.Name.Contains(_query, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var game in games.Take(_shown))
        {
            var id = game.AppId;
            var name = game.Name;
            body.Children.Add(Tagged(Row(name, game.Shortcut ? "Non-Steam game" : "Steam game", Icons.Wrench,
                () => OpenGame(id, name)), "game:" + id));
        }

        if (games.Length > _shown)
        {
            var added = "game:" + games[_shown].AppId;
            body.Children.Add(Tagged(Row("Load more", $"{_shown} of {games.Length}", Icons.ArrowDown, () =>
            {
                _shown += ListChunk;
                RenderList();
                PagedRows.FocusAdded(this, added);
            }), "games.more"));
        }

        if (_gamesRead && !_readingGames && _gamesError is null && games.Length == 0)
        {
            body.Children.Add(Tagged(Caption(_games.Count == 0
                ? "Your Steam library has no games."
                : "No games match the search."), "games.empty"));
        }

        SetContent(body);
        if (!_gamesRead && !_readingGames)
        {
            _ = RunSafelyAsync(ReadGamesAsync(), "games");
        }
    }

    private async Task ReadGamesAsync()
    {
        var generation = NavigationGeneration;
        _readingGames = true;
        CurrentLevel?.Invoke();
        OverlayLibraryResult result;
        try
        {
            result = await OverlayLibraryLookup.ReadAsync(Steam);
        }
        finally
        {
            _readingGames = false;
            _gamesRead = true;
        }

        if (result.Succeeded)
        {
            _games = result.Games;
        }

        // A failed read keeps the last good list rather than presenting it as an empty library.
        _gamesError = result.Error;
        if (generation == NavigationGeneration)
        {
            CurrentLevel?.Invoke();
        }
    }

    /// <summary>Runs one service command off the UI thread; the refusal, if any, is shown as a toast.</summary>
    /// <param name="operation">The command.</param>
    /// <param name="what">What it was, for the log.</param>
    /// <param name="onApplied">Runs on the UI thread after an accepted command while this level is current.</param>
    private void RunTool(Func<GameToolsService, CancellationToken, Task<SteamUiCommandResult>> operation,
        string what, Action? onApplied = null)
    {
        if (_service is not { } service)
        {
            return;
        }

        _ = RunCommandAsync(token => operation(service, token), what, onApplied);
    }
}

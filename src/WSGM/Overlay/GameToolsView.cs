using System;
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
///     detection and tool actions the routed Steam page offers, all through <see cref="IGameToolsSource" />.
/// </summary>
/// <remarks>
///     Entering the page opens the game whose Steam page is on screen, when Steam reports one; Back from that
///     game returns to the list. Install, update, repair, configure and remove run in the service and outlive
///     the page; their outcome arrives as the game's notice or error.
/// </remarks>
public sealed partial class GameToolsView : ServiceSubView
{
    private readonly GameList _games = new();
    private bool _resolveCurrent = true;
    private IGameToolsSource? _service;

    /// <inheritdoc />
    protected override string LogScope => "Game Tools";

    /// <summary>Reads the Steam library for the game list; null reads it from the session's Steam client.</summary>
    /// <remarks>Headless captures supply a fixed library here instead of a Steam client.</remarks>
    internal Func<Task<SteamLibraryReadResult>>? LibraryReader { get; set; }

    /// <inheritdoc />
    /// <remarks>The game list shows no service state, so the service's progress does not redraw it.</remarks>
    private protected override bool RedrawsOnSourceChange => HasNestedLevel;

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
        if (_service is null)
        {
            body.Children.Add(Caption("Game Tools is not available in this session."));
            SetContent(body);
            return;
        }

        body.Children.Add(Tagged(Caption(
            "Add tools such as OptiScaler to a game's folder. Choose a game, or open its page in Steam first."),
            "games.intro"));
        AddGameList(body, _games, Icons.Wrench, ReadLibraryAsync, game => OpenGame(game.AppId, game.Name),
            RenderList);
        SetContent(body);
        EnsureGameList(_games, ReadLibraryAsync);
    }

    private Task<SteamLibraryReadResult> ReadLibraryAsync()
    {
        return LibraryReader?.Invoke() ?? OverlayLibraryLookup.ReadAsync(Steam);
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     Picks which game a launch-wrapper action applies to, for the case where the
///     overlay could not tell what the user was looking at in Steam.
/// </summary>
/// <remarks>
///     The common path never reaches this view: opening the panel from a game's page
///     resolves that game directly. It exists for the panel opened from the library
///     root, and for a Steam that reports no current app.
/// </remarks>
public sealed class LaunchWrapperView : ServiceSubView
{
    // A full Steam library is one ActionButton per title in a non-virtualizing
    // host, so it is paged the way LibraryTabsView's multi-select is: laying out
    // a 1000+ title account in one pass stalls the overlay's UI thread.
    private const int PageSize = 200;
    private string _customArguments = "";
    private SteamLibraryApp? _customGame;

    private string? _customPath;
    private IReadOnlyList<SteamLibraryApp> _games = [];
    private LaunchActionService? _actions;
    private SteamLibraryApp? _actionGame;
    private LaunchActionState? _state;
    private DispatcherTimer? _refresh;
    private bool _reading;
    private bool _busy;
    private string? _draftPath;
    private string _draftArguments = "";
    private LaunchWrapperMode _suggestedMode;

    internal void AttachLaunchActions(LaunchActionService? actions)
    {
        _actions = actions;
    }

    /// <summary>Shows fresh per-game state and all shared launch actions.</summary>
    internal void OpenActions(SteamLibraryApp game, LaunchWrapperMode suggestedMode,
        string? customPath = null, string arguments = "")
    {
        _suggestedMode = suggestedMode;
        _actionGame = game;
        _state = null;
        _draftPath = customPath;
        _draftArguments = arguments;
        NavigationStack.Clear();
        CurrentLevel = null;
        AttachSource(_actions);
        Navigate(RenderActions);
        _ = RefreshActionsAsync();
        // Steam announces no change to launch fields edited in Steam itself, so they are read again
        // while the page shows. A draw only shows the state already read.
        _refresh ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refresh.Tick -= OnRefresh;
        _refresh.Tick += OnRefresh;
        _refresh.Start();
    }

    private protected override void RenderHome()
    {
        RenderActions();
    }

    internal override void Leave()
    {
        _refresh?.Stop();
        AttachSource(null);
        _actionGame = null;
        base.Leave();
    }

    private void OnRefresh(object? sender, EventArgs args)
    {
        if (IsEffectivelyVisible)
        {
            _ = RefreshActionsAsync();
        }
    }

    private async Task RefreshActionsAsync()
    {
        if (_reading || _actions is null || _actionGame is not { } game)
        {
            return;
        }

        var generation = NavigationGeneration;
        _reading = true;
        try
        {
            var state = await _actions.ReadAsync(SteamApps.NormalizeAppId(game.AppId));
            if (_actionGame?.AppId == game.AppId && generation == NavigationGeneration && state != _state)
            {
                _state = state;
                CurrentLevel?.Invoke();
            }
        }
        finally
        {
            _reading = false;
        }
    }

    private void RenderActions()
    {
        var body = NewStack(_actionGame?.Name ?? "Launch Actions");
        if (_state is not { } state)
        {
            body.Children.Add(Caption(_actions is null ? "Launch actions unavailable" : "Reading launch configuration…"));
            SetContent(body);
            return;
        }

        body.Children.Add(Tagged(Caption($"Current: {state.Ownership}"), "launch.current"));
        body.Children.Add(Tagged(Caption(state.CanRestore ? "Original launch action recorded. Restore is available." : "No restoration snapshot recorded."), "launch.restore-status"));
        body.Children.Add(Tagged(Caption(state.InputRoute), "launch.input"));
        body.Children.Add(Tagged(Caption(state.ExternallyChanged ? "Launch fields differ from WSGM's saved action. The recorded original is retained." : ""), "launch.external"));
        body.Children.Add(Tagged(Caption(state.WrapperAvailable ? "" : "Launch wrapper missing. Repair the installation before applying wrapper actions."), "launch.wrapper"));
        body.Children.Add(Tagged(Caption(state.Error ?? state.Notice ?? ""), "launch.result"));
        body.Children.Add(Tagged(Caption(state.IsShortcut ? $"Target: {state.Target}\nLaunch arguments: {state.Options}" : $"Launch options: {state.Options}"), "launch.fields"));
        AddMode(LaunchWrapperMode.Deelevate, "launch.deelevate");
        AddMode(LaunchWrapperMode.InputLease, "launch.lease");
        AddMode(LaunchWrapperMode.Both, "launch.both");
        var custom = Tagged(Row("Custom launch action…", "Choose an executable or script", Icons.Play,
            () => _ = ChooseCustomAsync()), "launch.custom");
        custom.IsEnabled = !_busy && state.Error is null;
        body.Children.Add(custom);
        if (_draftPath is { } path)
        {
            var apply = Tagged(Row("Apply selected custom action", $"{path} {_draftArguments}", Icons.Play,
                () => ConfirmChange(LaunchWrapperMode.None, path, _draftArguments)), "launch.apply-custom");
            apply.IsEnabled = !_busy && state.Error is null;
            body.Children.Add(apply);
        }

        var restore = Tagged(Row("Restore original launch action", "Remove WSGM changes and restore the recorded original", Icons.Restart,
            () => ConfirmChange(LaunchWrapperMode.None)), "launch.restore");
        restore.IsEnabled = !_busy && state.CanRestore && state.Error is null;
        body.Children.Add(restore);
        body.Children.Add(Tagged(Row("Refresh", "Read current launch fields from Steam", Icons.Restart,
            () => _ = RefreshActionsAsync()), "launch.refresh"));
        body.Children.Add(Tagged(Row("Back", "", Icons.ArrowLeft, () => Back()), "launch.back"));
        SetContent(body);
        return;

        void AddMode(LaunchWrapperMode mode, string key)
        {
            var label = LaunchActionService.Label(mode);
            var row = Tagged(mode == _suggestedMode
                ? PrimaryRow(label, "Configure this game's launch wrapper", Icons.Play, () => ConfirmChange(mode))
                : Row(label, "Configure this game's launch wrapper", Icons.Play, () => ConfirmChange(mode)), key);
            row.IsEnabled = !_busy && state.WrapperAvailable && state.Error is null;
            body.Children.Add(row);
        }
    }

    private async Task ChooseCustomAsync()
    {
        var game = _actionGame;
        var generation = NavigationGeneration;
        var path = await PickPathAsync(false, ".exe", ".cmd", ".bat", ".ps1");
        if (path is null || game != _actionGame || generation != NavigationGeneration)
        {
            return;
        }

        _draftPath = path;
        _draftArguments = "";
        EditText("Custom action arguments (optional)", "", 0, value =>
        {
            _draftArguments = value;
            CurrentLevel?.Invoke();
        });
        CurrentLevel?.Invoke();
    }

    private void ConfirmChange(LaunchWrapperMode mode, string? path = null, string arguments = "")
    {
        if (_actions is not { } actions || _actionGame is not { } game || _busy)
        {
            return;
        }

        ConfirmCommand(path is not null ? "Replace launch action" : mode == LaunchWrapperMode.None ? "Restore original launch action" : LaunchActionService.Label(mode),
            $"Replace the current launch action for {game.Name}? The recorded original is retained until restoration is confirmed.",
            async token =>
            {
                if (_busy)
                {
                    return new SteamUiCommandResult(false, "A launch action is already being applied.");
                }

                _busy = true;
                try
                {
                    var appId = SteamApps.NormalizeAppId(game.AppId);
                    var result = path is null
                        ? await actions.ApplyAsync(appId, mode, token)
                        : await actions.ApplyCustomAsync(appId, path, arguments, token);
                    return new SteamUiCommandResult(result.Ok, result.Detail);
                }
                finally
                {
                    _busy = false;
                    Dispatcher.UIThread.Post(() =>
                    {
                        CurrentLevel?.Invoke();
                        _ = RefreshActionsAsync();
                    });
                }
            });
    }

    /// <inheritdoc />
    protected override string LogScope => "Launch wrappers";

    /// <summary>
    ///     Raised when the user chooses a game. The overlay then applies the
    ///     pending action and leaves this sub-view.
    /// </summary>
    public event Action<SteamLibraryApp>? Picked;

    /// <summary>Raised with the selected file, arguments, and game for a custom action.</summary>
    public event Action<string, string, SteamLibraryApp>? CustomPicked;

    /// <summary>Loads the library and shows the picker.</summary>
    /// <param name="heading">What the caller is about to do, as a title.</param>
    public void Open(string heading)
    {
        _refresh?.Stop();
        _actionGame = null;
        AttachSource(null);
        _customPath = null;
        _customGame = null;
        NavigationStack.Clear();
        CurrentLevel = null;
        _ = RunSafelyAsync(RenderGameListAsync(heading), "game list");
    }

    /// <summary>Asks for optional arguments before applying the custom action.</summary>
    /// <param name="path">The executable or script the user picked.</param>
    /// <param name="game">
    ///     The game whose Steam page is on screen, or <c>null</c> to
    ///     ask which game the action applies to.
    /// </param>
    public void OpenCustom(string path, SteamLibraryApp? game = null)
    {
        _refresh?.Stop();
        _actionGame = null;
        AttachSource(null);
        _customPath = path;
        _customArguments = "";
        _customGame = game;
        NavigationStack.Clear();
        CurrentLevel = null;
        Navigate(RenderArgumentChoice);
    }

    private void RenderArgumentChoice()
    {
        var name = Path.GetFileName(_customPath) ?? "";
        var stack = NewStack("Custom launch action");
        // Name the target when it is already known, so the flow that skips the
        // picker still says which game it is about to change.
        stack.Children.Add(Caption(_customGame is { } target ? $"{name} → {target.Name}" : name));
        stack.Children.Add(PrimaryRow("No arguments", "Continue without additional arguments",
            Icons.Play, ContinueCustomAction));
        stack.Children.Add(Row("Add arguments", "Enter command-line arguments", Icons.CopyDoc,
            () => EditText($"Arguments for {name}", _customArguments, 0, value =>
            {
                _customArguments = value;
                Dispatcher.UIThread.Post(ContinueCustomAction);
            })));
        stack.Children.Add(Row("Cancel", "Do not change the game", Icons.ExitFullscreen,
            () => Back()));
        SetContent(stack);
    }

    // The game open in Steam is the target, the same rule the launch-fix buttons
    // follow. The picker below is only for the panel opened from the library root
    // and for a Steam that reported no current app.
    private void ContinueCustomAction()
    {
        NavigationStack.Clear();
        CurrentLevel = null;
        if (_customPath is { } path && _customGame is { } game)
        {
            CustomPicked?.Invoke(path, _customArguments, game);
            return;
        }

        _ = RunSafelyAsync(RenderGameListAsync("Replace launch action"), "game list");
    }

    private async Task RenderGameListAsync(string heading)
    {
        Navigate(() => RenderLoading(heading));
        var generation = NavigationGeneration;
        var result = await OverlayLibraryLookup.ReadAsync(Steam);
        var games = result.Games;
        // The picker load is asynchronous, so a Back press (or a second open) while
        // Steam was answering must discard this result rather than redraw over it.
        if (generation != NavigationGeneration)
        {
            return;
        }

        if (!result.Succeeded)
        {
            var failed = NewStack(heading);
            failed.Children.Add(Caption(result.Error!));
            failed.Children.Add(Row("Retry", "", Icons.Restart,
                () => _ = RunSafelyAsync(RenderGameListAsync(heading), "game list")));
            SetContent(failed);
            return;
        }

        _games = games;
        Replace(() => RenderGamePage(heading, 0));
    }

    private void RenderGamePage(string heading, int page)
    {
        var pageCount = Math.Max(1, (_games.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pageCount - 1);
        var current = page;
        var stack = NewStack(heading);
        if (_games.Count == 0)
        {
            // A Steam that could not list its games took the failure branch above, so an
            // empty list here is a library with no games.
            stack.Children.Add(Caption("Your Steam library has no games."));
        }
        else
        {
            stack.Children.Add(Caption(
                "Choose a game, or open one in Steam and use this panel from its page."));
            if (pageCount > 1)
            {
                stack.Children.Add(Caption($"Page {page + 1} of {pageCount} · {_games.Count} games"));
                if (page > 0)
                {
                    stack.Children.Add(Row("Previous page", "", Icons.Restart,
                        () => Replace(() => RenderGamePage(heading, current - 1))));
                }
            }

            foreach (var game in _games.Skip(page * PageSize).Take(PageSize))
            {
                var g = game;
                stack.Children.Add(Row(
                    g.Name,
                    g.Shortcut ? "Non-Steam shortcut" : "",
                    Icons.SteamLike,
                    () =>
                    {
                        if (_customPath is { } path)
                        {
                            CustomPicked?.Invoke(path, _customArguments, g);
                        }
                        else
                        {
                            Picked?.Invoke(g);
                        }
                    }));
            }
        }

        stack.Children.Add(SectionLabel(""));
        if (page + 1 < pageCount)
        {
            stack.Children.Add(Row("Next page",
                $"Games {(page + 1) * PageSize + 1}–{Math.Min(_games.Count, (page + 2) * PageSize)}",
                Icons.Play, () => Replace(() => RenderGamePage(heading, current + 1))));
        }

        stack.Children.Add(Row("Back", "Cancel", Icons.ExitFullscreen, () => Back()));
        SetContent(stack);
    }
}

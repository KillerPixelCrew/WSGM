using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WSGM.Controls;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>
/// Picks which game a launch-wrapper action applies to, for the case where the
/// overlay could not tell what the user was looking at in Steam.
/// </summary>
/// <remarks>
/// The common path never reaches this view: opening the panel from a game's page
/// resolves that game directly. It exists for the panel opened from the library
/// root, and for a Steam that reports no current app.
/// </remarks>
public sealed class LaunchWrapperView : OverlaySubView
{
    private IReadOnlyList<SteamCollections.AppInfo> _games = [];

    /// <summary>Raised when the user chooses a game. The overlay then applies the
    /// pending action and leaves this sub-view.</summary>
    public event Action<SteamCollections.AppInfo>? Picked;

    /// <inheritdoc />
    protected override string LogScope => "Launch wrappers";

    /// <summary>Loads the library and shows the picker.</summary>
    /// <param name="heading">What the caller is about to do, as a title.</param>
    public void Open(string heading)
    {
        _stack.Clear();
        _current = null;
        _ = RunSafelyAsync(RenderGameListAsync(heading), "game list");
    }

    private async Task RenderGameListAsync(string heading)
    {
        Navigate(() => RenderLoading(heading));
        var generation = _navigationGeneration;
        var games = await SafeGamesAsync();
        // The picker load is asynchronous, so a Back press (or a second open) while
        // Steam was answering must discard this result rather than redraw over it.
        if (generation != _navigationGeneration)
        {
            return;
        }

        _games = games;
        Replace(() =>
        {
            var stack = NewStack(heading);
            if (_games.Count == 0)
            {
                // GetGamesAsync answers empty for an unreachable Steam too, so this
                // says "could not read" rather than claiming the library is empty.
                stack.Children.Add(Caption("Couldn't read your library from Steam. Is it running?"));
            }
            else
            {
                stack.Children.Add(Caption(
                    "Choose a game, or open one in Steam and use this panel from its page."));
                foreach (var game in _games)
                {
                    var g = game;
                    stack.Children.Add(Row(
                        g.Name,
                        g.Shortcut ? "Non-Steam shortcut" : "",
                        Icons.SteamLike,
                        () => Picked?.Invoke(g)));
                }
            }
            stack.Children.Add(SectionLabel(""));
            stack.Children.Add(Row("Back", "Cancel", Icons.ExitFullscreen, () => Back()));
            SetContent(stack);
        });
    }

    private async Task<IReadOnlyList<SteamCollections.AppInfo>> SafeGamesAsync()
    {
        try
        {
            return await SteamCollections.GetGamesAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"{LogScope}: could not list games: {ex.Message}");
            return [];
        }
    }
}

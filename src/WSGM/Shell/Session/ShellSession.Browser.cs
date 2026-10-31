using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    private BrowserSession? _browser;
    private BrowserGameContext? _browserGame;
    private uint _browserGameRequested;

    private void InitializeBrowser()
    {
        // This owner holds preferences and no engine/environment/controller. Title lookup and
        // the isolated host are admitted only by BrowserSession.Open from the selected page.
        _browser ??= new BrowserSession(_store.Context, _config.Browser, PersistBrowserOptions,
            ReadBrowserGame, RefreshBrowserGame);
    }

    private BrowserOptions PersistBrowserOptions(BrowserOptions requested)
    {
        var before = _browser?.Options ?? _config.Browser.Normalize();
        var memoryChanged = requested.MemoryBehavior != before.MemoryBehavior;
        var zoomChanged = requested.ZoomFactor != before.ZoomFactor;
        var saved = _store.Update(config =>
        {
            var changed = false;
            if (memoryChanged && config.Browser.MemoryBehavior != requested.MemoryBehavior)
            {
                config.Browser.MemoryBehavior = requested.MemoryBehavior;
                changed = true;
            }

            if (zoomChanged && config.Browser.ZoomFactor != requested.ZoomFactor)
            {
                config.Browser.ZoomFactor = requested.ZoomFactor;
                changed = true;
            }

            return changed;
        });
        _config.Browser = saved.Browser.Normalize();
        return _config.Browser;
    }

    private BrowserGameContext? ReadBrowserGame()
    {
        var target = _runningApplications?.Current;
        return target is { State: RunningApplicationTargetState.Active or RunningApplicationTargetState.IdentityOnly, SteamAppId: { } appId }
               && _browserGame?.AppId == appId.ToString(CultureInfo.InvariantCulture) ? _browserGame : null;
    }

    private void RefreshBrowserGame()
    {
        var target = _runningApplications?.Current;
        var appId = target is { State: RunningApplicationTargetState.Active or RunningApplicationTargetState.IdentityOnly }
            ? target.SteamAppId ?? 0 : 0;
        if (appId == _browserGameRequested)
        {
            return;
        }

        _browserGameRequested = appId;
        _browserGame = null;
        if (appId == 0)
        {
            _browser?.GameContextChanged();
            return;
        }

        _ = ResolveBrowserGameAsync(appId);
    }

    /// <summary>
    ///     Resolves a running game's title: the loaded import entry first, then Steam's app details.
    ///     The Browser and Notes share this lookup.
    /// </summary>
    /// <param name="appId">The Steam app id of the running game.</param>
    /// <param name="cancellationToken">Cancels the Steam read.</param>
    /// <returns>The title, or null when neither source knows the game.</returns>
    private async Task<string?> ResolveGameTitleAsync(uint appId, CancellationToken cancellationToken)
    {
        if (_libraryImportState.EntriesIfLoaded()?.FirstOrDefault(entry => entry.AppId == appId) is { } entry)
        {
            return entry.Name;
        }

        return _steamClient is { } steam
            ? (await steam.Apps.ReadDetailsAsync(appId, cancellationToken).ConfigureAwait(false)).Value?.Name
            : null;
    }

    private async Task ResolveBrowserGameAsync(uint appId)
    {
        try
        {
            if (await ResolveGameTitleAsync(appId, _shutdownCancellation.Token).ConfigureAwait(false)
                is not { Length: > 0 } title)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!_shutdownCancellation.IsCancellationRequested && appId == _browserGameRequested)
                {
                    _browserGame = new BrowserGameContext(appId.ToString(CultureInfo.InvariantCulture), title);
                    _browser?.GameContextChanged();
                }
            });
        }
        catch (Exception) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Optional local title context does not prevent ordinary browsing. No search is sent.
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_shutdownCancellation.IsCancellationRequested && appId == _browserGameRequested
                    && _browserGame is null)
                {
                    // A later user-initiated open may retry an unavailable local Steam observation.
                    _browserGameRequested = 0;
                }
            });
        }
    }
}

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    private BrowserSession? _browser;
    private BrowserGameContext? _browserGame;
    private uint _browserGameRequested;
    private int _browserGameGeneration;

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
        var generation = ++_browserGameGeneration;
        if (appId == 0)
        {
            _browser?.GameContextChanged();
            return;
        }

        if (_libraryImportState.EntriesIfLoaded()?.FirstOrDefault(entry => entry.AppId == appId) is { } entry)
        {
            _browserGame = new BrowserGameContext(appId.ToString(CultureInfo.InvariantCulture), entry.Name);
            _browser?.GameContextChanged();
        }
        else
        {
            _ = ResolveBrowserGameAsync(appId, generation);
        }
    }

    private async Task ResolveBrowserGameAsync(uint appId, int generation)
    {
        try
        {
            if (_steamClient is not { } steam)
            {
                return;
            }

            var details = await steam.Apps.ReadDetailsAsync(appId, _shutdownCancellation.Token).ConfigureAwait(false);
            if (details.Value?.Name is not { Length: > 0 and <= 512 } title)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!_shutdownCancellation.IsCancellationRequested && generation == _browserGameGeneration)
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
                if (!_shutdownCancellation.IsCancellationRequested && generation == _browserGameGeneration
                    && _browserGame is null)
                {
                    // A later user-initiated open may retry an unavailable local Steam observation.
                    _browserGameRequested = 0;
                }
            });
        }
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>
///     The overlay's session-lifetime tools: Programs, Notes with its Steam capture, the screensavers
///     and the Browser with the running game's title it offers.
/// </summary>
/// <param name="state">The session's live state.</param>
/// <param name="steamUi">The Steam transport and client the tools read titles and captures through.</param>
/// <param name="library">The import state imported titles are read from.</param>
/// <param name="performance">The running application the tools follow.</param>
internal sealed class OverlayTools(
    SessionState state,
    SteamUiServices steamUi,
    LibraryServices library,
    PerformanceServices performance)
{
    private BrowserGameContext? _browserGame;
    private uint _browserGameRequested;

    /// <summary>The overlay's Programs page.</summary>
    internal ProgramsService? Programs { get; private set; }

    /// <summary>The overlay's Notes.</summary>
    internal NotesService? Notes { get; private set; }

    /// <summary>The screensavers.</summary>
    internal ScreensaverService? Screensavers { get; private set; }

    /// <summary>The Browser's preferences owner; it holds no engine until a page opens.</summary>
    internal BrowserSession? Browser { get; private set; }

    /// <summary>
    ///     Deletes media previews a crash left behind, then creates Programs, the Browser, the screensavers
    ///     and Notes. No sheet exists yet, so no preview is in use.
    /// </summary>
    internal void Start()
    {
        // A crash can leave media preview files behind. No sheet exists yet, so none is in use.
        OverlayMediaPreview.DeleteStaleFiles(state.Store.Context.Root);
        Programs ??= new ProgramsService(state.Store, state.Config);
        ShellSession.TryStart("browser", StartBrowser);
        Screensavers ??= new ScreensaverService(state.Store.Context, () => state.Config.Screensavers,
            change => state.Config.Screensavers =
                state.CommitSetting(config => change(config.Screensavers), false).Screensavers,
            () => state.Config.Cef.Enabled && steamUi.SharedContextReady());
        Screensavers.ConfigurationChanged();
        Notes ??= new NotesService(
            new NoteStore(Path.Combine(state.Store.Context.Root, "notes")),
            () => performance.CurrentApplication,
            appId => library.ImportState.EntriesIfLoaded()?.FirstOrDefault(entry => entry.AppId == appId)
                is { } entry
                ? (entry.Source, entry.Key, entry.Name)
                : null,
            steamUi.Client is null ? null : ResolveGameTitleAsync)
        {
            Countdown = new NotesCountdown()
        };
        if (!state.OverlayTestOnly)
        {
            var steamNotes = new NotesSteamCaptureSurface(
                () => state.Config.Cef.Enabled ? steamUi.Transport : null, () => steamUi.Client);
            Notes.SteamOverlayOpen = steamNotes.OverlayOpenAsync;
            Notes.SteamCapturePorts = new NotesCapturePorts(steamNotes, Notes.Countdown!,
                (target, token) => NotesGameCapture.CaptureAsync(target, steamNotes.OverlayOpenAsync, token),
                NotesGameCapture.IsAlive);
        }
    }

    private void StartBrowser()
    {
        // This owner holds preferences and no engine/environment/controller. Title lookup and
        // the isolated host are admitted only by BrowserSession.Open from the selected page.
        Browser ??= new BrowserSession(state.Store.Context, state.Config.Browser, PersistBrowserOptions,
            ReadBrowserGame, RefreshBrowserGame);
    }

    private BrowserOptions PersistBrowserOptions(BrowserOptions requested)
    {
        var before = Browser?.Options ?? state.Config.Browser.Normalize();
        var memoryChanged = requested.MemoryBehavior != before.MemoryBehavior;
        var zoomChanged = requested.ZoomFactor != before.ZoomFactor;
        var saved = state.Store.Update(config =>
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
        state.Config.Browser = saved.Browser.Normalize();
        return state.Config.Browser;
    }

    private BrowserGameContext? ReadBrowserGame()
    {
        var target = performance.RunningApplications?.Current;
        return target is { State: RunningApplicationTargetState.Active or RunningApplicationTargetState.IdentityOnly, SteamAppId: { } appId }
               && _browserGame?.AppId == appId.ToString(CultureInfo.InvariantCulture) ? _browserGame : null;
    }

    private void RefreshBrowserGame()
    {
        var target = performance.RunningApplications?.Current;
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
            Browser?.GameContextChanged();
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
        if (library.ImportState.EntriesIfLoaded()?.FirstOrDefault(entry => entry.AppId == appId) is { } entry)
        {
            return entry.Name;
        }

        return steamUi.Client is { } steam
            ? (await steam.Apps.ReadDetailsAsync(appId, cancellationToken).ConfigureAwait(false)).Value?.Name
            : null;
    }

    private async Task ResolveBrowserGameAsync(uint appId)
    {
        try
        {
            if (await ResolveGameTitleAsync(appId, state.ShutdownToken).ConfigureAwait(false)
                is not { Length: > 0 } title)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!state.ShutdownToken.IsCancellationRequested && appId == _browserGameRequested)
                {
                    _browserGame = new BrowserGameContext(appId.ToString(CultureInfo.InvariantCulture), title);
                    Browser?.GameContextChanged();
                }
            });
        }
        catch (Exception) when (state.ShutdownToken.IsCancellationRequested)
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
                if (!state.ShutdownToken.IsCancellationRequested && appId == _browserGameRequested
                                                                 && _browserGame is null)
                {
                    // A later user-initiated open may retry an unavailable local Steam observation.
                    _browserGameRequested = 0;
                }
            });
        }
    }

    /// <summary>Closes the Browser, so no page outlives shutdown's start.</summary>
    internal void CloseBrowser()
    {
        Browser?.Dispose();
    }

    /// <summary>The Browser's own cleanup, or a completed task.</summary>
    internal Task BrowserShutdownCompletion => Browser?.ShutdownCompletion ?? Task.CompletedTask;

    /// <summary>Disposes Programs, Notes and the screensavers, in that order.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    /// <returns>Completion of the steps, which never fault.</returns>
    internal async Task StopAsync(ShutdownSteps steps)
    {
        await steps.StepAsync("Disposing Programs during application shutdown failed", async () =>
        {
            var programs = Programs;
            Programs = null;
            if (programs is not null)
            {
                await programs.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await steps.StepAsync("Disposing Notes during application shutdown failed", async () =>
        {
            var notes = Notes;
            Notes = null;
            if (notes is not null)
            {
                await notes.DisposeAsync().ConfigureAwait(false);
                (notes.Countdown as NotesCountdown)?.Close();
            }
        }).ConfigureAwait(false);
        await steps.StepAsync("Disposing screensavers during application shutdown failed", async () =>
        {
            var screensavers = Screensavers;
            Screensavers = null;
            if (screensavers is not null)
            {
                await screensavers.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }
}

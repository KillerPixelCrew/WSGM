using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Leaves and returns to a game's Steam overlay for a Notes screenshot.</summary>
/// <remarks>
///     Both directions use what Steam's own interface does. "Back to game" is Steam's
///     <c>RaiseWindowForGame</c> for the exact overlay process (the toolkit's
///     <see cref="SteamGameWindowActivation" />). Steam opens a page in a game's gamepad overlay by navigating that
///     overlay window, which is how it shows the controller configurator there; Notes returns the same way, to
///     the exact overlay window identified by process and app id, and never to another window as a fallback.
/// </remarks>
internal sealed class NotesSteamCaptureSurface(Func<ISteamUiTransport?> transport, Func<SteamClient?> client)
    : INotesCaptureSurface
{
    /// <inheritdoc />
    public async Task<bool> ReturnToGameAsync(NotesCaptureTarget target, CancellationToken cancellationToken)
    {
        if (transport() is not { } current)
        {
            return false;
        }

        var raised = await SteamGameWindowActivation.RaiseAsync(current, target.ProcessId, cancellationToken)
            .ConfigureAwait(false);
        if (!raised)
        {
            Log.Warn($"Notes screenshot: Steam did not return to the game (pid {target.ProcessId}).");
            return false;
        }

        var window = WindowFinder.FindWindow([target.ProcessId], null);
        if (window != 0)
        {
            WindowFinder.BringToForeground(window);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ReopenAsync(NotesCaptureTarget target, string pageId, CancellationToken cancellationToken)
    {
        if (client() is not { } steam)
        {
            return false;
        }

        var result = await steam.EvaluateAsync(SteamUiTargetRole.SharedJsContext,
            OpenOverlayExpression(target.ProcessId, target.SteamAppId, NotesSteamSurface.Route),
            TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        return result.Answered && result.Value == "true";
    }

    /// <summary>Whether Steam reports its overlay or a side menu open over a game process.</summary>
    /// <param name="processId">The game process.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True only when Steam confirms it open; an unknown state does not block the capture.</returns>
    internal async Task<bool> OverlayOpenAsync(uint processId, CancellationToken cancellationToken)
    {
        if (transport() is not { } current)
        {
            return false;
        }

        try
        {
            var snapshot = await SteamSideMenuObserver.ReadAsync(current, cancellationToken).ConfigureAwait(false);
            return snapshot.Windows?.Any(window => window.ProcessId == processId
                                                   && (window.OverlayActive == true
                                                       || window.Menu != SteamSideMenu.None)) == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"Notes screenshot: Steam overlay state unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>Navigates exactly one game overlay window to a route, the way Steam opens its own pages there.</summary>
    /// <param name="processId">The overlay's game process.</param>
    /// <param name="appId">The overlay's app id.</param>
    /// <param name="route">The route.</param>
    /// <returns>JavaScript resolving to true only when one matching gamepad overlay window navigated.</returns>
    internal static string OpenOverlayExpression(uint processId, uint appId, string route)
    {
        return $$"""
                 (()=>{
                   try {
                     const pid={{processId.ToString(CultureInfo.InvariantCulture)}},appid={{appId.ToString(CultureInfo.InvariantCulture)}};
                     const route={{SteamCef.JsString(route)}};
                     const windows=window.SteamUIStore?.WindowStore?.OverlayWindows;
                     if(!pid||!appid||!Array.isArray(windows))return false;
                     const matches=windows.filter(w=>w.params?.browserInfo?.m_unPID===pid&&w.params?.browserInfo?.m_unAppID===appid);
                     if(matches.length!==1||matches[0].IsGamepadUIOverlayWindow?.()!==true||typeof matches[0].Navigate!=='function')return false;
                     matches[0].MenuStore?.CloseSideMenus?.();
                     matches[0].Navigate(route);
                     return true;
                   }catch{return false;}
                 })()
                 """;
    }
}

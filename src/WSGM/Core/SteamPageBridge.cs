using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Reads which game page the user is viewing from Steam's own library UI over the CEF
/// leg (<see cref="SteamCef"/>): the focused element's React fiber first, then the largest
/// visible wide library-asset image in the rendered DOM, then the library route.</summary>
/// <remarks>
/// The library badge that used to live here is a toolkit surface now
/// (<c>SteamLibraryBadgeSurface</c>, fed by <c>Shell\LibraryBadges</c>); nothing in this class
/// writes to the page.
/// </remarks>
public static class SteamPageBridge
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    // Current-game detection, two signals in priority order — the focused element's React fiber,
    // then the largest wide visible library-asset image. Both live-verified; the rules and their
    // evidence are in docs\steam-cef.md.
    private const string CurrentAppIdJs =
        "(()=>{try{" +
        "try{const el=document.activeElement;" +
        "if(el){const fk=Object.keys(el).find(k=>k.startsWith('__reactFiber$'));" +
        "let f=fk?el[fk]:null,hops=0;" +
        "while(f&&hops<40){const p=f.memoizedProps;" +
        "if(p&&typeof p==='object'){" +
        "if(typeof p.appid==='number')return {id:p.appid,src:'focus'};" +
        "const a=p.app||p.overview||p.appOverview;" +
        "if(a&&typeof a.appid==='number')return {id:a.appid,src:'focus'};}" +
        "f=f.return;hops++;}}}catch(e){}" +
        "const cx=window.innerWidth/2,ch=window.innerHeight;" +
        "const imgs=document.querySelectorAll('img');let best=0,bestW=0;" +
        "for(const i of imgs){const r=i.getBoundingClientRect();" +
        "if(r.width<600||r.width<=r.height)continue;" +
        "if(r.bottom<=0||r.top>=ch||cx<r.left||cx>r.right)continue;" +
        "if(i.checkVisibility&&!i.checkVisibility({checkOpacity:true,checkVisibilityCSS:true}))continue;" +
        "const m=(i.src||'').match(/assets\\/(\\d+)\\//);" +
        "if(m&&r.width>bestW){bestW=r.width;best=Number(m[1]);}}" +
        "return {id:best,src:best?'hero image':'none'};}catch(e){return {id:0,src:'error'};}})()";

    // Fallback when the page shows no artwork at all (a custom shortcut with no
    // images): Steam's SPA router keeps SharedJSContext's location on the current
    // route, and a viewed game page is /routes/library/app/<appid>. Live-verified
    // on this machine with a shortcut open in Big Picture (route carried the
    // shortcut's generated id while the page had zero library-asset images).
    private const string RouteAppIdJs =
        "(()=>{try{const m=window.location.pathname.match(/\\/library\\/app\\/(\\d+)/);" +
        "return m?Number(m[1]):0;}catch(e){return 0;}})()";

    /// <summary>The app id of the game page the user is currently viewing, or 0 when
    /// not on a game page / unreachable. In the visible window two signals run in
    /// order (both live-verified, see <c>CurrentAppIdJs</c>): the FOCUSED element's
    /// React fiber first, then the largest wide library-asset image.
    /// Fallback for pages with neither (custom shortcuts): the library route in
    /// SharedJSContext (live-verified). The matching signal is named in the log line,
    /// so a detection that silently changed which one carries it is diagnosable from a
    /// pasted wsgm.log.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<long> GetCurrentAppIdAsync(CancellationToken cancellationToken = default)
    {
        var expression = "JSON.stringify(Object.assign({ok:true}," + CurrentAppIdJs + "))";
        var result = await SteamUiTransportSession.EvaluateOnVisibleWindowAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        var fromPage = ParseAppId(result);
        if (fromPage > 0)
        {
            Log.Info($"Steam current app {fromPage} ({ParseSignal(result)}).");
            return fromPage;
        }
        var routeResult = await SteamUiTransportSession.EvaluateAsync(
            "JSON.stringify({ok:true,id:" + RouteAppIdJs + "})", Budget, cancellationToken)
            .ConfigureAwait(false);
        var fromRoute = ParseAppId(routeResult);
        if (fromRoute > 0)
        {
            Log.Info($"Steam current app {fromRoute} (library route).");
        }
        return fromRoute;
    }

    private static long ParseAppId(CefEvalResult result)
    {
        if (!result.Reachable || result.Value is null)
        {
            return 0;
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            if (document.RootElement.TryGetProperty("id", out var appid)
                && appid.TryGetInt64(out var value))
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Current-app parse failed: {ex.Message}");
        }
        return 0;
    }

    /// <summary>Names the in-page signal that produced the app id, for the log line.
    /// Falls back to the old generic label if the shape is ever missing, so a decode
    /// surprise degrades the diagnostic instead of the detection.</summary>
    private static string ParseSignal(CefEvalResult result)
    {
        if (result.Value is null)
        {
            return "in-page";
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            if (document.RootElement.TryGetProperty("src", out var src)
                && src.ValueKind == JsonValueKind.String)
            {
                return src.GetString() ?? "in-page";
            }
        }
        catch (Exception)
        {
            // ParseAppId already logged whatever went wrong with this payload.
        }
        return "in-page";
    }
}

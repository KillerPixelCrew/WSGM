using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Reaches into Steam's own library UI over the CEF leg (<see cref="SteamCef"/>):
/// <list type="bullet">
/// <item><b>Current-game detection</b> — which game page the user is viewing, read from
/// the largest visible wide library-asset image in the rendered DOM.</item>
/// <item><b>In-page card badge</b> — a resident script installs a <c>MutationObserver</c>
/// that renders an "On: &lt;card&gt;" badge on a game page when that game lives on a
/// tracked card. The observer runs inside the visible Steam page and survives its SPA
/// navigations; WSGM re-asserts it on reconnect (idempotent via a
/// <c>window.__wsgm</c> sentinel).</item>
/// </list>
/// Coexists with CSSLoader-Desktop (device-verified concurrent CDP; source-verified no
/// surface overlap): everything is namespaced under <c>window.__wsgm</c>, the badge wears
/// a unique <c>wsgm-badge</c> class (never CSSLoader's <c>css-loader-style</c>), and nothing
/// is appended to <c>document.head</c> or removed that WSGM did not create.</summary>
public static class SteamPageBridge
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    // Current-game detection, two signals in priority order, both live-verified:
    //
    // 1) The FOCUSED element's React fiber. Big Picture's gamepad UI keeps DOM focus
    //    on the selected control (the "focus outline"), and walking the fiber's
    //    parents finds the props of the component that owns it — a carousel cover or
    //    a game page carries its app there (props.appid or props.app.appid).
    //    Live-verified with an imageless custom shortcut focused on a theme home
    //    screen: returned its generated appid at 10 hops while the page had zero
    //    library-asset images. Image-independent, so it also CLEARS correctly when
    //    a non-game element is focused (its ancestors carry no appid).
    //
    // 2) Fallback: the LARGEST WIDE library-asset image (assets/<appid>/…) that
    //    straddles the horizontal viewport center and is effectively visible
    //    (checkVisibility, feature-detected — a stale faded-out hero must not win;
    //    live-verified rejecting an opacity-0 leftover). Covers mouse/touch use
    //    where DOM focus may sit on the body.
    private const string CurrentAppIdJs =
        "(()=>{try{" +
        "try{const el=document.activeElement;" +
        "if(el){const fk=Object.keys(el).find(k=>k.startsWith('__reactFiber$'));" +
        "let f=fk?el[fk]:null,hops=0;" +
        "while(f&&hops<40){const p=f.memoizedProps;" +
        "if(p&&typeof p==='object'){" +
        "if(typeof p.appid==='number')return p.appid;" +
        "const a=p.app||p.overview||p.appOverview;" +
        "if(a&&typeof a.appid==='number')return a.appid;}" +
        "f=f.return;hops++;}}}catch(e){}" +
        "const cx=window.innerWidth/2,ch=window.innerHeight;" +
        "const imgs=document.querySelectorAll('img');let best=0,bestW=0;" +
        "for(const i of imgs){const r=i.getBoundingClientRect();" +
        "if(r.width<600||r.width<=r.height)continue;" +
        "if(r.bottom<=0||r.top>=ch||cx<r.left||cx>r.right)continue;" +
        "if(i.checkVisibility&&!i.checkVisibility({checkOpacity:true,checkVisibilityCSS:true}))continue;" +
        "const m=(i.src||'').match(/assets\\/(\\d+)\\//);" +
        "if(m&&r.width>bestW){bestW=r.width;best=Number(m[1]);}}return best;}catch(e){return 0;}})()";

    // Fallback when the page shows no artwork at all (a custom shortcut with no
    // images): Steam's SPA router keeps SharedJSContext's location on the current
    // route, and a viewed game page is /routes/library/app/<appid>. Live-verified
    // on this machine with a shortcut open in Big Picture (route carried the
    // shortcut's generated id while the page had zero library-asset images).
    private const string RouteAppIdJs =
        "(()=>{try{const m=window.location.pathname.match(/\\/library\\/app\\/(\\d+)/);" +
        "return m?Number(m[1]):0;}catch(e){return 0;}})()";

    /// <summary>The app id of the game page the user is currently viewing, or 0 when
    /// not on a game page / unreachable. Primary signal: the visible window's
    /// library-asset image URLs (device-verified). Fallback for pages with no images
    /// (custom shortcuts): the library route in SharedJSContext (live-verified).</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<long> GetCurrentAppIdAsync(CancellationToken cancellationToken = default)
    {
        var expression = "JSON.stringify({ok:true,appid:" + CurrentAppIdJs + "})";
        var result = await SteamCef.EvaluateOnVisibleWindowAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        var fromImages = ParseAppId(result);
        if (fromImages > 0)
        {
            Log.Info($"Steam current app {fromImages} (hero image).");
            return fromImages;
        }
        var routeResult = await SteamCef.EvaluateAsync(
            "JSON.stringify({ok:true,appid:" + RouteAppIdJs + "})", Budget, cancellationToken)
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
            if (document.RootElement.TryGetProperty("appid", out var appid)
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

    /// <summary>Disconnects the resident badge observer and removes its node from the
    /// visible Steam page. Best-effort shutdown for desktop mode and process exit.</summary>
    public static Task<CefEvalResult> DisableBadgeAsync(CancellationToken cancellationToken = default)
        => SteamCef.EvaluateOnVisibleWindowAsync(
            "(()=>{try{window.__wsgm&&window.__wsgm.disableBadge&&window.__wsgm.disableBadge();return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false,err:String(e)});}})()",
            Budget, cancellationToken);

    /// <summary>Installs (idempotently) the resident badge observer and pushes the
    /// current app-id → card-name map. Call whenever the card set changes or after a
    /// reconnect; the sentinel makes re-calls cheap no-ops for the observer while still
    /// refreshing the data. Best-effort — a closed/absent Steam simply does nothing.</summary>
    /// <param name="appIdToCard">Map of app id to the card name to show for it.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<bool> UpdateCardBadgesAsync(
        IReadOnlyDictionary<long, string> appIdToCard, CancellationToken cancellationToken = default)
    {
        var map = BuildMapLiteral(appIdToCard);
        var expression =
            "(()=>{try{" +
            "window.__wsgm=window.__wsgm||{};" +
            "window.__wsgm.cardMap=" + map + ";" +
            InstallBadgeScript +
            "if(window.__wsgm.renderBadge)window.__wsgm.renderBadge();" +
            "return JSON.stringify({ok:true,installed:!!window.__wsgm.badgeInstalled});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        // The badge lives in the VISIBLE library window (the DOM the user sees), not the
        // headless SharedJSContext where the stores are.
        var result = await SteamCef.EvaluateOnVisibleWindowAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return false;
        }
        if (result.Value is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(result.Value);
                if (document.RootElement.TryGetProperty("ok", out var ok)
                    && ok.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
                var err = document.RootElement.TryGetProperty("err", out var e) ? e.GetString() : null;
                Log.Warn($"Card badge install failed: {err}.");
            }
            catch
            {
                // Non-fatal; the badge is a convenience.
            }
        }
        return false;
    }

    private static string BuildMapLiteral(IReadOnlyDictionary<long, string> appIdToCard)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (appId, name) in appIdToCard)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            sb.Append('"').Append(appId.ToString(CultureInfo.InvariantCulture)).Append('"')
                .Append(':').Append(SteamCef.JsString(name));
        }
        return sb.Append('}').ToString();
    }

    // Bumped whenever the resident script's behavior changes: a live Steam session
    // keeps whatever observer was installed into it, so without a version gate an
    // upgraded WSGM would keep talking to the OLD detection logic until Steam
    // restarts. On mismatch the old observer is disconnected and replaced.
    private const int BadgeScriptVersion = 3;

    // The resident badge script, installed into the VISIBLE library window. Idempotent
    // per version (sentinel-guarded), namespaced under window.__wsgm, and non-destructive
    // to CSSLoader: the badge wears the unique class "wsgm-badge" (never "css-loader-style",
    // which CSSLoader bulk-removes), lives on document.body (never document.head, where
    // CSSLoader's styles + probe are), and the observer removes only its own node.
    //
    // Current game: read from the page's library-asset image URLs
    // (assets/<appid>/library_hero|logo) — device-verified, locale/DOM-hash independent.
    // A fixed-position pill (proven visible on device) shows "On: <card>" when the viewed
    // game is on a tracked card. Re-render triggers: mutations (childList + src — page
    // navigations swap the hero), plus a 2 s interval as the safety net, because a
    // cover-flow focus change may only shuffle classes/transforms and fire NEITHER
    // watched mutation — the live-reported stale-badge case on an imageless shortcut.
    private static readonly string InstallBadgeScript =
        "if(window.__wsgm.badgeVer!==" + BadgeScriptVersion + "){" +
        "if(window.__wsgm.disableBadge){try{window.__wsgm.disableBadge();}catch(e){}}" +
        "window.__wsgm.badgeVer=" + BadgeScriptVersion + ";window.__wsgm.badgeInstalled=true;" +
        "const BID='wsgm-card-badge';" +
        // Same detection as GetCurrentAppIdAsync — one source string, so the
        // center/visibility rules can never drift between the two consumers.
        "const curId=()=>" + CurrentAppIdJs + ";" +
        "const remove=()=>{const b=document.getElementById(BID);if(b)b.remove();};" +
        "const render=()=>{try{const id=curId();const map=window.__wsgm.cardMap||{};" +
        "const name=id&&map[id];if(!name){remove();return;}" +
        "let b=document.getElementById(BID);" +
        "if(!b){b=document.createElement('div');b.id=BID;b.className='wsgm-badge';" +
        "b.style.cssText='position:fixed;top:16px;left:16px;z-index:99999;display:inline-flex;"
            + "align-items:center;gap:6px;padding:5px 12px;border-radius:5px;"
            + "background:rgba(20,25,32,.9);color:#e6edf3;font-size:14px;font-weight:600;"
            + "box-shadow:0 2px 10px rgba(0,0,0,.5);pointer-events:none;';"
            + "document.body.appendChild(b);}" +
        "const text='\\u25C9 On: '+name;if(b.textContent!==text)b.textContent=text;}catch(e){}};" +
        "window.__wsgm.renderBadge=render;" +
        "try{let queued=false;const obs=new MutationObserver(ms=>{if(ms.every(m=>m.target.closest&&m.target.closest('#'+BID)))return;" +
        "if(!queued){queued=true;requestAnimationFrame(()=>{queued=false;render();});}});" +
        "obs.observe(document.body,{childList:true,subtree:true,attributes:true,attributeFilter:['src']});" +
        "const iv=setInterval(()=>{if(!document.hidden)render();},2000);" +
        "window.__wsgm.badgeObserver=obs;" +
        "window.__wsgm.disableBadge=()=>{obs.disconnect();clearInterval(iv);remove();" +
        "window.__wsgm.badgeInstalled=false;window.__wsgm.badgeVer=0;};}" +
        "catch(e){window.__wsgm.badgeInstalled=false;window.__wsgm.badgeVer=0;}" +
        "render();}";
}

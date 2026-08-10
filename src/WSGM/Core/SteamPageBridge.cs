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

    // The current game is read from the visible window's library-asset image URLs
    // (steamloopback.host/assets/<appid>/library_hero|logo|…) — a stable Steam asset
    // convention, device-verified, and locale/DOM-hash independent. SharedJSContext is
    // headless (empty DOM) so this MUST run on the visible window.
    // The current game = the appid of the LARGEST WIDE visible library-asset image (the
    // hero banner). Robust across art naming: some games serve `assets/<id>/library_hero`,
    // others a hashed `assets/<id>/<hash>` — the appid is always in the path, and only a
    // detail page has a big landscape hero (grid capsules are portrait, width<=height, so
    // they're skipped and the badge clears when you leave a game). Device-verified.
    private const string CurrentAppIdJs =
        "(()=>{try{const imgs=document.querySelectorAll('img');let best=0,bestW=0;" +
        "for(const i of imgs){const r=i.getBoundingClientRect();" +
        "if(r.width<600||r.width<=r.height)continue;" +
        "const m=(i.src||'').match(/assets\\/(\\d+)\\//);" +
        "if(m&&r.width>bestW){bestW=r.width;best=Number(m[1]);}}return best;}catch(e){return 0;}})()";

    /// <summary>The app id of the game page the user is currently viewing in the visible
    /// library window, or 0 when not on a game page / unreachable. Read from the page's
    /// library-asset image URLs (device-verified).</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<long> GetCurrentAppIdAsync(CancellationToken cancellationToken = default)
    {
        var expression = "JSON.stringify({ok:true,appid:" + CurrentAppIdJs + "})";
        var result = await SteamCef.EvaluateOnVisibleWindowAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return 0;
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (root.TryGetProperty("appid", out var appid) && appid.TryGetInt64(out var value))
            {
                if (value > 0)
                {
                    Log.Info($"Steam current app {value}.");
                }
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

    // The resident badge script, installed into the VISIBLE library window. Idempotent
    // (sentinel-guarded), namespaced under window.__wsgm, and non-destructive to
    // CSSLoader: the badge wears the unique class "wsgm-badge" (never "css-loader-style",
    // which CSSLoader bulk-removes), lives on document.body (never document.head, where
    // CSSLoader's styles + probe are), and the observer removes only its own node.
    //
    // Current game: read from the page's library-asset image URLs
    // (assets/<appid>/library_hero|logo) — device-verified, locale/DOM-hash independent.
    // A fixed-position pill (proven visible on device) shows "On: <card>" when the viewed
    // game is on a tracked card; the observer re-renders as the user navigates between
    // game pages (the hero image src changes).
    private const string InstallBadgeScript =
        "if(!window.__wsgm.badgeInstalled){window.__wsgm.badgeInstalled=true;" +
        "const BID='wsgm-card-badge';" +
        "const curId=()=>{try{const imgs=document.querySelectorAll('img');let best=0,bestW=0;" +
        "for(const i of imgs){const r=i.getBoundingClientRect();" +
        "if(r.width<600||r.width<=r.height)continue;" +
        "const m=(i.src||'').match(/assets\\/(\\d+)\\//);" +
        "if(m&&r.width>bestW){bestW=r.width;best=Number(m[1]);}}return best;}catch(e){return 0;}};" +
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
        "window.__wsgm.badgeObserver=obs;window.__wsgm.disableBadge=()=>{obs.disconnect();remove();window.__wsgm.badgeInstalled=false;};}catch(e){window.__wsgm.badgeInstalled=false;}" +
        "render();}";
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One injected library tab: a title + the exact app ids it should contain.
/// WSGM computes the ids (from filters, cards, or genres); Steam renders them.</summary>
/// <param name="Id">Stable unique tab id (e.g. <c>wsgm-card-…</c>).</param>
/// <param name="Title">The tab's display title.</param>
/// <param name="AppIds">The app ids the tab shows.</param>
public sealed record InjectedTab(string Id, string Title, IReadOnlyList<int> AppIds);

/// <summary>Adds real WSGM tabs to Steam's library tab strip — TabMaster's mechanism,
/// re-implemented without Decky and driven from an injected <c>SharedJSContext</c>
/// script (device-verified live). The script captures Steam's webpack registry
/// (<c>webpackChunksteamui</c>), finds React, and hijacks the current dispatcher's
/// <c>useMemo</c> so that whenever the library recomputes its tab array it appends our
/// tabs. Each tab renders a <b>fake in-memory collection</b> (a plain object of the
/// tab's app overviews) through Steam's own grid component — so NO real Steam
/// collection is created. WSGM only supplies <c>window.__wsgm.tabs</c> (id/title/appids);
/// the resident script does the patching.
///
/// <para>Fragility is inherent (it rides Steam's minified React internals) and accepted:
/// the useMemo dispatcher slot and the grid component's <c>Library_FilteredByHeader</c>
/// marker are the two things that can shift on a major Steam UI update. A kill switch
/// (<c>window.__wsgm.disableTabs()</c>) and a Steam restart both fully recover.</para></summary>
public static class SteamLibraryTabs
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    /// <summary>Installs the resident tab-injection script (idempotent) and sets the
    /// current tab list. Passing an empty list clears WSGM's tabs on the next library
    /// render. Runs in <c>SharedJSContext</c>, where the webpack registry and React
    /// live.</summary>
    /// <param name="tabs">The tabs to show, in order.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<bool> SyncTabsAsync(
        IReadOnlyList<InjectedTab> tabs, CancellationToken cancellationToken = default)
    {
        var expression =
            "(()=>{try{" + ResidentSetup +
            "window.__wsgm.tabs=" + BuildDefs(tabs) + ";" +
            "if(window.__wsgm.forceRerender)window.__wsgm.forceRerender();" +
            "return JSON.stringify({ok:true,installed:!!window.__wsgm.tabsInstalled," +
            "count:(window.__wsgm.tabs||[]).length});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.stack)||e)});}})()";

        var result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
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
                var root = document.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    var count = root.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                    Log.Info($"Library tabs injected: {count} tabs.");
                    return true;
                }
                var err = root.TryGetProperty("err", out var e) ? e.GetString() : null;
                Log.Warn($"Library tab injection failed: {err}.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Library tab injection parse failed: {ex.Message}");
            }
        }
        return false;
    }

    private static string BuildDefs(IReadOnlyList<InjectedTab> tabs)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < tabs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            var t = tabs[i];
            sb.Append("{id:").Append(SteamCef.JsString(t.Id))
                .Append(",title:").Append(SteamCef.JsString(t.Title))
                .Append(",appids:[")
                .Append(string.Join(",", t.AppIds.Select(a => a.ToString(CultureInfo.InvariantCulture))))
                .Append("]}");
        }
        return sb.Append(']').ToString();
    }

    // The resident setup: define window.__wsgm, capture React, install helpers and the
    // useMemo dispatcher hijack (once). Guarded by window.__wsgm.tabsInstalled so
    // re-running (each sync / after reconnect) only refreshes the functions. Namespaced
    // under window.__wsgm to coexist with CSSLoader. This is the exact script verified
    // live against Steam's Big Picture library.
    private const string ResidentSetup = """
        var W=window.__wsgm=window.__wsgm||{};
        if(!W._react){
          if(!window.webpackChunksteamui)throw new Error('webpack not ready');
          var req;window.webpackChunksteamui.push([[Symbol('wsgm')],{},function(r){req=r;}]);
          if(!req)throw new Error('no require');
          for(var id of Object.keys(req.m)){var e;try{e=req(id);}catch(x){continue;}
            if(e&&e.createElement&&e.useMemo&&e.version){W._react=e;break;}}
          if(!W._react)throw new Error('React not found');
        }
        var React=W._react;
        W.findInTree=function(node,pred,depth){depth=depth||0;
          if(depth>40||node==null)return null;
          if(Array.isArray(node)){for(var n of node){var r=W.findInTree(n,pred,depth+1);if(r)return r;}return null;}
          if(typeof node!=='object')return null;
          try{if(pred(node))return node;}catch(e){}
          var kids=node.props&&node.props.children;
          return kids?W.findInTree(kids,pred,depth+1):null;};
        W.makeCollection=function(id,title,appids){var as=window.appStore;
          var apps=appids.map(function(a){return as.GetAppOverviewByAppID(a);}).filter(Boolean);
          var map=new Map();apps.forEach(function(a){map.set(a.appid,a);});
          return {id:id,displayName:title,allApps:apps,visibleApps:apps.slice(),apps:map,
            AsDeletableCollection:function(){return null;},AsDragDropCollection:function(){return null;},
            AsEditableCollection:function(){return null;},bAllowsDragAndDrop:false,bIsDeletable:false,
            bIsDynamic:false,bIsEditable:false,
            GetAppCountWithToolsFilter:function(f){return (f&&f.Matches)?apps.filter(function(x){return f.Matches(x);}).length:apps.length;}};};
        W.patchTabs=function(v){try{
          var isNested=Array.isArray(v)&&Array.isArray(v[0]);
          var tabs=isNested?v[0]:v;
          if(!Array.isArray(tabs))return v;
          var tmpl=tabs.find(function(t){return t&&t.id==='AllGames';});
          if(!tmpl)return v;
          if(!W._gridType){var g=W.findInTree(tmpl.content,function(el){return el&&el.type&&el.type.toString&&el.type.toString().includes('Library_FilteredByHeader');});
            if(g){W._gridType=g.type;W._gridProps=g.props;}}
          var existing=new Set(tabs.map(function(t){return t&&t.id;}));
          var add=[];
          for(var d of (W.tabs||[])){
            if(existing.has(d.id))continue;
            var coll=W.makeCollection(d.id,d.title,d.appids||[]);
            var content=tmpl.content;
            if(W._gridType&&React){content=React.createElement(W._gridType,Object.assign({},W._gridProps,{collection:coll}));}
            (function(def,content){add.push({title:def.title,id:def.id,content:content,footer:tmpl.footer,
              renderTabAddon:function(){return React?React.createElement('span',null,String((def.appids||[]).length)):null;}});})(d,content);
          }
          if(!add.length)return v;
          var out=tabs.concat(add);
          return isNested?[out,v[1]]:out;
        }catch(e){return v;}};
        if(!W.tabsInstalled){
          var internals=React.__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE;
          if(!internals||!('H' in internals))throw new Error('React dispatcher slot not found');
          var wrapped=new WeakMap();var cur=internals.H;
          Object.defineProperty(internals,'H',{configurable:true,
            get:function(){var c=cur;if(!c||typeof c!=='object'||typeof c.useMemo!=='function')return c;
              var w=wrapped.get(c);if(!w){var realUseMemo=c.useMemo;w=Object.create(c);
                w.useMemo=function(fn,deps){return realUseMemo.call(c,function(){return W.patchTabs(fn());},deps);};
                wrapped.set(c,w);}return w;},
            set:function(v){cur=v;}});
          W.disableTabs=function(){try{Object.defineProperty(internals,'H',{configurable:true,writable:true,value:cur});}catch(e){}W.tabsInstalled=false;};
          W.tabsInstalled=true;
        }
        """;
}

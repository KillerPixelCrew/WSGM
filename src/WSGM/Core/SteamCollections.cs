using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One Steam user collection (which Steam renders as a library
/// category/tab).</summary>
/// <param name="Id">Steam's collection id (e.g. <c>uc-…</c>).</param>
/// <param name="Name">The display name.</param>
/// <param name="AppIds">The app ids currently in the collection.</param>
public sealed record SteamCollectionInfo(string Id, string Name, IReadOnlyList<long> AppIds);

/// <summary>Outcome of a collection sync.</summary>
/// <param name="Reachable">Whether Steam's debug port answered.</param>
/// <param name="Ok">Whether the collection was created/updated.</param>
/// <param name="Id">The collection id on success.</param>
/// <param name="Error">The reason on failure.</param>
public readonly record struct SteamCollectionSyncResult(
    bool Reachable, bool Ok, string? Id, string? Error);

/// <summary>Reads Steam library data and cleans up collection IDs created by the
/// retired collection-backed tab implementation. New tabs are injected by
/// <see cref="SteamLibraryTabs"/> and never create user collections.</summary>
public static class SteamCollections
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    /// <summary>Lists the current user collections and their app ids.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<IReadOnlyList<SteamCollectionInfo>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        const string expression =
            "(()=>{try{const cs=collectionStore;" +
            "const cols=(cs.userCollections||[]).map(c=>({id:c.id,name:c.displayName," +
            "appids:(c.allApps||c.visibleApps||[]).map(a=>a.appid)}));" +
            "return JSON.stringify({ok:true,collections:cols});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        var result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return Array.Empty<SteamCollectionInfo>();
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("collections", out var cols))
            {
                return Array.Empty<SteamCollectionInfo>();
            }
            var list = new List<SteamCollectionInfo>();
            foreach (var col in cols.EnumerateArray())
            {
                var id = col.GetProperty("id").GetString() ?? "";
                var name = col.GetProperty("name").GetString() ?? "";
                var appIds = new List<long>();
                foreach (var appId in col.GetProperty("appids").EnumerateArray())
                {
                    if (appId.TryGetInt64(out var value))
                    {
                        appIds.Add(value);
                    }
                }
                list.Add(new SteamCollectionInfo(id, name, appIds));
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam collections list parse failed: {ex.Message}");
            return Array.Empty<SteamCollectionInfo>();
        }
    }

    /// <summary>Makes a WSGM-owned collection's membership exactly
    /// <paramref name="appIds"/> (adds/removes the difference) and saves. When
    /// <paramref name="existingId"/> names a still-present collection it is updated
    /// in place; otherwise a NEW collection is created (never an existing one
    /// adopted by name — that would clobber a user/SRM collection). Idempotent.</summary>
    /// <param name="name">The name for a newly created collection.</param>
    /// <param name="appIds">The exact app ids the collection should contain.</param>
    /// <param name="existingId">The id WSGM previously created for this tab, or
    /// null to always create a new one.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<SteamCollectionSyncResult> SyncAsync(
        string name, IReadOnlyCollection<long> appIds, string? existingId = null,
        CancellationToken cancellationToken = default)
    {
        var nameLiteral = SteamCef.JsString(name);
        var wantLiteral = "[" + string.Join(
            ",", appIds.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]";
        var existingLiteral = string.IsNullOrEmpty(existingId)
            ? "null"
            : SteamCef.JsString(existingId);

        var expression =
            "(async()=>{try{const cs=collectionStore,as=appStore;" +
            "const name=" + nameLiteral + ",want=" + wantLiteral + ",existing=" + existingLiteral + ";" +
            "let col=null;" +
            "if(existing){col=cs.GetCollection(existing);" +
            "if(col&&!(cs.userCollections||[]).some(c=>c.id===col.id))col=null;}" +
            "if(!col){const ov=want.map(id=>as.GetAppOverviewByAppID(id)).filter(Boolean);" +
            "col=cs.NewUnsavedCollection(name,undefined,ov);await col.Save();}" +
            "const cur=new Set((col.allApps||col.visibleApps||[]).map(a=>a.appid));" +
            "const wantSet=new Set(want);" +
            "const toAdd=want.filter(id=>!cur.has(id)).map(id=>as.GetAppOverviewByAppID(id)).filter(Boolean);" +
            "const toRemove=[...cur].filter(id=>!wantSet.has(id)).map(id=>as.GetAppOverviewByAppID(id)).filter(Boolean);" +
            "const dd=col.AsDragDropCollection?col.AsDragDropCollection():null;" +
            "if(dd){if(toAdd.length)dd.AddApps(toAdd);if(toRemove.length)dd.RemoveApps(toRemove);}" +
            "if(toAdd.length||toRemove.length)await col.Save();" +
            "return JSON.stringify({ok:true,id:col.id,count:(col.allApps||col.visibleApps||[]).length," +
            "added:toAdd.length,removed:toRemove.length});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        var result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return new SteamCollectionSyncResult(false, false, null, result.Error);
        }
        if (result.Value is null)
        {
            return new SteamCollectionSyncResult(true, false, null, "No response from Steam.");
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var added = root.TryGetProperty("added", out var a) ? a.GetInt32() : 0;
                var removed = root.TryGetProperty("removed", out var r) ? r.GetInt32() : 0;
                Log.Info($"Steam collection synced: \"{name}\" (+{added}/-{removed}).");
                return new SteamCollectionSyncResult(true, true, id, null);
            }
            var err = root.TryGetProperty("err", out var e) ? e.GetString() : "unknown error";
            Log.Warn($"Steam collection sync failed for \"{name}\": {err}.");
            return new SteamCollectionSyncResult(true, false, null, err);
        }
        catch (Exception ex)
        {
            return new SteamCollectionSyncResult(true, false, null, ex.Message);
        }
    }

    /// <summary>Outcome of evaluating a compiled filter over the library.</summary>
    /// <param name="Reachable">Whether Steam's debug port answered.</param>
    /// <param name="Ok">Whether Steam evaluated and returned a valid result.</param>
    /// <param name="AppIds">The matching app ids (empty is a valid successful result).</param>
    public readonly record struct FilterEvalResult(bool Reachable, bool Ok, IReadOnlyList<long> AppIds);

    /// <summary>Runs a compiled filter evaluation (from
    /// <see cref="LibraryFilter.BuildEvaluation"/>) in Steam's <c>appStore</c> and
    /// returns the matching app ids. The predicate runs entirely in Steam's V8, so
    /// every overview field is read live.</summary>
    /// <param name="evaluationJs">The self-contained IIFE the filter compiler produced,
    /// resolving to <c>JSON.stringify({ok, appids})</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<FilterEvalResult> EvaluateFilterAsync(
        string evaluationJs, CancellationToken cancellationToken = default)
    {
        var result = await SteamCef.EvaluateAsync(evaluationJs, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable)
        {
            return new FilterEvalResult(false, false, Array.Empty<long>());
        }
        if (result.Value is null)
        {
            return new FilterEvalResult(true, false, Array.Empty<long>());
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("appids", out var ids))
            {
                var err = root.TryGetProperty("err", out var e) ? e.GetString() : "unknown error";
                Log.Warn($"Filter evaluation failed: {err}.");
                return new FilterEvalResult(true, false, Array.Empty<long>());
            }
            var list = new List<long>();
            foreach (var appId in ids.EnumerateArray())
            {
                if (appId.TryGetInt64(out var value))
                {
                    list.Add(value);
                }
            }
            return new FilterEvalResult(true, true, list);
        }
        catch (Exception ex)
        {
            Log.Warn($"Filter evaluation parse failed: {ex.Message}");
            return new FilterEvalResult(true, false, Array.Empty<long>());
        }
    }

    /// <summary>Evaluates multiple compiled filters in one CEF exchange.</summary>
    /// <param name="filterExpressions">Self-contained filter IIFEs.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<IReadOnlyList<FilterEvalResult>> EvaluateFiltersAsync(
        IReadOnlyList<string> filterExpressions, CancellationToken cancellationToken = default)
    {
        if (filterExpressions.Count == 0)
        {
            return Array.Empty<FilterEvalResult>();
        }
        var expression = "(()=>JSON.stringify({values:[" + string.Join(",", filterExpressions
            .Select(static value => "JSON.parse((" + value + "))")) + "]}))()";
        var result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return Enumerable.Repeat(
                new FilterEvalResult(false, false, Array.Empty<long>()), filterExpressions.Count).ToList();
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var values = document.RootElement.GetProperty("values");
            var output = new List<FilterEvalResult>();
            foreach (var value in values.EnumerateArray())
            {
                if (!value.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                    || !value.TryGetProperty("appids", out var appids))
                {
                    output.Add(new FilterEvalResult(true, false, Array.Empty<long>()));
                    continue;
                }
                var ids = new List<long>();
                foreach (var appid in appids.EnumerateArray())
                {
                    if (appid.TryGetInt64(out var id))
                    {
                        ids.Add(id);
                    }
                }
                output.Add(new FilterEvalResult(true, true, ids));
            }
            while (output.Count < filterExpressions.Count)
            {
                output.Add(new FilterEvalResult(true, false, Array.Empty<long>()));
            }
            return output;
        }
        catch (Exception ex)
        {
            Log.Warn($"Batched filter evaluation parse failed: {ex.Message}");
            return Enumerable.Repeat(
                new FilterEvalResult(true, false, Array.Empty<long>()), filterExpressions.Count).ToList();
        }
    }

    /// <summary>One app's id and display name (for whitelist/blacklist pickers,
    /// card "view games" name resolution, and the artwork changer's target list).</summary>
    /// <param name="AppId">The Steam app id (a shortcut's generated id for shortcuts).</param>
    /// <param name="Name">The display name.</param>
    /// <param name="Shortcut">True for a non-Steam shortcut, whose id has no Steam
    /// store page (SteamGridDB lookups must go by name instead).</param>
    public sealed record AppInfo(long AppId, string Name, bool Shortcut = false);

    /// <summary>Lists the user's games AND non-Steam shortcuts (id + name), sorted by
    /// name — the source for the whitelist/blacklist app pickers, for resolving a
    /// card's installed ids to names, and for the artwork changer. Shortcuts come from
    /// the all-apps collection (the type-games collection excludes them) and are
    /// flagged, since their generated ids mean nothing outside this machine.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<IReadOnlyList<AppInfo>> GetGamesAsync(
        CancellationToken cancellationToken = default)
    {
        const string expression =
            "(()=>{try{const cs=collectionStore;" +
            "const g=cs.GetCollection('type-games');" +
            "const games=(g&&(g.allApps||g.visibleApps))||[];" +
            "const ids=new Set(games.map(a=>a.appid));" +
            "const ac=cs.allAppsCollection;" +
            "const all=(ac&&(ac.allApps||ac.visibleApps))||games;" +
            "const out=[];const seen=new Set();" +
            "for(const a of all){" +
            "const sc=typeof a.BIsShortcut==='function'?!!a.BIsShortcut():a.appid>=2147483648;" +
            "if(!ids.has(a.appid)&&!sc)continue;" +
            "if(seen.has(a.appid))continue;seen.add(a.appid);" +
            "out.push({id:a.appid,name:a.display_name||String(a.appid),sc:sc});}" +
            "return JSON.stringify({ok:true,apps:out});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        var result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return Array.Empty<AppInfo>();
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("apps", out var apps))
            {
                return Array.Empty<AppInfo>();
            }
            var list = new List<AppInfo>();
            foreach (var app in apps.EnumerateArray())
            {
                if (app.GetProperty("id").TryGetInt64(out var id))
                {
                    var shortcut = app.TryGetProperty("sc", out var sc)
                        && sc.ValueKind == JsonValueKind.True;
                    list.Add(new AppInfo(
                        id,
                        app.GetProperty("name").GetString() ?? id.ToString(CultureInfo.InvariantCulture),
                        shortcut));
                }
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam games list parse failed: {ex.Message}");
            return Array.Empty<AppInfo>();
        }
    }

    /// <summary>One store tag (genre) present in the library.</summary>
    /// <param name="TagId">Steam's numeric tag id.</param>
    /// <param name="Name">The localized tag name.</param>
    /// <param name="Count">How many library games carry it.</param>
    public sealed record TagInfo(int TagId, string Name, int Count);

    /// <summary>Lists the store tags (genres) actually used in the library, with their
    /// localized names and game counts, most-used first — the source for the Tag
    /// filter's multi-select.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<IReadOnlyList<TagInfo>> GetLibraryTagsAsync(
        CancellationToken cancellationToken = default)
    {
        const string expression =
            "(()=>{try{const cs=collectionStore,as=appStore;" +
            "const g=cs.GetCollection('type-games');" +
            "const apps=(g&&(g.allApps||g.visibleApps))||[];" +
            "const m=as.m_mapStoreTagLocalization||{};const byTag={};" +
            "for(const a of apps)for(const t of (a.store_tag||[])){const nm=m[t];if(!nm)continue;" +
            "(byTag[t]=byTag[t]||{id:t,name:nm,count:0}).count++;}" +
            "const out=Object.values(byTag).sort((a,b)=>b.count-a.count);" +
            "return JSON.stringify({ok:true,tags:out});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        var result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return Array.Empty<TagInfo>();
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("tags", out var tags))
            {
                return Array.Empty<TagInfo>();
            }
            var list = new List<TagInfo>();
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.GetProperty("id").TryGetInt32(out var id))
                {
                    var name = tag.GetProperty("name").GetString() ?? "";
                    var count = tag.TryGetProperty("count", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
                    if (name.Length > 0)
                    {
                        list.Add(new TagInfo(id, name, count));
                    }
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam tags list parse failed: {ex.Message}");
            return Array.Empty<TagInfo>();
        }
    }

    /// <summary>Deletes the WSGM-owned collection with the given id, if it is still
    /// a present user collection. Keyed by id, never name, so it can never remove a
    /// user/SRM collection WSGM did not create. Returns whether the channel was
    /// reachable (deleting a missing collection still counts as reachable success).</summary>
    /// <param name="collectionId">The collection id WSGM created.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<bool> DeleteByIdAsync(
        string collectionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(collectionId))
        {
            return true;
        }
        var idLiteral = SteamCef.JsString(collectionId);
        var expression =
            "(async()=>{try{const cs=collectionStore;const id=" + idLiteral + ";" +
            "const col=cs.GetCollection(id);" +
            "if(!col||!(cs.userCollections||[]).some(c=>c.id===col.id))" +
            "return JSON.stringify({ok:true,deleted:false});" +
            "const del=col.AsDeletableCollection&&col.AsDeletableCollection();" +
            "if(del)await del.Delete();else if(typeof col.Delete==='function')await col.Delete();" +
            "else await cs.DeleteCollection(col);" +
            "return JSON.stringify({ok:true,deleted:true});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        var result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            return document.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }
}

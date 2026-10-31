using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Core;

/// <summary>
///     Compiles a <see cref="FilterNode" /> tree into a JavaScript predicate over
///     a Steam <c>appStore</c> app overview, plus a hoisted prologue of reusable lookups
///     (collection sets, compiled regexes, id sets). The shape mirrors TabMaster's
///     <c>filterFunctions</c> evaluation (per-group <c>every</c>/<c>some</c>, per-node
///     <c>inverted ? !r : r</c>). Pure and unit-testable — no Steam contact here; the
///     resulting JS is run by <see cref="EvaluateAsync" />.
/// </summary>
public static class LibraryFilter
{
    /// <summary>
    ///     Whether a kind's invert toggle is meaningful (the others already
    ///     express both directions through their own params).
    /// </summary>
    /// <param name="kind">The filter kind.</param>
    /// <returns>True for collection, tag, regex, SD-card, and merge predicates.</returns>
    public static bool CanInvert(FilterKind kind)
    {
        return kind is
            FilterKind.Collection or FilterKind.Tag or FilterKind.Regex
            or FilterKind.SdCard or FilterKind.Merge;
    }

    /// <summary>
    ///     Whether a node is complete enough to evaluate (mirrors TabMaster's
    ///     <c>isValidParams</c>): non-empty lists/patterns, a merge with ≥1 child, etc.
    /// </summary>
    /// <param name="node">The node to validate.</param>
    /// <returns>Whether required fields and all merge children are valid; an empty merge is invalid.</returns>
    public static bool IsValid(FilterNode node)
    {
        return node.Kind switch
        {
            FilterKind.Collection => !string.IsNullOrEmpty(node.CollectionId),
            FilterKind.Tag => node.TagIds.Count > 0,
            FilterKind.Regex => LibraryFilterPattern.IsSafeRegex(node.Pattern),
            FilterKind.Whitelist or FilterKind.Blacklist => node.AppIds.Count > 0,
            FilterKind.ReleaseDate or FilterKind.LastPlayed => node.DaysAgo > 0 || node.Year > 0,
            FilterKind.SdCard => node.CardScope != SdCardScope.Specific
                                 || !string.IsNullOrEmpty(node.ContentId),
            FilterKind.Merge => node.Children.Count > 0 && node.Children.All(IsValid),
            // Installed/Platform/thresholds are always well-formed (a threshold of 0 is legal).
            _ => true
        };
    }

    /// <summary>
    ///     Builds the complete evaluation expression: prologue lookups, the
    ///     compiled predicate, the category candidate gather, and a JSON result. The
    ///     returned string is a self-contained IIFE that resolves to
    ///     <c>JSON.stringify({ok, appids})</c>.
    /// </summary>
    /// <param name="root">
    ///     The tab's top-level group (its <see cref="FilterNode.Mode" />
    ///     is the tab's AND/OR).
    /// </param>
    /// <param name="categories">Category prefilter bitfield.</param>
    /// <param name="cards">Resolver for SD-card membership.</param>
    /// <returns>A self-contained JavaScript IIFE returning a JSON result; no Steam evaluation occurs here.</returns>
    public static string BuildEvaluation(FilterNode root, LibraryFilterCategories categories, ISdCardResolver cards)
    {
        var emit = new Emitter(cards);
        var predicate = NodeExpr(root, emit);
        var cats = ((int)categories).ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append("(()=>{try{");
        sb.Append("const cs=collectionStore,as=appStore;");
        sb.Append("const _colCache=Object.create(null);");
        sb.Append("const inCol=(id,appid)=>{let s=_colCache[id];if(!s){const c=cs.GetCollection(id);"
                  + "s=_colCache[id]=new Set(((c&&(c.allApps||c.visibleApps))||[]).map(x=>x.appid));}"
                  + "return s.has(appid);};");
        sb.Append(emit.Prologue);
        sb.Append("const pred=(a)=>(").Append(predicate).Append(");");
        sb.Append("const cats=").Append(cats).Append(';');
        sb.Append("const seen=new Set(),cand=[];");
        sb.Append("const addC=(id)=>{const c=cs.GetCollection(id);if(!c)return;"
                  + "const arr=(cats&16)?(c.allApps||[]):(c.visibleApps||c.allApps||[]);"
                  + "for(const a of arr){if(!seen.has(a.appid)){seen.add(a.appid);cand.push(a);}}};");
        sb.Append("if(cats&1)addC('type-games');");
        sb.Append("if(cats&2)addC('type-software');");
        sb.Append("if(cats&8192)addC('type-music');");
        sb.Append("const out=[];for(const a of cand){try{if(pred(a))out.push(a.appid);}catch(e){}}");
        sb.Append("return JSON.stringify({ok:true,appids:out});}");
        sb.Append("catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()");
        return sb.ToString();
    }

    /// <summary>Compiles just the predicate expression for a node (exposed for tests).</summary>
    /// <param name="node">The node to compile.</param>
    /// <param name="cards">Resolver for SD-card membership.</param>
    /// <returns>The predicate expression only; any hoisted lookup declarations are omitted.</returns>
    internal static string CompilePredicate(FilterNode node, ISdCardResolver cards)
    {
        return NodeExpr(node, new Emitter(cards));
    }

    private static string NodeExpr(FilterNode node, Emitter emit)
    {
        var expr = CoreExpr(node, emit);
        return node.Inverted && CanInvert(node.Kind) ? "!(" + expr + ")" : expr;
    }

    private static string CoreExpr(FilterNode node, Emitter emit)
    {
        switch (node.Kind)
        {
            case FilterKind.Merge:
                if (node.Children.Count == 0)
                {
                    return "true";
                }

                var op = node.Mode == FilterMode.And ? "&&" : "||";
                return "(" + string.Join(op, node.Children.Select(c => NodeExpr(c, emit))) + ")";

            case FilterKind.Collection:
                return "inCol(" + Js(node.CollectionId) + ",a.appid)";

            case FilterKind.Installed:
                return node.BoolValue ? "(!!a.installed)" : "(!a.installed)";

            case FilterKind.Tag:
                return TagExpr(node, emit);

            case FilterKind.Regex:
                return RegexExpr(node, emit);

            // Compared unsigned on both sides: the picker saves a shortcut's id unsigned, an older
            // configuration may hold it signed, and Steam keeps it signed in the page.
            case FilterKind.Whitelist:
                return emit.IntSet(UnsignedAppIds(node.AppIds)) + ".has(a.appid>>>0)";

            case FilterKind.Blacklist:
                return "!" + emit.IntSet(UnsignedAppIds(node.AppIds)) + ".has(a.appid>>>0)";

            case FilterKind.Platform:
                return node.Platform == PlatformKind.NonSteam
                    ? "((a.app_type&1073741824)!==0)"
                    : "((a.app_type&1073741824)===0)";

            case FilterKind.ReviewScore:
                return ReviewExpr(node);

            case FilterKind.TimePlayed:
                return TimeExpr(node);

            case FilterKind.SizeOnDisk:
                return "(((Number(a.size_on_disk)||0)/1073741824)" + Cmp(node.Condition)
                                                                   + Num(node.Threshold) + ")";

            case FilterKind.ReleaseDate:
                return "((Number(a.rt_original_release_date)||0)" + Cmp(node.Condition)
                                                                  + DateThreshold(node) + ")";

            case FilterKind.LastPlayed:
                return "((Number(a.rt_last_time_played)||0)" + Cmp(node.Condition)
                                                             + DateThreshold(node) + ")";

            case FilterKind.SdCard:
                return emit.IntSet(emit.Cards.Resolve(node.CardScope, node.ContentId))
                       + ".has(a.appid)";

            default:
                return "true";
        }
    }

    private static string TagExpr(FilterNode node, Emitter emit)
    {
        var arr = emit.IntArray(node.TagIds);
        var method = node.Mode == FilterMode.And ? ".every" : ".some";
        return "(" + arr + method + "(t=>(a.store_tag||[]).includes(t)))";
    }

    private static string RegexExpr(FilterNode node, Emitter emit)
    {
        var rx = emit.Regex(node.Pattern);
        return "(" + rx + "?" + rx + ".test(a.display_name||''):false)";
    }

    private static string ReviewExpr(FilterNode node)
    {
        var field = node.ScoreType == ReviewScoreType.Metacritic
            ? "a.metacritic_score"
            : "a.review_percentage";
        return "((Number(" + field + ")||0)" + Cmp(node.Condition) + Num(node.Threshold) + ")";
    }

    private static string TimeExpr(FilterNode node)
    {
        var perUnit = node.Units switch
        {
            TimeUnit.Hours => 60.0,
            TimeUnit.Days => 1440.0,
            _ => 1.0
        };
        return "((Number(a.minutes_playtime_forever)||0)" + Cmp(node.Condition)
                                                          + Num(node.Threshold * perUnit) + ")";
    }

    // Above = at/after the threshold (>=); Below = before it (<) — matches TabMaster's
    // above/below semantics for both numeric thresholds and dates.
    private static string Cmp(ThresholdCondition c)
    {
        return c == ThresholdCondition.Above ? ">=" : "<";
    }

    // Emitted into JS that runs in Steam's V8, where Date.now() is available (unlike
    // the workflow sandbox). daysAgo takes precedence over an absolute date.
    private static string DateThreshold(FilterNode node)
    {
        if (node.DaysAgo > 0)
        {
            return "(Date.now()/1000-" + node.DaysAgo.ToString(CultureInfo.InvariantCulture)
                                       + "*86400)";
        }

        var y = node.Year > 0 ? node.Year : 1970;
        var m = node.Month is >= 1 and <= 12 ? node.Month : 1;
        var d = node.Day is >= 1 and <= 31 ? node.Day : 1;
        // Date.UTC returns epoch milliseconds as a Number — it is NOT a Date, so calling
        // .getTime() on it throws TypeError and silently empties every absolute-date tab.
        return "(Date.UTC(" + y.ToString(CultureInfo.InvariantCulture) + ","
               + (m - 1).ToString(CultureInfo.InvariantCulture) + ","
               + d.ToString(CultureInfo.InvariantCulture) + ")/1000)";
    }

    private static string Js(string value)
    {
        return SteamCef.JsString(value);
    }

    private static IEnumerable<long> UnsignedAppIds(IEnumerable<long> ids)
    {
        return ids.Select(static id => (long)SteamApps.NormalizeAppId(id));
    }

    private static string Num(double v)
    {
        return v.ToString("0.############", CultureInfo.InvariantCulture);
    }

    /// <summary>Evaluates multiple compiled filters in one CEF exchange.</summary>
    /// <param name="steam">The session's Steam client.</param>
    /// <param name="filterExpressions">Self-contained filter IIFEs.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>Evaluation results in expression order; unanswered/invalid exchanges produce failed results.</returns>
    public static async Task<IReadOnlyList<FilterEvalResult>> EvaluateAsync(
        SteamClient steam, IReadOnlyList<string> filterExpressions, CancellationToken cancellationToken = default)
    {
        // Every filter in one exchange, so a tab sync is one round trip rather than one per tab.
        var budget = TimeSpan.FromSeconds(12);
        if (filterExpressions.Count == 0)
        {
            return [];
        }

        var expression = "(()=>JSON.stringify({values:[" + string.Join(",", filterExpressions
            .Select(static value => "JSON.parse((" + value + "))")) + "]}))()";
        var result = await steam.EvaluateAsync(SteamUiTargetRole.SharedJsContext, expression, budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Answered || result.Value is null)
        {
            return [.. Enumerable.Repeat(new FilterEvalResult(false, false, []), filterExpressions.Count)];
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
                    output.Add(new FilterEvalResult(true, false, []));
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
                output.Add(new FilterEvalResult(true, false, []));
            }

            return output;
        }
        catch (Exception ex)
        {
            Log.Warn($"Batched filter evaluation parse failed: {ex.Message}");
            return [.. Enumerable.Repeat(new FilterEvalResult(true, false, []), filterExpressions.Count)];
        }
    }

    /// <summary>
    ///     Accumulates hoisted prologue declarations (sets/arrays/regexes are
    ///     declared once and referenced from the per-app predicate, so nothing is rebuilt
    ///     per candidate).
    /// </summary>
    private sealed class Emitter(ISdCardResolver cards)
    {
        private readonly StringBuilder _prologue = new();
        private int _n;

        public ISdCardResolver Cards { get; } = cards;

        public string Prologue => _prologue.ToString();

        public string IntSet(IEnumerable<long> ids)
        {
            var name = "_s" + _n++;
            _prologue.Append("const ").Append(name).Append("=new Set([")
                .Append(string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))))
                .Append("]);");
            return name;
        }

        public string IntArray(IEnumerable<int> ids)
        {
            var name = "_a" + _n++;
            _prologue.Append("const ").Append(name).Append("=[")
                .Append(string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))))
                .Append("];");
            return name;
        }

        public string Regex(string pattern)
        {
            var name = "_r" + _n++;
            // Compiled once, case-insensitive; a bad pattern yields null (predicate
            // then treats it as no match) rather than throwing per app.
            _prologue.Append("let ").Append(name).Append(";try{").Append(name)
                .Append("=new RegExp(").Append(SteamCef.JsString(pattern))
                .Append(",'i');}catch(e){").Append(name).Append("=null;}");
            return name;
        }
    }
}

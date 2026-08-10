using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WSGM.Core;

/// <summary>The kind of a <see cref="FilterNode"/>. Mirrors TabMaster's filter set,
/// trimmed to the core the user selected. <see cref="Merge"/> is a group node that
/// nests children under its own <see cref="FilterNode.Mode"/>, giving arbitrary
/// AND/OR trees.</summary>
public enum FilterKind
{
    /// <summary>Membership in a Steam collection.</summary>
    Collection,

    /// <summary>Installed / not installed.</summary>
    Installed,

    /// <summary>Has one of (any/all) the given store tags (genres).</summary>
    Tag,

    /// <summary>Title matches a regular expression.</summary>
    Regex,

    /// <summary>App id is in an explicit include list.</summary>
    Whitelist,

    /// <summary>App id is NOT in an explicit exclude list.</summary>
    Blacklist,

    /// <summary>Steam app vs non-Steam shortcut.</summary>
    Platform,

    /// <summary>Metacritic / Steam review score above or below a threshold.</summary>
    ReviewScore,

    /// <summary>Playtime above or below a threshold.</summary>
    TimePlayed,

    /// <summary>Install size above or below a threshold.</summary>
    SizeOnDisk,

    /// <summary>Original release date on/after or before a point.</summary>
    ReleaseDate,

    /// <summary>Last-played date on/after or before a point.</summary>
    LastPlayed,

    /// <summary>Installed on a MicroSD/removable card WSGM tracks.</summary>
    SdCard,

    /// <summary>A nested group of child filters combined by <see cref="FilterNode.Mode"/>.</summary>
    Merge,
}

/// <summary>Combination logic for a group / multi-value filter.</summary>
public enum FilterMode
{
    /// <summary>Every child / value must match.</summary>
    And,

    /// <summary>At least one child / value must match.</summary>
    Or,
}

/// <summary>Threshold comparison direction.</summary>
public enum ThresholdCondition
{
    /// <summary>Value at or above the threshold (dates: on/after).</summary>
    Above,

    /// <summary>Value below the threshold (dates: before).</summary>
    Below,
}

/// <summary>Which review score a <see cref="FilterKind.ReviewScore"/> filter reads.</summary>
public enum ReviewScoreType
{
    /// <summary>Metacritic score (0–100).</summary>
    Metacritic,

    /// <summary>Steam positive-review percentage (0–100).</summary>
    SteamPercent,
}

/// <summary>Time unit for a <see cref="FilterKind.TimePlayed"/> threshold.</summary>
public enum TimeUnit
{
    /// <summary>Minutes.</summary>
    Minutes,

    /// <summary>Hours.</summary>
    Hours,

    /// <summary>Days.</summary>
    Days,
}

/// <summary>Steam app vs non-Steam shortcut, for <see cref="FilterKind.Platform"/>.</summary>
public enum PlatformKind
{
    /// <summary>A real Steam app.</summary>
    Steam,

    /// <summary>A non-Steam shortcut.</summary>
    NonSteam,
}

/// <summary>Which card(s) a <see cref="FilterKind.SdCard"/> filter matches.</summary>
public enum SdCardScope
{
    /// <summary>The card currently inserted.</summary>
    Inserted,

    /// <summary>Any card WSGM tracks.</summary>
    Any,

    /// <summary>One specific card, by content id (<see cref="FilterNode.ContentId"/>).</summary>
    Specific,
}

/// <summary>One node in a custom tab's filter tree. A flat shape (all params on one
/// type, only those relevant to <see cref="Kind"/> used) keeps it trivially
/// serializable for System.Text.Json source-gen — the same modelling TabMaster uses
/// (<c>{type, inverted, params}</c>). Compiled to a JS predicate by
/// <see cref="LibraryFilter"/>.</summary>
public sealed class FilterNode
{
    /// <summary>The filter kind (selects which params below are meaningful).</summary>
    public FilterKind Kind { get; set; }

    /// <summary>Negates this node's result. Honored for invertible kinds
    /// (<see cref="LibraryFilter.CanInvert"/>); harmless otherwise.</summary>
    public bool Inverted { get; set; }

    /// <summary>Group/multi-value combination: children of a <see cref="FilterKind.Merge"/>,
    /// or the and/or over a <see cref="FilterKind.Tag"/> filter's tag list.</summary>
    public FilterMode Mode { get; set; } = FilterMode.And;

    /// <summary>Child filters of a <see cref="FilterKind.Merge"/> group.</summary>
    public List<FilterNode> Children { get; set; } = [];

    /// <summary>Steam collection id (<see cref="FilterKind.Collection"/>).</summary>
    public string CollectionId { get; set; } = "";

    /// <summary>Boolean param: installed-state for <see cref="FilterKind.Installed"/>.</summary>
    public bool BoolValue { get; set; } = true;

    /// <summary>Tag ids for <see cref="FilterKind.Tag"/>.</summary>
    public List<int> TagIds { get; set; } = [];

    /// <summary>App ids for <see cref="FilterKind.Whitelist"/> / <see cref="FilterKind.Blacklist"/>.</summary>
    public List<long> AppIds { get; set; } = [];

    /// <summary>Title pattern for <see cref="FilterKind.Regex"/>.</summary>
    public string Pattern { get; set; } = "";

    /// <summary>Steam vs non-Steam (<see cref="FilterKind.Platform"/>).</summary>
    public PlatformKind Platform { get; set; } = PlatformKind.Steam;

    /// <summary>Numeric threshold for ReviewScore / TimePlayed / SizeOnDisk.</summary>
    public double Threshold { get; set; }

    /// <summary>Comparison direction for threshold and date filters.</summary>
    public ThresholdCondition Condition { get; set; } = ThresholdCondition.Above;

    /// <summary>Score source for <see cref="FilterKind.ReviewScore"/>.</summary>
    public ReviewScoreType ScoreType { get; set; } = ReviewScoreType.SteamPercent;

    /// <summary>Time unit for <see cref="FilterKind.TimePlayed"/>.</summary>
    public TimeUnit Units { get; set; } = TimeUnit.Hours;

    /// <summary>Relative date param: match apps within this many days of now. When
    /// &gt; 0 it takes precedence over the absolute <see cref="Year"/>/<see cref="Month"/>/<see cref="Day"/>.</summary>
    public int DaysAgo { get; set; }

    /// <summary>Absolute-date year for ReleaseDate / LastPlayed (0 = unset).</summary>
    public int Year { get; set; }

    /// <summary>Absolute-date month 1–12 (0 = unset → treated as January).</summary>
    public int Month { get; set; }

    /// <summary>Absolute-date day 1–31 (0 = unset → treated as the 1st).</summary>
    public int Day { get; set; }

    /// <summary>Which card(s) a <see cref="FilterKind.SdCard"/> filter matches.</summary>
    public SdCardScope CardScope { get; set; } = SdCardScope.Inserted;

    /// <summary>Content id of the specific card for <see cref="SdCardScope.Specific"/>.</summary>
    public string ContentId { get; set; } = "";

    /// <summary>Deep-copies this node (so an editor can cancel without mutating the
    /// saved tree).</summary>
    public FilterNode Clone() => new()
    {
        Kind = Kind,
        Inverted = Inverted,
        Mode = Mode,
        Children = Children.Select(c => c.Clone()).ToList(),
        CollectionId = CollectionId,
        BoolValue = BoolValue,
        TagIds = [.. TagIds],
        AppIds = [.. AppIds],
        Pattern = Pattern,
        Platform = Platform,
        Threshold = Threshold,
        Condition = Condition,
        ScoreType = ScoreType,
        Units = Units,
        DaysAgo = DaysAgo,
        Year = Year,
        Month = Month,
        Day = Day,
        CardScope = CardScope,
        ContentId = ContentId,
    };
}

/// <summary>Resolves the concrete app-id set for a <see cref="FilterKind.SdCard"/> node
/// from WSGM's own card model (Steam does not know our card→game mapping, so it is
/// baked into the compiled JS as a literal set).</summary>
public interface ISdCardResolver
{
    /// <summary>App ids on the card(s) selected by <paramref name="scope"/> /
    /// <paramref name="contentId"/>. Empty when no such card is known.</summary>
    IReadOnlyCollection<long> Resolve(SdCardScope scope, string contentId);
}

/// <summary>Compiles a <see cref="FilterNode"/> tree into a JavaScript predicate over
/// a Steam <c>appStore</c> app overview, plus a hoisted prologue of reusable lookups
/// (collection sets, compiled regexes, id sets). The shape mirrors TabMaster's
/// <c>filterFunctions</c> evaluation (per-group <c>every</c>/<c>some</c>, per-node
/// <c>inverted ? !r : r</c>). Pure and unit-testable — no Steam contact here; the
/// resulting JS is run by <see cref="SteamCollections.EvaluateFilterAsync"/>.</summary>
public static class LibraryFilter
{
    /// <summary>Bitfield category flags (values match TabMaster's so the concepts
    /// line up): which app kinds are candidates before the predicate runs.</summary>
    [Flags]
    public enum Categories
    {
        /// <summary>Games.</summary>
        Games = 1,

        /// <summary>Software / applications.</summary>
        Software = 2,

        /// <summary>Include hidden apps (allApps vs visibleApps).</summary>
        Hidden = 16,

        /// <summary>Soundtracks / music.</summary>
        Music = 8192,
    }

    /// <summary>Whether a kind's invert toggle is meaningful (the others already
    /// express both directions through their own params).</summary>
    /// <param name="kind">The filter kind.</param>
    public static bool CanInvert(FilterKind kind) => kind is
        FilterKind.Collection or FilterKind.Tag or FilterKind.Regex
        or FilterKind.SdCard or FilterKind.Merge;

    /// <summary>Whether a node is complete enough to evaluate (mirrors TabMaster's
    /// <c>isValidParams</c>): non-empty lists/patterns, a merge with ≥1 child, etc.</summary>
    /// <param name="node">The node to validate.</param>
    public static bool IsValid(FilterNode node) => node.Kind switch
    {
        FilterKind.Collection => !string.IsNullOrEmpty(node.CollectionId),
        FilterKind.Tag => node.TagIds.Count > 0,
        FilterKind.Regex => !string.IsNullOrWhiteSpace(node.Pattern),
        FilterKind.Whitelist or FilterKind.Blacklist => node.AppIds.Count > 0,
        FilterKind.ReleaseDate or FilterKind.LastPlayed => node.DaysAgo > 0 || node.Year > 0,
        FilterKind.SdCard => node.CardScope != SdCardScope.Specific
            || !string.IsNullOrEmpty(node.ContentId),
        FilterKind.Merge => node.Children.Count > 0 && node.Children.All(IsValid),
        // Installed/Platform/thresholds are always well-formed (a threshold of 0 is legal).
        _ => true,
    };

    /// <summary>Builds the complete evaluation expression: prologue lookups, the
    /// compiled predicate, the category candidate gather, and a JSON result. The
    /// returned string is a self-contained IIFE that resolves to
    /// <c>JSON.stringify({ok, appids})</c>.</summary>
    /// <param name="root">The tab's top-level group (its <see cref="FilterNode.Mode"/>
    /// is the tab's AND/OR).</param>
    /// <param name="categories">Category prefilter bitfield.</param>
    /// <param name="cards">Resolver for SD-card membership.</param>
    public static string BuildEvaluation(FilterNode root, Categories categories, ISdCardResolver cards)
    {
        var emit = new Emitter(cards);
        var predicate = NodeExpr(root, emit);
        var cats = ((int)categories).ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append("(()=>{try{");
        sb.Append("const cs=collectionStore,as=appStore;");
        sb.Append("const _colCache={};");
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
        sb.Append("if(!cand.length)addC('type-games');");
        sb.Append("const out=[];for(const a of cand){try{if(pred(a))out.push(a.appid);}catch(e){}}");
        sb.Append("return JSON.stringify({ok:true,appids:out});}");
        sb.Append("catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()");
        return sb.ToString();
    }

    /// <summary>Compiles just the predicate expression for a node (exposed for tests).</summary>
    /// <param name="node">The node to compile.</param>
    /// <param name="cards">Resolver for SD-card membership.</param>
    public static string CompilePredicate(FilterNode node, ISdCardResolver cards)
        => NodeExpr(node, new Emitter(cards));

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

            case FilterKind.Whitelist:
                return emit.IntSet(node.AppIds) + ".has(a.appid)";

            case FilterKind.Blacklist:
                return "!" + emit.IntSet(node.AppIds) + ".has(a.appid)";

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
            ? "a.metacritic_score" : "a.review_percentage";
        return "((Number(" + field + ")||0)" + Cmp(node.Condition) + Num(node.Threshold) + ")";
    }

    private static string TimeExpr(FilterNode node)
    {
        var perUnit = node.Units switch
        {
            TimeUnit.Hours => 60.0,
            TimeUnit.Days => 1440.0,
            _ => 1.0,
        };
        return "((Number(a.minutes_playtime_forever)||0)" + Cmp(node.Condition)
            + Num(node.Threshold * perUnit) + ")";
    }

    // Above = at/after the threshold (>=); Below = before it (<) — matches TabMaster's
    // above/below semantics for both numeric thresholds and dates.
    private static string Cmp(ThresholdCondition c) => c == ThresholdCondition.Above ? ">=" : "<";

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
        return "(new Date(" + y.ToString(CultureInfo.InvariantCulture) + ","
            + (m - 1).ToString(CultureInfo.InvariantCulture) + ","
            + d.ToString(CultureInfo.InvariantCulture) + ").getTime()/1000)";
    }

    private static string Js(string value) => SteamCef.JsString(value);

    private static string Num(double v) =>
        v.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>Accumulates hoisted prologue declarations (sets/arrays/regexes are
    /// declared once and referenced from the per-app predicate, so nothing is rebuilt
    /// per candidate).</summary>
    private sealed class Emitter
    {
        private readonly StringBuilder _prologue = new();
        private int _n;

        public Emitter(ISdCardResolver cards) => Cards = cards;

        public ISdCardResolver Cards { get; }

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

using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>
///     One node in a custom tab's filter tree. A flat shape (all params on one
///     type, only those relevant to <see cref="Kind" /> used) keeps it trivially
///     serializable for System.Text.Json source-gen — the same modelling TabMaster uses
///     (<c>{type, inverted, params}</c>). Compiled to a JS predicate by
///     <see cref="LibraryFilter" />.
/// </summary>
public sealed class FilterNode
{
    /// <summary>The filter kind (selects which params below are meaningful).</summary>
    public FilterKind Kind { get; set; }

    /// <summary>
    ///     Negates this node's result. Honored for invertible kinds
    ///     (<see cref="LibraryFilter.CanInvert" />); harmless otherwise.
    /// </summary>
    public bool Inverted { get; set; }

    /// <summary>
    ///     Group/multi-value combination: children of a <see cref="FilterKind.Merge" />,
    ///     or the and/or over a <see cref="FilterKind.Tag" /> filter's tag list.
    /// </summary>
    public FilterMode Mode { get; set; } = FilterMode.And;

    /// <summary>Child filters of a <see cref="FilterKind.Merge" /> group.</summary>
    public List<FilterNode> Children { get; set; } = [];

    /// <summary>Steam collection id (<see cref="FilterKind.Collection" />).</summary>
    public string CollectionId { get; set; } = "";

    /// <summary>Boolean param: installed-state for <see cref="FilterKind.Installed" />.</summary>
    public bool BoolValue { get; set; } = true;

    /// <summary>Tag ids for <see cref="FilterKind.Tag" />.</summary>
    public List<int> TagIds { get; set; } = [];

    /// <summary>App ids for <see cref="FilterKind.Whitelist" /> / <see cref="FilterKind.Blacklist" />.</summary>
    public List<long> AppIds { get; set; } = [];

    /// <summary>Title pattern for <see cref="FilterKind.Regex" />.</summary>
    public string Pattern { get; set; } = "";

    /// <summary>Steam vs non-Steam (<see cref="FilterKind.Platform" />).</summary>
    public PlatformKind Platform { get; set; } = PlatformKind.Steam;

    /// <summary>Numeric threshold for ReviewScore / TimePlayed / SizeOnDisk.</summary>
    public double Threshold { get; set; }

    /// <summary>Comparison direction for threshold and date filters.</summary>
    public ThresholdCondition Condition { get; set; } = ThresholdCondition.Above;

    /// <summary>Score source for <see cref="FilterKind.ReviewScore" />.</summary>
    public ReviewScoreType ScoreType { get; set; } = ReviewScoreType.SteamPercent;

    /// <summary>Time unit for <see cref="FilterKind.TimePlayed" />.</summary>
    public TimeUnit Units { get; set; } = TimeUnit.Hours;

    /// <summary>
    ///     Relative date param: match apps within this many days of now. When
    ///     &gt; 0 it takes precedence over the absolute <see cref="Year" />/<see cref="Month" />/<see cref="Day" />.
    /// </summary>
    public int DaysAgo { get; set; }

    /// <summary>Absolute-date year for ReleaseDate / LastPlayed (0 = unset).</summary>
    public int Year { get; set; }

    /// <summary>Absolute-date month 1–12 (0 = unset → treated as January).</summary>
    public int Month { get; set; }

    /// <summary>Absolute-date day 1–31 (0 = unset → treated as the 1st).</summary>
    public int Day { get; set; }

    /// <summary>Which card(s) a <see cref="FilterKind.SdCard" /> filter matches.</summary>
    public SdCardScope CardScope { get; set; } = SdCardScope.Inserted;

    /// <summary>Content id of the specific card for <see cref="SdCardScope.Specific" />.</summary>
    public string ContentId { get; set; } = "";

    /// <summary>
    ///     Deep-copies this node (so an editor can cancel without mutating the
    ///     saved tree).
    /// </summary>
    /// <returns>A new node with recursively copied children and independent mutable lists.</returns>
    public FilterNode Clone()
    {
        return new FilterNode
        {
            Kind = Kind,
            Inverted = Inverted,
            Mode = Mode,
            Children = [.. Children.Select(c => c.Clone())],
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
            ContentId = ContentId
        };
    }
}

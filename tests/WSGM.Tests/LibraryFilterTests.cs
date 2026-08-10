using WSGM.Core;

namespace WSGM.Tests;

public sealed class LibraryFilterTests
{
    private sealed class StubCards(IReadOnlyCollection<int> ids) : ISdCardResolver
    {
        public IReadOnlyCollection<int> Resolve(SdCardScope scope, string contentId) => ids;
    }

    private static readonly ISdCardResolver NoCards = new StubCards([]);

    private static string Compile(FilterNode node) => LibraryFilter.CompilePredicate(node, NoCards);

    // Full evaluation (predicate + hoisted prologue) — hoisted literals (id sets, tag
    // arrays, compiled regexes) live in the prologue, not the returned predicate.
    private static string Full(FilterNode node, ISdCardResolver? cards = null)
        => LibraryFilter.BuildEvaluation(node, LibraryFilter.Categories.Games, cards ?? NoCards);

    // ---- per-kind predicates ----

    [Fact]
    public void InstalledCompilesToTruthyCheck()
    {
        Assert.Equal("(!!a.installed)", Compile(new FilterNode { Kind = FilterKind.Installed, BoolValue = true }));
        Assert.Equal("(!a.installed)", Compile(new FilterNode { Kind = FilterKind.Installed, BoolValue = false }));
    }

    [Fact]
    public void TagAndUsesEveryOrUsesSome()
    {
        var andNode = new FilterNode { Kind = FilterKind.Tag, Mode = FilterMode.And, TagIds = [10, 20] };
        Assert.Contains("[10,20]", Full(andNode));
        Assert.Contains(".every(", Compile(andNode));

        var orNode = new FilterNode { Kind = FilterKind.Tag, Mode = FilterMode.Or, TagIds = [10] };
        Assert.Contains(".some(", Compile(orNode));
    }

    [Fact]
    public void RegexEmbedsEncodedPattern()
    {
        var node = new FilterNode { Kind = FilterKind.Regex, Pattern = "^Half" };
        Assert.Contains("\"^Half\"", Full(node));
        Assert.Contains(".test(a.display_name", Compile(node));
    }

    [Fact]
    public void WhitelistAndBlacklistUseSetMembership()
    {
        Assert.Contains(".has(a.appid)", Compile(new FilterNode { Kind = FilterKind.Whitelist, AppIds = [1, 2] }));
        Assert.StartsWith("!", Compile(new FilterNode { Kind = FilterKind.Blacklist, AppIds = [1, 2] }));
    }

    [Fact]
    public void PlatformDistinguishesSteamFromNonSteam()
    {
        Assert.Contains("===0", Compile(new FilterNode { Kind = FilterKind.Platform, Platform = PlatformKind.Steam }));
        Assert.Contains("!==0", Compile(new FilterNode { Kind = FilterKind.Platform, Platform = PlatformKind.NonSteam }));
    }

    [Fact]
    public void ReviewScorePicksFieldAndCondition()
    {
        var meta = Compile(new FilterNode { Kind = FilterKind.ReviewScore, ScoreType = ReviewScoreType.Metacritic, Condition = ThresholdCondition.Above, Threshold = 80 });
        Assert.Contains("a.metacritic_score", meta);
        Assert.Contains(">=80", meta);

        var pct = Compile(new FilterNode { Kind = FilterKind.ReviewScore, ScoreType = ReviewScoreType.SteamPercent, Condition = ThresholdCondition.Below, Threshold = 50 });
        Assert.Contains("a.review_percentage", pct);
        Assert.Contains("<50", pct);
    }

    [Fact]
    public void TimePlayedConvertsUnitsToMinutes()
    {
        var hours = Compile(new FilterNode { Kind = FilterKind.TimePlayed, Units = TimeUnit.Hours, Threshold = 2, Condition = ThresholdCondition.Above });
        Assert.Contains("minutes_playtime_forever", hours);
        Assert.Contains(">=120", hours);

        var days = Compile(new FilterNode { Kind = FilterKind.TimePlayed, Units = TimeUnit.Days, Threshold = 1, Condition = ThresholdCondition.Above });
        Assert.Contains(">=1440", days);
    }

    [Fact]
    public void SizeOnDiskComparesGigabytes()
        => Assert.Contains("/1073741824", Compile(new FilterNode { Kind = FilterKind.SizeOnDisk, Threshold = 10, Condition = ThresholdCondition.Above }));

    [Fact]
    public void ReleaseDateDaysAgoUsesRelativeThreshold()
    {
        var js = Compile(new FilterNode { Kind = FilterKind.ReleaseDate, DaysAgo = 30, Condition = ThresholdCondition.Above });
        Assert.Contains("rt_original_release_date", js);
        Assert.Contains("Date.now()", js);
        Assert.Contains("30*86400", js);
    }

    [Fact]
    public void SdCardBakesResolvedAppIdsAsSet()
    {
        var cards = new StubCards([7, 8, 9]);
        var node = new FilterNode { Kind = FilterKind.SdCard, CardScope = SdCardScope.Inserted };
        Assert.Contains("7,8,9", Full(node, cards));
        Assert.Contains(".has(a.appid)", LibraryFilter.CompilePredicate(node, cards));
    }

    // ---- combination + inversion ----

    [Fact]
    public void MergeAndJoinsChildrenWithAmpersands()
    {
        var node = new FilterNode { Kind = FilterKind.Merge, Mode = FilterMode.And };
        node.Children.Add(new FilterNode { Kind = FilterKind.Installed, BoolValue = true });
        node.Children.Add(new FilterNode { Kind = FilterKind.Platform, Platform = PlatformKind.Steam });
        Assert.Contains("&&", Compile(node));
    }

    [Fact]
    public void MergeOrJoinsChildrenWithPipes()
    {
        var node = new FilterNode { Kind = FilterKind.Merge, Mode = FilterMode.Or };
        node.Children.Add(new FilterNode { Kind = FilterKind.Installed, BoolValue = true });
        node.Children.Add(new FilterNode { Kind = FilterKind.Installed, BoolValue = false });
        Assert.Contains("||", Compile(node));
    }

    [Fact]
    public void EmptyMergeIsAlwaysTrue()
        => Assert.Equal("true", Compile(new FilterNode { Kind = FilterKind.Merge }));

    [Fact]
    public void InvertWrapsInvertibleKinds()
    {
        var inverted = Compile(new FilterNode { Kind = FilterKind.Collection, CollectionId = "uc-1", Inverted = true });
        Assert.StartsWith("!(", inverted);
    }

    [Fact]
    public void InvertIsIgnoredForNonInvertibleKinds()
    {
        // Installed is not invertible (its BoolValue already expresses both directions).
        var js = Compile(new FilterNode { Kind = FilterKind.Installed, BoolValue = true, Inverted = true });
        Assert.Equal("(!!a.installed)", js);
    }

    // ---- validity ----

    [Theory]
    [InlineData(FilterKind.Tag, false)]
    [InlineData(FilterKind.Regex, false)]
    [InlineData(FilterKind.Collection, false)]
    [InlineData(FilterKind.Whitelist, false)]
    [InlineData(FilterKind.Installed, true)]
    [InlineData(FilterKind.Platform, true)]
    public void ValidityRequiresPopulatedParams(FilterKind kind, bool validWhenEmpty)
        => Assert.Equal(validWhenEmpty, LibraryFilter.IsValid(new FilterNode { Kind = kind }));

    [Fact]
    public void MergeIsValidOnlyWhenEveryChildIs()
    {
        var group = new FilterNode { Kind = FilterKind.Merge };
        group.Children.Add(new FilterNode { Kind = FilterKind.Regex }); // empty pattern → invalid
        Assert.False(LibraryFilter.IsValid(group));

        group.Children[0].Pattern = "x";
        Assert.True(LibraryFilter.IsValid(group));
    }

    // ---- full evaluation wrapper ----

    [Fact]
    public void BuildEvaluationEmitsCandidateGatherAndJsonResult()
    {
        var root = new FilterNode { Kind = FilterKind.Merge, Mode = FilterMode.And };
        root.Children.Add(new FilterNode { Kind = FilterKind.Installed, BoolValue = true });
        var js = LibraryFilter.BuildEvaluation(root, LibraryFilter.Categories.Games, NoCards);

        Assert.Contains("collectionStore", js);
        Assert.Contains("type-games", js);
        Assert.Contains("JSON.stringify({ok:true,appids:out})", js);
        Assert.Contains("appStore", js);
    }

    [Fact]
    public void CanInvertMatchesTabMasterSet()
    {
        Assert.True(LibraryFilter.CanInvert(FilterKind.Collection));
        Assert.True(LibraryFilter.CanInvert(FilterKind.Tag));
        Assert.True(LibraryFilter.CanInvert(FilterKind.Merge));
        Assert.False(LibraryFilter.CanInvert(FilterKind.Installed));
        Assert.False(LibraryFilter.CanInvert(FilterKind.ReviewScore));
    }
}

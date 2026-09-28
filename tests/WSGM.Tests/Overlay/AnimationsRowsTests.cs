using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Overlay;

public sealed class AnimationsRowsTests
{
    private static SteamAnimationsItem Item(string id, string target = AnimationTargets.Boot, bool downloaded = true,
        bool custom = false)
    {
        return new SteamAnimationsItem(id, id, "Squishy", target, null, null, "", 4, 20, "2025-01-01", downloaded,
            custom);
    }

    private static SteamAnimationsState State(bool restart, params (string Slot, string Id)[] slots)
    {
        var assignment = AnimationSlots.All.ToDictionary(slot => slot, _ => "", StringComparer.Ordinal);
        foreach (var (slot, id) in slots)
        {
            assignment[slot] = id;
        }

        return new SteamAnimationsState("browse", assignment, [Item("a")],
            new SteamAnimationsBrowse("all", "Newest", "", [], 0, false, null, null), null,
            new SteamAnimationsSettings(false, @"C:\lib", @"C:\steam\movies", restart), false, null, null, 1);
    }

    [Fact]
    public void TheSummaryCountsTheSlotsPlayingFromTheLibraryAndSaysWhenSteamMustRestart()
    {
        Assert.Equal("Steam plays its own boot and suspend movies.", AnimationsRows.Summary(State(false)));
        Assert.Equal("2 of 3 slots play a movie from the library. Restart Steam to see the change.",
            AnimationsRows.Summary(State(true, (AnimationSlots.Boot, "a"), (AnimationSlots.Throbber, "a"))));
    }

    [Fact]
    public void ALibraryLineNamesTheKindTheAuthorAndTheSlotsItPlaysIn()
    {
        var state = State(false, (AnimationSlots.Suspend, "a"), (AnimationSlots.Throbber, "a"));
        Assert.Equal("Suspend · Squishy · Plays: Suspend, Suspend from a game",
            AnimationsRows.Describe(Item("a", AnimationTargets.Suspend), state));
        Assert.Equal("Your file · Squishy",
            AnimationsRows.Describe(Item("b", AnimationTargets.Any, custom: true), state));
    }

    [Fact]
    public void AListingLineCarriesTheCountsAndWhetherTheLibraryHoldsIt()
    {
        Assert.Equal("Boot · Squishy · 2025-01-01 · 4 likes · 20 downloads · In the library",
            AnimationsRows.DescribeListing(Item("a")));
        Assert.Equal("Suspend · Squishy · 2025-01-01 · 4 likes · 20 downloads",
            AnimationsRows.DescribeListing(Item("b", AnimationTargets.Suspend, false)));
    }
}

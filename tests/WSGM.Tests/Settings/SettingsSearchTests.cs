using WSGM.Settings;

namespace WSGM.Tests.Settings;

public sealed class SettingsSearchTests
{
    private static readonly SettingsSearchEntry[] Entries =
    [
        new(0, "General › Power", "Sleep again after an unexplained wake", "Back to sleep when nobody woke it.", "On", null),
        new(0, "General › Start", "Start WSGM at sign-in", "The service starts WSGM.", "On", null),
        new(1, "Steam › Session", "Restart Steam when it exits", "Brings Big Picture back.", "On", null),
        new(6, "Startup › Timing", "Before the first app", "Gives Windows time after startup.", string.Empty, null)
    ];

    [Fact]
    public void ATitleThatStartsWithTheQueryComesBeforeOneThatHoldsItAndThenTheRest()
    {
        var found = SettingsSearch.Find(Entries, "start");

        Assert.Equal(["Start WSGM at sign-in", "Restart Steam when it exits", "Before the first app"],
            found.Select(entry => entry.Title));
    }

    [Fact]
    public void EveryWordMustAppearButAnywhere()
    {
        Assert.Equal("Restart Steam when it exits", Assert.Single(SettingsSearch.Find(Entries, "steam session")).Title);
        Assert.Empty(SettingsSearch.Find(Entries, "steam wake"));
    }

    [Fact]
    public void CaseDoesNotMatter()
    {
        Assert.Single(SettingsSearch.Find(Entries, "BIG PICTURE"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyQueryFindsNothing(string query)
    {
        Assert.Empty(SettingsSearch.Find(Entries, query));
    }
}

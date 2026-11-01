using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamClientBuildNoticeTests
{
    private static readonly SteamClientBuild Checked = new("stable", "1792618285", "3708f234683c51cc73a9");
    private static readonly SteamClientBuild Updated = new("stable", "1799999999", null);
    private static readonly SteamClientBuild[] Known = [Checked];

    [Fact]
    public void ACheckedBuildShowsNothing()
    {
        Harness harness = new(Checked);

        Assert.Equal(SteamClientBuildStatus.Checked, harness.Notice.Evaluate());
        Assert.Null(harness.Notice.Text);
        Assert.Equal(0, harness.Changes);
    }

    [Fact]
    public void AnUncheckedBuildShowsItsNoticeOnce()
    {
        Harness harness = new(Updated);

        Assert.Equal(SteamClientBuildStatus.Unchecked, harness.Notice.Evaluate());
        Assert.Equal(SteamClientBuildNotice.Describe(Updated), harness.Notice.Text);
        Assert.Contains("Stable 1799999999", harness.Notice.Text, StringComparison.Ordinal);
        Assert.Contains(SteamClientBuildNotice.ReportUrl, harness.Notice.Text, StringComparison.Ordinal);
        harness.Notice.Evaluate();
        Assert.Equal(1, harness.Changes);
    }

    [Fact]
    public void AnUnreadableBuildShowsNothing()
    {
        Harness harness = new(null);

        Assert.Equal(SteamClientBuildStatus.Unreadable, harness.Notice.Evaluate());
        Assert.Null(harness.Notice.Text);
    }

    [Fact]
    public void ADismissedBuildStaysDismissedAndALaterBuildShowsAgain()
    {
        Harness harness = new(Updated);
        harness.Notice.Evaluate();

        Assert.True(harness.Notice.Dismiss());
        Assert.Null(harness.Notice.Text);
        Assert.Equal(Updated.Key, harness.Stored.DismissedSteamClientBuild);
        Assert.False(harness.Notice.Dismiss());

        // The next start reads the same build: the dismissal holds.
        Harness restarted = new(Updated) { Stored = harness.Stored };
        restarted.Notice.Evaluate();
        Assert.Null(restarted.Notice.Text);

        // Steam updates again: the newer build is announced.
        SteamClientBuild later = new("stable", "1800000000", null);
        restarted.Build = later;
        restarted.Notice.Evaluate();
        Assert.Equal(SteamClientBuildNotice.Describe(later), restarted.Notice.Text);
    }

    [Fact]
    public void ABetaBuildNamesItsChannel()
    {
        Assert.Contains("Beta 1793235415",
            SteamClientBuildNotice.Describe(new SteamClientBuild("publicbeta", "1793235415", null)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADismissalThatCannotBeSavedStillHidesTheNoticeForThisSession()
    {
        Harness harness = new(Updated) { CommitFails = true };
        harness.Notice.Evaluate();

        Assert.True(harness.Notice.Dismiss());
        Assert.Null(harness.Notice.Text);
        Assert.Null(harness.Stored.DismissedSteamClientBuild);
    }

    private sealed class Harness
    {
        internal Harness(SteamClientBuild? build)
        {
            Build = build;
            Notice = new SteamClientBuildNotice(() => Build, Known, () => Stored, (change, _) =>
            {
                if (CommitFails)
                {
                    throw new IOException("The configuration is locked.");
                }

                var fresh = ConfigJson.Clone(Stored, ConfigJsonContext.Default.AppConfig);
                change(fresh);
                Stored = fresh;
                return fresh;
            });
            Notice.Changed += () => Changes++;
        }

        internal SteamClientBuild? Build { get; set; }

        internal int Changes { get; private set; }

        internal bool CommitFails { get; init; }

        internal SteamClientBuildNotice Notice { get; }

        internal AppConfig Stored { get; set; } = AppConfigRules.Normalize(new AppConfig()).Value;
    }
}

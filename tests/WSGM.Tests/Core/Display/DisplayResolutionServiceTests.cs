using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class DisplayResolutionServiceTests
{
    private static readonly DisplayResolution[] Accepted =
    [
        new(1280, 800),
        new(1920, 1200)
    ];

    private static DisplayResolutionService Service(
        List<DisplayResolution> applied,
        int[]? discoveryCount = null,
        bool applySucceeds = true)
    {
        var counter = discoveryCount ?? new int[1];
        return new DisplayResolutionService(
            () =>
            {
                counter[0]++;
                return Accepted;
            },
            (width, height) =>
            {
                if (applySucceeds)
                {
                    applied.Add(new DisplayResolution(width, height));
                }

                return applySucceeds;
            });
    }

    [Fact]
    public void OnlyDiscoveredResolutionsAreOffered()
    {
        List<DisplayResolution> applied = [];

        Assert.Equal(Accepted, Service(applied).Options());
    }

    [Fact]
    public void DiscoveryIsCachedBecauseTestingEveryModeIsNotCheap()
    {
        int[] count = [0];
        var service = Service([], count);

        service.Options();
        service.Options();

        Assert.Equal(1, count[0]);
    }

    [Fact]
    public void DisplayChangeRefreshesSupportedChoices()
    {
        var modes = Accepted;
        List<DisplayResolution> applied = [];
        var service = new DisplayResolutionService(() => modes, (width, height) =>
        {
            applied.Add(new DisplayResolution(width, height));
            return true;
        });
        Assert.True(service.Apply(Accepted[0]));
        modes = [new DisplayResolution(2560, 1440)];

        service.InvalidateOptions();

        Assert.Equal(modes, service.Options());
        Assert.False(service.Apply(Accepted[0]));
        Assert.Equal(Accepted[0], Assert.Single(applied));
    }

    [Fact]
    public void AResolutionDiscoveryDidNotAcceptIsNeverSentToTheDriver()
    {
        // One that was never validated may not display at all, and recovering from a mode the user
        // cannot see is not something to leave them to do.
        List<DisplayResolution> applied = [];
        var service = Service(applied);

        Assert.False(service.Apply(new DisplayResolution(3840, 2160)));
        Assert.Empty(applied);
    }

    [Fact]
    public void AnAcceptedResolutionIsApplied()
    {
        List<DisplayResolution> applied = [];
        var service = Service(applied);

        Assert.True(service.Apply(new DisplayResolution(1280, 800)));
        Assert.Equal(new DisplayResolution(1280, 800), Assert.Single(applied));
    }

    [Fact]
    public void TheChosenResolutionIsTheOnlyWriteSoNothingPutsAnOlderOneBack()
    {
        // An Ally X booted at a saved 1120x630; the user picked 1920x1080, and restoring the mode found
        // before that first change put 1120x630 back when WSGM exited (2026-10-20).
        List<DisplayResolution> applied = [];
        var service = Service(applied);

        service.Apply(new DisplayResolution(1280, 800));
        service.Apply(new DisplayResolution(1920, 1200));

        Assert.Equal([new DisplayResolution(1280, 800), new DisplayResolution(1920, 1200)], applied);
    }

    [Fact]
    public void ARefusedResolutionIsReportedAsNotApplied()
    {
        Assert.False(Service([], applySucceeds: false).Apply(new DisplayResolution(1280, 800)));
    }
}

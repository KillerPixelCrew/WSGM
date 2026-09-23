using WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

/// <summary>
///     The whole injection policy. Two invariants matter more than any individual case: a
///     controller-only launch never takes a route that writes into the game, and neither does a
///     runtime the launcher could not establish.
/// </summary>
public sealed class LaunchRouteSelectorTests
{
    private static readonly PackagedRuntime[] EveryRuntime =
        [PackagedRuntime.Unknown, PackagedRuntime.AppContainer, PackagedRuntime.PackagedWin32];

    private static bool Injects(LaunchRoute route)
    {
        return route is LaunchRoute.AppContainerOverlay or LaunchRoute.PackagedWin32Overlay;
    }

    [Fact]
    public void ControllerOnlyNeverInjects()
    {
        // The theory that matters: whatever the game turns out to be, asking for controller-only
        // cannot produce a route that writes into it.
        foreach (var runtime in EveryRuntime)
        {
            var decision = LaunchRouteSelector.Select(RequestedInputMode.ControllerOnly, runtime);

            Assert.Equal(LaunchRoute.ControllerOnly, decision.Route);
            Assert.False(Injects(decision.Route));
        }
    }

    [Fact]
    public void AnUnestablishedRuntimeNeverInjects()
    {
        // There is no validated route for a title that is neither shape, and guessing one would
        // mean writing into somebody's game on the strength of a guess.
        var decision = LaunchRouteSelector.Select(RequestedInputMode.SteamOverlay, PackagedRuntime.Unknown);

        Assert.False(Injects(decision.Route));
        Assert.Equal(LaunchRoute.SuperviseOnly, decision.Route);
    }

    [Fact]
    public void AnUnestablishedRuntimeStillRunsTheSessionButReportsItDegraded()
    {
        // The user asked to play. Steam's running state and containment are worth keeping even when
        // the overlay is not available, but the outcome must not read as success.
        var decision = LaunchRouteSelector.Select(RequestedInputMode.SteamOverlay, PackagedRuntime.Unknown);

        Assert.True(decision.Degraded);
    }

    [Fact]
    public void AnAppContainerTitleTakesTheBridgedRoute()
    {
        var decision = LaunchRouteSelector.Select(
            RequestedInputMode.SteamOverlay, PackagedRuntime.AppContainer);

        Assert.Equal(LaunchRoute.AppContainerOverlay, decision.Route);
        Assert.False(decision.Degraded);
    }

    [Fact]
    public void APackagedWin32TitleTakesTheLaunchHelperRoute()
    {
        var decision = LaunchRouteSelector.Select(
            RequestedInputMode.SteamOverlay, PackagedRuntime.PackagedWin32);

        Assert.Equal(LaunchRoute.PackagedWin32Overlay, decision.Route);
        Assert.False(decision.Degraded);
    }

    [Fact]
    public void EveryDecisionSaysWhyInWordsAUserCouldRead()
    {
        // The reason goes into the log and, for the importer, in front of the user. A route with no
        // stated reason is one nobody can argue with when it turns out to be wrong.
        foreach (var runtime in EveryRuntime)
        {
            foreach (var mode in new[] { RequestedInputMode.SteamOverlay, RequestedInputMode.ControllerOnly })
            {
                var decision = LaunchRouteSelector.Select(mode, runtime);

                Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
                Assert.EndsWith(".", decision.Reason, StringComparison.Ordinal);
            }
        }
    }
}

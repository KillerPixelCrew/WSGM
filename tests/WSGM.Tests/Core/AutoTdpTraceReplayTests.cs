using WSGM.Core;
using WSGM.Tests.Builders;

namespace WSGM.Tests.Core;

/// <summary>
///     Drives the controller from recorded traces rather than from constructed windows.
/// </summary>
/// <remarks>
///     Issue 181 asks for an offline replay path so a candidate policy can be judged against the same
///     inputs a handheld produced. These fixtures are the small, hand-authored version of that; the
///     captures they were derived from are on the issue. See <c>Fixtures\AutoTdp\README.md</c>.
/// </remarks>
public sealed class AutoTdpTraceReplayTests
{
    [Fact]
    public void SteadyPlayAtTheCapBringsTheLimitDownByTheHeadroomItsLoadShows()
    {
        var decisions = Replay("capped-descent.csv");

        // About 61 % busy at 20 W sizes the first probe at two steps; the accepted probe's own
        // windows, busier at the lower limit, size the next one at a single step without a dwell.
        Assert.Equal(
            new[] { 16, 14 },
            decisions.Where(decision => decision.Action is AutoTdpAction.Probe)
                .Select(decision => decision.Watts));
        Assert.Equal("probe-accepted", decisions[15].Reason);
        Assert.Equal(14, decisions[^1].Watts);
        Assert.DoesNotContain(decisions, decision => decision.Action is AutoTdpAction.Raise);
    }

    [Fact]
    public void LateFramesOnASaturatedGpuRaiseTheLimitByTheDeficit()
    {
        var decisions = Replay("power-limited-climb.csv");

        Assert.Equal(
            new[] { 21, 29 },
            decisions.Where(decision => decision.Action is AutoTdpAction.Raise)
                .Select(decision => decision.Watts));
    }

    [Fact]
    public void ALoadingStallOnAnIdleGpuChangesNothing()
    {
        var decisions = Replay("loading-stall.csv");

        Assert.All(decisions, decision =>
        {
            Assert.False(decision.RequiresWrite);
            Assert.Equal(15, decision.Watts);
        });
        Assert.Contains(decisions, decision => decision.Reason == "quarantine-stall");
        Assert.Contains(decisions, decision => decision.Reason == "quarantine-ended");
    }

    private static IReadOnlyList<AutoTdpDecision> Replay(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AutoTdp", fixture);
        return [.. AutoTdpTraceReplay.Run(path).Select(decision => decision.Replayed)];
    }
}

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
    public void SteadyPlayAtTheCapBringsTheLimitDown()
    {
        var decisions = Replay("capped-descent.csv");

        // Each accepted probe continues the descent without a dwell and doubles the step, until GPU
        // load climbs about 12 points across the second probe and the third halves back to one.
        Assert.Equal(
            new[] { 18, 14, 13 },
            decisions.Where(decision => decision.Action is AutoTdpAction.Probe)
                .Select(decision => decision.Watts));
        Assert.Equal(2, decisions.Count(decision => decision.Reason == "probe-accepted"));
        Assert.Equal(13, decisions[^1].Watts);
        Assert.DoesNotContain(decisions, decision => decision.Action is AutoTdpAction.Raise);
    }

    [Fact]
    public void LateFramesOnASaturatedGpuRaiseTheLimitByTheDeficit()
    {
        var decisions = Replay("power-limited-climb.csv");

        Assert.Equal(
            new[] { 23, 30 },
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

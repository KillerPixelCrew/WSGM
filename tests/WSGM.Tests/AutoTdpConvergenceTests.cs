using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class AutoTdpConvergenceTests
{
    private static readonly AutoTdpLimits Limits = new(8, 30, 2);
    private const string Context = "capped-game";

    [Fact]
    public void SuccessfulCappedProbesKeepDescendingUntilTheMinimumAndHoldThere()
    {
        AutoTdpController controller = new();
        controller.Start(20, Limits, Context);
        var decisions = Run(controller, 180, 16.6);
        Assert.Equal(new[] { 18, 16, 14, 12, 10, 8 },
            decisions.Where(d => d.Action == AutoTdpAction.Probe).Select(d => d.Watts));
        Assert.All(decisions.TakeLast(20), d =>
        {
            Assert.Equal("at-minimum", d.Reason);
            Assert.False(d.RequiresWrite);
        });
    }

    [Fact]
    public void FailedProbeHoldsItsFloorWithoutRepeatingSettlingAndStillRecoversFromLoad()
    {
        AutoTdpController controller = new();
        controller.Start(20, Limits, Context);
        Run(controller, AutoTdpController.SettledWindows + AutoTdpController.SettleWindows, 16.6);
        Assert.Equal(AutoTdpAction.Restore, Run(controller, 1, 22)[0].Action);
        var settled = Run(controller, 50, 16.6);
        Assert.All(settled.TakeLast(20), d => Assert.Equal("below-learned-floor", d.Reason));
        Assert.Equal(20, controller.Watts);
        Assert.Equal(AutoTdpAction.Raise, Run(controller, 3, 22)[^1].Action);
        Assert.Equal(22, controller.Watts);
    }

    [Fact]
    public void SustainedMissesAtMaximumKeepCantReachUntilDeliveryRecovers()
    {
        AutoTdpController controller = new();
        controller.Start(30, Limits, Context);
        var decisions = Run(controller, 30, 22);
        Assert.All(decisions.Skip(2), d =>
        {
            Assert.Equal("at-maximum", d.Reason);
            Assert.False(d.RequiresWrite);
            Assert.Equal("Can't Reach", ShellSession.AutoTdpActivity(true,
                new AutoTdpStatus(AutoTdpState.Controlling, d.Watts, 22, 16.6, Context, d.Reason, d.Action)));
        });
        Assert.NotEqual("at-maximum", Run(controller, 1, 16.6)[0].Reason);
    }

    private static IReadOnlyList<AutoTdpDecision> Run(AutoTdpController controller, int count, double frametime) =>
        AutoTdpReplay.Run(controller, Limits, AutoTdpReplay.Run(count, frametime, 16.6, Context, capped: true));
}

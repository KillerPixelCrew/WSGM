using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Core;

public sealed class RtssOsdAutoTdpActivityTests
{
    [Theory]
    [InlineData((int)AutoTdpAction.Hold, "on-target", "Holding")]
    [InlineData((int)AutoTdpAction.Hold, "tracking-headroom", "Holding")]
    [InlineData((int)AutoTdpAction.Raise, "sustained-miss", "Raising")]
    [InlineData((int)AutoTdpAction.Probe, "probe-down", "Lowering")]
    [InlineData((int)AutoTdpAction.Restore, "probe-failed", "Restoring")]
    [InlineData((int)AutoTdpAction.Restore, "probe-interrupted", "Restoring")]
    [InlineData((int)AutoTdpAction.Hold, "settling", "Settling")]
    [InlineData((int)AutoTdpAction.Hold, "probe-pending", "Testing")]
    [InlineData((int)AutoTdpAction.Hold, "quarantine-hiatus", "Stalled")]
    [InlineData((int)AutoTdpAction.Hold, "quarantine-recovery", "Stalled")]
    [InlineData((int)AutoTdpAction.Hold, "unresponsive", "Holding")]
    public void ControllingState_DescribesTheCurrentDecision(
        int action,
        string detail,
        string expected)
    {
        var status = new AutoTdpStatus(
            AutoTdpState.Controlling,
            17,
            16.6,
            16.7,
            "steam:480",
            detail,
            (AutoTdpAction)action);

        Assert.Equal(expected, PerformanceServices.AutoTdpActivity(true, status));
    }

    [Fact]
    public void DisabledState_HasNoActivity()
    {
        Assert.Equal(string.Empty, PerformanceServices.AutoTdpActivity(false, null));
    }

    [Theory]
    [InlineData("probe-down", "Testing a lower limit")]
    [InlineData("below-learned-floor", "below-learned-floor")]
    [InlineData("Paused by a manual change.", "Paused by a manual change.")]
    public void AReasonTokenBecomesSomethingWorthShowing(string reason, string expected)
    {
        Assert.Equal(expected, AutoTdpReason.Describe(reason));
    }
}

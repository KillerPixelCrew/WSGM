using WSGM.Device.Contracts.Lifecycle;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ControllerReleaseOrderingTests
{
    [Fact]
    public void TargetAndHidHideCannotBeRemovedBeforePhysicalRelease()
    {
        ControllerReleaseOrder order = new();
        order.Advance(ControllerReleaseBoundary.RoutingStopped);
        order.Advance(ControllerReleaseBoundary.OutputStopped);
        order.Advance(ControllerReleaseBoundary.TargetNeutralized);

        Assert.True(order.HidHideMustRemain);
        Assert.False(order.CanRemoveTarget);
        Assert.Throws<InvalidOperationException>(() =>
            order.Advance(ControllerReleaseBoundary.TargetRemoved));
        Assert.Throws<InvalidOperationException>(() =>
            order.Advance(ControllerReleaseBoundary.HidHideOwnedDeltasRemoved));
    }

    [Fact]
    public void VerifiedReleaseRemovesTargetBeforeOwnedHidHideDeltas()
    {
        ControllerReleaseOrder order = StartedRelease();
        order.RecordPhysicalRelease(topologyVerified: true);
        Assert.True(order.CanRemoveTarget);
        order.Advance(ControllerReleaseBoundary.TargetRemoved);
        Assert.True(order.CanRemoveHidHide);
        order.Advance(ControllerReleaseBoundary.HidHideOwnedDeltasRemoved);
        order.Advance(ControllerReleaseBoundary.Completed);

        Assert.Equal(ControllerHandoffResult.ReleasedVerified, order.Result);
    }

    [Fact]
    public void TimeoutStillContinuesCleanupButCannotClaimVerifiedHandoff()
    {
        ControllerReleaseOrder order = StartedRelease();
        order.RecordPhysicalReleaseTimeout();
        order.Advance(ControllerReleaseBoundary.TargetRemoved);
        order.Advance(ControllerReleaseBoundary.HidHideOwnedDeltasRemoved);
        order.Advance(ControllerReleaseBoundary.Completed);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, order.Result);
    }

    private static ControllerReleaseOrder StartedRelease()
    {
        ControllerReleaseOrder order = new();
        order.Advance(ControllerReleaseBoundary.RoutingStopped);
        order.Advance(ControllerReleaseBoundary.OutputStopped);
        order.Advance(ControllerReleaseBoundary.TargetNeutralized);
        order.Advance(ControllerReleaseBoundary.PhysicalReleaseRequested);
        return order;
    }
}

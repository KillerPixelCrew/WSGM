using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ControllerMakeSafeTests
{
    [Fact]
    public void SequenceRefusesTargetRemovalBeforeThePhysicalReleaseConcludes()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized();

        Assert.False(sequence.CanRemoveTarget);
        Assert.True(sequence.HidHideMustRemain);
        Assert.Throws<InvalidOperationException>(sequence.RecordTargetRemoved);
    }

    [Fact]
    public void SequenceRefusesHidHideRemovalWhileTheTargetStillExists()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);

        Assert.False(sequence.CanRemoveHidHide);
        Assert.True(sequence.HidHideMustRemain);
        Assert.Throws<InvalidOperationException>(() => sequence.RecordHidHideRemoved(verified: true));
    }

    [Fact]
    public void CompleteVerifiedSequenceReportsAVerifiedRelease()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved();
        sequence.RecordHidHideRemoved(verified: true);

        Assert.Equal(ControllerHandoffResult.ReleasedVerified, sequence.Complete());
        Assert.Equal(ControllerHandoffStep.WsgmStateRemoved, sequence.Step);
        Assert.False(sequence.HidHideMustRemain);
    }

    [Fact]
    public void AnUnverifiedPluginTopologyDowngradesTheResultButStillRemovesWsgmState()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyUnverified);
        sequence.RecordTargetRemoved();
        sequence.RecordHidHideRemoved(verified: true);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
        Assert.True(sequence.TargetRemoved);
        Assert.True(sequence.HidHideRemoved);
    }

    [Fact]
    public void AnUnobservedPluginReleaseStillPermitsRemovalAndReportsUnverified()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized();
        sequence.RecordPluginReleaseUnobserved();

        Assert.True(sequence.CanRemoveTarget);
        Assert.Equal(ControllerHandoffStep.TopologyUnverified, sequence.Step);
        sequence.RecordTargetRemoved();
        sequence.RecordHidHideRemoved(verified: true);
        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void AnUnverifiedHidHideRemovalDowngradesAnOtherwiseCleanSequence()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved();
        sequence.RecordHidHideRemoved(verified: false);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void APluginReportingAVerifiedTopologyWithAnUnverifiedResultIsNotTreatedAsClean()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized();
        sequence.RecordPluginRelease(
            ControllerHandoffStep.TopologyVerified,
            ControllerHandoffResult.ReleasedUnverified);
        sequence.RecordTargetRemoved();
        sequence.RecordHidHideRemoved(verified: true);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void SequenceRefusesASecondPluginReleaseAndAWsgmOwnedStepFromThePlugin()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);
        Assert.Throws<InvalidOperationException>(() => sequence.RecordPluginRelease(
            ControllerHandoffStep.TopologyVerified,
            ControllerHandoffResult.ReleasedVerified));

        ControllerMakeSafeSequence fresh = new();
        fresh.RecordNeutralized();
        Assert.Throws<InvalidOperationException>(() => fresh.RecordPluginRelease(
            ControllerHandoffStep.VirtualTargetNeutralized,
            ControllerHandoffResult.ReleasedVerified));
    }

    [Fact]
    public void SequenceRefusesCompletionBeforeWsgmStateIsRemoved()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved();

        Assert.Throws<InvalidOperationException>(() => sequence.Complete());
        Assert.Equal(ControllerHandoffResult.InProgress, sequence.Result);
    }

    [Fact]
    public void SequenceRefusesNeutralizingTwice()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized();

        Assert.Throws<InvalidOperationException>(sequence.RecordNeutralized);
    }

    private static ControllerMakeSafeSequence Released(ControllerHandoffStep step)
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized();
        sequence.RecordPluginRelease(
            step,
            step is ControllerHandoffStep.TopologyVerified
                ? ControllerHandoffResult.ReleasedVerified
                : ControllerHandoffResult.ReleasedUnverified);
        return sequence;
    }
}

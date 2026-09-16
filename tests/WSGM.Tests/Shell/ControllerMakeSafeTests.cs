using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class ControllerMakeSafeTests
{
    [Fact]
    public void SequenceRefusesTargetRemovalBeforeThePhysicalReleaseConcludes()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(true);

        Assert.False(sequence.CanRemoveTarget);
        Assert.True(sequence.HidHideMustRemain);
        Assert.Throws<InvalidOperationException>(() => sequence.RecordTargetRemoved(true));
    }

    [Fact]
    public void SequenceRefusesHidHideRemovalWhileTheTargetStillExists()
    {
        var sequence = Released(ControllerHandoffStep.TopologyVerified);

        Assert.False(sequence.CanRemoveHidHide);
        Assert.True(sequence.HidHideMustRemain);
        Assert.Throws<InvalidOperationException>(() => sequence.RecordHidHideRemoved(true));
    }

    [Fact]
    public void CompleteVerifiedSequenceReportsAVerifiedRelease()
    {
        var sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved(true);
        sequence.RecordHidHideRemoved(true);

        Assert.Equal(ControllerHandoffResult.ReleasedVerified, sequence.Complete());
        Assert.Equal(ControllerHandoffStep.WsgmStateRemoved, sequence.Step);
        Assert.False(sequence.HidHideMustRemain);
    }

    [Fact]
    public void AnUnverifiedPluginTopologyDowngradesTheResultButStillRemovesWsgmState()
    {
        var sequence = Released(ControllerHandoffStep.TopologyUnverified);
        sequence.RecordTargetRemoved(true);
        sequence.RecordHidHideRemoved(true);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
        Assert.True(sequence.TargetRemoved);
        Assert.True(sequence.HidHideRemoved);
    }

    [Fact]
    public void AnUnobservedPluginReleaseStillPermitsRemovalAndReportsUnverified()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(true);
        sequence.RecordPluginReleaseUnobserved();

        Assert.True(sequence.CanRemoveTarget);
        Assert.Equal(ControllerHandoffStep.TopologyUnverified, sequence.Step);
        sequence.RecordTargetRemoved(true);
        sequence.RecordHidHideRemoved(true);
        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void AnUnverifiedHidHideRemovalDowngradesAnOtherwiseCleanSequence()
    {
        var sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved(true);
        sequence.RecordHidHideRemoved(false);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void APluginReportingAVerifiedTopologyWithAnUnverifiedResultIsNotTreatedAsClean()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(true);
        sequence.RecordPluginRelease(
            ControllerHandoffStep.TopologyVerified,
            ControllerHandoffResult.ReleasedUnverified);
        sequence.RecordTargetRemoved(true);
        sequence.RecordHidHideRemoved(true);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void SequenceRefusesASecondPluginReleaseAndAWsgmOwnedStepFromThePlugin()
    {
        var sequence = Released(ControllerHandoffStep.TopologyVerified);
        Assert.Throws<InvalidOperationException>(() => sequence.RecordPluginRelease(
            ControllerHandoffStep.TopologyVerified,
            ControllerHandoffResult.ReleasedVerified));

        ControllerMakeSafeSequence fresh = new();
        fresh.RecordNeutralized(true);
        Assert.Throws<InvalidOperationException>(() => fresh.RecordPluginRelease(
            ControllerHandoffStep.VirtualTargetNeutralized,
            ControllerHandoffResult.ReleasedVerified));
    }

    [Fact]
    public void SequenceRefusesCompletionBeforeWsgmStateIsRemoved()
    {
        var sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved(true);

        Assert.Throws<InvalidOperationException>(() => sequence.Complete());
        Assert.Equal(ControllerHandoffResult.InProgress, sequence.Result);
    }

    [Fact]
    public void SequenceRefusesNeutralizingTwice()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(true);

        Assert.Throws<InvalidOperationException>(() => sequence.RecordNeutralized(true));
    }

    [Fact]
    public void AnUnverifiedNeutralizationDowngradesAnOtherwiseCleanSequence()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(false);
        sequence.RecordPluginRelease(
            ControllerHandoffStep.TopologyVerified,
            ControllerHandoffResult.ReleasedVerified);
        sequence.RecordTargetRemoved(true);
        sequence.RecordHidHideRemoved(true);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void AFailedTargetRemovalStillRemovesHidHideButIsNeverReportedAsVerified()
    {
        var sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved(false);
        sequence.RecordHidHideRemoved(true);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
        Assert.True(sequence.HidHideRemoved);
    }

    private static ControllerMakeSafeSequence Released(ControllerHandoffStep step)
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(true);
        sequence.RecordPluginRelease(
            step,
            step is ControllerHandoffStep.TopologyVerified
                ? ControllerHandoffResult.ReleasedVerified
                : ControllerHandoffResult.ReleasedUnverified);
        return sequence;
    }
}

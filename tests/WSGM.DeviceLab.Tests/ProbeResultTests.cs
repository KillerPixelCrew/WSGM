using WSGM.DeviceLab.Core.Catalog;

namespace WSGM.DeviceLab.Tests;

/// <summary>
/// The executable specification of probe verdicts: four dimensions recorded apart, and the order in
/// which they decide.
/// </summary>
public class ProbeResultTests
{
    [Fact]
    public void ACompletedProbeWhoseObservationMatched_IsCompatible()
    {
        Assert.Equal(CompatibilityVerdict.Compatible, Result(
            ProbeExecution.Completed, ProbeObservation.Match,
            ProbeMutation.None, ProbeCleanup.NotRequired).Verdict);
    }

    [Fact]
    public void ACompletedProbeWhoseObservationContradicted_IsIncompatible()
    {
        Assert.Equal(CompatibilityVerdict.Incompatible, Result(
            ProbeExecution.Completed, ProbeObservation.Mismatch,
            ProbeMutation.None, ProbeCleanup.NotRequired).Verdict);
    }

    [Fact]
    public void AFailedRestore_QuarantinesEvenWhenEverythingElseSucceeded()
    {
        // A module whose write worked but whose restore did not is more dangerous than one that never
        // worked: the successful part invites using it again.
        Assert.Equal(CompatibilityVerdict.Quarantined, Result(
            ProbeExecution.Completed, ProbeObservation.Match,
            ProbeMutation.AppliedVerified, ProbeCleanup.RestoreFailed).Verdict);
    }

    [Fact]
    public void AProbeBlockedBeforeWriting_IsBlockedNotInconclusive()
    {
        // Nothing reached the device, so nothing about the device is uncertain.
        Assert.Equal(CompatibilityVerdict.Blocked, Result(
            ProbeExecution.AccessDenied, ProbeObservation.NoSignal,
            ProbeMutation.None, ProbeCleanup.NotRequired).Verdict);
    }

    [Fact]
    public void AProbeThatTimedOutAfterAnUnverifiedWrite_IsInconclusiveNotBlocked()
    {
        // The distinction the four dimensions exist for: the device may not be as it was found, so
        // this cannot be dismissed as merely blocked.
        Assert.Equal(CompatibilityVerdict.Inconclusive, Result(
            ProbeExecution.Timeout, ProbeObservation.NoSignal,
            ProbeMutation.AppliedUnverified, ProbeCleanup.NotRequired).Verdict);
    }

    [Fact]
    public void AProbeThatFailedWithAnUnverifiedRestore_IsInconclusive()
    {
        Assert.Equal(CompatibilityVerdict.Inconclusive, Result(
            ProbeExecution.Disconnected, ProbeObservation.TopologyChanged,
            ProbeMutation.AppliedVerified, ProbeCleanup.RestoreUnverified).Verdict);
    }

    [Fact]
    public void AnUnstableObservation_IsInconclusiveRatherThanIncompatible()
    {
        // Varying between repetitions establishes nothing either way, and calling it incompatible
        // would discard a module that may work once the instability is understood.
        Assert.Equal(CompatibilityVerdict.Inconclusive, Result(
            ProbeExecution.Completed, ProbeObservation.Unstable,
            ProbeMutation.None, ProbeCleanup.NotRequired).Verdict);
    }

    [Fact]
    public void NoSignalOnACompletedProbe_IsInconclusive()
    {
        Assert.Equal(CompatibilityVerdict.Inconclusive, Result(
            ProbeExecution.Completed, ProbeObservation.NoSignal,
            ProbeMutation.None, ProbeCleanup.NotRequired).Verdict);
    }

    [Fact]
    public void APrerequisiteMissing_IsBlockedNotIncompatible()
    {
        // The module was never given a chance to be wrong. Recording it as incompatible would
        // permanently reject a module that works once the provider is present.
        Assert.Equal(CompatibilityVerdict.Blocked, Result(
            ProbeExecution.PrerequisiteMissing, ProbeObservation.NoSignal,
            ProbeMutation.NotApplied, ProbeCleanup.NotRequired).Verdict);
    }

    [Fact]
    public void QuarantineWinsOverEveryExecutionResult()
    {
        foreach (ProbeExecution execution in Enum.GetValues<ProbeExecution>())
        {
            Assert.Equal(CompatibilityVerdict.Quarantined, Result(
                execution, ProbeObservation.Match,
                ProbeMutation.AppliedUnverified, ProbeCleanup.RestoreFailed).Verdict);
        }
    }

    private static ProbeResult Result(
        ProbeExecution execution,
        ProbeObservation observation,
        ProbeMutation mutation,
        ProbeCleanup cleanup) => new()
        {
            Execution = execution,
            Observation = observation,
            Mutation = mutation,
            Cleanup = cleanup,
        };
}

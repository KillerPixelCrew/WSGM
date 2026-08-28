using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of the device cycle: what ends it, what does not, and what a crash
/// is allowed to look like.
/// </summary>
public class DeviceCycleTransitionTests
{
    [Theory]
    [InlineData(LifecycleTrigger.WsgmExiting)]
    [InlineData(LifecycleTrigger.IntegrationDisabled)]
    public void OnlyTheTwoTerminalTriggers_BeginDeactivation(LifecycleTrigger trigger)
    {
        Assert.Equal(DeviceCycleState.Deactivating,
            DeviceCycleTransitions.Next(DeviceCycleState.Active, trigger));
    }

    [Theory]
    [InlineData(DeviceCycleState.Activating)]
    [InlineData(DeviceCycleState.Active)]
    [InlineData(DeviceCycleState.Degraded)]
    [InlineData(DeviceCycleState.Passive)]
    [InlineData(DeviceCycleState.Suspended)]
    [InlineData(DeviceCycleState.Quarantined)]
    public void ATerminalTrigger_AppliesFromAnyState(DeviceCycleState state)
    {
        // The user's intent to stop must not depend on which state the cycle happened to be in.
        Assert.Equal(DeviceCycleState.Deactivating,
            DeviceCycleTransitions.Next(state, LifecycleTrigger.WsgmExiting));
    }

    [Fact]
    public void AHostFaultWithinBudget_StaysInsideTheCycle()
    {
        // A crash is a fault inside the running cycle. Treating it as deactivation would report a
        // clean handoff to an external manager that never happened.
        DeviceCycleState next = DeviceCycleTransitions.Next(
            DeviceCycleState.Active, LifecycleTrigger.HostFaulted, faultsInWindow: 0);

        Assert.Equal(DeviceCycleState.Activating, next);
    }

    [Fact]
    public void AHostFaultThatExhaustsTheBudget_Quarantines()
    {
        DeviceCycleState next = DeviceCycleTransitions.Next(
            DeviceCycleState.Active,
            LifecycleTrigger.HostFaulted,
            faultsInWindow: RestartPolicy.Default.MaxRestarts);

        Assert.Equal(DeviceCycleState.Quarantined, next);
    }

    [Fact]
    public void AHostFault_NeverReachesDisabled()
    {
        for (int faults = 0; faults <= RestartPolicy.Default.MaxRestarts + 2; faults++)
        {
            Assert.NotEqual(DeviceCycleState.Disabled, DeviceCycleTransitions.Next(
                DeviceCycleState.Active, LifecycleTrigger.HostFaulted, faults));
        }
    }

    [Fact]
    public void Quarantine_IsOnlyLeftByAnExplicitRetry()
    {
        // Automatic recovery would reintroduce the crash loop quarantine exists to stop.
        Assert.Equal(DeviceCycleState.Quarantined, DeviceCycleTransitions.Next(
            DeviceCycleState.Quarantined, LifecycleTrigger.HostRestarted));

        Assert.Equal(DeviceCycleState.Activating, DeviceCycleTransitions.Next(
            DeviceCycleState.Quarantined, LifecycleTrigger.ManualRetry));
    }

    [Fact]
    public void SuspendAndResume_StayWithinOneCycle()
    {
        DeviceCycleState suspended = DeviceCycleTransitions.Next(
            DeviceCycleState.Active, LifecycleTrigger.SystemSuspending);
        Assert.Equal(DeviceCycleState.Suspended, suspended);

        Assert.Equal(DeviceCycleState.Activating,
            DeviceCycleTransitions.Next(suspended, LifecycleTrigger.SystemResumed));
    }

    [Fact]
    public void ADeviceGenerationChange_ReacquiresWithoutEndingTheCycle()
    {
        // The host is fine; its handles are not. Re-acquisition happens in the same already-running
        // host.
        Assert.Equal(DeviceCycleState.Activating, DeviceCycleTransitions.Next(
            DeviceCycleState.Active, LifecycleTrigger.DeviceGenerationChanged));
    }

    [Fact]
    public void StartingWhileAlreadyRunning_ChangesNothing()
    {
        Assert.Equal(DeviceCycleState.Active, DeviceCycleTransitions.Next(
            DeviceCycleState.Active, LifecycleTrigger.WsgmStarted));
    }

    [Fact]
    public void EnablingFromDisabled_BeginsDetection()
    {
        Assert.Equal(DeviceCycleState.Detected, DeviceCycleTransitions.Next(
            DeviceCycleState.Disabled, LifecycleTrigger.IntegrationEnabled));
    }

    [Fact]
    public void DisabledStaysDisabled_UntilSomeoneEnablesIt()
    {
        foreach (LifecycleTrigger trigger in Enum.GetValues<LifecycleTrigger>())
        {
            if (trigger is LifecycleTrigger.WsgmStarted or LifecycleTrigger.IntegrationEnabled)
            {
                continue;
            }

            Assert.Equal(DeviceCycleState.Disabled,
                DeviceCycleTransitions.Next(DeviceCycleState.Disabled, trigger));
        }
    }

    [Fact]
    public void Deactivating_EndsOnlyWhenTheReleaseSequenceSaysSo()
    {
        // Including on a timeout: the sequence still completes, records the unverified handoff, and
        // signals completion. Nothing else may cut it short while hardware is still being restored.
        foreach (LifecycleTrigger trigger in Enum.GetValues<LifecycleTrigger>())
        {
            DeviceCycleState next =
                DeviceCycleTransitions.Next(DeviceCycleState.Deactivating, trigger);

            Assert.Equal(
                trigger is LifecycleTrigger.DeactivationCompleted
                    ? DeviceCycleState.Disabled
                    : DeviceCycleState.Deactivating,
                next);
        }
    }

    [Fact]
    public void ASecondExitRequestDuringDeactivation_DoesNotRestartIt()
    {
        Assert.Equal(DeviceCycleState.Deactivating, DeviceCycleTransitions.Next(
            DeviceCycleState.Deactivating, LifecycleTrigger.WsgmExiting));
    }

    [Theory]
    [InlineData(DeviceCycleState.Active, true)]
    [InlineData(DeviceCycleState.Degraded, true)]
    [InlineData(DeviceCycleState.Suspended, true)]
    [InlineData(DeviceCycleState.Activating, true)]
    [InlineData(DeviceCycleState.Passive, false)]
    [InlineData(DeviceCycleState.Quarantined, false)]
    [InlineData(DeviceCycleState.Disabled, false)]
    public void OwnsHardware_IdentifiesTheStatesWithRealCleanupWork(
        DeviceCycleState state,
        bool expected)
    {
        // Quarantined owns nothing because quarantine already failed open and released it.
        Assert.Equal(expected, DeviceCycleTransitions.OwnsHardware(state));
    }

    [Fact]
    public void AFullRun_DesktopToGameAndBack_NeverRecreatesTheCycle()
    {
        // Shell mode is deliberately not a lifecycle trigger. This walks a realistic run and asserts
        // the cycle stays in one generation throughout.
        DeviceCycleState state = DeviceCycleTransitions.Next(
            DeviceCycleState.Disabled, LifecycleTrigger.WsgmStarted);
        state = DeviceCycleTransitions.Next(state, LifecycleTrigger.HostRestarted);
        Assert.Equal(DeviceCycleState.Activating, state);

        // Game starts, game exits, Steam restarts, controller management toggled - none of which is
        // expressible as a trigger, which is the point.
        Assert.Equal(DeviceCycleState.Activating, state);

        state = DeviceCycleTransitions.Next(state, LifecycleTrigger.SystemSuspending);
        state = DeviceCycleTransitions.Next(state, LifecycleTrigger.SystemResumed);
        Assert.Equal(DeviceCycleState.Activating, state);

        state = DeviceCycleTransitions.Next(state, LifecycleTrigger.WsgmExiting);
        Assert.Equal(DeviceCycleState.Deactivating, state);
        Assert.Equal(DeviceCycleState.Disabled,
            DeviceCycleTransitions.Next(state, LifecycleTrigger.DeactivationCompleted));
    }
}

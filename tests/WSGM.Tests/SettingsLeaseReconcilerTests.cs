using WSGM.Settings;

namespace WSGM.Tests;

public sealed class SettingsLeaseReconcilerTests
{
    [Fact]
    public void ShouldHold_OverlayStillClosing_IgnoresTransientSettingsDeactivation()
        => Assert.True(SettingsLeaseReconciler.ShouldHold(
            gameModeSurface: true,
            leaseEnabled: true,
            closed: false,
            minimized: false,
            active: false,
            hasChildSurface: false,
            handoffPending: true));

    [Fact]
    public void ShouldHold_HandoffCompleteAndSettingsInactive_ReleasesClaim()
        => Assert.False(SettingsLeaseReconciler.ShouldHold(
            gameModeSurface: true,
            leaseEnabled: true,
            closed: false,
            minimized: false,
            active: false,
            hasChildSurface: false,
            handoffPending: false));

    [Fact]
    public void CompleteAcquire_NativeLeaseUnavailable_CloseStillReleasesOwnerClaim()
    {
        var reconciler = new SettingsLeaseReconciler();

        Assert.Equal(SettingsLeaseAction.Acquire, reconciler.SetDesired(true));
        // AcquireFor returned after logging a native failure, but its named owner
        // claim was registered before that attempt.
        Assert.Equal(SettingsLeaseAction.None, reconciler.CompleteAcquireFor());

        Assert.Equal(SettingsLeaseAction.Release, reconciler.SetDesired(false));
    }

    [Fact]
    public void CompleteAcquire_WindowClosedDuringNativeAttempt_ReleasesOwnerClaimNext()
    {
        var reconciler = new SettingsLeaseReconciler();

        Assert.Equal(SettingsLeaseAction.Acquire, reconciler.SetDesired(true));
        Assert.Equal(SettingsLeaseAction.None, reconciler.SetDesired(false));

        Assert.Equal(SettingsLeaseAction.Release, reconciler.CompleteAcquireFor());
    }

    [Fact]
    public void InheritClaim_LiveHandoff_UsesExistingLeaseUntilSettingsCloses()
    {
        var reconciler = new SettingsLeaseReconciler();

        Assert.Equal(SettingsLeaseAction.None, reconciler.InheritClaim(leaseApplied: true));

        Assert.Equal(SettingsLeaseAction.Release, reconciler.SetDesired(false));
    }

    [Fact]
    public void InheritClaim_NoNativeLease_PerformsColdAcquireOffTheUiThread()
    {
        var reconciler = new SettingsLeaseReconciler();

        Assert.Equal(SettingsLeaseAction.Acquire, reconciler.InheritClaim(leaseApplied: false));
        Assert.Equal(SettingsLeaseAction.None, reconciler.CompleteAcquireFor());

        Assert.Equal(SettingsLeaseAction.Release, reconciler.SetDesired(false));
    }
}

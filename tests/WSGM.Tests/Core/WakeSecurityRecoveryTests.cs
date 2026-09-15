using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class WakeSecurityRecoveryTests
{
    [Fact]
    public void SavedRecoveryPreservesAbsentValuesAndPerSchemeIdentity()
    {
        var original = new WakeSecuritySnapshot(false, -1, 1, -1, [new(Guid.NewGuid(), -1, 0)]);
        AppConfig config = new();
        LockScreenSettings.CaptureInto(config, original);
        var restored = LockScreenSettings.RecoverySnapshot(config);
        Assert.True(config.PreviousLockOnWakeSnapshotCaptured);
        Assert.Equal(original.PolicyExisted, restored.PolicyExisted);
        Assert.Equal(original.PolicyAc, restored.PolicyAc);
        Assert.Equal(original.PolicyDc, restored.PolicyDc);
        Assert.Equal(original.NoLockScreen, restored.NoLockScreen);
        Assert.Equal(Assert.Single(original.Schemes), Assert.Single(restored.Schemes));
    }

    [Fact]
    public void LegacyRecoveryPreservesPersonalizationWithoutClaimingPolicyOwnership()
    {
        var restored = LockScreenSettings.RecoverySnapshot(new AppConfig { PreviousNoLockScreen = 1 });
        Assert.True(restored.PolicyExisted);
        Assert.Equal(-1, restored.PolicyAc);
        Assert.Empty(restored.Schemes);
        Assert.Equal(1, restored.NoLockScreen);
    }
}

using WSGM.Device.Msi.Claw8A2Vm;

namespace WSGM.Device.Tests;

public sealed class Intel3dFeatureLayoutTests
{
    // Measured against intel/drivers.gpu.control-library's igcl_api.h and confirmed on the
    // reference unit on 2026-09-10: the managed mirror reported 56 and the driver answered the
    // read. Same hazard as the Arc Sync layouts -- the Size field is checked by the driver, and its
    // refusal looks exactly like "this machine has no Endurance Gaming", so drift removes the
    // feature silently rather than loudly.
    [Theory]
    [InlineData("ctl_3d_feature_getset_t", 56)]
    [InlineData("ctl_endurance_gaming_t", 8)]
    public void ManagedMirrors_MatchTheNativeHeaderExactly(string native, int expected)
    {
        (int getSet, int endurance) = Intel3dFeatureTransport.NativeStructureSizes;

        Assert.Equal(expected, native == "ctl_3d_feature_getset_t" ? getSet : endurance);
    }
}

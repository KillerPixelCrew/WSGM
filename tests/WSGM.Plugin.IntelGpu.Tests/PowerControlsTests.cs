using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Display;
using WSGM.Plugin.IntelGpu.Igcl;
using WSGM.Plugin.IntelGpu.Tests.Fakes;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

public sealed class PowerControlsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAroundTheFirstPowerSetterDistinguishesRefusalFromPartialChange(bool afterWrite)
    {
        using var cancellation = new CancellationTokenSource();
        var driver = new FakeIgclDriver();
        using var session = driver.OpenSession();
        var output = new IgclOutput(3, Assert.Single(session.Adapters), 0, default, true);
        var control = Assert.Single(PowerSavingControls.Build(session, output, PowerSavingControls.FeatureLrr,
                "display-fixture", new Placement("display", "power", 0), IntelLog.None),
            control => control.CapabilityId == "display.lrr.plugged-in");
        if (afterWrite)
        {
            driver.AfterPowerWrite = cancellation.Cancel;
        }
        else
        {
            cancellation.Cancel();
        }

        var failure = Assert.Throws<DriverFailure>(() => control.Write(CapabilityValue.Choice("lrr-1"),
            new WriteAdmission(cancellation.Token, Deadline.Never, () => true)));
        Assert.Equal(afterWrite, failure.Attempted);
        Assert.Equal(afterWrite ? new[] { PowerSavingControls.FeaturePsr } : [], driver.PowerWrites);
        session.Dispose();
        GC.KeepAlive(driver);
    }
}

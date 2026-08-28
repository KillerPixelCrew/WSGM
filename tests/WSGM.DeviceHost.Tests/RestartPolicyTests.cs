using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.DeviceHost.Tests;

public class RestartPolicyTests
{
    [Fact]
    public void Evaluate_DefaultPolicy_UsesTheFrozenOneFourSixteenSecondSequenceThenQuarantines()
    {
        FaultResponse first = RestartPolicy.Default.Evaluate(0, out TimeSpan firstBackoff);
        FaultResponse second = RestartPolicy.Default.Evaluate(1, out TimeSpan secondBackoff);
        FaultResponse third = RestartPolicy.Default.Evaluate(2, out TimeSpan thirdBackoff);
        FaultResponse fourth = RestartPolicy.Default.Evaluate(3, out TimeSpan fourthBackoff);

        Assert.Equal(FaultResponse.Restart, first);
        Assert.Equal(TimeSpan.FromSeconds(1), firstBackoff);
        Assert.Equal(FaultResponse.Restart, second);
        Assert.Equal(TimeSpan.FromSeconds(4), secondBackoff);
        Assert.Equal(FaultResponse.Restart, third);
        Assert.Equal(TimeSpan.FromSeconds(16), thirdBackoff);
        Assert.Equal(FaultResponse.Quarantine, fourth);
        Assert.Equal(TimeSpan.Zero, fourthBackoff);
    }

    [Fact]
    public void Evaluate_LargeFaultCount_CapsWithoutOverflow()
    {
        RestartPolicy policy = new() { MaxRestarts = int.MaxValue };

        FaultResponse response = policy.Evaluate(100_000, out TimeSpan backoff);

        Assert.Equal(FaultResponse.Restart, response);
        Assert.Equal(policy.MaxBackoff, backoff);
    }
}

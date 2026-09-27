using WSGM.Device.Tests;
using WSGM.Install;

namespace WSGM.Tests.Core;

/// <summary>
///     The two-run handshake that buys a boot with nothing attached to the USB/IP driver.
/// </summary>
/// <remarks>
///     Setup stages, the next sign-in honours and marks, the second setup run installs. The
///     interesting property is the last one: an update nobody finishes has to stop suppressing
///     sign-in on its own, or an abandoned upgrade would leave a handheld without WSGM forever.
/// </remarks>
public sealed class DriverUpdateGateTests
{
    [Fact]
    public void AnAbsentGateSaysNothingIsStaged()
    {
        using TemporaryDirectory directory = new();

        Assert.Equal(DriverUpdateGateState.None, DriverUpdateGate.Read(Gate(directory)));
    }

    [Fact]
    public void StagingSurvivesUntilASignInHonoursIt()
    {
        using TemporaryDirectory directory = new();
        var gate = Gate(directory);

        Assert.True(DriverUpdateGate.Stage(gate));

        Assert.Equal(DriverUpdateGateState.Pending, DriverUpdateGate.Read(gate));
        Assert.Equal(DriverUpdateGateState.Pending, DriverUpdateGate.Read(gate));
    }

    [Fact]
    public void TheSignInThatHonoursTheGateMarksItForSetupToFind()
    {
        using TemporaryDirectory directory = new();
        var gate = Gate(directory);
        DriverUpdateGate.Stage(gate);

        Assert.True(DriverUpdateGate.Consume(gate));

        Assert.Equal(DriverUpdateGateState.Consumed, DriverUpdateGate.Read(gate));
    }

    [Fact]
    public void OnlyOneSignInCanHonourAStagedGate()
    {
        using TemporaryDirectory directory = new();
        var gate = Gate(directory);
        DriverUpdateGate.Stage(gate);
        DriverUpdateGate.Consume(gate);

        // The second boot is not the reserved one; it must sign in normally.
        Assert.False(DriverUpdateGate.Consume(gate));
    }

    [Fact]
    public void AnAbandonedUpdateStopsHoldingSignInBack()
    {
        using TemporaryDirectory directory = new();
        var gate = Gate(directory);
        DriverUpdateGate.Stage(gate);
        DriverUpdateGate.Consume(gate);

        // What the sign-in service does when it meets a gate it has already honoured: clear it,
        // so the cost of an update nobody came back to finish is one sign-in, not every one.
        DriverUpdateGate.Clear(gate);

        Assert.Equal(DriverUpdateGateState.None, DriverUpdateGate.Read(gate));
    }

    [Fact]
    public void AGarbledGateIsNotAReasonToHoldASignInBack()
    {
        using TemporaryDirectory directory = new();
        var gate = Gate(directory);
        File.WriteAllText(gate, "who knows");

        Assert.Equal(DriverUpdateGateState.None, DriverUpdateGate.Read(gate));
        Assert.False(DriverUpdateGate.Consume(gate));
    }

    [Fact]
    public void StagingCreatesTheDirectoryItNeeds()
    {
        using TemporaryDirectory directory = new();
        var gate = Path.Combine(directory.Root, "nested", "driver-update.gate");

        Assert.True(DriverUpdateGate.Stage(gate));
        Assert.Equal(DriverUpdateGateState.Pending, DriverUpdateGate.Read(gate));
    }

    private static string Gate(TemporaryDirectory directory)
    {
        return Path.Combine(directory.Root, "driver-update.gate");
    }
}

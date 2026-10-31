using WSGM.Shell;
using PairingOutcome = WindowsDeviceControl.WindowsRadio.PairingOutcome;

namespace WSGM.Tests.Shell;

public class BluetoothPairingTests
{
    [Theory]
    [InlineData(PairingOutcome.Paired, "Pad is paired.")]
    [InlineData(PairingOutcome.AlreadyPaired, "Pad was already paired.")]
    [InlineData(PairingOutcome.Cancelled, "Pairing with Pad was cancelled.")]
    public void PairOutcomeWordingNamesTheDevice(PairingOutcome outcome, string expected)
    {
        Assert.Equal(expected, BluetoothPairing.DescribeOutcome(outcome, "Pad", ""));
    }

    [Fact]
    public void AFailedPairingSuggestsPairingMode()
    {
        Assert.Contains(
            "pairing mode",
            BluetoothPairing.DescribeOutcome(PairingOutcome.Failed, "Pad", ""));
    }

    [Fact]
    public void AStartupErrorUsesTheWindowsMessageWhenThereIsOne()
    {
        // A null outcome is the attempt that threw before Windows produced one.
        Assert.Equal("no such device", BluetoothPairing.DescribeOutcome(null, "Pad", "no such device"));
        Assert.Contains("Pad", BluetoothPairing.DescribeOutcome(null, "Pad", ""));
    }
}

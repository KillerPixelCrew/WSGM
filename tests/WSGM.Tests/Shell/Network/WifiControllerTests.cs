using WindowsDeviceControl;
using WSGM.Shell;
using WifiConnectOutcome = WindowsDeviceControl.WindowsRadio.WifiConnectOutcome;
using WifiConnectRefusal = WindowsDeviceControl.WindowsRadio.WifiConnectRefusal;
using WifiFailureKind = WindowsDeviceControl.WindowsRadio.WifiFailureKind;

namespace WSGM.Tests.Shell;

public class WifiControllerTests
{
    [Fact]
    public void OnlyARejectedKeyAsksTheUserToRetypeThePassword()
    {
        Assert.Contains(
            "password",
            WifiController.DescribeConnectFailure(WifiFailureKind.KeyRejected, 0, ""));
    }

    [Fact]
    public void AnUnreachableNetworkNeverBlamesThePassword()
    {
        // Re-prompting here would make the user retype a password that was never
        // even tried, which is worse than saying the network was not reachable.
        var message = WifiController.DescribeConnectFailure(WifiFailureKind.Unreachable, 0, "");
        Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("range", message);
    }

    [Fact]
    public void AnUnknownFailureFallsBackToWindowsMessage()
    {
        Assert.Equal(
            "boom",
            WifiController.DescribeConnectFailure(WifiFailureKind.Unknown, 0, "boom"));
        // ...and still says something when there is no message at all.
        Assert.False(string.IsNullOrWhiteSpace(
            WifiController.DescribeConnectFailure(WifiFailureKind.Unknown, 0, "")));
    }

    [Fact]
    public void TheLocationConsentGateIsNamedRatherThanShownAsARawError()
    {
        // ERROR_ACCESS_DENIED from a scan is the 24H2 consent gate, not something elevating or
        // retrying can fix, so it must not read as a generic failure.
        Assert.Equal(
            "Windows is blocking the Wi-Fi scan until location access is allowed "
            + "(Settings > Privacy & security > Location).",
            WifiController.DescribeScanFailure(5, "WlanGetAvailableNetworkList failed (Win32 5)."));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(5023)]
    [InlineData(1168)]
    [InlineData(0)]
    public void EveryOtherScanFailureKeepsItsOwnMessage(int status)
    {
        // A status that merely starts with 5 is not the consent gate.
        var message = $"WlanGetAvailableNetworkList failed (Win32 {status}).";
        Assert.Equal($"Wi-Fi scan failed: {message}", WifiController.DescribeScanFailure(status, message));
    }

    [Theory]
    [InlineData(WifiConnectRefusal.InvalidPassphrase,
        "The password must be 8-63 printable ASCII characters, or 64 hex digits. (Parameter 'passphrase')")]
    [InlineData(WifiConnectRefusal.UnsupportedAuthentication,
        "This network does not advertise a supported personal-key authentication method.")]
    [InlineData(WifiConnectRefusal.NeedsPassword, "This network needs a password and has no saved profile.")]
    [InlineData(WifiConnectRefusal.UnsupportedSecurity, "This network's authentication method is not supported.")]
    public void ARefusedJoinReadsAsItAlwaysHas(WifiConnectRefusal refusal, string expected)
    {
        Assert.Equal(expected, WifiController.DescribeConnectResult(
            new WindowsRadio.WifiConnectResult(WifiConnectOutcome.Refused, 0, refusal)));
    }

    [Fact]
    public void AJoinWithoutAVerdictReadsAsItAlwaysHas()
    {
        Assert.Equal(
            "The Wi-Fi connection attempt did not complete.",
            WifiController.DescribeConnectResult(
                new WindowsRadio.WifiConnectResult(WifiConnectOutcome.Pending, 0, null)));
    }

    [Fact]
    public void AFailedJoinKeepsTheReasonWording()
    {
        // MSMSEC_PSK_MISMATCH_SUSPECTED: a rejected key, so the password is named.
        Assert.Equal(
            WifiController.DescribeConnectFailure(WifiFailureKind.KeyRejected, 294932u, ""),
            WifiController.DescribeConnectResult(
                new WindowsRadio.WifiConnectResult(WifiConnectOutcome.Failed, 294932u, null)));
    }
}

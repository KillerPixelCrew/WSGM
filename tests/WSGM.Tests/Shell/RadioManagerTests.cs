using WindowsDeviceControl;
using WSGM.Shell;
using PairingOutcome = WindowsDeviceControl.WindowsRadio.PairingOutcome;
using RadioPower = WindowsDeviceControl.WindowsRadio.Power;
using WifiConnectOutcome = WindowsDeviceControl.WindowsRadio.WifiConnectOutcome;
using WifiConnectRefusal = WindowsDeviceControl.WindowsRadio.WifiConnectRefusal;
using WifiFailureKind = WindowsDeviceControl.WindowsRadio.WifiFailureKind;
using WifiNetworkKey = WindowsDeviceControl.WindowsRadio.WifiNetworkKey;
using WifiSecurity = WindowsDeviceControl.WindowsRadio.WifiSecurity;

namespace WSGM.Tests.Shell;

public class RadioManagerTests
{
    [Theory]
    [InlineData(RadioPower.Off, "is off")]
    [InlineData(RadioPower.Disabled, "blocked")]
    [InlineData(RadioPower.Absent, "no Wi-Fi adapter")]
    [InlineData(RadioPower.Unknown, "unavailable")]
    public void AnUnusableRadioSaysWhyRatherThanJustOff(RadioPower power, string expected)
    {
        // "Off" for a policy-blocked or missing adapter leaves the user
        // pressing a switch that cannot do anything.
        Assert.Contains(expected, RadioManager.DescribeUnavailable(power, "Wi-Fi"));
    }

    [Fact]
    public void OnlyARejectedKeyAsksTheUserToRetypeThePassword()
    {
        Assert.Contains(
            "password",
            RadioManager.DescribeConnectFailure(WifiFailureKind.KeyRejected, 0, ""));
    }

    [Fact]
    public void AnUnreachableNetworkNeverBlamesThePassword()
    {
        // Re-prompting here would make the user retype a password that was never
        // even tried, which is worse than saying the network was not reachable.
        var message = RadioManager.DescribeConnectFailure(WifiFailureKind.Unreachable, 0, "");
        Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("range", message);
    }

    [Fact]
    public void AnUnknownFailureFallsBackToWindowsMessage()
    {
        Assert.Equal(
            "boom",
            RadioManager.DescribeConnectFailure(WifiFailureKind.Unknown, 0, "boom"));
        // ...and still says something when there is no message at all.
        Assert.False(string.IsNullOrWhiteSpace(
            RadioManager.DescribeConnectFailure(WifiFailureKind.Unknown, 0, "")));
    }

    [Fact]
    public void TheLocationConsentGateIsNamedRatherThanShownAsARawError()
    {
        // ERROR_ACCESS_DENIED from a scan is the 24H2 consent gate, not something elevating or
        // retrying can fix, so it must not read as a generic failure.
        Assert.Equal(
            "Windows is blocking the Wi-Fi scan until location access is allowed "
            + "(Settings > Privacy & security > Location).",
            RadioManager.DescribeScanFailure(5, "WlanGetAvailableNetworkList failed (Win32 5)."));
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
        Assert.Equal($"Wi-Fi scan failed: {message}", RadioManager.DescribeScanFailure(status, message));
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
        Assert.Equal(expected, RadioManager.DescribeConnectResult(
            new WindowsRadio.WifiConnectResult(WifiConnectOutcome.Refused, 0, refusal)));
    }

    [Fact]
    public void AJoinWithoutAVerdictReadsAsItAlwaysHas()
    {
        Assert.Equal(
            "The Wi-Fi connection attempt did not complete.",
            RadioManager.DescribeConnectResult(new WindowsRadio.WifiConnectResult(WifiConnectOutcome.Pending, 0, null)));
    }

    [Fact]
    public void AFailedJoinKeepsTheReasonWording()
    {
        // MSMSEC_PSK_MISMATCH_SUSPECTED: a rejected key, so the password is named.
        Assert.Equal(
            RadioManager.DescribeConnectFailure(WifiFailureKind.KeyRejected, 294932u, ""),
            RadioManager.DescribeConnectResult(
                new WindowsRadio.WifiConnectResult(WifiConnectOutcome.Failed, 294932u, null)));
    }

    [Theory]
    [InlineData(PairingOutcome.Paired, "Pad is paired.")]
    [InlineData(PairingOutcome.AlreadyPaired, "Pad was already paired.")]
    [InlineData(PairingOutcome.Cancelled, "Pairing with Pad was cancelled.")]
    public void PairOutcomeWordingNamesTheDevice(PairingOutcome outcome, string expected)
    {
        Assert.Equal(expected, RadioManager.DescribePairOutcome(outcome, "Pad", ""));
    }

    [Fact]
    public void AFailedPairingSuggestsPairingMode()
    {
        Assert.Contains(
            "pairing mode",
            RadioManager.DescribePairOutcome(PairingOutcome.Failed, "Pad", ""));
    }

    [Fact]
    public void AStartupErrorUsesTheWindowsMessageWhenThereIsOne()
    {
        // A null outcome is the attempt that threw before Windows produced one.
        Assert.Equal("no such device", RadioManager.DescribePairOutcome(null, "Pad", "no such device"));
        Assert.Contains("Pad", RadioManager.DescribePairOutcome(null, "Pad", ""));
    }
}

public class RadioEntryTests
{
    [Fact]
    public void ASecuredNetworkWithoutASavedProfileAsksForAPassword()
    {
        var entry = new WifiNetworkEntry(new WifiNetworkKey("Cafe"u8, WifiSecurity.PersonalPsk)) { Security = WifiSecurity.PersonalPsk };
        Assert.True(entry.NeedsPassword);
    }

    [Fact]
    public void ASavedNetworkNeverAsksForAPasswordAgain()
    {
        var entry = new WifiNetworkEntry(new WifiNetworkKey("Cafe"u8, WifiSecurity.PersonalPsk))
        {
            Security = WifiSecurity.PersonalPsk,
            Saved = true
        };
        Assert.False(entry.NeedsPassword);
    }

    [Fact]
    public void AnOpenNetworkNeverAsksForAPassword()
    {
        var entry = new WifiNetworkEntry(new WifiNetworkKey("Cafe"u8, WifiSecurity.Open)) { Security = WifiSecurity.Open };
        Assert.False(entry.NeedsPassword);
    }

    [Fact]
    public void NeedsPasswordRaisesChangeNotificationWhenTheSavedFlagFlips()
    {
        var entry = new WifiNetworkEntry(new WifiNetworkKey("Cafe"u8, WifiSecurity.PersonalPsk)) { Security = WifiSecurity.PersonalPsk };
        var raised = new List<string?>();
        entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        entry.Saved = true;

        // Without this the password prompt would keep appearing for a network
        // that has just been saved.
        Assert.Contains(nameof(WifiNetworkEntry.NeedsPassword), raised);
    }

    [Fact]
    public void ADeviceWithoutANameStillShowsSomethingSelectable()
    {
        var entry = new BluetoothDeviceEntry("BT#1");
        Assert.False(string.IsNullOrWhiteSpace(entry.Name));
    }

    [Fact]
    public void TheRowActionFollowsPairedAndBusyState()
    {
        var entry = new BluetoothDeviceEntry("BT#1");
        Assert.Equal("Pair", entry.ActionText);

        // Paired is where the primary action becomes the SOFT one. Unpairing
        // lives on its own button, so a tap meant as "disconnect" can never
        // destroy the pairing.
        entry.Paired = true;
        entry.AudioConnectable = true;
        Assert.Equal("Connect", entry.ActionText);

        entry.AudioActive = true;
        Assert.Equal("Disconnect", entry.ActionText);

        entry.Busy = true;
        Assert.Equal("Working...", entry.ActionText);
    }

    [Fact]
    public void TheConnectActionFollowsTheAudioEndpointsNotTheAssociation()
    {
        // A headset can hold an association for another profile while its audio
        // endpoints are unplugged. Reading the broader state would label the
        // button Disconnect and then send the opposite one-shot.
        var entry = new BluetoothDeviceEntry("BT#1")
        {
            Paired = true,
            AudioConnectable = true,
            Connected = true,
            AudioActive = false
        };
        Assert.Equal("Connect", entry.ActionText);
    }

    [Fact]
    public void APairedDeviceWithNoConnectActionOffersOnlyRemove()
    {
        // Mice and gamepads reconnect on their own initiative when used; there
        // is no host-side connect for them, and Windows shows none either.
        var entry = new BluetoothDeviceEntry("BT#1") { Paired = true };
        Assert.False(entry.PrimaryActionVisible);
        Assert.True(entry.RemoveVisible);

        // An unpaired stranger offers Pair only while Windows says pairing is
        // actually possible — a stale endpoint would fail every time.
        var stranger = new BluetoothDeviceEntry("BT#2");
        Assert.False(stranger.PrimaryActionVisible);
        stranger.CanPair = true;
        Assert.True(stranger.PrimaryActionVisible);
        Assert.False(stranger.RemoveVisible);
    }

    [Fact]
    public void ActionTextIsRepublishedWhenPairedOrBusyChanges()
    {
        var entry = new BluetoothDeviceEntry("BT#1");
        var raised = new List<string?>();
        entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        entry.Paired = true;
        entry.Busy = true;

        // The button label is derived, so it needs its own notification or the
        // row keeps offering "Pair" for an already-paired device.
        Assert.Equal(2, raised.FindAll(n => n == nameof(BluetoothDeviceEntry.ActionText)).Count);
    }
}

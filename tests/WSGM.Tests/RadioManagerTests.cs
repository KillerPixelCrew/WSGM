using System.Runtime.InteropServices;
using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests;

public class RadioManagerTests
{
    [Theory]
    [InlineData(RadioPower.Off, 0, "Off")]
    [InlineData(RadioPower.Disabled, 0, "Blocked by Windows")]
    [InlineData(RadioPower.Absent, 0, "No Wi-Fi adapter")]
    [InlineData(RadioPower.Unknown, 0, "State unavailable")]
    [InlineData(RadioPower.On, 0, "Connected")]
    [InlineData(RadioPower.On, 1, "Connecting...")]
    [InlineData(RadioPower.On, 2, "Not connected")]
    public void WifiWordingCoversEveryRadioAndInterfaceState(
        RadioPower power, int interfaceState, string expected)
        => Assert.Equal(expected, RadioManager.DescribeWifi(power, interfaceState));

    [Fact]
    public void APoweredOffWifiRadioNeverClaimsAConnection()
    {
        // The interface can still report "connected" for a moment after the radio
        // goes down; the radio state has to win or the tile lies.
        Assert.Equal("Off", RadioManager.DescribeWifi(RadioPower.Off, 0));
    }

    [Theory]
    [InlineData(RadioPower.Off, 3, "Off")]
    [InlineData(RadioPower.Absent, 0, "No Bluetooth adapter")]
    [InlineData(RadioPower.On, 0, "On")]
    [InlineData(RadioPower.On, 2, "On, 2 device(s)")]
    public void BluetoothWordingCoversEveryRadioState(
        RadioPower power, int devices, string expected)
        => Assert.Equal(expected, RadioManager.DescribeBluetooth(power, devices));

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
        // Verdict 1 is the wrong-password case.
        Assert.Contains("password", RadioManager.DescribeConnectFailure(1, 0, ""));
    }

    [Fact]
    public void AnUnreachableNetworkNeverBlamesThePassword()
    {
        // Re-prompting here would make the user retype a password that was never
        // even tried, which is worse than saying the network was not reachable.
        var message = RadioManager.DescribeConnectFailure(3, 0, "");
        Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("range", message);
    }

    [Fact]
    public void AnUnknownFailureFallsBackToTheHelpersOwnMessage()
    {
        Assert.Equal("boom", RadioManager.DescribeConnectFailure(4, 0, "boom"));
        // ...and still says something when there is no message at all.
        Assert.False(string.IsNullOrWhiteSpace(RadioManager.DescribeConnectFailure(4, 0, "")));
    }

    [Fact]
    public void TheLocationConsentGateIsNamedRatherThanShownAsARawError()
    {
        // Win32 5 from a scan is the 24H2 consent gate, not something elevating
        // or retrying can fix, so it must not read as a generic failure.
        var message = RadioManager.DescribeScanFailure("WlanScan failed (Win32 5)");
        Assert.Contains("location", message, StringComparison.OrdinalIgnoreCase);

        var other = RadioManager.DescribeScanFailure("WlanScan failed (Win32 1168)");
        Assert.DoesNotContain("location", other, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, "Pad is paired.")]
    [InlineData(1, "Pad was already paired.")]
    [InlineData(2, "Pairing with Pad was cancelled.")]
    public void PairOutcomeWordingNamesTheDevice(int outcome, string expected)
        => Assert.Equal(expected, RadioManager.DescribePairOutcome(outcome, "Pad", ""));

    [Fact]
    public void AFailedPairingSuggestsPairingMode()
        => Assert.Contains("pairing mode", RadioManager.DescribePairOutcome(3, "Pad", ""));

    [Fact]
    public void AStartupErrorUsesTheHelpersMessageWhenThereIsOne()
    {
        Assert.Equal("no such device", RadioManager.DescribePairOutcome(-1, "Pad", "no such device"));
        Assert.Contains("Pad", RadioManager.DescribePairOutcome(-1, "Pad", ""));
    }

    // ---- ABI record layouts ----
    //
    // These decode a buffer built here to the layout the Rust side declares. If
    // either side's field order or padding drifts, the SSID or device id would
    // come back as garbage at runtime with no compiler error anywhere.

    [Fact]
    public void AWifiRecordDecodesEveryFieldFromItsDeclaredOffsets()
    {
        var buffer = Marshal.AllocHGlobal(NativeRadio.WifiRecordSize);
        try
        {
            Zero(buffer, NativeRadio.WifiRecordSize);
            WriteUtf16(buffer, 0, "Cafe");
            Marshal.WriteInt32(buffer, 128, 73); // signal
            Marshal.WriteInt32(buffer, 132, 1); // security: pre-shared key
            Marshal.WriteInt32(buffer, 136, 1); // saved
            Marshal.WriteInt32(buffer, 140, 0); // connectable
            Marshal.WriteInt32(buffer, 144, 1); // connected

            var network = NativeRadio.ReadWifiNetwork(buffer);
            Assert.Equal("Cafe", network.Ssid);
            Assert.Equal(73, network.Signal);
            Assert.Equal(1, network.Security);
            Assert.True(network.Saved);
            Assert.False(network.Connectable);
            Assert.True(network.Connected);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ABluetoothRecordDecodesItsTwoStringFieldsIndependently()
    {
        var buffer = Marshal.AllocHGlobal(NativeRadio.BluetoothRecordSize);
        try
        {
            Zero(buffer, NativeRadio.BluetoothRecordSize);
            WriteUtf16(buffer, 0, "BT#abc");
            WriteUtf16(buffer, 512, "WH-1000XM5");
            Marshal.WriteInt32(buffer, 768, 1); // paired
            Marshal.WriteInt32(buffer, 772, 0); // can pair
            Marshal.WriteInt32(buffer, 776, 1); // connected
            WriteUtf16(buffer, 780, "8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");

            var device = NativeRadio.ReadBluetoothDevice(buffer);
            Assert.Equal("BT#abc", device.Id);
            Assert.Equal("WH-1000XM5", device.Name);
            Assert.True(device.Paired);
            Assert.False(device.CanPair);
            Assert.True(device.Connected);
            Assert.Equal("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", device.Container);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void AnUnterminatedStringFieldStopsAtTheFieldEdge()
    {
        // The helper clips rather than failing, so the last unit can be non-NUL
        // only if something went wrong upstream — reading past it would walk into
        // the next field.
        const int units = 4;
        var buffer = Marshal.AllocHGlobal(units * 2);
        try
        {
            for (var i = 0; i < units; i++)
            {
                Marshal.WriteInt16(buffer, i * 2, 'x');
            }
            Assert.Equal("xxxx", NativeRadio.ReadFixedString(buffer, 0, units));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void AnEmptyStringFieldDecodesToEmptyRatherThanNull()
    {
        var buffer = Marshal.AllocHGlobal(8);
        try
        {
            Zero(buffer, 8);
            Assert.Equal("", NativeRadio.ReadFixedString(buffer, 0, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void TheRecordSizesMatchTheRustDeclarations()
    {
        // ssid[64] + 5 ints; id[256] + name[128] + 3 ints + container[40];
        // container[40] + 1 int.
        Assert.Equal(148, NativeRadio.WifiRecordSize);
        Assert.Equal(860, NativeRadio.BluetoothRecordSize);
        Assert.Equal(84, NativeRadio.BluetoothAudioRecordSize);
    }

    private static void Zero(nint buffer, int bytes)
        => Marshal.Copy(new byte[bytes], 0, buffer, bytes);

    private static void WriteUtf16(nint buffer, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            Marshal.WriteInt16(buffer, offset + (i * 2), value[i]);
        }
        Marshal.WriteInt16(buffer, offset + (value.Length * 2), 0);
    }
}

public class RadioEntryTests
{
    [Fact]
    public void ASecuredNetworkWithoutASavedProfileAsksForAPassword()
    {
        var entry = new WifiNetworkEntry("Cafe") { Security = WifiSecurity.Personal };
        Assert.True(entry.NeedsPassword);
    }

    [Fact]
    public void ASavedNetworkNeverAsksForAPasswordAgain()
    {
        var entry = new WifiNetworkEntry("Cafe")
        {
            Security = WifiSecurity.Personal,
            Saved = true,
        };
        Assert.False(entry.NeedsPassword);
    }

    [Fact]
    public void AnOpenNetworkNeverAsksForAPassword()
    {
        var entry = new WifiNetworkEntry("Cafe") { Security = WifiSecurity.Open };
        Assert.False(entry.NeedsPassword);
    }

    [Fact]
    public void NeedsPasswordRaisesChangeNotificationWhenTheSavedFlagFlips()
    {
        var entry = new WifiNetworkEntry("Cafe") { Security = WifiSecurity.Personal };
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
            AudioActive = false,
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

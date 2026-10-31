using WSGM.Shell;
using WifiNetworkKey = WindowsDeviceControl.WindowsRadio.WifiNetworkKey;
using WifiSecurity = WindowsDeviceControl.WindowsRadio.WifiSecurity;

namespace WSGM.Tests.Shell;

public class RadioEntryTests
{
    [Fact]
    public void ASecuredNetworkWithoutASavedProfileAsksForAPassword()
    {
        var entry = new WifiNetworkEntry(new WifiNetworkKey("Cafe"u8, WifiSecurity.PersonalPsk))
            { Security = WifiSecurity.PersonalPsk };
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
        var entry = new WifiNetworkEntry(new WifiNetworkKey("Cafe"u8, WifiSecurity.Open))
            { Security = WifiSecurity.Open };
        Assert.False(entry.NeedsPassword);
    }

    [Fact]
    public void NeedsPasswordRaisesChangeNotificationWhenTheSavedFlagFlips()
    {
        var entry = new WifiNetworkEntry(new WifiNetworkKey("Cafe"u8, WifiSecurity.PersonalPsk))
            { Security = WifiSecurity.PersonalPsk };
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

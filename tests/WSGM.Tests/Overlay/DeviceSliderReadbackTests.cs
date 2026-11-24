using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Overlay;

public sealed class DeviceSliderReadbackTests
{
    [Fact]
    public void UnknownValueIsNotPresentedAsAMinimumOrWrittenDuringRefresh()
    {
        var writes = 0;
        DeviceSliderRow row = new("curve", "Curve", "", -24, 12, 1, CapabilityUnit.None, null, true, _ => writes++);
        var slider = Assert.IsType<UnknownValueSlider>(row.FocusTarget);
        row.RefreshReadback(-24, 12, 1, null, true);
        Assert.True(slider.IsEnabled);
        Assert.False(row.HasPendingUserChange);
        Assert.Equal(0, writes);
        slider.Value = -23;
        Assert.True(row.HasPendingUserChange);
    }

    [Fact]
    public void AnUnknownValueHasNoThumbAndAnnouncesUnknownUntilTheUserMovesIt()
    {
        DeviceSliderRow row = new("hue", "Hue", "", 0, 359, 1, CapabilityUnit.None, null, true, _ => { });
        var slider = Assert.IsType<UnknownValueSlider>(row.FocusTarget);
        Assert.True(slider.IsValueUnknown);
        Assert.Contains(":unknown", slider.Classes);
        var peer = ControlAutomationPeer.CreatePeerForElement(slider);
        Assert.Null(peer.GetProvider<IRangeValueProvider>());
        Assert.Equal("Unknown", peer.GetProvider<IValueProvider>()!.Value);

        // A readback makes it known: the value is read as the row shows it and the thumb is drawn.
        row.RefreshReadback(0, 359, 1, 120, true);
        Assert.False(slider.IsValueUnknown);
        Assert.Equal("120", peer.GetProvider<IValueProvider>()!.Value);
        Assert.Null(peer.GetProvider<IRangeValueProvider>());

        // Unknown again, and then the user's own move makes it known.
        row.RefreshReadback(0, 359, 1, null, true);
        Assert.True(slider.IsValueUnknown);
        slider.Value = 30;
        Assert.False(slider.IsValueUnknown);
    }

    [Fact]
    public void AKnownValueIsReadWithItsUnitRatherThanAsAPercentageOfTheRange()
    {
        DeviceSliderRow row = new("power", "Sustained power", "", 5, 30, 1, CapabilityUnit.Watt, 17, true, _ => { });
        var slider = Assert.IsType<UnknownValueSlider>(row.FocusTarget);
        var peer = ControlAutomationPeer.CreatePeerForElement(slider);
        Assert.Equal("Sustained power", peer.GetName());
        Assert.Equal("17" + CapabilityRowProjection.UnitSuffix(CapabilityUnit.Watt),
            peer.GetProvider<IValueProvider>()!.Value);
        Assert.Null(peer.GetProvider<IRangeValueProvider>());

        slider.Value = 18;
        Assert.Equal("18" + CapabilityRowProjection.UnitSuffix(CapabilityUnit.Watt),
            peer.GetProvider<IValueProvider>()!.Value);
    }

    [Fact]
    public void ASliderWithoutValueTextKeepsItsRangeValue()
    {
        UnknownValueSlider slider = new() { Minimum = 0, Maximum = 100, Value = 40 };
        var peer = ControlAutomationPeer.CreatePeerForElement(slider);
        Assert.Equal(40, peer.GetProvider<IRangeValueProvider>()!.Value);
        Assert.Null(peer.GetProvider<IValueProvider>());
    }

    [Fact]
    public void ProgrammaticReadbackUpdatesTheSameSliderWithoutSchedulingAWrite()
    {
        var writes = 0;
        DeviceSliderRow row = new("power", "Power", "", 8, 37, 1, CapabilityUnit.Watt, 30, true, _ => writes++);
        var slider = Assert.IsType<UnknownValueSlider>(row.FocusTarget);
        row.RefreshReadback(8, 37, 1, 17, true);
        Assert.Same(slider, row.FocusTarget);
        Assert.Equal(17, slider.Value);
        Assert.False(row.HasPendingUserChange);
        Assert.Equal(0, writes);

        slider.Value = 18;
        Assert.True(row.HasPendingUserChange);
        row.RefreshReadback(8, 37, 1, 15, true);
        Assert.Equal(18, slider.Value);
        row.RefreshReadback(8, 37, 1, 15, false);
        Assert.False(row.HasPendingUserChange);
        Assert.False(slider.IsEnabled);
        Assert.Equal(0, writes);
    }
}

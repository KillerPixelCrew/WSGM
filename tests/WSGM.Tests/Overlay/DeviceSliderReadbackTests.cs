using Avalonia.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;

namespace WSGM.Tests;

public sealed class DeviceSliderReadbackTests
{
    [Fact]
    public void ProgrammaticReadbackUpdatesTheSameSliderWithoutSchedulingAWrite()
    {
        int writes = 0;
        DeviceSliderRow row = new("power", "Power", "", 8, 37, 1, CapabilityUnit.Watt, 30, true, _ => writes++);
        Slider slider = Assert.IsType<Slider>(row.FocusTarget);
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

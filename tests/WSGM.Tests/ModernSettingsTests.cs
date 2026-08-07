using WSGM.Core;

namespace WSGM.Tests;

/// <summary>Modern Settings URI mapping used by Quick Access.</summary>
public sealed class ModernSettingsTests
{
    [Fact]
    public void BluetoothUsesTheDedicatedWindowsSettingsUri()
        => Assert.Equal("ms-settings:bluetooth", ModernSettings.UriFor(ModernSettingsPage.Bluetooth));

    [Fact]
    public void WifiUsesTheDedicatedWindowsSettingsUri()
        => Assert.Equal("ms-settings:network-wifi", ModernSettings.UriFor(ModernSettingsPage.Wifi));
}

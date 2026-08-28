using WSGM.DeviceLab.Core.Inventory;

namespace WSGM.DeviceLab.Tests;

/// <summary>
/// The executable specification of location-path handling, using paths measured on the reference
/// unit.
/// </summary>
public class DevicePropertyTests
{
    [Fact]
    public void AnInterfacePathIsReducedToItsCompositeDevice()
    {
        // Measured: a HID interface resolves to ...#USB(2)#USBMI(0). The trailing component names
        // which interface it is, and a controller mode switch rearranges the interfaces - the very
        // event continuation has to survive.
        Assert.Equal(
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)",
            DeviceProperties.ToDeviceLevelPath(
                "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USBMI(0)"));
    }

    [Fact]
    public void TwoInterfacesOfOneDeviceReduceToTheSameKey()
    {
        // The gamepad and MCU interfaces of the composite controller must be recognised as one
        // device, or continuation would treat a mode switch as two separate devices vanishing.
        string gamepad = DeviceProperties.ToDeviceLevelPath(
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USBMI(0)")!;
        string mcu = DeviceProperties.ToDeviceLevelPath(
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USBMI(2)")!;

        Assert.Equal(gamepad, mcu);
    }

    [Fact]
    public void ADeviceLevelPathIsLeftAlone()
    {
        // The composite parent already resolves to this form; reducing it again must not truncate it.
        const string Path = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)";

        Assert.Equal(Path, DeviceProperties.ToDeviceLevelPath(Path));
    }

    [Fact]
    public void DevicesOnDifferentPortsKeepDifferentKeys()
    {
        Assert.NotEqual(
            DeviceProperties.ToDeviceLevelPath("PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USBMI(0)"),
            DeviceProperties.ToDeviceLevelPath("PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)#USBMI(0)"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnAbsentPathYieldsNoKey(string? path)
    {
        // Absent is the honest answer, and it starts a new device generation rather than guessing.
        Assert.Null(DeviceProperties.ToDeviceLevelPath(path));
    }

    [Fact]
    public void ResolveLocationPath_OnThisMachine_FindsThePathForAPresentUsbDevice()
    {
        // Runs against real hardware: this is the check that would have caught the original bug,
        // where the property was read only off the device in hand and every HID interface came back
        // null because the property lives further up the chain.
        MachineInventory inventory = WindowsInventoryCollector.Collect(DateTimeOffset.UtcNow);

        UsbInterfaceInventory[] present = [.. inventory.UsbInterfaces.Where(i => i.Present)];

        Assert.NotEmpty(present);
        Assert.Contains(present, i => i.LocationPath is { Length: > 0 });
        Assert.All(
            present.Where(i => i.LocationPath is { Length: > 0 }),
            i => Assert.NotNull(i.DeviceLevelLocationPath));
    }

    [Fact]
    public void ResolveLocationPath_ForAnInstanceThatDoesNotExist_ReturnsNull()
    {
        Assert.Null(DeviceProperties.ResolveLocationPath(@"USB\VID_FFFF&PID_FFFF\NOT-A-REAL-DEVICE"));
    }
}

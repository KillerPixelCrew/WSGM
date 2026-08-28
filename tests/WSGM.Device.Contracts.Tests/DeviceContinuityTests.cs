using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of following a device across re-enumeration — captured from a real
/// XInput to DirectInput mode switch on the reference unit.
/// </summary>
public class DeviceContinuityTests
{
    private const string Location = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)";

    [Fact]
    public void FindContinuation_AcrossAControllerModeSwitch_FollowsTheLocationPath()
    {
        // The real capture: XInput enumerates as PID_1901 with an iSerialNumber, DirectInput as
        // PID_1902 with a hub/port instance ID. Only the location path is byte-identical across it.
        UsbEndpointObservation beforeSwitch = new()
        {
            VendorId = "0DB0",
            ProductId = "1901",
            DeviceRelease = "0229",
            LocationPath = Location,
        };

        UsbEndpointObservation afterSwitch = new()
        {
            VendorId = "0DB0",
            ProductId = "1902",
            DeviceRelease = "0229",
            LocationPath = Location,
        };

        Assert.Same(afterSwitch, DeviceContinuity.FindContinuation(beforeSwitch, [afterSwitch]));
    }

    [Fact]
    public void FindContinuation_DoesNotMatchTheSameModelOnAnotherPort()
    {
        // A second identical controller plugged in elsewhere is a different device. Matching on
        // identity would hand its handles to the wrong one.
        UsbEndpointObservation previous = Endpoint(Location);
        UsbEndpointObservation otherPort = Endpoint("PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)");

        Assert.Null(DeviceContinuity.FindContinuation(previous, [otherPort]));
    }

    [Fact]
    public void FindContinuation_WithNoLocationPath_RefusesToGuess()
    {
        // Identity fields are exactly the ones a mode switch changes, so guessing would produce a
        // confident wrong answer instead of an honest re-acquisition.
        UsbEndpointObservation previous = Endpoint(null);

        Assert.Null(DeviceContinuity.FindContinuation(previous, [Endpoint(Location)]));
    }

    [Fact]
    public void FindContinuation_IgnoresLocationPathCasing()
    {
        UsbEndpointObservation previous = Endpoint(Location);
        UsbEndpointObservation returned = Endpoint(Location.ToLowerInvariant());

        Assert.NotNull(DeviceContinuity.FindContinuation(previous, [returned]));
    }

    [Fact]
    public void ContinuesGeneration_WhenEveryEndpointComesBack_IsTrue()
    {
        UsbEndpointObservation[] before = [Endpoint(Location), Endpoint("HUB(3)")];
        UsbEndpointObservation[] after = [Endpoint("HUB(3)"), Endpoint(Location)];

        Assert.True(DeviceContinuity.ContinuesGeneration(before, after));
    }

    [Fact]
    public void ContinuesGeneration_WhenAnEndpointVanishes_IsFalse()
    {
        // A new generation invalidates every handle, which is the correct response to an endpoint
        // that did not come back.
        UsbEndpointObservation[] before = [Endpoint(Location), Endpoint("HUB(3)")];
        UsbEndpointObservation[] after = [Endpoint(Location)];

        Assert.False(DeviceContinuity.ContinuesGeneration(before, after));
    }

    [Fact]
    public void JournalPolicy_KeepsACorruptFileRatherThanDeletingIt()
    {
        // A corrupt journal is the record of hardware that may still be changed. Deleting it destroys
        // the only evidence a person could act on.
        Assert.Equal(CorruptionResponse.QuarantineFile, JournalPolicy.Default.OnCorruption);
        Assert.True(JournalPolicy.Default.AtomicReplace);
    }

    private static UsbEndpointObservation Endpoint(string? location) => new()
    {
        VendorId = "0DB0",
        ProductId = "1901",
        LocationPath = location,
    };
}

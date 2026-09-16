using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;

namespace WSGM.DeviceLab.Tests.Capture;

public sealed class RedactionTests
{
    [Fact]
    public void RedactionRejectsDifferentSourceIdsThatBecomeTheSameToken()
    {
        CaptureStreamFile[] streams =
        [
            new() { SourceId = @"HID\VID_1234&PID_5678\private-unit", Events = [] },
            new() { SourceId = @"HID\VID_1234&PID_5678\PRIVATE-UNIT", Events = [] }
        ];
        var error = Assert.Throws<InvalidDataException>(() =>
            ObserveOnlyCaptureWorkflow.RedactStreams(streams, new CaptureRedactor()));

        Assert.Contains("duplicate", error.Message);
        Assert.DoesNotContain("private-unit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InventoryAndStreamsShareOneTokenMapWithoutMergingDistinctIdentifiers()
    {
        const string first = @"HID\VID_1234&PID_5678\unit-one";
        const string second = @"HID\VID_1234&PID_5678\unit-two";
        MachineInventory original = new()
        {
            SchemaVersion = 1,
            CapturedAt = DateTimeOffset.UnixEpoch,
            Firmware = new FirmwareInventory(),
            UsbInterfaces = [new UsbInterfaceInventory { InstanceId = first }]
        };
        CaptureRedactor redactor = new();
        var inventory = InventoryRedaction.ToShareable(original, redactor);
        var streams = ObserveOnlyCaptureWorkflow.RedactStreams(
        [
            new CaptureStreamFile { SourceId = second, Events = [] },
            new CaptureStreamFile { SourceId = first, Events = [] }
        ], redactor);

        var inventoryId = Assert.Single(inventory.UsbInterfaces).InstanceId;
        Assert.Equal(inventoryId, streams[1].SourceId);
        Assert.NotEqual(inventoryId, streams[0].SourceId);
        Assert.Equal(first, original.UsbInterfaces[0].InstanceId);
        Assert.Contains(redactor.Summarize(),
            summary => summary is { Category: RedactionCategory.DeviceInstance, Occurrences: 2 });
    }

    [Fact]
    public void CaptureRedaction_KeepsHardwareFingerprintAndRemovesUnitIdentity()
    {
        const string UnitPath = @"USB\VID_0DB0&PID_1901\00006F64096B22E7";

        var redacted = new CaptureRedactor().Redact(UnitPath);

        Assert.Contains("VID_0DB0&PID_1901", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("00006F64096B22E7", redacted, StringComparison.Ordinal);
    }
}

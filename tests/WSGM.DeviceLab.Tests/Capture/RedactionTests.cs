using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;

namespace WSGM.Device.Tests;

public sealed class RedactionTests
{
    [Fact]
    public void RedactionRejectsDifferentSourceIdsThatBecomeTheSameToken()
    {
        CaptureStreamFile[] streams =
        [
            new() { SourceId = @"HID\VID_1234&PID_5678\private-unit", Events = [] },
            new() { SourceId = @"HID\VID_1234&PID_5678\PRIVATE-UNIT", Events = [] },
        ];
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ObserveOnlyCaptureWorkflow.RedactStreams(streams, new CaptureRedactor()));

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
            UsbInterfaces = [new() { InstanceId = first }],
        };
        CaptureRedactor redactor = new();
        MachineInventory inventory = InventoryRedaction.ToShareable(original, redactor);
        IReadOnlyList<CaptureStreamFile> streams = ObserveOnlyCaptureWorkflow.RedactStreams(
            [new() { SourceId = second, Events = [] }, new() { SourceId = first, Events = [] }], redactor);

        string inventoryId = Assert.Single(inventory.UsbInterfaces).InstanceId;
        Assert.Equal(inventoryId, streams[1].SourceId);
        Assert.NotEqual(inventoryId, streams[0].SourceId);
        Assert.Equal(first, original.UsbInterfaces[0].InstanceId);
        Assert.Contains(redactor.Summarize(), summary => summary.Category == RedactionCategory.DeviceInstance && summary.Occurrences == 2);
    }

    [Fact]
    public void CaptureRedaction_KeepsHardwareFingerprintAndRemovesUnitIdentity()
    {
        const string UnitPath = @"USB\VID_0DB0&PID_1901\00006F64096B22E7";

        string redacted = new CaptureRedactor().Redact(UnitPath);

        Assert.Contains("VID_0DB0&PID_1901", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("00006F64096B22E7", redacted, StringComparison.Ordinal);
    }
}

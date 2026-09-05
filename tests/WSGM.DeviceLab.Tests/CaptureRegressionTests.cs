using System.Text;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;

namespace WSGM.Device.Tests;

public sealed class CaptureRegressionTests
{
    [Theory]
    [InlineData((int)EventDiscontinuity.SourceRestarted)]
    [InlineData((int)EventDiscontinuity.SuspendResume)]
    [InlineData((int)EventDiscontinuity.ClockReset)]
    [InlineData((int)EventDiscontinuity.DeviceGenerationChanged)]
    public void LateReceiptPreservesAnExplicitSegmentReason(int reasonValue)
    {
        EventDiscontinuity reason = (EventDiscontinuity)reasonValue;
        PassiveCaptureTimeline timeline = new(new ReceiptClock());
        CaptureStreamEvent first = timeline.Record(Observation(1));
        CaptureStreamEvent second = timeline.Record(Observation(2) with { Discontinuity = reason });

        Assert.True(second.QpcReceiptTime < first.QpcReceiptTime);
        Assert.Equal(reason, second.Discontinuity);
        Assert.Equal(first.ClockSegment + 1, second.ClockSegment);
    }

    [Theory]
    [InlineData("Baseline", true)]
    [InlineData("OemSettingAfter", true)]
    [InlineData("999", false)]
    [InlineData("-1", false)]
    [InlineData("0", false)]
    [InlineData("baseline", false)]
    [InlineData(" Baseline", false)]
    public void OperatorMarkersAcceptOnlyExactNamedKinds(string value, bool accepted)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"v1\t{value}\taction\tlabel");
        CaptureStreamEvent captureEvent = new PassiveCaptureTimeline(new ReceiptClock()).Record(
            Observation(1) with
            {
                SourceId = GuidedOperatorMarkers.SourceId,
                Payload = new CapturedPayload { Disposition = PayloadDisposition.Included, Length = bytes.Length, Bytes = bytes },
            });

        Assert.Equal(accepted, GuidedOperatorMarkers.TryDecode(captureEvent, out _, out _, out _));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xC0, 0xAF })]
    [InlineData(new byte[] { 0xE2, 0x82 })]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]
    public void OperatorMarkersRejectMalformedUtf8(byte[] invalidSuffix)
    {
        byte[] bytes = [.. "v1\tBaseline\taction\tlabel"u8.ToArray(), .. invalidSuffix];
        CaptureStreamEvent captureEvent = new PassiveCaptureTimeline(new ReceiptClock()).Record(
            Observation(1) with
            {
                SourceId = GuidedOperatorMarkers.SourceId,
                Payload = new CapturedPayload { Disposition = PayloadDisposition.Included, Length = bytes.Length, Bytes = bytes },
            });

        Assert.False(GuidedOperatorMarkers.TryDecode(captureEvent, out _, out string action, out string label));
        Assert.Empty(action);
        Assert.Empty(label);
    }

    [Theory]
    [InlineData("Möwe 日本語")]
    [InlineData("valid replacement character: \uFFFD")]
    public void OperatorMarkersPreserveValidUnicodeLabels(string label)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"v1\tBaseline\taction\t{label}");
        CaptureStreamEvent captureEvent = new PassiveCaptureTimeline(new ReceiptClock()).Record(
            Observation(1) with
            {
                SourceId = GuidedOperatorMarkers.SourceId,
                Payload = new CapturedPayload { Disposition = PayloadDisposition.Included, Length = bytes.Length, Bytes = bytes },
            });

        Assert.True(GuidedOperatorMarkers.TryDecode(captureEvent, out _, out string action, out string decoded));
        Assert.Equal("action", action);
        Assert.Equal(label, decoded);
    }

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
    public void WhitespaceAfterEnvironmentExpansionHasNoExecutablePath()
    {
        const string variable = "WSGM_TEST_EMPTY_COMMAND";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "   ");
            Assert.Null(WindowsInventoryCollector.ExtractExecutablePath($"%{variable}%"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void CancelledInventoryReportsALockedTemporaryFileAndRemovesItOnceReleased()
    {
        using TemporaryDirectory directory = new();
        string path = directory.GetPath("inventory.tmp");
        using (FileStream locked = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            DeviceLabInventoryResult result = Assert.IsType<DeviceLabInventoryResult>(
                DeviceLabInventoryWorkflow.CleanupCancelledWrite(path));
            Assert.Equal(DeviceLabInventoryStatus.WriteFailed, result.Status);
            Assert.Contains(path, result.Error);
            Assert.True(File.Exists(path));
        }
        Assert.Null(DeviceLabInventoryWorkflow.CleanupCancelledWrite(path));
        Assert.False(File.Exists(path));
        Assert.Null(DeviceLabInventoryWorkflow.CleanupCancelledWrite(path));
    }

    private static PassiveObservation Observation(long sequence) => new()
    {
        SourceId = "test-source",
        RecipeStepId = "test-step",
        SourceSequence = sequence,
        DeviceGeneration = 1,
        Payload = new CapturedPayload { Disposition = PayloadDisposition.NotCaptured, Length = 0 },
    };

    private sealed class ReceiptClock : ICaptureReceiptClock
    {
        private long _timestamp = 110;

        public long Frequency => 1000;

        public long GetTimestamp() => _timestamp -= 10;
    }
}

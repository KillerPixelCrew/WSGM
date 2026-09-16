using System.Text;
using WSGM.DeviceLab.Capture;

namespace WSGM.DeviceLab.Tests.Capture;

public sealed class PassiveCaptureTests
{
    [Theory]
    [InlineData((int)EventDiscontinuity.SourceRestarted)]
    [InlineData((int)EventDiscontinuity.SuspendResume)]
    [InlineData((int)EventDiscontinuity.ClockReset)]
    [InlineData((int)EventDiscontinuity.DeviceGenerationChanged)]
    public void LateReceiptPreservesAnExplicitSegmentReason(int reasonValue)
    {
        var reason = (EventDiscontinuity)reasonValue;
        PassiveCaptureTimeline timeline = new(new ReceiptClock());
        var first = timeline.Record(Observation(1));
        var second = timeline.Record(Observation(2) with { Discontinuity = reason });

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
        var bytes = Encoding.UTF8.GetBytes($"v1\t{value}\taction\tlabel");
        var captureEvent = new PassiveCaptureTimeline(new ReceiptClock()).Record(
            Observation(1) with
            {
                SourceId = GuidedOperatorMarkers.SourceId,
                Payload = new CapturedPayload { Disposition = PayloadDisposition.Included, Length = bytes.Length, Bytes = bytes }
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
        var captureEvent = new PassiveCaptureTimeline(new ReceiptClock()).Record(
            Observation(1) with
            {
                SourceId = GuidedOperatorMarkers.SourceId,
                Payload = new CapturedPayload { Disposition = PayloadDisposition.Included, Length = bytes.Length, Bytes = bytes }
            });

        Assert.False(GuidedOperatorMarkers.TryDecode(captureEvent, out _, out var action, out var label));
        Assert.Empty(action);
        Assert.Empty(label);
    }

    [Theory]
    [InlineData("Möwe 日本語")]
    [InlineData("valid replacement character: \uFFFD")]
    public void OperatorMarkersPreserveValidUnicodeLabels(string label)
    {
        var bytes = Encoding.UTF8.GetBytes($"v1\tBaseline\taction\t{label}");
        var captureEvent = new PassiveCaptureTimeline(new ReceiptClock()).Record(
            Observation(1) with
            {
                SourceId = GuidedOperatorMarkers.SourceId,
                Payload = new CapturedPayload { Disposition = PayloadDisposition.Included, Length = bytes.Length, Bytes = bytes }
            });

        Assert.True(GuidedOperatorMarkers.TryDecode(captureEvent, out _, out var action, out var decoded));
        Assert.Equal("action", action);
        Assert.Equal(label, decoded);
    }

    private static PassiveObservation Observation(long sequence) => new()
    {
        SourceId = "test-source",
        RecipeStepId = "test-step",
        SourceSequence = sequence,
        DeviceGeneration = 1,
        Payload = new CapturedPayload { Disposition = PayloadDisposition.NotCaptured, Length = 0 }
    };

    private sealed class ReceiptClock : ICaptureReceiptClock
    {
        private long _timestamp = 110;

        public long Frequency => 1000;

        public long GetTimestamp() => _timestamp -= 10;
    }
}

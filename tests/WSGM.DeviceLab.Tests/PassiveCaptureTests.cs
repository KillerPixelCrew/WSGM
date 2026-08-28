using WSGM.DeviceLab.Core.Capture;

namespace WSGM.DeviceLab.Tests;

/// <summary>Passive capture preserves raw chronology, uncertainty, and correlation limits.</summary>
public class PassiveCaptureTests
{
    [Fact]
    public void Timeline_PreservesSequenceLossAndLateCrossLaneArrival()
    {
        PassiveCaptureTimeline timeline = new(new FakeClock(100, 90));

        timeline.Record(Observation("hid", sequence: 1, value: 0));
        CaptureStreamEvent late = timeline.Record(Observation("hid", sequence: 3, value: 1));

        Assert.Equal(EventLossState.SequenceGap, late.Loss);
        Assert.Equal(EventDiscontinuity.LateArrival, late.Discontinuity);
        Assert.Equal([2L, 1L], timeline.SnapshotByQpc().Select(captureEvent => captureEvent.GlobalSequence));
        Assert.Equal([1L, 2L], timeline.SnapshotByReceipt().Select(captureEvent => captureEvent.GlobalSequence));
    }

    [Fact]
    public void Timeline_SegmentsSourceClockResetSuspendAndDeviceGeneration()
    {
        PassiveCaptureTimeline timeline = new(new FakeClock(100, 110, 120, 130));
        timeline.Record(Observation("sensor", sequence: 1, value: 0) with
        {
            SourceTime = SourceTime(20),
        });
        CaptureStreamEvent reset = timeline.Record(Observation("sensor", sequence: 2, value: 0) with
        {
            SourceTime = SourceTime(10),
        });
        timeline.MarkSuspendResume();
        CaptureStreamEvent resumed = timeline.Record(Observation("sensor", sequence: 3, value: 0));
        CaptureStreamEvent generation = timeline.Record(Observation(
            "sensor", sequence: 4, value: 0, deviceGeneration: 2));

        Assert.Equal(EventDiscontinuity.ClockReset, reset.Discontinuity);
        Assert.Equal(1, reset.ClockSegment);
        Assert.Equal(EventDiscontinuity.SuspendResume, resumed.Discontinuity);
        Assert.Equal(2, resumed.ClockSegment);
        Assert.Equal(EventDiscontinuity.DeviceGenerationChanged, generation.Discontinuity);
        Assert.Equal(3, generation.ClockSegment);
    }

    [Fact]
    public void GuidedMarkers_CoverButtonAxisMotionDetachAndExternalOemChange()
    {
        GuidedOperatorMarkerKind[] kinds =
        [
            GuidedOperatorMarkerKind.ButtonPress,
            GuidedOperatorMarkerKind.ButtonRelease,
            GuidedOperatorMarkerKind.AxisPosition,
            GuidedOperatorMarkerKind.MotionFace,
            GuidedOperatorMarkerKind.Attach,
            GuidedOperatorMarkerKind.Detach,
            GuidedOperatorMarkerKind.OemSettingBefore,
            GuidedOperatorMarkerKind.OemSettingAfter,
        ];
        PassiveCaptureTimeline timeline = new(new FakeClock(Enumerable.Range(1, kinds.Length).Select(value => (long)value).ToArray()));

        for (int index = 0; index < kinds.Length; index++)
        {
            CaptureStreamEvent marker = timeline.Record(GuidedOperatorMarkers.Create(
                "guided",
                kinds[index] is GuidedOperatorMarkerKind.ButtonPress or GuidedOperatorMarkerKind.ButtonRelease
                    ? "button-a"
                    : $"action-{index}",
                kinds[index],
                "operator label",
                index + 1,
                1));

            Assert.True(GuidedOperatorMarkers.TryDecode(marker, out GuidedOperatorMarkerKind decoded, out _, out _));
            Assert.Equal(kinds[index], decoded);
        }

        Assert.Empty(GuidedOperatorMarkers.Validate(timeline.SnapshotByReceipt()));
    }

    [Fact]
    public void DuplicateOperatorMarkers_RemainRawButMakeTheActionAmbiguous()
    {
        PassiveCaptureTimeline timeline = new(new FakeClock(10, 11, 12));
        timeline.Record(GuidedOperatorMarkers.Create("step", "button-a", GuidedOperatorMarkerKind.ButtonPress, "A", 1, 1));
        timeline.Record(GuidedOperatorMarkers.Create("step", "button-a", GuidedOperatorMarkerKind.ButtonPress, "A", 2, 1));
        timeline.Record(GuidedOperatorMarkers.Create("step", "button-a", GuidedOperatorMarkerKind.ButtonRelease, "A", 3, 1));

        IReadOnlyList<string> errors = GuidedOperatorMarkers.Validate(timeline.SnapshotByReceipt());

        Assert.Single(errors);
        Assert.Contains("Duplicate", errors[0], StringComparison.Ordinal);
        Assert.Equal(3, timeline.SnapshotByReceipt().Count);
    }

    [Fact]
    public void Correlation_RequiresStableBaselineActionAndReleaseOnExpectedSource()
    {
        PassiveCaptureTimeline timeline = CorrelationTimeline(actionReturnsToBaseline: true);

        IReadOnlyList<PassiveCorrelationFinding> findings = PassiveCorrelationAnalyzer.Analyze(
            Request(timeline.SnapshotByReceipt()));

        PassiveCorrelationFinding finding = Assert.Single(findings);
        Assert.Equal("hid.target", finding.SourceId);
        Assert.Equal(1, finding.ByteOffset);
        Assert.Equal("correlation-only", finding.EvidenceKind);
        Assert.Equal(6, finding.SupportingEventIds.Count);
        CaptureAnalysisResult result = PassiveCorrelationAnalyzer.ToAnalysisResult("analysis-a", finding);
        Assert.Equal(finding.SupportingEventIds, result.SupportingEventIds);
        Assert.Contains(result.Limitations, limitation =>
            limitation.Contains("not proof of causality", StringComparison.Ordinal));
    }

    [Fact]
    public void UnrelatedKeyboardActivity_DoesNotCreateADeviceCorrelation()
    {
        PassiveCaptureTimeline timeline = CorrelationTimeline(actionReturnsToBaseline: true);

        IReadOnlyList<PassiveCorrelationFinding> findings = PassiveCorrelationAnalyzer.Analyze(
            Request(timeline.SnapshotByReceipt()) with
            {
                ExpectedSourceIds = new HashSet<string>(StringComparer.Ordinal) { "keyboard.unrelated" },
            });

        Assert.Empty(findings);
    }

    [Fact]
    public void FalseCorrelation_ThatDoesNotReturnOnReleaseIsRejected()
    {
        PassiveCaptureTimeline timeline = CorrelationTimeline(actionReturnsToBaseline: false);

        Assert.Empty(PassiveCorrelationAnalyzer.Analyze(Request(timeline.SnapshotByReceipt())));
    }

    [Fact]
    public async Task MissingAndTimedOutSources_ArePreservedAsRawEvidence()
    {
        PassiveCaptureTimeline timeline = new(new FakeClock(1, 2));
        PassiveCaptureCoordinator coordinator = new(
            [new TimeoutSource()],
            timeline);
        ObserveOnlyRecipe recipe = new()
        {
            SchemaVersion = CaptureSchema.RecipeVersion,
            RecipeId = "bounded",
            DisplayName = "Bounded",
            Steps =
            [
                Step("missing", "missing.source"),
                Step("timeout", "timeout.source"),
            ],
        };

        await coordinator.RunAsync(recipe, CancellationToken.None);

        IReadOnlyList<CaptureStreamEvent> events = timeline.SnapshotByReceipt();
        Assert.Equal(EventAccessState.Unavailable, events[0].Access);
        Assert.True(events[1].TimedOut);
    }

    private static PassiveCaptureTimeline CorrelationTimeline(bool actionReturnsToBaseline)
    {
        PassiveCaptureTimeline timeline = new(new FakeClock(10, 11, 12, 20, 21, 22, 23, 30, 31, 32, 33));
        timeline.Record(Observation("hid.target", 1, 0));
        timeline.Record(Observation("hid.target", 2, 0));
        timeline.Record(Observation("keyboard.unrelated", 1, 0));
        timeline.Record(GuidedOperatorMarkers.Create("action", "button-a", GuidedOperatorMarkerKind.ButtonPress, "A", 1, 1));
        timeline.Record(Observation("hid.target", 3, 1));
        timeline.Record(Observation("hid.target", 4, 1));
        timeline.Record(Observation("keyboard.unrelated", 2, 1));
        timeline.Record(GuidedOperatorMarkers.Create("action", "button-a", GuidedOperatorMarkerKind.ButtonRelease, "A", 2, 1));
        timeline.Record(Observation("hid.target", 5, actionReturnsToBaseline ? (byte)0 : (byte)1));
        timeline.Record(Observation("hid.target", 6, actionReturnsToBaseline ? (byte)0 : (byte)1));
        timeline.Record(Observation("keyboard.unrelated", 3, 1));
        return timeline;
    }

    private static PassiveCorrelationRequest Request(IReadOnlyList<CaptureStreamEvent> events) => new()
    {
        AnalysisId = "button-a-analysis",
        ActionId = "button-a",
        ExpectedSourceIds = new HashSet<string>(StringComparer.Ordinal) { "hid.target" },
        Events = events,
        ContextWindowTicks = 15,
    };

    private static PassiveObservation Observation(
        string source,
        long sequence,
        byte value,
        long deviceGeneration = 1)
    {
        byte[] bytes = [0, value];
        return new PassiveObservation
        {
            SourceId = source,
            RecipeStepId = "observe",
            SourceSequence = sequence,
            DeviceGeneration = deviceGeneration,
            Payload = new CapturedPayload
            {
                Length = bytes.Length,
                Disposition = PayloadDisposition.Included,
                Bytes = bytes,
                Sha256 = CaptureHashFile.Hash(bytes),
            },
        };
    }

    private static CaptureSourceTimestamp SourceTime(long value) => new()
    {
        Value = value,
        Frequency = 1_000,
        ClockId = "sensor-clock",
    };

    private static ObservationStep Step(string id, string source) => new()
    {
        StepId = id,
        SourceId = source,
        Kind = ObservationStepKind.TelemetryReadback,
        DurationMilliseconds = 1,
    };

    private sealed class FakeClock(params long[] values) : ICaptureReceiptClock
    {
        private readonly Queue<long> _values = new(values);

        public long Frequency => 1_000;

        public long GetTimestamp() => _values.Dequeue();
    }

    private sealed class TimeoutSource : IPassiveCaptureSource
    {
        public string SourceId => "timeout.source";

        public async Task ObserveAsync(
            ObservationStep step,
            Func<PassiveObservation, ValueTask> emit,
            CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}

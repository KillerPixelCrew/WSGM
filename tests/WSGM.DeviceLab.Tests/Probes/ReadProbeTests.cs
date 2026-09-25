using WSGM.DeviceLab.Probes;

namespace WSGM.DeviceLab.Tests.Probes;

public sealed class ReadProbeTests
{
    [Fact]
    public async Task InvalidDataReturnsARejectionAndRetainsEarlierSamples()
    {
        ReadProbeWorkerRequest request = new()
        {
            SchemaVersion = 1,
            ProbeId = "test",
            ProbeVersion = 1,
            FamilyId = "test",
            EndpointId = "test",
            Family = ReadProbeFamily.Version,
            MaximumReadsPerSecond = 20,
            TimeoutMilliseconds = 2000,
            Repetitions = 2
        };
        var response = await ReadProbeExecutor.ExecuteAsync(
            new MalformedSecondRead(), request, CancellationToken.None);

        Assert.Equal(ReadProbeWorkerStatus.Rejected, response.Status);
        Assert.Single(response.Samples);
        Assert.Equal("Truncated response.", response.Error);
        Assert.False(response.HardwareMutationObserved);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(-1L, false)]
    [InlineData(256L, false)]
    [InlineData(8L, true)]
    public void VersionNumericBoundsAreEnforced(long? value, bool accepted)
    {
        ReadProbeMetadata metadata = new()
        {
            Id = "test",
            Version = 1,
            FamilyId = "test",
            EndpointId = "test",
            ResourceId = "test",
            Family = ReadProbeFamily.Version,
            MaximumReadsPerSecond = 20,
            TimeoutMilliseconds = 2000,
            Repetitions = 1,
            ExpectedResponse = new ReadProbeResponseExpectation
            {
                ValueKind = ReadProbeValueKind.Version,
                MinimumLength = 1,
                MaximumLength = 32,
                MinimumValue = 0,
                MaximumValue = 255
            },
            CrossCheck = new ReadProbeCrossCheck { Id = "test", Kind = ReadProbeCrossCheckKind.Equal }
        };
        ReadProbeWorkerResponse response = new()
        {
            SchemaVersion = 1,
            ProbeId = "test",
            ProbeVersion = 1,
            Status = ReadProbeWorkerStatus.Completed,
            Samples = [Sample() with { NumericValue = value }]
        };

        Assert.Empty(ReadProbeMetadataPolicy.Validate(metadata));
        Assert.Equal(accepted, ReadProbeResponseValidator.Validate(metadata, response).Accepted);
    }

    [Fact]
    public void FanRpmProbe_AllowsLiveTachometerMovementAcrossReads()
    {
        var metadata = MsiClawReadProbes.Family.Probes
            .Single(probe => probe.Id.EndsWith("fan-rpm", StringComparison.Ordinal));
        ReadProbeWorkerResponse response = new()
        {
            SchemaVersion = 1,
            ProbeId = metadata.Id,
            ProbeVersion = metadata.Version,
            Status = ReadProbeWorkerStatus.Completed,
            Samples =
            [
                FanSample("3000,3100", "3010,3090"),
                FanSample("3030,3120", "3020,3110")
            ],
            HardwareMutationObserved = false
        };

        Assert.False(metadata.ExpectedResponse.MustBeStable);
        Assert.Equal(ReadProbeCrossCheckKind.Present, metadata.CrossCheck.Kind);
        Assert.True(ReadProbeResponseValidator.Validate(metadata, response).Accepted);
    }

    [Fact]
    public void ReadProbeSupervisor_OutlivesTheWorkersSemanticDeadline()
    {
        var metadata = MsiClawReadProbes.Family.Probes[0];

        Assert.True(
            ReadProbeWorkerSupervisor.ProcessDeadline(metadata)
            > TimeSpan.FromMilliseconds(metadata.TimeoutMilliseconds));
    }

    [Fact]
    public void ReadProbeResponse_MutationOrMissingCrossCheck_IsRejected()
    {
        var metadata = MsiClawReadProbes.Family.Probes
            .Single(probe => probe.Id.EndsWith("charge-limit", StringComparison.Ordinal));
        ReadProbeSample sample = new()
        {
            ValueKind = ReadProbeValueKind.Integer,
            StatusCode = 1,
            Length = 2,
            NumericValue = 80,
            NormalizedValue = "80",
            ElapsedMilliseconds = 5,
            CrossCheckValue = "80",
            CrossCheckNumericValue = 80
        };
        ReadProbeWorkerResponse response = new()
        {
            SchemaVersion = 1,
            ProbeId = metadata.Id,
            ProbeVersion = metadata.Version,
            Status = ReadProbeWorkerStatus.Completed,
            Samples = [sample, sample],
            HardwareMutationObserved = false
        };

        Assert.True(ReadProbeResponseValidator.Validate(metadata, response).Accepted);
        Assert.Equal(
            "response.mutation",
            ReadProbeResponseValidator.Validate(
                metadata,
                response with { HardwareMutationObserved = true }).Code);
        Assert.Equal(
            "response.cross-check",
            ReadProbeResponseValidator.Validate(
                metadata,
                response with
                {
                    Samples = [sample with { CrossCheckValue = "79" }, sample]
                }).Code);
    }

    private static ReadProbeSample FanSample(string value, string crossCheck)
    {
        return new ReadProbeSample
        {
            ValueKind = ReadProbeValueKind.Text,
            StatusCode = 1,
            Length = 5,
            NormalizedValue = value,
            ElapsedMilliseconds = 5,
            CrossCheckValue = crossCheck
        };
    }

    private static ReadProbeSample Sample()
    {
        return new ReadProbeSample
        {
            ValueKind = ReadProbeValueKind.Version,
            StatusCode = 0,
            Length = 3,
            NumericValue = 8,
            NormalizedValue = "8.0",
            ElapsedMilliseconds = 1,
            CrossCheckValue = "8.0"
        };
    }

    private sealed class MalformedSecondRead : IReadProbeProfile
    {
        private int _reads;

        public CompiledReadProbeDescriptor Descriptor { get; } =
            new("test", 1, "test", "test", ReadProbeFamily.Version, 20, 2000, 2);

        public ValueTask<ReadProbeSample> ReadOnceAsync(CancellationToken cancellationToken)
        {
            return ++_reads == 1
                ? ValueTask.FromResult(Sample())
                : throw new InvalidDataException("Truncated response.");
        }
    }
}

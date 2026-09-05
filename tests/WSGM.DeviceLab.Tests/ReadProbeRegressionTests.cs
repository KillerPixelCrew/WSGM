using WSGM.DeviceLab.Probes;

namespace WSGM.Device.Tests;

public sealed class ReadProbeRegressionTests
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
            Repetitions = 2,
        };
        ReadProbeWorkerResponse response = await ReadProbeExecutor.ExecuteAsync(
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
            ExpectedResponse = new()
            {
                ValueKind = ReadProbeValueKind.Version,
                MinimumLength = 1,
                MaximumLength = 32,
                MinimumValue = 0,
                MaximumValue = 255,
            },
            CrossCheck = new() { Id = "test", Kind = ReadProbeCrossCheckKind.Equal },
        };
        ReadProbeWorkerResponse response = new()
        {
            SchemaVersion = 1,
            ProbeId = "test",
            ProbeVersion = 1,
            Status = ReadProbeWorkerStatus.Completed,
            Samples = [Sample() with { NumericValue = value }],
        };

        Assert.Empty(ReadProbeMetadataPolicy.Validate(metadata));
        Assert.Equal(accepted, ReadProbeResponseValidator.Validate(metadata, response).Accepted);
    }

    private static ReadProbeSample Sample() => new()
    {
        ValueKind = ReadProbeValueKind.Version,
        StatusCode = 0,
        Length = 3,
        NumericValue = 8,
        NormalizedValue = "8.0",
        ElapsedMilliseconds = 1,
        CrossCheckValue = "8.0",
    };

    private sealed class MalformedSecondRead : IReadProbeProfile
    {
        private int _reads;

        public CompiledReadProbeDescriptor Descriptor { get; } =
            new("test", 1, "test", "test", ReadProbeFamily.Version, 20, 2000, 2);

        public ValueTask<ReadProbeSample> ReadOnceAsync(CancellationToken cancellationToken) =>
            ++_reads == 1 ? ValueTask.FromResult(Sample()) : throw new InvalidDataException("Truncated response.");
    }
}

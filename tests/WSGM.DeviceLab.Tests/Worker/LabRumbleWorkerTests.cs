using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Transports;

namespace WSGM.DeviceLab.Tests.Worker;

public sealed class LabRumbleWorkerTests
{
    [Fact]
    public void Zero_IsSkippedAfterASuccessfulZero()
    {
        FakeOutput output = null!;
        using LabRumbleWorker worker = new(log => output = new FakeOutput(log));

        worker.SetIntensity(new LabRumbleFrame(40, 40));
        worker.SetIntensity(LabRumbleFrame.Zero);
        worker.Zero();

        Assert.Equal(["stream", "stream-stop"], output.Purposes);
    }

    [Fact]
    public void Zero_StillRunsAfterAFailedStreamZero()
    {
        FakeOutput output = null!;
        using LabRumbleWorker worker = new(log => output = new FakeOutput(log) { FailingZeros = 1 });

        worker.SetIntensity(new LabRumbleFrame(40, 40));
        Assert.Throws<LabRumbleWriteException>(() => worker.SetIntensity(LabRumbleFrame.Zero));
        worker.Zero();
        worker.Zero();

        Assert.Equal(["stream", "stream-stop", "worker-zero"], output.Purposes);
    }

    [Fact]
    public void Zero_StillRunsAfterAFailedZeroWrite()
    {
        FakeOutput output = null!;
        using LabRumbleWorker worker = new(log => output = new FakeOutput(log) { FailingZeros = 1 });

        var write = worker.Write(LabRumbleFrame.Zero, "zero");
        worker.Zero();

        Assert.NotNull(write.Error);
        Assert.Equal(["zero", "worker-zero"], output.Purposes);
    }

    [Fact]
    public void Zero_AFailedSafetyZeroSaysSo()
    {
        using LabRumbleWorker worker = new(log => new FakeOutput(log) { FailingZeros = 2 });

        worker.Write(LabRumbleFrame.Zero, "zero");
        var failed = Assert.Throws<LabRumbleWriteException>(worker.Zero);

        Assert.StartsWith("The safety zero failed", failed.Message, StringComparison.Ordinal);
    }

    private sealed class FakeOutput(LabRumbleLog log) : ILabRumbleOutput
    {
        public int FailingZeros { get; set; }

        public List<string> Purposes { get; } = [];

        public LabRumbleRoute Route { get; } = new("fake", "xinput", "Fake", "A fake route");

        public void Write(LabRumbleFrame frame, string purpose)
        {
            Purposes.Add(purpose);
            string? error = null;
            if (frame.IsZero && FailingZeros > 0)
            {
                FailingZeros--;
                error = "The zero write failed.";
            }

            log.Add(new LabRumbleWrite(DateTimeOffset.UtcNow, Route.Id, purpose, frame, error is null ? 0 : 5,
                error, null));
            if (error is not null)
            {
                throw new LabRumbleWriteException(error);
            }
        }

        public void Dispose()
        {
        }
    }
}

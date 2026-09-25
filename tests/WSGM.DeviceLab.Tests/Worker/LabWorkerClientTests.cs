using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Tests.Worker;

public sealed class LabWorkerClientTests
{
    [Fact]
    public async Task Start_CompletesTheHandshakeWithARealWorker()
    {
        // The worker reads its secret to end of stream before it says hello. A client that held the
        // authorization pipe open until the hello deadlocked both processes (2026-09-25, 0.1.2).
        var starting = Task.Run(LabWorkerClient.Start);

        var client = await starting.WaitAsync(TimeSpan.FromSeconds(15));

        client.Dispose();
    }
}

extern alias packagedLaunch;
using packagedLaunch::WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

public sealed class ShutdownRequestTests
{
    [Fact]
    public async Task ConcurrentShutdownRequestsLeaveRetirementToTheOwner()
    {
        using var cancellation = new CancellationTokenSource();
        using var ownerEntered = new ManualResetEventSlim();
        using var ownerContinue = new ManualResetEventSlim();
        var retired = 0;
        var shutdown = new ShutdownRequest(cancellation.Cancel);
        var owner = Task.Run(() =>
        {
            try
            {
                ownerEntered.Set();
                Assert.True(ownerContinue.Wait(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                Interlocked.Increment(ref retired);
            }
        });
        try
        {
            Assert.True(ownerEntered.Wait(TimeSpan.FromSeconds(5)));
            Parallel.For(0, 20, _ => shutdown.Request());
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(0, Volatile.Read(ref retired));
        }
        finally
        {
            ownerContinue.Set();
            await owner;
        }

        shutdown.Request();
        Assert.Equal(1, retired);
    }

    [Fact]
    public void ADisposedCancellationSourceCannotEscapeShutdown()
    {
        var cancellation = new CancellationTokenSource();
        var shutdown = new ShutdownRequest(cancellation.Cancel);
        cancellation.Dispose();
        shutdown.Request();
    }

    [Fact]
    public void FailingCancellationCallbacksCannotEscapeShutdown()
    {
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() => throw new InvalidOperationException());
        var shutdown = new ShutdownRequest(cancellation.Cancel);
        shutdown.Request();
        Assert.True(cancellation.IsCancellationRequested);
    }
}

using WSGM.Core;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class ExplorerDesktopHostTests : IDisposable
{
    public ExplorerDesktopHostTests()
    {
        ApplicationShutdownRequest.ResetForTests();
    }

    public void Dispose()
    {
        ApplicationShutdownRequest.ResetForTests();
    }

    [Fact]
    public async Task SessionEndRefusesDesktopRestoreBeforeNativeShellObservation()
    {
        using var config = new TemporaryConfigStore();
        await using var host = new ExplorerDesktopHost(config.Context);
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.RestoreDesktopAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CancelledDeadlineRefusesDesktopRestoreBeforeNativeShellObservation()
    {
        using var config = new TemporaryConfigStore();
        await using var host = new ExplorerDesktopHost(config.Context);
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.RestoreDesktopAsync(TimeSpan.FromSeconds(1), deadline.Token));
    }
}

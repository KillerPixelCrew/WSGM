using System.Net;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.Tests.Shell;

public sealed class EmulatorServiceTests
{
    [Fact]
    public async Task AdmittedInstallOutlivesPageRequestAndExplicitCancelStopsOwnedWork()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new WaitingRelease();
        using var manager = new EmulatorManager(new UserDataContext(directory.Root, "unused-config"), handler);
        await manager.Initialization.WaitAsync(AsyncConditions.TimeLimit);
        using var backend = new EmulatorService(manager, _ => { });
        using var request = new CancellationTokenSource();

        var accepted = await backend.InstallEmulatorAsync("duckstation", "latest", request.Token);
        Assert.True(accepted.Succeeded);
        var download = await handler.Entered.Task.WaitAsync(AsyncConditions.TimeLimit);
        request.Cancel();
        Assert.False(download.IsCancellationRequested);
        Assert.True(backend.ReadProgressState().Busy);
        var repeated = await backend.InstallEmulatorAsync("duckstation", "latest", CancellationToken.None);
        Assert.False(repeated.Succeeded);

        await backend.CancelAsync(CancellationToken.None);
        await backend.OperationCompletion.WaitAsync(AsyncConditions.TimeLimit);
        Assert.True(download.IsCancellationRequested);
        Assert.False(backend.ReadProgressState().Busy);
        Assert.Contains("cancelled", backend.ReadProgressState().Status, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manager.GetSnapshot().Installations);
    }

    [Fact]
    public async Task BackendDisposalCancelsTheOwnedInstall()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new WaitingRelease();
        using var manager = new EmulatorManager(new UserDataContext(directory.Root, "unused-config"), handler);
        await manager.Initialization.WaitAsync(AsyncConditions.TimeLimit);
        using var backend = new EmulatorService(manager, _ => { });
        Assert.True((await backend.InstallEmulatorAsync("duckstation", "latest", CancellationToken.None)).Succeeded);
        var download = await handler.Entered.Task.WaitAsync(AsyncConditions.TimeLimit);
        backend.Dispose();
        await backend.OperationCompletion.WaitAsync(AsyncConditions.TimeLimit);
        Assert.True(download.IsCancellationRequested);
        Assert.Empty(manager.GetSnapshot().Installations);
    }

    [Fact]
    public async Task CancelImmediatelyAfterAdmissionStopsWorkBeforeManagerDispatch()
    {
        using var directory = new TemporaryDirectory();
        using var manager = new EmulatorManager(new UserDataContext(directory.Root, "unused-config"));
        await manager.Initialization.WaitAsync(AsyncConditions.TimeLimit);
        using var backend = new EmulatorService(manager, _ => { });
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredManager = false;
        Assert.True((await backend.StartOperation("queued install", async token =>
        {
            await dispatch.Task.WaitAsync(token);
            enteredManager = true;
            await manager.InstallAsync("duckstation", "latest", token);
        }, CancellationToken.None)).Succeeded);

        await backend.CancelAsync(CancellationToken.None);
        await backend.OperationCompletion.WaitAsync(AsyncConditions.TimeLimit);
        Assert.False(enteredManager);
        Assert.Contains("cancelled", backend.ReadProgressState().Status, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manager.GetSnapshot().Installations);
    }

    [Fact]
    public async Task AdmissionReturnsBeforeSynchronousPreparationFinishes()
    {
        using var directory = new TemporaryDirectory();
        using var manager = new EmulatorManager(new UserDataContext(directory.Root, "unused-config"));
        await manager.Initialization.WaitAsync(AsyncConditions.TimeLimit);
        using var backend = new EmulatorService(manager, _ => { });
        using var preparation = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = Task.Run(() => backend.StartOperation("synchronous preparation", token =>
        {
            entered.TrySetResult();
            preparation.Wait(token);
            return Task.CompletedTask;
        }, CancellationToken.None));
        try
        {
            Assert.True((await admission.WaitAsync(AsyncConditions.TimeLimit)).Succeeded);
            await entered.Task.WaitAsync(AsyncConditions.TimeLimit);
            Assert.False(backend.OperationCompletion.IsCompleted);
            await backend.CancelAsync(CancellationToken.None);
            await backend.OperationCompletion.WaitAsync(AsyncConditions.TimeLimit);
            Assert.Contains("cancelled", backend.ReadProgressState().Status, StringComparison.OrdinalIgnoreCase);
            Assert.False(backend.OperationCompletion.IsCanceled);
            Assert.True((await backend.StartOperation("next operation", _ => Task.CompletedTask,
                CancellationToken.None)).Succeeded);
            await backend.OperationCompletion.WaitAsync(AsyncConditions.TimeLimit);
        }
        finally
        {
            preparation.Set();
            await admission.WaitAsync(AsyncConditions.TimeLimit);
            await backend.OperationCompletion.WaitAsync(AsyncConditions.TimeLimit);
        }
    }

    private sealed class WaitingRelease : HttpMessageHandler
    {
        internal TaskCompletionSource<CancellationToken> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

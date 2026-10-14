using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Shell;

public sealed class SessionModesTests
{
    [AvaloniaFact]
    public void UiWarningIsDeliveredImmediately()
    {
        using UiFixture fixture = new();
        var modes = new SessionModes(new AppConfig(), null);
        var warnings = new List<string>();
        modes.SteamStartFailed += warnings.Add;

        modes.ReportWarning("Steam could not start.");

        Assert.Equal(["Steam could not start."], warnings);
    }

    [AvaloniaFact]
    public async Task WorkerWarningReachesTheUiOwnerWithoutBlockingRecovery()
    {
        using UiFixture fixture = new();
        var modes = new SessionModes(new AppConfig(), null);
        TaskCompletionSource<string> delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        modes.SteamStartFailed += warning =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            delivered.SetResult(warning);
        };

        await Task.Run(() =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            modes.ReportWarning("Steam did not exit.");
        });

        Assert.Equal("Steam did not exit.", await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public void WorkerWarningQueuedBeforeShutdownDoesNotReopenTheOverlay()
    {
        using UiFixture fixture = new();
        var modes = new SessionModes(new AppConfig(), null);
        var warnings = 0;
        modes.SteamStartFailed += _ => warnings++;

        // Avalonia can pump messages during a synchronous wait. Disable processing so the worker's
        // warning is still queued when shutdown closes admission, then explicitly drain it.
        using (Dispatcher.UIThread.DisableProcessing())
        {
            Task.Run(() => modes.ReportWarning("Steam did not exit."))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.Equal(0, warnings);
            modes.RequestShutdown();
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, warnings);
    }

    [AvaloniaFact]
    public async Task ShutdownSuppressesNewWarningsFromEitherThread()
    {
        using UiFixture fixture = new();
        var modes = new SessionModes(new AppConfig(), null);
        var warnings = 0;
        modes.SteamStartFailed += _ => warnings++;
        modes.RequestShutdown();

        modes.ReportWarning("Steam could not start.");
        await Task.Run(() => modes.ReportWarning("Steam did not exit."));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, warnings);
    }
}

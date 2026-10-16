using System.Diagnostics;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class ExplorerDesktopHostTests : IDisposable
{
    [Fact]
    public async Task ForcedExitUsesTheHeldHelperHandleAndTheRequestedExitStatus()
    {
        using var helper = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("The isolated helper could not start.");
        try
        {
            _ = helper.Handle;
            NativeShellProcess.Terminate(helper, exitCode: 0);
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, helper.ExitCode);
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: false);
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

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

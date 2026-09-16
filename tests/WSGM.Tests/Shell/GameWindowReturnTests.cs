using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class GameWindowReturnTests
{
    [Fact]
    public async Task SteamRaisingConsoleStillEndsAtSelectedGameHwnd()
    {
        nint foreground = 10;
        List<string> log = [];
        TaskCompletionSource<bool> steam = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var subject = new GameWindowReturn((pid, _) =>
        {
            Assert.Equal(42u, pid);
            foreground = 20; // Steam selected the console; both windows may share the game PID.
            return steam.Task;
        }, _ => 42, _ => false, hwnd =>
        {
            foreground = hwnd;
            return true;
        }, log.Add);

        var work = subject.ReturnAsync(30, 42, CancellationToken.None);
        Assert.Equal(20, foreground);
        steam.SetResult(true);
        await work;
        Assert.Equal(30, foreground);
        Assert.Contains(log, line => line.Contains("foreground verified=True", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableOrFaultedSteamStillFocusesSelectedWindow(bool throws)
    {
        nint focused = 0;
        var subject = new GameWindowReturn((_, _) => throws
                ? Task.FromException<bool>(new InvalidOperationException("unavailable"))
                : Task.FromResult(false),
            _ => 42, _ => false, hwnd =>
            {
                focused = hwnd;
                return true;
            }, _ => { });
        await subject.ReturnAsync(30, 42, CancellationToken.None);
        Assert.Equal(30, focused);
    }

    [Fact]
    public async Task SelectingConsoleDoesNotRaiseSteamGame()
    {
        var raised = false;
        nint focused = 0;
        var subject = new GameWindowReturn((_, _) =>
            {
                raised = true;
                return Task.FromResult(true);
            },
            _ => 42, _ => true, hwnd =>
            {
                focused = hwnd;
                return true;
            }, _ => { });
        await subject.ReturnAsync(20, 42, CancellationToken.None);
        Assert.False(raised);
        Assert.Equal(20, focused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacedWindowIsNeverFocused(bool replacedDuringRaise)
    {
        var owner = replacedDuringRaise ? 42u : 99u;
        var raised = false;
        var subject = new GameWindowReturn((_, _) =>
        {
            raised = true;
            owner = 99;
            return Task.FromResult(true);
        }, _ => owner, _ => false, _ => throw new InvalidOperationException("Must not focus"), _ => { });
        await subject.ReturnAsync(30, 42, CancellationToken.None);
        Assert.Equal(replacedDuringRaise, raised);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopenOrShutdownCancelsFinalFocus(bool shutdown)
    {
        using var request = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        var subject = new GameWindowReturn((_, _) =>
        {
            (shutdown ? lifetime : request).Cancel();
            return Task.FromResult(true);
        }, _ => 42, _ => false, _ => throw new InvalidOperationException("Must not focus"), _ => { }, lifetime.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subject.ReturnAsync(30, 42, request.Token));
    }

    [Fact]
    public async Task RefusedForegroundIsReportedWithoutRetry()
    {
        var attempts = 0;
        List<string> log = [];
        var subject = new GameWindowReturn((_, _) => Task.FromResult(true), _ => 42, _ => false,
            _ =>
            {
                attempts++;
                return false;
            }, log.Add);
        await subject.ReturnAsync(30, 42, CancellationToken.None);
        Assert.Equal(1, attempts);
        Assert.Contains(log, line => line.Contains("foreground verified=False", StringComparison.Ordinal));
    }

    [Fact]
    public void SwitcherReplacesReusedHwndInsteadOfRetainingOldProcess()
    {
        var model = new AppSwitcherViewModel();
        model.Reconcile([new WindowFinder.AppWindow(30, "Game", 42)], 30, Create);
        var original = model.Entries[0];
        model.Reconcile([new WindowFinder.AppWindow(30, "Another process", 99)], 30, Create);
        Assert.NotSame(original, model.Entries[0]);
        Assert.Equal(99u, model.Entries[0].ProcessId);
        return;

        static AppSwitcherEntry Create(WindowFinder.AppWindow window)
        {
            return new AppSwitcherEntry(window.Hwnd, window.Title, false, null) { ProcessId = window.ProcessId };
        }
    }
}
